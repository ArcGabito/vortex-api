using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using VortexApi.Client;
using VortexApi.Services;

namespace VortexApi.Tests;

/// <summary>Enlace público firmado: el CRM comparte una cotización y el cliente la acepta con su firma o la rechaza.</summary>
public sealed class PublicLinksTests : IAsyncLifetime
{
    private const string Password = "User-Pass-2026!";
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly VortexApiFactory _factory = new();
    private HttpClient _platform = null!;
    private VortexApiClient _sdk = null!;
    private string _crm = null!, _erp = null!;

    public async Task InitializeAsync()
    {
        _platform = await _factory.AdminClientAsync();
        (_crm, _erp) = await CreateOrganizationAsync("Acme SA", "acme");
        _sdk = Sdk("CRM");
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static VortexPublicLinkRequest Quote(Guid id, DateTimeOffset expires, string number = "COT-0001-000001", string? notes = "Pago a 30 días") =>
        new("quote", id, expires, new VortexPublicDocument(
            "Cotización", number, new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 24), "PEN",
            new VortexPublicDocumentParty("Café Andino SAC", "20123456789", "Av. Lima 123"),
            new VortexPublicDocumentParty("Bodega <Sur>", "20111222333"),
            [new("Café 250 g", 10, "UN", 18.5m, 185m), new("Instalación", 1, "SERV", 50, 50, Optional: true)],
            [new("Total", 185m, Grand: true)], notes, SellerName: "Luis"), "es");

    private HttpClient Anonymous() => _factory.CreateClient();

    private static string TokenOf(VortexPublicLink link) => link.Url[(link.Url.LastIndexOf('/') + 1)..];

    [Fact]
    public async Task El_cliente_ve_la_cotizacion_la_acepta_con_su_firma_y_el_CRM_recibe_el_evento()
    {
        var quoteId = Guid.NewGuid();
        var link = await _sdk.CreatePublicLinkAsync(_crm, Quote(quoteId, _factory.Clock.GetUtcNow().AddDays(15)));
        Assert.Equal(VortexPublicLinkStatus.Open, link.Status);
        Assert.Matches("/p/[A-Za-z0-9_-]{43}$", link.Url);

        // Volver a pedirlo (otro equipo, o tras editar) da la misma URL y actualiza la copia.
        var again = await _sdk.CreatePublicLinkAsync(_crm, Quote(quoteId, _factory.Clock.GetUtcNow().AddDays(20), notes: "Pago contado"));
        Assert.Equal((link.Id, link.Url), (again.Id, again.Url));

        var page = await Anonymous().GetAsync($"p/{TokenOf(link)}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Bodega &lt;Sur&gt;", html);   // el contenido se escapa
        Assert.Contains("Pago contado", html);
        Assert.Contains("Aceptar y firmar", html);

        var accept = await Anonymous().PostAsJsonAsync($"api/public/links/{TokenOf(link)}/accept",
            new { signerName = "Rosa Quispe", signerEmail = "rosa@bodega.pe", signature = Png, comment = "Conforme" });
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal("Accepted", (await accept.ReadJsonAsync()).GetProperty("status").GetString());

        // Una segunda respuesta no cambia nada.
        var twice = await Anonymous().PostAsJsonAsync($"api/public/links/{TokenOf(link)}/reject", new { });
        Assert.Equal("link_already_responded", await twice.ErrorCodeAsync());

        var events = await _sdk.GetEventsAsync(_crm, 0, [VortexHubEvents.PublicLinkResponses], excludeSource: "CRM");
        var e = Assert.Single(events.Events);
        Assert.Equal(("HUB", VortexHubEvents.PublicLinkAccepted, quoteId.ToString()), (e.Source, e.Type, e.SubjectId));
        var data = e.DataAs<VortexPublicLinkResponded>();
        Assert.True(data.Accepted);
        Assert.Equal(("CRM", "quote", quoteId, "Rosa Quispe", "Conforme"), (data.Product, data.DocumentType, data.DocumentId, data.SignerName, data.Comment));

        var detail = (await _sdk.GetPublicLinkAsync(_crm, link.Id))!;
        Assert.Equal((VortexPublicLinkStatus.Accepted, Png, 1), (detail.Status, detail.Signature, detail.ViewCount));

        // Ya respondido: pedirlo de nuevo lo devuelve tal cual y la página muestra quién aceptó.
        Assert.Equal(VortexPublicLinkStatus.Accepted, (await _sdk.CreatePublicLinkAsync(_crm, Quote(quoteId, _factory.Clock.GetUtcNow().AddDays(5)))).Status);
        var after = await (await Anonymous().GetAsync($"p/{TokenOf(link)}")).Content.ReadAsStringAsync();
        Assert.Contains("Aceptado por Rosa Quispe", after);
        Assert.DoesNotContain("id=\"accept\"", after);
    }

    [Fact]
    public async Task El_rechazo_llega_como_evento_y_un_enlace_retirado_o_vencido_no_acepta_respuestas()
    {
        var rejected = await _sdk.CreatePublicLinkAsync(_crm, Quote(Guid.NewGuid(), _factory.Clock.GetUtcNow().AddDays(15)));
        (await Anonymous().PostAsJsonAsync($"api/public/links/{TokenOf(rejected)}/reject", new { comment = "Muy caro" })).EnsureSuccessStatusCode();
        var e = Assert.Single((await _sdk.GetEventsAsync(_crm, 0, [VortexHubEvents.PublicLinkResponses])).Events);
        Assert.Equal(VortexHubEvents.PublicLinkRejected, e.Type);
        Assert.Equal("Muy caro", e.DataAs<VortexPublicLinkResponded>().Comment);

        var revoked = await _sdk.CreatePublicLinkAsync(_crm, Quote(Guid.NewGuid(), _factory.Clock.GetUtcNow().AddDays(15)));
        await _sdk.RevokePublicLinkAsync(_crm, revoked.Id);
        var gone = await Anonymous().PostAsJsonAsync($"api/public/links/{TokenOf(revoked)}/accept", new { signerName = "Rosa", signature = Png });
        Assert.Equal((HttpStatusCode.Gone, "link_revoked"), (gone.StatusCode, await gone.ErrorCodeAsync()));
        Assert.Contains("retirado", await (await Anonymous().GetAsync($"p/{TokenOf(revoked)}")).Content.ReadAsStringAsync());

        var expiring = await _sdk.CreatePublicLinkAsync(_crm, Quote(Guid.NewGuid(), _factory.Clock.GetUtcNow().AddDays(1)));
        _factory.Clock.Advance(TimeSpan.FromDays(2));
        var expired = await Anonymous().PostAsJsonAsync($"api/public/links/{TokenOf(expiring)}/accept", new { signerName = "Rosa", signature = Png });
        Assert.Equal("link_expired", await expired.ErrorCodeAsync());
        var view = await (await Anonymous().GetAsync($"api/public/links/{TokenOf(expiring)}")).ReadJsonAsync();
        Assert.True(view.GetProperty("expired").GetBoolean());

        // Un enlace respondido no se puede retirar.
        var conflict = await Assert.ThrowsAsync<VortexApiException>(() => _sdk.RevokePublicLinkAsync(_crm, rejected.Id));
        Assert.Equal("link_already_responded", conflict.Code);
    }

    [Fact]
    public async Task Un_token_alterado_o_de_otro_hub_no_abre_nada_y_cada_sistema_solo_ve_sus_enlaces()
    {
        var link = await _sdk.CreatePublicLinkAsync(_crm, Quote(Guid.NewGuid(), _factory.Clock.GetUtcNow().AddDays(15)));
        var token = TokenOf(link);
        var tampered = (token[0] == 'A' ? 'B' : 'A') + token[1..];
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync($"p/{tampered}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync("p/no-es-un-token")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().GetAsync($"api/public/links/{Guid.NewGuid():N}")).StatusCode);

        // La firma debe ser un PNG.
        var bad = await Anonymous().PostAsJsonAsync($"api/public/links/{token}/accept",
            new { signerName = "Rosa", signature = "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // El ERP de la misma organización y el CRM de otra no lo ven.
        Assert.Null(await Sdk("ERP").GetPublicLinkAsync(_erp, link.Id));
        var (otherCrm, _) = await CreateOrganizationAsync("Globex", "globex");
        Assert.Null(await _sdk.GetPublicLinkAsync(otherCrm, link.Id));
        Assert.Empty(await _sdk.GetPublicLinksAsync(otherCrm, "quote"));
        Assert.Single(await _sdk.GetPublicLinksAsync(_crm, "quote", link.DocumentId));

        // Sin token de sistema no se crean enlaces.
        var anonymous = await Anonymous().PostAsJsonAsync("api/public-links", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task El_vencimiento_debe_ser_futuro_y_la_pagina_sale_en_el_idioma_del_enlace()
    {
        var past = await Assert.ThrowsAsync<VortexApiException>(() =>
            _sdk.CreatePublicLinkAsync(_crm, Quote(Guid.NewGuid(), _factory.Clock.GetUtcNow().AddMinutes(-1))));
        Assert.Equal("validation_error", past.Code);

        var english = await _sdk.CreatePublicLinkAsync(_crm, Quote(Guid.NewGuid(), _factory.Clock.GetUtcNow().AddDays(15)) with { Language = "en-US" });
        Assert.Equal("en", english.Language);
        Assert.Contains("Accept and sign", await (await Anonymous().GetAsync($"p/{TokenOf(english)}")).Content.ReadAsStringAsync());
    }

    [Fact]
    public void Los_textos_de_la_pagina_estan_en_los_tres_idiomas()
    {
        var keys = PublicLinkPage.TextKeys;
        Assert.Equal(["es", "en", "pt"], keys.Keys);
        Assert.All(keys.Values, k => Assert.Equal(keys["es"].Order(), k.Order()));
    }

    private VortexApiClient Sdk(string product) => new(_factory.CreateClient(),
        new VortexApiClientOptions { ProductCode = product, ProductKey = VortexApiFactory.ProductKeys[product] });

    private async Task<(string Crm, string Erp)> CreateOrganizationAsync(string name, string slug)
    {
        var orgId = (await _platform.PostOkAsync("api/admin/organizations", new { name })).GetProperty("id").GetGuid();
        foreach (var product in new[] { "CRM", "ERP" })
            await _platform.PutOkAsync($"api/admin/organizations/{orgId}/products/{product}", new { autoAssignUsers = true });
        await _platform.PostOkAsync("api/admin/users", new { organizationId = orgId, email = $"luis@{slug}.test", fullName = "Luis", password = Password });
        var crm = await Sdk("CRM").LoginAsync($"luis@{slug}.test", Password, $"device-crm-{slug}".PadRight(20, '0'));
        var erp = await Sdk("ERP").LoginAsync($"luis@{slug}.test", Password, $"device-erp-{slug}".PadRight(20, '0'));
        return (crm.AccessToken, erp.AccessToken);
    }
}
