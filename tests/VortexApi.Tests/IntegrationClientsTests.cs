using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VortexApi.Client;

namespace VortexApi.Tests;

/// <summary>Aplicaciones externas que se integran con Vortex mediante OAuth2 client_credentials y scopes.</summary>
public sealed class IntegrationClientsTests : IAsyncLifetime
{
    private const string Password = "User-Pass-2026!";

    private readonly VortexApiFactory _factory = new();
    private HttpClient _platform = null!;
    private HttpClient _orgAdmin = null!;
    private HttpClient _erp = null!;



    public async Task InitializeAsync()
    {
        _platform = await _factory.AdminClientAsync();
        (_orgAdmin, _erp) = await CreateOrganizationAsync(_factory, _platform, "Acme SA", "acme");
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Online_store_pushes_orders_and_reads_catalog_within_its_scopes()
    {
        await _erp.PostOkAsync("api/master/items", new { sku = "CAFE-250", name = "Café", price = 18.5m });
        var (clientId, secret) = await CreateIntegrationAsync("Tienda WooCommerce", "tienda-woo", "master.read", "events.publish:order.*");
        var store = await IntegrationHttpAsync(clientId, secret);

        // Lee el catálogo…
        var items = await (await store.GetAsync("api/master/items")).ReadJsonAsync();
        Assert.Equal("CAFE-250", items.GetProperty("items")[0].GetProperty("sku").GetString());

        // …pero no puede modificarlo.
        Assert.Equal(HttpStatusCode.Forbidden, (await store.PostAsJsonAsync("api/master/items", new { sku = "X", name = "X" })).StatusCode);

        // Publica pedidos, y solo pedidos.
        var order = await store.PostAsJsonAsync("api/events", new { id = Guid.NewGuid(), type = "order.created", data = new { number = "WC-1001", total = 37 } });
        Assert.Equal(HttpStatusCode.Created, order.StatusCode);
        var sale = await store.PostAsJsonAsync("api/events", new { type = "sale.completed", data = new { } });
        Assert.Equal(HttpStatusCode.Forbidden, sale.StatusCode);
        Assert.Equal("insufficient_scope", await sale.ErrorCodeAsync());

        // No tiene events.read ni acceso a la consola ni a endpoints de los sistemas Vortex.
        Assert.Equal(HttpStatusCode.Forbidden, (await store.GetAsync("api/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await store.GetAsync("api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await store.PostAsync("api/access/heartbeat", null)).StatusCode);

        // El ERP ve el pedido con el origen de la integración.
        var feed = await (await _erp.GetAsync("api/events?types=order.*")).ReadJsonAsync();
        var e = Assert.Single(feed.GetProperty("events").EnumerateArray());
        Assert.Equal("EXT:tienda-woo", e.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("userId").ValueKind);
    }

    [Fact]
    public async Task Master_data_written_by_integration_is_attributed_to_it()
    {
        var (clientId, secret) = await CreateIntegrationAsync("Facturador SUNAT", "ose", "master.write");
        var app = await IntegrationHttpAsync(clientId, secret);
        await app.PostOkAsync("api/master/parties", new { name = "Cliente Web", documentType = "DNI", documentNumber = "12345678" });

        var changes = await (await _erp.GetAsync("api/master/changes")).ReadJsonAsync();
        var change = Assert.Single(changes.GetProperty("changes").EnumerateArray());
        Assert.Equal("EXT:ose", change.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, change.GetProperty("changedByUserId").ValueKind);
    }

    [Fact]
    public async Task Token_endpoint_follows_oauth2()
    {
        var (clientId, secret) = await CreateIntegrationAsync("BI", "power-bi", "master.read", "events.read");
        var http = _factory.CreateClient();

        // client_secret_basic
        var basic = new HttpRequestMessage(HttpMethod.Post, "connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "events.read" }),
        };
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
        var response = await http.SendAsync(basic);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var token = await response.ReadJsonAsync();
        Assert.Equal("Bearer", token.GetProperty("token_type").GetString());
        Assert.Equal("events.read", token.GetProperty("scope").GetString());
        Assert.True(token.GetProperty("expires_in").GetInt32() > 0);

        // El token pedido solo con events.read no lee datos maestros aunque la aplicación tenga master.read.
        var narrow = _factory.CreateClient();
        narrow.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("access_token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await narrow.GetAsync("api/master/items")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await narrow.GetAsync("api/events")).StatusCode);

        Assert.Equal(("invalid_client", HttpStatusCode.Unauthorized), await TokenErrorAsync(clientId, "vxs_incorrecto"));
        Assert.Equal(("invalid_client", HttpStatusCode.Unauthorized), await TokenErrorAsync("vxc_noexiste", secret));
        Assert.Equal(("invalid_scope", HttpStatusCode.BadRequest), await TokenErrorAsync(clientId, secret, scope: "master.write"));
        Assert.Equal(("unsupported_grant_type", HttpStatusCode.BadRequest), await TokenErrorAsync(clientId, secret, grant: "password"));
    }

    [Fact]
    public async Task Rotating_secret_or_disabling_revokes_existing_tokens_immediately()
    {
        var (clientId, secret) = await CreateIntegrationAsync("Pasarela", "culqi", "events.read");
        var app = await IntegrationHttpAsync(clientId, secret);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("api/events")).StatusCode);

        var id = await IdOfAsync(clientId);
        var rotated = await _orgAdmin.PostOkAsync($"api/admin/integrations/{id}/rotate-secret", new { });
        var newSecret = rotated.GetProperty("clientSecret").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("api/events")).StatusCode);
        Assert.Equal(("invalid_client", HttpStatusCode.Unauthorized), await TokenErrorAsync(clientId, secret));

        var renewed = await IntegrationHttpAsync(clientId, newSecret);
        Assert.Equal(HttpStatusCode.OK, (await renewed.GetAsync("api/events")).StatusCode);

        await _orgAdmin.PutOkAsync($"api/admin/integrations/{id}", new { name = "Pasarela", scopes = new[] { "events.read" }, isActive = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await renewed.GetAsync("api/events")).StatusCode);
        Assert.Equal(("invalid_client", HttpStatusCode.Unauthorized), await TokenErrorAsync(clientId, newSecret));
    }

    [Fact]
    public async Task Integration_cannot_reach_other_organizations()
    {
        var (otherAdmin, otherErp) = await CreateOrganizationAsync(_factory, _platform, "Globex", "globex");
        var foreign = await otherErp.PostOkAsync("api/master/items", new { sku = "G-1", name = "Globex" });

        var (clientId, secret) = await CreateIntegrationAsync("BI", "bi", "master.read");
        var app = await IntegrationHttpAsync(clientId, secret);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync($"api/master/items/{foreign.GetProperty("id").GetGuid()}")).StatusCode);

        // Tampoco el OrgAdmin de otra organización ve o administra esta integración.
        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.GetAsync($"api/admin/integrations/{await IdOfAsync(clientId)}")).StatusCode);
    }

    [Fact]
    public async Task Integration_manages_only_its_own_webhooks_and_does_not_receive_its_own_events()
    {
        await _orgAdmin.PostOkAsync("api/admin/webhooks", new { name = "Del admin", url = "https://erp.acme.test/hooks", eventTypes = new[] { "*" } });

        var (clientId, secret) = await CreateIntegrationAsync("Tienda", "tienda", "webhooks.manage", "events.publish:order.*");
        var app = await IntegrationHttpAsync(clientId, secret);
        var created = await app.PostOkAsync("api/admin/webhooks", new
        {
            name = "Stock para la tienda", url = "https://tienda.acme.test/vortex", eventTypes = new[] { "order.*", "stock.*" },
            receiverProduct = "ERP", // se ignora: el receptor es la propia aplicación
        });
        Assert.Equal("EXT:tienda", created.GetProperty("webhook").GetProperty("receiverProduct").GetString());

        var list = await (await app.GetAsync("api/admin/webhooks")).ReadJsonAsync();
        Assert.Equal(["Stock para la tienda"], list.EnumerateArray().Select(w => w.GetProperty("name").GetString()));

        await app.PostOkAsync("api/events", new { type = "order.created", data = new { } });
        await _factory.DeliverWebhooksAsync();
        Assert.Equal(["https://erp.acme.test/hooks"], _factory.Webhooks.Requests.Select(r => r.Url.ToString()));
    }

    [Fact]
    public async Task Integrations_are_managed_only_by_org_admin_with_valid_scopes()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _erp.GetAsync("api/admin/integrations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _platform.GetAsync("api/admin/integrations")).StatusCode);

        var bad = await _orgAdmin.PostAsJsonAsync("api/admin/integrations", new { name = "X", slug = "x1", scopes = new[] { "todo.poderoso" } });
        Assert.Equal("invalid_scope", await bad.ErrorCodeAsync());
        var wildcard = await _orgAdmin.PostAsJsonAsync("api/admin/integrations", new { name = "X", slug = "x2", scopes = new[] { "events.publish:*" } });
        Assert.Equal("invalid_scope", await wildcard.ErrorCodeAsync());
        var badSlug = await _orgAdmin.PostAsJsonAsync("api/admin/integrations", new { name = "X", slug = "Con Espacios", scopes = new[] { "events.read" } });
        Assert.Equal(HttpStatusCode.BadRequest, badSlug.StatusCode);

        await CreateIntegrationAsync("Uno", "dup", "events.read");
        var dup = await _orgAdmin.PostAsJsonAsync("api/admin/integrations", new { name = "Dos", slug = "dup", scopes = new[] { "events.read" } });
        Assert.Equal("slug_in_use", await dup.ErrorCodeAsync());

        var scopes = await (await _orgAdmin.GetAsync("api/admin/integrations/scopes")).ReadJsonAsync();
        Assert.Contains(scopes.EnumerateArray(), s => s.GetProperty("scope").GetString() == "master.read");

        // El secreto no se vuelve a mostrar.
        var listed = await (await _orgAdmin.GetAsync("api/admin/integrations")).ReadJsonAsync();
        Assert.DoesNotContain("clientSecret", listed.GetRawText());
    }

    [Fact]
    public async Task Each_integration_has_its_own_rate_limit()
    {
        using var factory = new VortexApiFactory(new Dictionary<string, string> { ["RateLimiting:IntegrationPermitsPerMinute"] = "3" });
        var platform = await factory.AdminClientAsync();
        var (orgAdmin, erp) = await CreateOrganizationAsync(factory, platform, "Limitada", "lim");
        var created = await orgAdmin.PostOkAsync("api/admin/integrations", new { name = "Ruidosa", slug = "ruidosa", scopes = new[] { "events.read" } });
        var app = await IntegrationHttpAsync(factory, created.GetProperty("client").GetProperty("clientId").GetString()!,
            created.GetProperty("clientSecret").GetString()!);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await app.GetAsync("api/events")).StatusCode);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);

        // Los sistemas Vortex no se ven afectados por el límite de la integración.
        Assert.Equal(HttpStatusCode.OK, (await erp.GetAsync("api/events")).StatusCode);
    }

    [Fact]
    public async Task Api_documentation_and_discovery_are_public()
    {
        var http = _factory.CreateClient();
        var openApi = await http.GetStringAsync("openapi/v1.json");
        Assert.Contains("/connect/token", openApi);
        Assert.Contains("clientCredentials", openApi);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("docs/v1")).StatusCode);

        var discovery = await (await http.GetAsync(".well-known/openid-configuration")).ReadJsonAsync();
        Assert.EndsWith("/connect/token", discovery.GetProperty("token_endpoint").GetString());
        Assert.Contains(discovery.GetProperty("scopes_supported").EnumerateArray(), s => s.GetString() == "events.read");

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/events/catalog")).StatusCode);
    }

    // ---------- Infraestructura ----------

    private async Task<(string ClientId, string Secret)> CreateIntegrationAsync(string name, string slug, params string[] scopes)
    {
        var created = await _orgAdmin.PostOkAsync("api/admin/integrations", new { name, slug, scopes });
        Assert.StartsWith("vxs_", created.GetProperty("clientSecret").GetString());
        return (created.GetProperty("client").GetProperty("clientId").GetString()!, created.GetProperty("clientSecret").GetString()!);
    }

    private async Task<Guid> IdOfAsync(string clientId) =>
        (await (await _orgAdmin.GetAsync("api/admin/integrations")).ReadJsonAsync()).EnumerateArray()
        .Single(c => c.GetProperty("clientId").GetString() == clientId).GetProperty("id").GetGuid();

    private Task<HttpClient> IntegrationHttpAsync(string clientId, string secret) => IntegrationHttpAsync(_factory, clientId, secret);

    private static async Task<HttpClient> IntegrationHttpAsync(VortexApiFactory factory, string clientId, string secret)
    {
        var response = await factory.CreateClient().PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret,
        }));
        var token = await response.ReadJsonAsync();
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("access_token").GetString());
        return http;
    }

    private async Task<(string, HttpStatusCode)> TokenErrorAsync(string clientId, string secret, string grant = "client_credentials", string? scope = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = grant, ["client_id"] = clientId, ["client_secret"] = secret };
        if (scope is not null)
            form["scope"] = scope;
        var response = await _factory.CreateClient().PostAsync("connect/token", new FormUrlEncodedContent(form));
        return ((await response.ReadJsonAsync()).GetProperty("error").GetString()!, response.StatusCode);
    }

    private static async Task<(HttpClient OrgAdmin, HttpClient Erp)> CreateOrganizationAsync(
        VortexApiFactory factory, HttpClient platform, string name, string slug)
    {
        var orgId = (await platform.PostOkAsync("api/admin/organizations", new { name })).GetProperty("id").GetGuid();
        await platform.PutOkAsync($"api/admin/organizations/{orgId}/products/ERP", new { autoAssignUsers = true });
        await platform.PostOkAsync("api/admin/users", new
        {
            organizationId = orgId, email = $"admin@{slug}.test", fullName = "Admin", password = Password, role = "OrgAdmin",
        });
        await platform.PostOkAsync("api/admin/users", new { organizationId = orgId, email = $"erp@{slug}.test", fullName = "ERP", password = Password });

        var client = new VortexApiClient(factory.CreateClient(),
            new VortexApiClientOptions { ProductCode = "ERP", ProductKey = VortexApiFactory.ProductKeys["ERP"] });
        var session = await client.LoginAsync($"erp@{slug}.test", Password, $"device-erp-{slug}".PadRight(20, '0'));
        var erp = factory.CreateClient();
        erp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return (await factory.AdminClientAsync($"admin@{slug}.test", Password), erp);
    }
}
