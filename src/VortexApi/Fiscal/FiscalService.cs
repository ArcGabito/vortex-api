using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Fiscal.Peru;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Fiscal;

/// <summary>
/// Facturación electrónica centralizada en el hub: los sistemas piden el comprobante (idempotente por su Id), el hub lo
/// arma con las reglas del país, lo firma con el certificado de la organización, lo envía a la autoridad y avisa el
/// resultado con <c>hub.fiscal.accepted</c> o <c>hub.fiscal.rejected</c>. Así el certificado y la clave SOL no salen del
/// hub y todas las cajas y equipos emiten con la misma numeración y el mismo certificado.
/// </summary>
public sealed class FiscalService(
    VortexDbContext db,
    FiscalSecrets secrets,
    ISunatClient sunat,
    EventPublisher events,
    AuditService audit,
    TimeProvider clock,
    IOptions<FiscalOptions> options,
    ILogger<FiscalService> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public static readonly string[] SupportedCountries = ["PE"];
    private static readonly TimeSpan NotConfiguredDelay = TimeSpan.FromMinutes(5);

    /// <summary>SUNAT: "El comprobante fue registrado previamente" (un reenvío de algo que ya llegó).</summary>
    public const string AlreadyRegistered = "1033";

    /// <summary>Días para anular por comunicación de baja o resumen diario, desde la emisión.</summary>
    public const int VoidWindowDays = 7;

    /// <summary>Espera antes de consultar un ticket (SUNAT procesa los resúmenes en segundos o minutos).</summary>
    private static readonly TimeSpan TicketDelay = TimeSpan.FromSeconds(30);

    private readonly FiscalOptions _options = options.Value;

    // ---------- Perfil ----------

    public async Task<FiscalProfileDto> GetProfileAsync(Guid orgId)
    {
        var profile = await db.FiscalProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.OrganizationId == orgId);
        var company = await db.CompanyProfiles.AsNoTracking().SingleOrDefaultAsync(c => c.Id == orgId);
        return ToDto(profile ?? new FiscalProfile { OrganizationId = orgId }, company);
    }

    public async Task<FiscalProfileDto> SaveProfileAsync(Guid orgId, FiscalProfileRequest r, Guid? actorUserId)
    {
        var company = await db.CompanyProfiles.AsNoTracking().SingleOrDefaultAsync(c => c.Id == orgId);
        var profile = await db.FiscalProfiles.SingleOrDefaultAsync(p => p.OrganizationId == orgId);
        if (profile is null)
        {
            profile = new FiscalProfile { OrganizationId = orgId };
            db.FiscalProfiles.Add(profile);
        }

        var errors = new List<string>();
        profile.Environment = r.Environment;
        profile.Ubigeo = Clean(r.Ubigeo);
        if (profile.Ubigeo is { } ubigeo && (ubigeo.Length != 6 || !ubigeo.All(char.IsAsciiDigit)))
            errors.Add("ubigeo debe tener 6 dígitos (código INEI del distrito).");
        profile.SolUser = Clean(r.SolUser)?.ToUpperInvariant();
        if (r.SolPassword is not null)
            profile.SolPassword = r.SolPassword.Length == 0 ? null : secrets.Protect(r.SolPassword);
        var algorithm = (Clean(r.SignatureAlgorithm) ?? profile.SignatureAlgorithm).ToLowerInvariant();
        if (algorithm is not ("sha1" or "sha256"))
            errors.Add("signatureAlgorithm debe ser sha1 o sha256.");
        profile.SignatureAlgorithm = algorithm;

        // Certificado nuevo (con su contraseña) o solo otra contraseña para el que ya está.
        if (r.CertificateBase64 is { Length: > 0 } || r.CertificatePassword is not null)
        {
            byte[]? pfx = null;
            if (r.CertificateBase64 is { Length: > 0 } base64)
            {
                try
                {
                    pfx = Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    errors.Add("certificateBase64 no es base64 válido.");
                }
            }
            else if (profile.Certificate is { } stored)
            {
                pfx = secrets.Unprotect(stored);
            }
            var password = r.CertificatePassword
                           ?? (profile.CertificatePassword is { } p ? secrets.UnprotectString(p) : "");
            if (pfx is not null)
            {
                try
                {
                    using var certificate = LoadCertificate(pfx, password);
                    profile.Certificate = secrets.Protect(pfx);
                    profile.CertificatePassword = secrets.Protect(password);
                    profile.CertificateSubject = certificate.Subject;
                    profile.CertificateExpiresAt = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
                }
                catch (CryptographicException)
                {
                    errors.Add("No se pudo abrir el certificado: revise el archivo PFX y su contraseña.");
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add(ex.Message);
                }
            }
        }

        profile.Enabled = r.Enabled;
        if (r.Enabled)
            errors.AddRange(MissingForEnabled(profile, company));
        if (errors.Count > 0)
            throw ApiException.BadRequest(ErrorCodes.Validation, string.Join(" ", errors));

        profile.UpdatedAt = clock.GetUtcNow();
        audit.Record(AuditActions.FiscalProfileUpdated, true, actorUserId, orgId,
            detail: $"{(profile.Enabled ? "habilitado" : "deshabilitado")}, {profile.Environment}");
        await db.SaveChangesAsync();
        return ToDto(profile, company);
    }

    private IEnumerable<string> MissingForEnabled(FiscalProfile profile, CompanyProfile? company)
    {
        if (company is null || !SupportedCountries.Contains(company.CountryCode))
            yield return "La facturación electrónica del hub está disponible para empresas de Perú (país de la empresa: PE).";
        else if (!PeruRules.IsValidRuc(company.TaxId))
            yield return "El RUC de la empresa (datos de la empresa) no es válido.";
        if (profile.SolUser is null || profile.SolPassword is null)
            yield return "Faltan el usuario y la clave SOL.";
        if (profile.Certificate is null)
            yield return "Falta el certificado digital (PFX).";
        else if (profile.CertificateExpiresAt is { } expires && expires <= clock.GetUtcNow())
            yield return "El certificado digital está vencido.";
    }

    // ---------- Comprobantes ----------

    /// <summary>
    /// Encola el comprobante. El mismo <see cref="FiscalDocumentRequest.SourceId"/> devuelve el que ya existe (el sistema puede
    /// reintentar desde su cola sin duplicar).
    /// </summary>
    public async Task<(FiscalDocumentDto Document, bool Created)> EnqueueAsync(ClaimsPrincipal user, FiscalDocumentRequest r)
    {
        var orgId = user.OrganizationId()!.Value;
        var product = MasterDataWriter.SourceOf(user);
        if (!user.HasClaim(VortexClaims.Features, FiscalEvents.Feature))
            throw new ApiException(StatusCodes.Status403Forbidden, ErrorCodes.FiscalNotEnabled,
                $"El módulo {FiscalEvents.Feature} no está habilitado para este sistema en la organización.");

        var existing = await db.FiscalDocuments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.OrganizationId == orgId && d.Product == product && d.SourceId == r.SourceId);
        if (existing is not null)
            return (ToDto(existing), false);

        var company = await db.CompanyProfiles.AsNoTracking().SingleOrDefaultAsync(c => c.Id == orgId);
        if (company is null || !SupportedCountries.Contains(company.CountryCode))
            throw ApiException.BadRequest(ErrorCodes.FiscalCountryUnsupported, "La facturación electrónica del hub aún no está disponible para el país de la empresa.");
        var profile = await db.FiscalProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.OrganizationId == orgId);
        if (profile is not { Enabled: true })
            throw ApiException.Conflict(ErrorCodes.FiscalNotConfigured, "La facturación electrónica no está configurada ni habilitada en el hub.");

        r = r with
        {
            Series = r.Series.Trim().ToUpperInvariant(),
            Currency = r.Currency.Trim().ToUpperInvariant(),
            Customer = r.Customer with { DocNumber = Clean(r.Customer.DocNumber), Name = r.Customer.Name.Trim() },
            Reference = r.Reference is { } reference ? reference with { Series = reference.Series.Trim().ToUpperInvariant() } : null,
        };
        var errors = PeruRules.Validate(r);
        if (errors.Count > 0)
            throw ApiException.BadRequest(ErrorCodes.Validation, string.Join(" ", errors));

        var typeCode = PeruUbl.TypeCode(r.Type);
        if (await NumberInUseAsync(orgId, r.Type, r.Series, r.Number))
            throw NumberInUse(r);

        var now = clock.GetUtcNow();
        var (_, totals) = PeruUbl.Compute(r.Lines);
        var document = new FiscalDocument
        {
            OrganizationId = orgId,
            Product = product,
            SourceId = r.SourceId,
            DocumentType = r.Type,
            Series = r.Series,
            Number = r.Number,
            IssueDate = r.IssueDate,
            CurrencyCode = r.Currency,
            Total = totals.Total,
            CustomerDocType = r.Customer.DocType,
            CustomerDocNumber = r.Customer.DocNumber,
            Request = JsonSerializer.Serialize(r, Json),
            Status = FiscalStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.FiscalDocuments.Add(document);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Otro equipo lo pidió a la vez: si es el mismo documento se devuelve; si es otro con el mismo número, conflicto.
            db.ChangeTracker.Clear();
            var same = await db.FiscalDocuments.AsNoTracking()
                .SingleOrDefaultAsync(d => d.OrganizationId == orgId && d.Product == product && d.SourceId == r.SourceId);
            if (same is not null)
                return (ToDto(same), false);
            throw NumberInUse(r);
        }
        logger.LogInformation("Comprobante {Type} {Series}-{Number} ({Code}) encolado para {Product}.", r.Type, r.Series, r.Number, typeCode, product);
        return (ToDto(document), true);
    }

    private Task<bool> NumberInUseAsync(Guid orgId, string type, string series, long number) =>
        db.FiscalDocuments.AnyAsync(d => d.OrganizationId == orgId && d.DocumentType == type && d.Series == series && d.Number == number);

    private static ApiException NumberInUse(FiscalDocumentRequest r) =>
        ApiException.Conflict(ErrorCodes.FiscalNumberInUse, $"El número {r.Series}-{r.Number} ya se usó en otro comprobante de este tipo.");

    /// <summary>Vuelve a poner en cola un comprobante que falló (p. ej. tras corregir la clave SOL o el certificado).</summary>
    public async Task<FiscalDocumentDto> RetryAsync(Guid orgId, string product, Guid id, Guid? actorUserId)
    {
        var document = await db.FiscalDocuments.SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == orgId && d.Product == product)
                       ?? throw ApiException.NotFound("Comprobante");
        if (document.Status != FiscalStatus.Failed)
            throw ApiException.Conflict(ErrorCodes.FiscalNotRetryable, "Solo se reintenta un comprobante que falló (los rechazados se emiten de nuevo con otro número).");
        var now = clock.GetUtcNow();
        document.Status = FiscalStatus.Pending;
        document.Attempts = 0;
        document.NextAttemptAt = now;
        document.UpdatedAt = now;
        audit.Record(AuditActions.FiscalDocumentRetried, true, actorUserId, orgId, product, document.Id.ToString(),
            $"{document.DocumentType} {document.FullNumber}");
        await db.SaveChangesAsync();
        return ToDto(document);
    }

    // ---------- Procesador ----------

    /// <summary>Firma y envía un lote de comprobantes pendientes. Devuelve cuántos intentó. Puede correr en varias réplicas.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await db.FiscalDocuments.AsNoTracking()
            .Where(d => d.Status == FiscalStatus.Pending && d.NextAttemptAt <= now && (d.LockedUntil == null || d.LockedUntil < now))
            .OrderBy(d => d.NextAttemptAt).Select(d => d.Id).Take(_options.BatchSize).ToListAsync(ct);

        var attempted = 0;
        foreach (var id in due)
        {
            var lockUntil = now.AddSeconds(_options.TimeoutSeconds * 2 + 30);
            var claimed = await db.FiscalDocuments
                .Where(d => d.Id == id && d.Status == FiscalStatus.Pending && (d.LockedUntil == null || d.LockedUntil < now))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LockedUntil, lockUntil), ct);
            if (claimed == 0)
                continue;
            try
            {
                await ProcessOneAsync(id, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Error al procesar el comprobante {Id}.", id);
                db.ChangeTracker.Clear();
                var retryAt = clock.GetUtcNow() + Backoff(1);
                await db.FiscalDocuments.Where(d => d.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(d => d.NextAttemptAt, retryAt)
                    .SetProperty(d => d.LastError, Truncate(ex.Message, 500)), CancellationToken.None);
            }
            attempted++;
        }
        db.ChangeTracker.Clear();
        return attempted + await ProcessVoidsAsync(ct);
    }

    private async Task ProcessOneAsync(Guid id, CancellationToken ct)
    {
        var document = await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, ct);
        var profile = await db.FiscalProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.OrganizationId == document.OrganizationId, ct);
        var company = await db.CompanyProfiles.AsNoTracking().SingleOrDefaultAsync(c => c.Id == document.OrganizationId, ct);
        var now = clock.GetUtcNow();

        // Sin perfil usable no se gasta un intento: queda pendiente hasta que lo configuren.
        var problem = profile is not { Enabled: true }
            ? "La facturación electrónica está deshabilitada en el hub."
            : MissingForEnabled(profile, company).FirstOrDefault();
        if (problem is not null || profile is null || company is null)
        {
            await UpdateAsync(id, d =>
            {
                d.LastError = problem;
                d.NextAttemptAt = now + NotConfiguredDelay;
            }, ct);
            return;
        }

        var request = JsonSerializer.Deserialize<FiscalDocumentRequest>(document.Request, Json)!;
        var ruc = company.TaxId!;
        string signedXml, hash, qr;
        if (document.SignedXml is { } alreadySigned)
        {
            // Se reenvía exactamente lo mismo: si el primer envío llegó, la autoridad no lo toma como otro comprobante.
            (signedXml, hash, qr) = (alreadySigned, document.Hash!, document.QrText!);
        }
        else
        {
            try
            {
                using var certificate = LoadCertificate(secrets.Unprotect(profile.Certificate!), secrets.UnprotectString(profile.CertificatePassword!));
                var xml = PeruUbl.Build(request, new PeruIssuer(ruc, company.LegalName, company.TradeName, profile.Ubigeo, company.AddressLine,
                    company.District, company.City, company.Region));
                (signedXml, hash) = XmlSigner.Sign(xml, certificate, profile.SignatureAlgorithm);
                qr = PeruUbl.QrText(ruc, request, hash);
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
            {
                await FinishAsync(id, FiscalStatus.Failed, d => d.LastError = $"No se pudo firmar: {Truncate(ex.Message, 400)}", ct);
                return;
            }
        }

        var fileName = PeruUbl.FileName(ruc, PeruUbl.TypeCode(document.DocumentType), document.Series, document.Number);
        var zip = SunatClient.Zip(fileName + ".xml", signedXml);
        var result = await sunat.SendBillAsync(profile.Environment, ruc + profile.SolUser, secrets.UnprotectString(profile.SolPassword!), fileName, zip, ct);
        if (result.Code == AlreadyRegistered)
        {
            // Un envío anterior sí llegó (se cortó la respuesta): en producción se trae su CDR; en beta no hay consulta y se
            // toma como aceptado con la observación.
            result = profile.Environment == FiscalEnvironment.Production
                ? await sunat.GetStatusCdrAsync(profile.Environment, ruc + profile.SolUser, secrets.UnprotectString(profile.SolPassword!), ruc,
                    PeruUbl.TypeCode(document.DocumentType), document.Series, document.Number, ct)
                : new SendResult(SendOutcome.Responded, "0", result.Description, [$"{AlreadyRegistered}: {result.Description}"], null);
        }

        var attempts = document.Attempts + 1;
        var status = StatusOf(result, attempts >= _options.MaxAttempts);
        void Apply(FiscalDocument d)
        {
            d.Attempts = attempts;
            d.SentAt = now;
            d.SignedXml = signedXml;
            d.Hash = hash;
            d.QrText = qr;
            d.ResponseCode = result.Code;
            d.ResponseDescription = Truncate(result.Description, 1000);
            d.Notes = result.Notes.Count > 0 ? Truncate(string.Join("\n", result.Notes), 2000) : null;
            d.CdrXml = result.CdrXml ?? d.CdrXml;
            d.LastError = result.Outcome is SendOutcome.Transient or SendOutcome.Error
                ? Truncate($"{result.Code} {result.Description}".Trim(), 500)
                : null;
            if (status == FiscalStatus.Pending)
                d.NextAttemptAt = now + Backoff(attempts);
        }

        if (status == FiscalStatus.Pending)
            await UpdateAsync(id, Apply, ct);
        else
            await FinishAsync(id, status, Apply, ct);
        logger.LogInformation("Comprobante {File}: {Status} ({Code} {Description}).", fileName, status, result.Code, result.Description);
    }

    /// <summary>Estado según la respuesta: CDR 0 aceptado (con notas, observado), 4000+ observado, 2000–3999 rechazado.</summary>
    public static FiscalStatus StatusOf(SendResult result, bool lastAttempt) => result.Outcome switch
    {
        SendOutcome.Responded => int.TryParse(result.Code, out var code)
            ? code switch
            {
                0 => result.Notes.Count > 0 ? FiscalStatus.Observed : FiscalStatus.Accepted,
                >= 4000 => FiscalStatus.Observed,
                _ => FiscalStatus.Rejected,
            }
            : FiscalStatus.Failed,
        SendOutcome.Rejected => FiscalStatus.Rejected,
        SendOutcome.Error => FiscalStatus.Failed,
        _ => lastAttempt ? FiscalStatus.Failed : FiscalStatus.Pending,
    };

    private async Task UpdateAsync(Guid id, Action<FiscalDocument> change, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var d = await db.FiscalDocuments.SingleAsync(x => x.Id == id, ct);
        change(d);
        d.LockedUntil = null;
        d.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Estado final y el evento para el sistema que lo pidió, en la misma transacción.</summary>
    private async Task FinishAsync(Guid id, FiscalStatus status, Action<FiscalDocument> change, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var d = await db.FiscalDocuments.SingleAsync(x => x.Id == id, ct);
            change(d);
            d.Status = status;
            d.LockedUntil = null;
            d.UpdatedAt = clock.GetUtcNow();
            var data = new FiscalDocumentResponded(d.SourceId, d.Product, d.DocumentType, d.Series, d.Number, d.Status, d.Hash, d.QrText,
                d.ResponseCode, d.ResponseDescription ?? d.LastError, d.Notes);
            var accepted = status is FiscalStatus.Accepted or FiscalStatus.Observed;
            await events.AppendAsync(new EventActor(d.OrganizationId, "HUB", null, null), accepted ? FiscalEvents.Accepted : FiscalEvents.Rejected,
                JsonSerializer.Serialize(data, Json), subjectType: d.DocumentType, subjectId: d.SourceId.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    // ---------- Anulación (fase 7b) ----------

    /// <summary>
    /// Pide anular un comprobante aceptado: facturas y sus notas por comunicación de baja, boletas y sus notas por resumen
    /// diario. Solo dentro de los <see cref="VoidWindowDays"/> días desde la emisión; después se corrige con una nota de crédito.
    /// Pedirlo otra vez devuelve la anulación en curso.
    /// </summary>
    public async Task<FiscalVoidDto> RequestVoidAsync(ClaimsPrincipal user, Guid sourceId, string reason)
    {
        var orgId = user.OrganizationId()!.Value;
        var product = MasterDataWriter.SourceOf(user);
        var document = await db.FiscalDocuments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.OrganizationId == orgId && d.Product == product && d.SourceId == sourceId)
            ?? throw ApiException.NotFound("Comprobante");
        var current = await db.FiscalVoids.AsNoTracking()
            .Where(v => v.DocumentId == document.Id && v.Status != FiscalVoidStatus.Rejected && v.Status != FiscalVoidStatus.Failed)
            .FirstOrDefaultAsync();
        if (current is not null)
            return ToDto(current, document.SourceId);
        if (document.Status is not (FiscalStatus.Accepted or FiscalStatus.Observed))
            throw ApiException.Conflict(ErrorCodes.FiscalNotVoidable, "Solo se anula un comprobante aceptado por SUNAT.");
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.ToOffset(PeruOffset).DateTime);
        if (today.DayNumber - document.IssueDate.DayNumber > VoidWindowDays)
            throw ApiException.Conflict(ErrorCodes.FiscalVoidLate,
                $"Pasaron más de {VoidWindowDays} días desde la emisión: corrígelo con una nota de crédito por anulación.");

        var kind = PeruSummaries.KindFor(document.DocumentType, document.Series);
        for (var attempt = 0; ; attempt++)
        {
            var correlative = await db.FiscalVoids.CountAsync(v => v.OrganizationId == orgId && v.Kind == kind && v.IssueDate == today) + 1;
            var entity = new FiscalVoid
            {
                OrganizationId = orgId, Product = product, DocumentId = document.Id, Kind = kind,
                SummaryId = PeruSummaries.SummaryId(kind, today, correlative), IssueDate = today, Reason = reason.Trim(),
                Status = FiscalVoidStatus.Pending, NextAttemptAt = now, CreatedAt = now, UpdatedAt = now,
            };
            db.FiscalVoids.Add(entity);
            audit.Record(AuditActions.FiscalVoidRequested, true, user.ActingUserId(), orgId, product, document.Id.ToString(),
                $"{document.DocumentType} {document.FullNumber} ({entity.SummaryId}): {entity.Reason}");
            try
            {
                await db.SaveChangesAsync();
                return ToDto(entity, document.SourceId);
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Otro pedido tomó el mismo correlativo del día: se vuelve a contar.
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<FiscalVoidDto>> GetVoidsAsync(Guid orgId, string product, Guid sourceId)
    {
        var document = await db.FiscalDocuments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.OrganizationId == orgId && d.Product == product && d.SourceId == sourceId)
            ?? throw ApiException.NotFound("Comprobante");
        var voids = await db.FiscalVoids.AsNoTracking().Where(v => v.DocumentId == document.Id).OrderBy(v => v.CreatedAt).ToListAsync();
        return voids.Select(v => ToDto(v, document.SourceId)).ToList();
    }

    /// <summary>
    /// Consulta en SUNAT el CDR de un comprobante (solo producción): si SUNAT lo tiene, el comprobante queda con ese
    /// resultado y el sistema recibe el evento. Sirve cuando un envío quedó sin respuesta.
    /// </summary>
    public async Task<FiscalDocumentDto> CheckCdrAsync(Guid orgId, string product, Guid id, Guid? actorUserId)
    {
        var document = await db.FiscalDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == orgId && d.Product == product)
                       ?? throw ApiException.NotFound("Comprobante");
        var profile = await db.FiscalProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.OrganizationId == orgId);
        var company = await db.CompanyProfiles.AsNoTracking().SingleOrDefaultAsync(c => c.Id == orgId);
        if (profile is not { Enabled: true } || company?.TaxId is null)
            throw ApiException.Conflict(ErrorCodes.FiscalNotConfigured, "La facturación electrónica no está configurada ni habilitada en el hub.");
        var result = await sunat.GetStatusCdrAsync(profile.Environment, company.TaxId + profile.SolUser, secrets.UnprotectString(profile.SolPassword!),
            company.TaxId, PeruUbl.TypeCode(document.DocumentType), document.Series, document.Number);
        if (result.Outcome != SendOutcome.Responded)
            throw ApiException.Conflict(ErrorCodes.FiscalCdrUnavailable, result.Description ?? "SUNAT no tiene la constancia de este comprobante.");
        audit.Record(AuditActions.FiscalCdrChecked, true, actorUserId, orgId, product, document.Id.ToString(), $"{document.FullNumber}: {result.Code}");
        var status = StatusOf(result, lastAttempt: true);
        await FinishAsync(id, status, d =>
        {
            d.ResponseCode = result.Code;
            d.ResponseDescription = Truncate(result.Description, 1000);
            d.Notes = result.Notes.Count > 0 ? Truncate(string.Join("\n", result.Notes), 2000) : d.Notes;
            d.CdrXml = result.CdrXml ?? d.CdrXml;
            d.LastError = null;
        }, CancellationToken.None);
        return ToDto(await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == id));
    }

    private static readonly TimeSpan PeruOffset = TimeSpan.FromHours(-5);

    /// <summary>Envía las anulaciones en cola y consulta los tickets de las enviadas.</summary>
    private async Task<int> ProcessVoidsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.FiscalVoids.AsNoTracking()
            .Where(v => (v.Status == FiscalVoidStatus.Pending || v.Status == FiscalVoidStatus.Sent) && v.NextAttemptAt <= now
                        && (v.LockedUntil == null || v.LockedUntil < now))
            .OrderBy(v => v.NextAttemptAt).Select(v => v.Id).Take(_options.BatchSize).ToListAsync(ct);
        var done = 0;
        foreach (var id in due)
        {
            var lockUntil = now.AddSeconds(_options.TimeoutSeconds * 2 + 30);
            if (await db.FiscalVoids.Where(v => v.Id == id && (v.LockedUntil == null || v.LockedUntil < now))
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.LockedUntil, lockUntil), ct) == 0)
                continue;
            try
            {
                await ProcessVoidAsync(id, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Error al procesar la anulación {Id}.", id);
                db.ChangeTracker.Clear();
                var retryAt = clock.GetUtcNow() + Backoff(1);
                await db.FiscalVoids.Where(v => v.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(v => v.NextAttemptAt, retryAt)
                    .SetProperty(v => v.LastError, Truncate(ex.Message, 500)), CancellationToken.None);
            }
            done++;
        }
        db.ChangeTracker.Clear();
        return done;
    }

    private async Task ProcessVoidAsync(Guid id, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var entry = await db.FiscalVoids.SingleAsync(v => v.Id == id, ct);
        var document = await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == entry.DocumentId, ct);
        var profile = await db.FiscalProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.OrganizationId == entry.OrganizationId, ct);
        var company = await db.CompanyProfiles.AsNoTracking().SingleOrDefaultAsync(c => c.Id == entry.OrganizationId, ct);
        var now = clock.GetUtcNow();
        var problem = profile is not { Enabled: true } ? "La facturación electrónica está deshabilitada en el hub." : MissingForEnabled(profile, company).FirstOrDefault();
        if (problem is not null || profile is null || company is null)
        {
            entry.LastError = problem;
            entry.NextAttemptAt = now + NotConfiguredDelay;
            entry.LockedUntil = null;
            await db.SaveChangesAsync(ct);
            return;
        }
        var ruc = company.TaxId!;
        var user = ruc + profile.SolUser;
        var password = secrets.UnprotectString(profile.SolPassword!);
        entry.Attempts++;
        entry.LockedUntil = null;
        entry.UpdatedAt = now;

        if (entry.Status == FiscalVoidStatus.Pending)
        {
            if (entry.SignedXml is null)
            {
                var request = JsonSerializer.Deserialize<FiscalDocumentRequest>(document.Request, Json)!;
                var item = new SummaryItem(document.DocumentType, document.Series, document.Number, document.CurrencyCode, document.Total,
                    document.CustomerDocType, document.CustomerDocNumber, request.Reference, PeruUbl.Compute(request.Lines).Totals);
                var issuer = new PeruIssuer(ruc, company.LegalName, company.TradeName, profile.Ubigeo, company.AddressLine, company.District, company.City,
                    company.Region);
                var xml = entry.Kind == PeruSummaries.VoidedKind
                    ? PeruSummaries.Voided(entry.SummaryId, document.IssueDate, entry.IssueDate, issuer, [(item, entry.Reason)])
                    : PeruSummaries.VoidSummary(entry.SummaryId, document.IssueDate, entry.IssueDate, issuer, [item]);
                try
                {
                    using var certificate = LoadCertificate(secrets.Unprotect(profile.Certificate!), secrets.UnprotectString(profile.CertificatePassword!));
                    entry.SignedXml = XmlSigner.Sign(xml, certificate, profile.SignatureAlgorithm).Xml;
                }
                catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
                {
                    await FinishVoidAsync(entry, document, FiscalVoidStatus.Failed, null, $"No se pudo firmar: {Truncate(ex.Message, 400)}", ct);
                    return;
                }
            }
            var fileName = PeruSummaries.FileName(ruc, entry.SummaryId);
            var sent = await sunat.SendSummaryAsync(profile.Environment, user, password, fileName, SunatClient.Zip(fileName + ".xml", entry.SignedXml), ct);
            switch (sent.Outcome)
            {
                case SendOutcome.Responded:
                    entry.Status = FiscalVoidStatus.Sent;
                    entry.Ticket = sent.Ticket;
                    entry.Attempts = 0;
                    entry.LastError = null;
                    entry.NextAttemptAt = now + TicketDelay;
                    await db.SaveChangesAsync(ct);
                    return;
                case SendOutcome.Transient when entry.Attempts < _options.MaxAttempts:
                    entry.LastError = Truncate($"{sent.Code} {sent.Description}".Trim(), 500);
                    entry.NextAttemptAt = now + Backoff(entry.Attempts);
                    await db.SaveChangesAsync(ct);
                    return;
                default:
                    await FinishVoidAsync(entry, document, sent.Outcome == SendOutcome.Transient ? FiscalVoidStatus.Failed : FiscalVoidStatus.Rejected,
                        sent.Code, sent.Description, ct);
                    return;
            }
        }

        var status = await sunat.GetStatusAsync(profile.Environment, user, password, entry.Ticket!, ct);
        if (status.Outcome == SendOutcome.Transient || status.InProcess)
        {
            // Sigue en proceso (98) o no hubo respuesta: se vuelve a consultar; tras muchos intentos queda para revisar.
            if (entry.Attempts >= _options.MaxAttempts * 3)
            {
                await FinishVoidAsync(entry, document, FiscalVoidStatus.Failed, status.StatusCode, status.Description ?? "SUNAT no terminó de procesar el ticket.", ct);
                return;
            }
            entry.LastError = status.InProcess ? null : Truncate(status.Description, 500);
            entry.NextAttemptAt = now + (status.InProcess ? TicketDelay * 2 : Backoff(entry.Attempts));
            await db.SaveChangesAsync(ct);
            return;
        }
        var accepted = status.Outcome == SendOutcome.Responded && status.StatusCode == "0" && status.Cdr?.Code == "0";
        entry.CdrXml = status.Cdr?.CdrXml;
        await FinishVoidAsync(entry, document, accepted ? FiscalVoidStatus.Accepted : FiscalVoidStatus.Rejected,
            status.Cdr?.Code ?? status.StatusCode, status.Cdr?.Description ?? status.Description, ct);
    }

    /// <summary>Resultado final de la anulación y el evento para el sistema; si se aceptó, el comprobante queda anulado.</summary>
    private async Task FinishVoidAsync(FiscalVoid entry, FiscalDocument document, FiscalVoidStatus status, string? code, string? description,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            entry.Status = status;
            entry.ResponseCode = code;
            entry.ResponseDescription = Truncate(description, 1000);
            entry.LastError = status == FiscalVoidStatus.Failed ? Truncate(description, 500) : null;
            entry.LockedUntil = null;
            entry.UpdatedAt = clock.GetUtcNow();
            var voided = status == FiscalVoidStatus.Accepted;
            if (voided)
                await db.FiscalDocuments.Where(d => d.Id == document.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, FiscalStatus.Voided)
                    .SetProperty(d => d.UpdatedAt, clock.GetUtcNow()), ct);
            var data = new FiscalVoidResponded(document.SourceId, document.Product, document.DocumentType, document.Series, document.Number, voided,
                entry.SummaryId, code, entry.ResponseDescription);
            await events.AppendAsync(new EventActor(entry.OrganizationId, "HUB", null, null), voided ? FiscalEvents.Voided : FiscalEvents.VoidRejected,
                JsonSerializer.Serialize(data, Json), subjectType: document.DocumentType, subjectId: document.SourceId.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        logger.LogInformation("Anulación {Summary} de {Number}: {Status} ({Code} {Description}).", entry.SummaryId, document.FullNumber, status, code, description);
    }

    public static FiscalVoidDto ToDto(FiscalVoid v, Guid sourceId) => new(v.Id, v.DocumentId, sourceId, v.Kind, v.SummaryId, v.IssueDate, v.Reason,
        v.Status, v.Ticket, v.ResponseCode, v.ResponseDescription, v.LastError, v.CreatedAt);

    /// <summary>1 min, 2, 4… hasta 2 h.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(60 * Math.Pow(2, Math.Max(attempts, 1) - 1), TimeSpan.FromHours(2).TotalSeconds));

    // ---------- Utilidades ----------

    public static X509Certificate2 LoadCertificate(byte[] pfx, string password)
    {
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
        using var key = certificate.GetRSAPrivateKey();
        if (key is null)
        {
            certificate.Dispose();
            throw new InvalidOperationException("El certificado no trae la llave privada RSA.");
        }
        return certificate;
    }

    public static FiscalDocumentDto ToDto(FiscalDocument d) => new(
        d.Id, d.SourceId, d.Product, d.DocumentType, d.Series, d.Number, d.IssueDate, d.CurrencyCode, d.Total, d.Status, d.Attempts, d.Hash,
        d.QrText, d.ResponseCode, d.ResponseDescription, d.Notes, d.LastError, d.CreatedAt, d.SentAt);

    private static FiscalProfileDto ToDto(FiscalProfile p, CompanyProfile? company) => new(
        p.Enabled, p.Environment, p.Ubigeo, p.SolUser, p.SolPassword is not null, p.Certificate is not null, p.CertificateSubject,
        p.CertificateExpiresAt, p.SignatureAlgorithm, company?.CountryCode, company?.TaxId);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max) => value is { } v && v.Length > max ? v[..max] : value;
}

/// <summary>Ejecuta <see cref="FiscalService.ProcessDueAsync"/> periódicamente.</summary>
public sealed class FiscalProcessor(IServiceScopeFactory scopes, IOptions<FiscalOptions> options, ILogger<FiscalProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ProcessorEnabled)
            return;
        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var attempted = 0;
            try
            {
                using var scope = scopes.CreateScope();
                attempted = await scope.ServiceProvider.GetRequiredService<FiscalService>().ProcessDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Error en el procesador de comprobantes electrónicos.");
            }
            if (attempted < options.Value.BatchSize)
                await Task.Delay(interval, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
