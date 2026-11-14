using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using VortexApi.Client;
using VortexApi.Fiscal;
using VortexApi.Fiscal.Peru;

namespace VortexApi.Tests;

/// <summary>Facturación electrónica en el hub: el ERP pide la factura, el hub la firma, la envía a SUNAT y avisa el resultado.</summary>
public sealed class FiscalTests : IAsyncLifetime
{
    private const string Password = "User-Pass-2026!";
    private const string PfxPassword = "clave-pfx";

    private readonly VortexApiFactory _factory = new();
    private readonly X509Certificate2 _certificate = FiscalPeruTests.SelfSigned();
    private HttpClient _platform = null!;
    private HttpClient _orgAdmin = null!;
    private string _erp = null!, _pos = null!, _wms = null!;

    public async Task InitializeAsync()
    {
        _platform = await _factory.AdminClientAsync();
        var orgId = (await _platform.PostOkAsync("api/admin/organizations", new { name = "Café Andino" })).GetProperty("id").GetGuid();
        foreach (var product in new[] { "ERP", "POS" })
            await _platform.PutOkAsync($"api/admin/organizations/{orgId}/products/{product}",
                new { autoAssignUsers = true, features = new[] { FiscalEvents.Feature } });
        await _platform.PutOkAsync($"api/admin/organizations/{orgId}/products/WMS", new { autoAssignUsers = true });
        await _platform.PostOkAsync("api/admin/users", new
        {
            organizationId = orgId, email = "admin@andino.test", fullName = "Admin", password = Password, role = "OrgAdmin",
        });
        await _platform.PostOkAsync("api/admin/users", new { organizationId = orgId, email = "luis@andino.test", fullName = "Luis", password = Password });
        _orgAdmin = await _factory.AdminClientAsync("admin@andino.test", Password);
        await _orgAdmin.PutOkAsync("api/master/company", new
        {
            legalName = "EMPRESA DE PRUEBA SAC", tradeName = "Café Andino", taxId = FiscalPeruTests.IssuerRuc, countryCode = "PE",
            currencyCode = "PEN", timeZone = "America/Lima", defaultTaxRate = 18,
            address = new { line = "Av. Lima 123", district = "LIMA", city = "LIMA", region = "LIMA" },
        });
        _erp = (await Sdk("ERP").LoginAsync("luis@andino.test", Password, "device-erp-andino-0000")).AccessToken;
        _pos = (await Sdk("POS").LoginAsync("luis@andino.test", Password, "device-pos-andino-0000")).AccessToken;
        _wms = (await Sdk("WMS").LoginAsync("luis@andino.test", Password, "device-wms-andino-0000")).AccessToken;
    }

    public Task DisposeAsync()
    {
        _certificate.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private VortexApiClient Sdk(string product) => new(_factory.CreateClient(),
        new VortexApiClientOptions { ProductCode = product, ProductKey = VortexApiFactory.ProductKeys[product] });

    private HttpClient Product(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<System.Text.Json.JsonElement> ConfigureAsync(bool enabled = true) => _orgAdmin.PutOkAsync("api/fiscal/profile", new
    {
        enabled, environment = "Test", ubigeo = "150101", solUser = "moddatos", solPassword = "moddatos",
        certificateBase64 = Convert.ToBase64String(_certificate.Export(X509ContentType.Pkcs12, PfxPassword)), certificatePassword = PfxPassword,
    });

    private static VortexFiscalRequest Invoice(long number = 1, Guid? sourceId = null) => new(
        sourceId ?? Guid.NewGuid(), VortexFiscalTypes.Invoice, "F001", number, new DateOnly(2026, 11, 11), "PEN",
        new VortexFiscalParty("6", FiscalPeruTests.CustomerRuc, "Bodega El Sur SAC"),
        [new VortexFiscalLine("Café molido 250 g", 10, "NIU", 18.5m, VortexFiscalTaxCategories.Taxed, 0.18m)]);

    [Fact]
    public async Task La_factura_del_ERP_se_firma_se_envia_a_SUNAT_y_el_ERP_recibe_el_evento()
    {
        var profile = await ConfigureAsync();
        Assert.True(profile.GetProperty("enabled").GetBoolean());
        Assert.True(profile.GetProperty("hasCertificate").GetBoolean());
        Assert.False(profile.TryGetProperty("solPassword", out _));
        Assert.Contains("EMPRESA DE PRUEBA SAC", profile.GetProperty("certificateSubject").GetString());

        var sdk = Sdk("ERP");
        var request = Invoice();
        var queued = await sdk.IssueFiscalDocumentAsync(_erp, request);
        Assert.Equal((VortexFiscalStatus.Pending, 218.30m, "ERP"), (queued.Status, queued.Total, queued.Product));

        // El ERP reintenta desde su cola: mismo comprobante, sin duplicar.
        var again = await sdk.IssueFiscalDocumentAsync(_erp, request);
        Assert.Equal(queued.Id, again.Id);

        Assert.Equal(1, await _factory.ProcessFiscalAsync());
        var sent = Assert.Single(_factory.Sunat.Requests);
        Assert.Equal((FiscalEnvironment.Test, FiscalPeruTests.IssuerRuc + "MODDATOS", "moddatos", $"{FiscalPeruTests.IssuerRuc}-01-F001-1"),
            (sent.Environment, sent.Username, sent.Password, sent.FileName));
        Assert.True(XmlSigner.Verify(sent.Xml, _certificate));

        var done = (await sdk.GetFiscalDocumentAsync(_erp, queued.Id))!;
        Assert.Equal(VortexFiscalStatus.Accepted, done.Status);
        Assert.True(done.IsValid);
        Assert.NotNull(done.Hash);
        Assert.Equal($"{FiscalPeruTests.IssuerRuc}|01|F001|1|33.30|218.30|2026-11-11|6|{FiscalPeruTests.CustomerRuc}|{done.Hash}|", done.QrText);

        var e = Assert.Single((await sdk.GetEventsAsync(_erp, 0, [VortexHubEvents.FiscalResponses])).Events);
        Assert.Equal((VortexHubEvents.FiscalAccepted, "HUB", "invoice", request.SourceId.ToString()), (e.Type, e.Source, e.SubjectType, e.SubjectId));
        var data = e.DataAs<VortexFiscalResponded>();
        Assert.Equal((request.SourceId, VortexFiscalStatus.Accepted, done.Hash), (data.SourceId, data.Status, data.Hash));

        var erp = Product(_erp);
        Assert.Equal(sent.Xml, await erp.GetStringAsync($"api/fiscal/documents/{queued.Id}/xml"));
        Assert.Contains("CDR", await erp.GetStringAsync($"api/fiscal/documents/{queued.Id}/cdr"));
        Assert.Single(await sdk.GetFiscalDocumentsAsync(_erp, sourceId: request.SourceId));

        // Cada sistema ve solo los suyos.
        Assert.Null(await Sdk("POS").GetFiscalDocumentAsync(_pos, queued.Id));
    }

    [Fact]
    public async Task Sin_el_modulo_sin_perfil_o_con_datos_invalidos_no_se_encola()
    {
        var notEnabled = await Product(_wms).PostAsJsonAsync("api/fiscal/documents", Invoice(), VortexApiFactory.Json);
        Assert.Equal(HttpStatusCode.Forbidden, notEnabled.StatusCode);
        Assert.Equal("fiscal_not_enabled", await notEnabled.ErrorCodeAsync());

        var notConfigured = await Product(_erp).PostAsJsonAsync("api/fiscal/documents", Invoice(), VortexApiFactory.Json);
        Assert.Equal(HttpStatusCode.Conflict, notConfigured.StatusCode);
        Assert.Equal("fiscal_not_configured", await notConfigured.ErrorCodeAsync());

        // Habilitar sin certificado ni clave SOL no se puede.
        var incomplete = await _orgAdmin.PutAsJsonAsync("api/fiscal/profile", new { enabled = true, environment = "Test" });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        var badPassword = await _orgAdmin.PutAsJsonAsync("api/fiscal/profile", new
        {
            enabled = false, environment = "Test",
            certificateBase64 = Convert.ToBase64String(_certificate.Export(X509ContentType.Pkcs12, PfxPassword)), certificatePassword = "otra",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badPassword.StatusCode);
        // Solo el OrgAdmin lo configura.
        Assert.Equal(HttpStatusCode.Forbidden, (await Product(_erp).PutAsJsonAsync("api/fiscal/profile", new { enabled = false })).StatusCode);

        await ConfigureAsync();
        var toDni = Invoice() with { Customer = new VortexFiscalParty("1", "12345678", "Ana") };
        var invalid = await Product(_erp).PostAsJsonAsync("api/fiscal/documents", toDni, VortexApiFactory.Json);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var sdk = Sdk("ERP");
        await sdk.IssueFiscalDocumentAsync(_erp, Invoice(5));
        var sameNumber = await Product(_pos).PostAsJsonAsync("api/fiscal/documents", Invoice(5), VortexApiFactory.Json);
        Assert.Equal(HttpStatusCode.Conflict, sameNumber.StatusCode);
        Assert.Equal("fiscal_number_in_use", await sameNumber.ErrorCodeAsync());
    }

    [Fact]
    public async Task Sin_conexion_con_SUNAT_se_reintenta_con_el_mismo_XML_y_un_error_definitivo_se_puede_reintentar()
    {
        await ConfigureAsync();
        var sdk = Sdk("POS");
        var receipt = new VortexFiscalRequest(Guid.NewGuid(), VortexFiscalTypes.Receipt, "B001", 1, new DateOnly(2026, 11, 11), "PEN",
            new VortexFiscalParty("-", null, "Clientes varios"),
            [new VortexFiscalLine("Café pasado", 2, "NIU", 5m, VortexFiscalTaxCategories.Taxed, 0.18m)]);
        var queued = await sdk.IssueFiscalDocumentAsync(_pos, receipt);

        _factory.Sunat.Responses.Enqueue(SendResult.Transient("Sin conexión"));
        await _factory.ProcessFiscalAsync();
        var waiting = (await sdk.GetFiscalDocumentAsync(_pos, queued.Id))!;
        Assert.Equal((VortexFiscalStatus.Pending, 1, "Sin conexión"), (waiting.Status, waiting.Attempts, waiting.LastError));
        Assert.Equal(0, await _factory.ProcessFiscalAsync()); // todavía no toca

        _factory.Clock.Advance(TimeSpan.FromMinutes(2));
        _factory.Sunat.Responses.Enqueue(new SendResult(SendOutcome.Error, "0102", "Usuario o contraseña incorrectos", [], null));
        await _factory.ProcessFiscalAsync();
        var failed = (await sdk.GetFiscalDocumentAsync(_pos, queued.Id))!;
        Assert.Equal(VortexFiscalStatus.Failed, failed.Status);
        Assert.Equal(_factory.Sunat.Requests[0].Xml, _factory.Sunat.Requests[1].Xml);
        var rejectedEvent = Assert.Single((await sdk.GetEventsAsync(_pos, 0, [VortexHubEvents.FiscalRejected])).Events);
        Assert.Equal(VortexFiscalStatus.Failed, rejectedEvent.DataAs<VortexFiscalResponded>().Status);

        // Tras corregir la clave, el POS lo reintenta y queda aceptado.
        Assert.Equal(VortexFiscalStatus.Pending, (await sdk.RetryFiscalDocumentAsync(_pos, queued.Id)).Status);
        await _factory.ProcessFiscalAsync();
        Assert.Equal(VortexFiscalStatus.Accepted, (await sdk.GetFiscalDocumentAsync(_pos, queued.Id))!.Status);
        Assert.Equal(3, _factory.Sunat.Requests.Count);

        var again = await Product(_pos).PostAsync($"api/fiscal/documents/{queued.Id}/retry", null);
        Assert.Equal("fiscal_not_retryable", await again.ErrorCodeAsync());
    }

    [Fact]
    public async Task Un_rechazo_de_SUNAT_llega_como_evento_y_las_observaciones_dejan_el_comprobante_valido()
    {
        await ConfigureAsync();
        var sdk = Sdk("ERP");
        var rejected = await sdk.IssueFiscalDocumentAsync(_erp, Invoice(10));
        var observed = await sdk.IssueFiscalDocumentAsync(_erp, Invoice(11));
        _factory.Sunat.Responses.Enqueue(new SendResult(SendOutcome.Rejected, "2800", "El dato ingresado en el tipo de documento no corresponde", [], null));
        _factory.Sunat.Responses.Enqueue(FakeSunat.Accepted("4252 - El dato ingresado como atributo @listName es incorrecto."));
        Assert.Equal(2, await _factory.ProcessFiscalAsync());

        var r = (await sdk.GetFiscalDocumentAsync(_erp, rejected.Id))!;
        Assert.Equal((VortexFiscalStatus.Rejected, "2800"), (r.Status, r.ResponseCode));
        var o = (await sdk.GetFiscalDocumentAsync(_erp, observed.Id))!;
        Assert.Equal(VortexFiscalStatus.Observed, o.Status);
        Assert.True(o.IsValid);
        Assert.Contains("4252", o.Notes);

        var events = (await sdk.GetEventsAsync(_erp, 0, [VortexHubEvents.FiscalResponses])).Events;
        Assert.Equal([VortexHubEvents.FiscalRejected, VortexHubEvents.FiscalAccepted], events.Select(e => e.Type));
        Assert.Equal(VortexFiscalStatus.Rejected, (await sdk.GetFiscalDocumentsAsync(_erp, status: VortexFiscalStatus.Rejected)).Single().Status);

        // Deshabilitado, lo pendiente espera sin gastar intentos.
        var pending = await sdk.IssueFiscalDocumentAsync(_erp, Invoice(12));
        await _orgAdmin.PutOkAsync("api/fiscal/profile", new { enabled = false, environment = "Test" });
        await _factory.ProcessFiscalAsync();
        var waiting = (await sdk.GetFiscalDocumentAsync(_erp, pending.Id))!;
        Assert.Equal((VortexFiscalStatus.Pending, 0), (waiting.Status, waiting.Attempts));
        Assert.Contains("deshabilitada", waiting.LastError);
    }

    // ---------- Fase 7b: anulación y consulta del CDR ----------

    private DateOnly Today => DateOnly.FromDateTime(_factory.Clock.GetUtcNow().ToOffset(TimeSpan.FromHours(-5)).DateTime);

    private async Task<VortexFiscalRequest> AcceptedAsync(VortexFiscalRequest request)
    {
        await Sdk("ERP").IssueFiscalDocumentAsync(_erp, request);
        await _factory.ProcessFiscalAsync();
        return request;
    }

    [Fact]
    public async Task La_factura_se_anula_por_comunicacion_de_baja_con_ticket_y_el_ERP_recibe_voided()
    {
        await ConfigureAsync();
        var sdk = Sdk("ERP");
        var invoice = await AcceptedAsync(Invoice(7) with { IssueDate = Today });

        var pending = await sdk.VoidFiscalDocumentAsync(_erp, invoice.SourceId, "Error en el precio");
        Assert.Equal(("RA", $"RA-{Today:yyyyMMdd}-1", VortexFiscalVoidStatus.Pending), (pending.Kind, pending.SummaryId, pending.Status));
        Assert.Equal(pending.Id, (await sdk.VoidFiscalDocumentAsync(_erp, invoice.SourceId, "otra vez")).Id);   // no duplica

        // Primera pasada: firma y sendSummary (devuelve el ticket); SUNAT aún procesa (98).
        _factory.Sunat.Tickets.Enqueue(new TicketStatus(SendOutcome.Responded, "98", null, null));
        await _factory.ProcessFiscalAsync();
        var summary = _factory.Sunat.Requests.Last();
        Assert.Equal($"{FiscalPeruTests.IssuerRuc}-RA-{Today:yyyyMMdd}-1", summary.FileName);
        Assert.True(XmlSigner.Verify(summary.Xml, _certificate));
        Assert.Contains("<sac:DocumentSerialID>F001</sac:DocumentSerialID>", summary.Xml);
        Assert.Contains("Error en el precio", summary.Xml);
        Assert.Equal(VortexFiscalVoidStatus.Sent, Assert.Single(await sdk.GetFiscalVoidsAsync(_erp, invoice.SourceId)).Status);

        _factory.Clock.Advance(TimeSpan.FromSeconds(31));
        await _factory.ProcessFiscalAsync();   // 98: sigue en proceso
        _factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await _factory.ProcessFiscalAsync();   // 0: aceptada
        Assert.Equal(2, _factory.Sunat.StatusQueries.Count);
        Assert.Equal(VortexFiscalVoidStatus.Accepted, Assert.Single(await sdk.GetFiscalVoidsAsync(_erp, invoice.SourceId)).Status);
        Assert.Equal(VortexFiscalStatus.Voided, (await sdk.GetFiscalDocumentsAsync(_erp, sourceId: invoice.SourceId)).Single().Status);

        var e = (await sdk.GetEventsAsync(_erp, 0, [VortexHubEvents.FiscalResponses])).Events.Last();
        Assert.Equal(VortexHubEvents.FiscalVoided, e.Type);
        var data = e.DataAs<VortexFiscalVoidResponded>();
        Assert.Equal((invoice.SourceId, true, $"RA-{Today:yyyyMMdd}-1"), (data.SourceId, data.Voided, data.SummaryId));

        // Pedirla otra vez devuelve la ya aceptada; un comprobante que no existe da 404.
        Assert.Equal(VortexFiscalVoidStatus.Accepted, (await sdk.VoidFiscalDocumentAsync(_erp, invoice.SourceId, "Otra vez")).Status);
        var unknown = await Product(_erp).PostAsJsonAsync($"api/fiscal/documents/by-source/{Guid.NewGuid()}/void", new { reason = "Otra vez" },
            VortexApiFactory.Json);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task La_boleta_va_por_resumen_diario_fuera_de_plazo_no_y_un_rechazo_avisa()
    {
        await ConfigureAsync();
        var sdk = Sdk("ERP");
        var receipt = await AcceptedAsync(new VortexFiscalRequest(Guid.NewGuid(), VortexFiscalTypes.Receipt, "B001", 3, Today, "PEN",
            new VortexFiscalParty("1", "45678912", "Ana Torres"), [new VortexFiscalLine("Café", 2, "NIU", 0, VortexFiscalTaxCategories.Taxed, 0.18m, Total: 37m)]));
        var rc = await sdk.VoidFiscalDocumentAsync(_erp, receipt.SourceId, "Venta anulada");
        Assert.Equal("RC", rc.Kind);
        _factory.Sunat.Tickets.Enqueue(new TicketStatus(SendOutcome.Responded, "99",
            new SendResult(SendOutcome.Responded, "2105", "El documento ya fue informado", [], "<cdr/>"), null));
        await _factory.ProcessFiscalAsync();
        var xml = _factory.Sunat.Requests.Last().Xml;
        Assert.Contains("<cbc:ConditionCode>3</cbc:ConditionCode>", xml);
        Assert.Contains("<cbc:ID>B001-3</cbc:ID>", xml);
        Assert.Contains("<sac:TotalAmount currencyID=\"PEN\">37.00</sac:TotalAmount>", xml);
        _factory.Clock.Advance(TimeSpan.FromSeconds(31));
        await _factory.ProcessFiscalAsync();
        var rejected = Assert.Single(await sdk.GetFiscalVoidsAsync(_erp, receipt.SourceId));
        Assert.Equal((VortexFiscalVoidStatus.Rejected, "2105"), (rejected.Status, rejected.ResponseCode));
        Assert.Equal(VortexFiscalStatus.Accepted, (await sdk.GetFiscalDocumentsAsync(_erp, sourceId: receipt.SourceId)).Single().Status);
        Assert.Equal(VortexHubEvents.FiscalVoidRejected, (await sdk.GetEventsAsync(_erp, 0, [VortexHubEvents.FiscalResponses])).Events.Last().Type);

        // Fuera de plazo (más de 7 días): se corrige con nota de crédito.
        var old = await AcceptedAsync(Invoice(8) with { IssueDate = Today.AddDays(-8) });
        var late = await Product(_erp).PostAsJsonAsync($"api/fiscal/documents/by-source/{old.SourceId}/void", new { reason = "Tarde" }, VortexApiFactory.Json);
        Assert.Equal((HttpStatusCode.Conflict, "fiscal_void_late"), (late.StatusCode, await late.ErrorCodeAsync()));
    }

    [Fact]
    public async Task Un_reenvio_que_SUNAT_ya_tenia_queda_aceptado_y_la_consulta_del_CDR_es_solo_de_produccion()
    {
        await ConfigureAsync();
        var sdk = Sdk("ERP");
        _factory.Sunat.Responses.Enqueue(new SendResult(SendOutcome.Error, "1033", "El comprobante fue registrado previamente con otros datos", [], null));
        var request = await AcceptedAsync(Invoice(9));
        var doc = (await sdk.GetFiscalDocumentsAsync(_erp, sourceId: request.SourceId)).Single();
        Assert.Equal(VortexFiscalStatus.Observed, doc.Status);   // en beta no hay consulta: aceptado con la observación
        Assert.Empty(_factory.Sunat.ConsultedCdrs);

        var check = await Product(_erp).PostAsync($"api/fiscal/documents/{doc.Id}/check", null);
        Assert.Equal((HttpStatusCode.Conflict, "fiscal_cdr_unavailable"), (check.StatusCode, await check.ErrorCodeAsync()));

        // En producción, el 1033 trae el CDR real del comprobante con getStatusCdr.
        await _orgAdmin.PutOkAsync("api/fiscal/profile", new
        {
            enabled = true, environment = "Production", ubigeo = "150101", solUser = "moddatos", solPassword = "moddatos",
            certificateBase64 = Convert.ToBase64String(_certificate.Export(X509ContentType.Pkcs12, PfxPassword)), certificatePassword = PfxPassword,
        });
        _factory.Sunat.Responses.Enqueue(new SendResult(SendOutcome.Error, "1033", "El comprobante fue registrado previamente con otros datos", [], null));
        var production = await AcceptedAsync(Invoice(10));
        Assert.Equal((FiscalPeruTests.IssuerRuc, "01", "F001", 10L), Assert.Single(_factory.Sunat.ConsultedCdrs));
        Assert.Equal(VortexFiscalStatus.Accepted, (await sdk.GetFiscalDocumentsAsync(_erp, sourceId: production.SourceId)).Single().Status);
        var checkedDoc = await sdk.CheckFiscalDocumentAsync(_erp, doc.Id);
        Assert.Equal(VortexFiscalStatus.Accepted, checkedDoc.Status);
    }
}
