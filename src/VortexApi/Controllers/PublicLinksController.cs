using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers;

/// <summary>
/// Enlaces públicos firmados de los documentos de un sistema (p. ej. la cotización del CRM para que el cliente la acepte
/// con su firma). El sistema crea el enlace con una copia del documento, lo comparte con el cliente y recibe la respuesta
/// como evento <c>hub.public_link.accepted</c> o <c>hub.public_link.rejected</c>.
/// </summary>
[ApiController]
[Route("api/public-links")]
[Authorize(Policy = Policies.ProductClient)]
public sealed class PublicLinksController(VortexDbContext db, PublicLinkTokens tokens, IConfiguration config, TimeProvider clock)
    : ControllerBase
{
    public const int MaxDocumentBytes = 64 * 1024;
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(366);

    /// <summary>
    /// Crea el enlace del documento o, si ya existe y nadie respondió, actualiza la copia, el idioma y el vencimiento (la URL
    /// no cambia; un enlace retirado vuelve a abrirse). Si el cliente ya respondió, lo devuelve tal como quedó.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<PublicLinkDto>> CreateOrUpdate(PublicLinkRequest request)
    {
        var (orgId, product) = Caller();
        var now = clock.GetUtcNow();
        var type = request.DocumentType.Trim().ToLowerInvariant();
        if (!EventTypePattern.IsValidType("x." + type))
            throw ApiException.BadRequest(ErrorCodes.Validation, "documentType inválido (minúsculas, números y _).");
        if (request.ExpiresAt <= now || request.ExpiresAt > now + MaxLifetime)
            throw ApiException.BadRequest(ErrorCodes.Validation, "expiresAt debe ser futuro y a menos de un año.");
        var language = UserLanguages.Parse(request.Language) ?? "es";
        var document = JsonSerializer.Serialize(request.Document, PublicLinkJson.Options);
        if (System.Text.Encoding.UTF8.GetByteCount(document) > MaxDocumentBytes)
            throw ApiException.BadRequest(ErrorCodes.Validation, $"El documento supera {MaxDocumentBytes / 1024} KB.");

        var link = await db.PublicLinks.SingleOrDefaultAsync(l =>
            l.OrganizationId == orgId && l.Product == product && l.DocumentType == type && l.DocumentId == request.DocumentId);
        if (link is { Status: PublicLinkStatus.Accepted or PublicLinkStatus.Rejected })
            return Ok(ToDto(link));

        var created = link is null;
        link ??= db.PublicLinks.Add(new PublicLink
        {
            OrganizationId = orgId, Product = product, DocumentType = type, DocumentId = request.DocumentId,
            DocumentNumber = "", Document = "", CreatedAt = now, CreatedByUserId = User.ActingUserId(),
        }).Entity;
        link.DocumentNumber = request.Document.Number.Trim();
        link.Document = document;
        link.Language = language;
        link.ExpiresAt = request.ExpiresAt;
        link.Status = PublicLinkStatus.Open;
        link.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (created)
        {
            // Dos equipos lo crearon a la vez: gana el primero y este devuelve el suyo.
            db.ChangeTracker.Clear();
            link = await db.PublicLinks.SingleAsync(l =>
                l.OrganizationId == orgId && l.Product == product && l.DocumentType == type && l.DocumentId == request.DocumentId);
            return Ok(ToDto(link));
        }
        return created ? CreatedAtAction(nameof(Get), new { id = link.Id }, ToDto(link)) : Ok(ToDto(link));
    }

    /// <summary>El enlace con su estado: cuántas veces se abrió y, si respondió, quién, cuándo y su firma.</summary>
    [HttpGet("{id:guid}")]
    public async Task<PublicLinkDto> Get(Guid id) => ToDto(await FindAsync(id), withSignature: true);

    /// <summary>Enlaces del sistema, filtrados por documento.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<PublicLinkDto>> List(string? documentType = null, Guid? documentId = null)
    {
        var (orgId, product) = Caller();
        var query = db.PublicLinks.AsNoTracking().Where(l => l.OrganizationId == orgId && l.Product == product);
        if (!string.IsNullOrWhiteSpace(documentType))
        {
            var type = documentType.Trim().ToLowerInvariant();
            query = query.Where(l => l.DocumentType == type);
        }
        if (documentId is { } doc)
            query = query.Where(l => l.DocumentId == doc);
        var links = await query.OrderByDescending(l => l.UpdatedAt).Take(200).ToListAsync();
        return links.Select(l => ToDto(l)).ToList();
    }

    /// <summary>Retira el enlace (deja de aceptar respuestas). Si el cliente ya respondió, no se puede.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var link = await FindAsync(id);
        if (link.Status is PublicLinkStatus.Accepted or PublicLinkStatus.Rejected)
            throw ApiException.Conflict(ErrorCodes.LinkAlreadyResponded, "El cliente ya respondió este enlace.");
        link.Status = PublicLinkStatus.Revoked;
        link.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        return NoContent();
    }

    private (Guid OrganizationId, string Product) Caller() =>
        (User.OrganizationId()!.Value, MasterDataWriter.SourceOf(User));

    private async Task<PublicLink> FindAsync(Guid id)
    {
        var (orgId, product) = Caller();
        return await db.PublicLinks.SingleOrDefaultAsync(l => l.Id == id && l.OrganizationId == orgId && l.Product == product)
               ?? throw ApiException.NotFound("Enlace");
    }

    private PublicLinkDto ToDto(PublicLink l, bool withSignature = false) => new(
        l.Id, PublicLinkJson.Url(Request, config, tokens.Create(l.Id)), l.DocumentType, l.DocumentId, l.DocumentNumber, l.Language,
        l.Status, l.ExpiresAt, l.CreatedAt, l.UpdatedAt, l.ViewCount, l.FirstViewedAt, l.LastViewedAt, l.RespondedAt, l.SignerName,
        l.SignerEmail, l.Comment, withSignature ? l.Signature : null);
}

public static class PublicLinkJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>URL de la página pública: <c>PublicLinks:BaseUrl</c> (la dirección pública del hub) o la del pedido.</summary>
    public static string Url(HttpRequest request, IConfiguration config, string token)
    {
        var baseUrl = config["PublicLinks:BaseUrl"] is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : $"{request.Scheme}://{request.Host}{request.PathBase}";
        return $"{baseUrl}/p/{token}";
    }
}
