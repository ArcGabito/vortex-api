using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VortexApi.Client;
using VortexApi.Services.Webhooks;

namespace VortexApi.Tests;

public sealed class IntegrationEventsTests : IAsyncLifetime
{
    private const string Password = "User-Pass-2026!";

    private readonly VortexApiFactory _factory = new();
    private HttpClient _platform = null!;
    private HttpClient _orgAdmin = null!;
    private HttpClient _pos = null!;
    private HttpClient _erp = null!;

    public async Task InitializeAsync()
    {
        _platform = await _factory.AdminClientAsync();
        (_orgAdmin, _pos, _erp) = await CreateOrganizationAsync("Acme SA", "acme");
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Sale_published_by_POS_is_consumed_by_ERP_and_retries_do_not_duplicate()
    {
        var saleId = Guid.NewGuid();
        var sale = new
        {
            id = saleId, type = "sale.completed", subjectType = "sale", subjectId = "B001-000123",
            data = new { total = 59.90m, currency = "PEN", lines = new[] { new { sku = "CAFE-250", qty = 2 } } },
        };

        var first = await _pos.PostAsJsonAsync("api/events", sale);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var retry = await _pos.PostAsJsonAsync("api/events", sale);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal((await first.ReadJsonAsync()).GetProperty("sequence").GetInt64(),
            (await retry.ReadJsonAsync()).GetProperty("sequence").GetInt64());

        await _erp.PostOkAsync("api/events", new { type = "invoice.issued", data = new { number = "F001-1" } });

        var feed = await (await _erp.GetAsync("api/events?since=0&types=sale.*&excludeSource=ERP")).ReadJsonAsync();
        var e = Assert.Single(feed.GetProperty("events").EnumerateArray());
        Assert.Equal(saleId, e.GetProperty("id").GetGuid());
        Assert.Equal("POS", e.GetProperty("source").GetString());
        Assert.Equal(59.90m, e.GetProperty("data").GetProperty("total").GetDecimal());
        Assert.Equal(2, feed.GetProperty("nextCursor").GetInt64());

        Assert.Equal(HttpStatusCode.OK, (await _erp.GetAsync($"api/events/{saleId}")).StatusCode);
    }

    [Fact]
    public async Task Offline_batch_upload_processes_each_event_independently()
    {
        var a = Guid.NewGuid();
        var results = await (await _pos.PostAsJsonAsync("api/events/batch", new
        {
            events = new object[]
            {
                new { id = a, type = "sale.completed", data = new { total = 10 }, occurredAt = _factory.Clock.GetUtcNow().AddHours(-3) },
                new { id = Guid.NewGuid(), type = "sale.completed", data = new { total = 20 } },
                new { id = a, type = "sale.completed", data = new { total = 10 } },
                new { id = Guid.NewGuid(), type = "Venta Hecha", data = new { } },
            },
        })).ReadJsonAsync();

        var items = results.EnumerateArray().ToList();
        Assert.Equal([true, true, false, false], items.Select(i => i.GetProperty("created").GetBoolean()));
        Assert.Equal(items[0].GetProperty("sequence").GetInt64(), items[2].GetProperty("sequence").GetInt64());
        Assert.Equal("invalid_event_type", items[3].GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData("master.item.updated", "{}")]
    [InlineData("hub.ping", "{}")]
    [InlineData("sale", "{}")]
    [InlineData("sale.completed", "[1,2]")]
    public async Task Invalid_events_are_rejected(string type, string data)
    {
        var response = await _pos.PostAsJsonAsync("api/events", new { type, data = JsonDocument.Parse(data).RootElement });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_systems_publish_and_future_events_are_rejected()
    {
        var fromAdmin = await _orgAdmin.PostAsJsonAsync("api/events", new { type = "sale.completed", data = new { } });
        Assert.Equal(HttpStatusCode.Forbidden, fromAdmin.StatusCode);

        var future = await _pos.PostAsJsonAsync("api/events",
            new { type = "sale.completed", data = new { }, occurredAt = _factory.Clock.GetUtcNow().AddHours(1) });
        Assert.Equal("invalid_event_data", await future.ErrorCodeAsync());

        var big = await _pos.PostAsJsonAsync("api/events",
            new { type = "sale.completed", data = new { blob = new string('x', 70_000) } });
        Assert.Equal("invalid_event_data", await big.ErrorCodeAsync());
    }

    [Fact]
    public async Task Master_data_changes_are_also_published_as_events()
    {
        var item = await _erp.PostOkAsync("api/master/items", new { sku = "CAFE-250", name = "Café", price = 18.5m });

        var feed = await (await _pos.GetAsync("api/events?types=master.*")).ReadJsonAsync();
        var e = Assert.Single(feed.GetProperty("events").EnumerateArray());
        Assert.Equal("master.item.created", e.GetProperty("type").GetString());
        Assert.Equal("ERP", e.GetProperty("source").GetString());
        Assert.Equal(item.GetProperty("id").GetGuid().ToString(), e.GetProperty("subjectId").GetString());
        Assert.Equal("CAFE-250", e.GetProperty("data").GetProperty("sku").GetString());
    }

    [Fact]
    public async Task Feed_filters_by_exact_type_category_and_longer_prefix()
    {
        await _pos.PostOkAsync("api/events", new { type = "sale.completed", data = new { } });
        await _pos.PostOkAsync("api/events", new { type = "sale.voided", data = new { } });
        await _erp.PostOkAsync("api/master/items", new { sku = "A", name = "A" });
        await _erp.PostOkAsync("api/master/sites", new { code = "T1", name = "Tienda" });

        async Task<string[]> Types(string filter) =>
            (await (await _erp.GetAsync($"api/events?types={filter}")).ReadJsonAsync()).GetProperty("events")
            .EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToArray();

        Assert.Equal(["sale.completed", "sale.voided"], await Types("sale.*"));
        Assert.Equal(["sale.voided", "master.item.created"], await Types("sale.voided,master.item.*"));
        Assert.Equal(["master.item.created", "master.site.created"], await Types("master.*"));
        Assert.Equal(4, (await Types("*")).Length);
    }

    [Fact]
    public async Task Organizations_do_not_see_each_others_events()
    {
        var (_, otherPos, _) = await CreateOrganizationAsync("Globex", "globex");
        var id = Guid.NewGuid();
        await _pos.PostOkAsync("api/events", new { id, type = "sale.completed", data = new { total = 1 } });

        Assert.Equal(HttpStatusCode.NotFound, (await otherPos.GetAsync($"api/events/{id}")).StatusCode);
        Assert.Empty((await (await otherPos.GetAsync("api/events")).ReadJsonAsync()).GetProperty("events").EnumerateArray());

        // Reutilizar el Id de otra organización no revela ni pisa su evento.
        var clash = await otherPos.PostAsJsonAsync("api/events", new { id, type = "sale.completed", data = new { } });
        Assert.Equal("event_id_in_use", await clash.ErrorCodeAsync());
    }

    [Fact]
    public async Task Webhook_receives_signed_events_but_not_its_own()
    {
        var created = await _orgAdmin.PostOkAsync("api/admin/webhooks", new
        {
            name = "Backend ERP", url = "https://erp.acme.test/hooks/vortex", eventTypes = new[] { "sale.*", "master.item.*" },
            receiverProduct = "ERP",
        });
        var secret = created.GetProperty("secret").GetString()!;
        Assert.StartsWith("whsec_", secret);

        await _pos.PostOkAsync("api/events", new { type = "sale.completed", data = new { total = 5 } });
        await _erp.PostOkAsync("api/events", new { type = "sale.completed", data = new { total = 7 } }); // propio: no se envía
        await _pos.PostOkAsync("api/events", new { type = "stock.adjusted", data = new { } });            // no suscrito
        await _pos.PostOkAsync("api/master/items", new { sku = "X-1", name = "X" });                        // master.item.created

        Assert.Equal(2, await _factory.DeliverWebhooksAsync());
        Assert.Equal(["sale.completed", "master.item.created"], _factory.Webhooks.Requests.Select(r => r.Headers["X-Vortex-Event"]));

        var request = _factory.Webhooks.Requests[0];
        Assert.Equal("https://erp.acme.test/hooks/vortex", request.Url.ToString());
        var signature = request.Headers[WebhookDeliveryService.SignatureHeader].Split(',').ToDictionary(p => p[..p.IndexOf('=')], p => p[(p.IndexOf('=') + 1)..]);
        Assert.Equal(WebhookDeliveryService.Sign(secret, signature["t"], request.Body), signature["v1"]);

        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("POS", body.GetProperty("source").GetString());
        Assert.Equal(5, body.GetProperty("data").GetProperty("total").GetInt32());

        Assert.Equal(0, await _factory.DeliverWebhooksAsync()); // nada pendiente
    }

    [Fact]
    public async Task Failed_deliveries_are_retried_with_backoff_and_can_be_requeued()
    {
        var webhookId = (await _orgAdmin.PostOkAsync("api/admin/webhooks", new
        {
            name = "Receptor", url = "https://hooks.acme.test/in", eventTypes = new[] { "*" },
        })).GetProperty("webhook").GetProperty("id").GetGuid();
        await _pos.PostOkAsync("api/events", new { type = "sale.completed", data = new { } });

        _factory.Webhooks.StatusCode = HttpStatusCode.InternalServerError;
        Assert.Equal(1, await _factory.DeliverWebhooksAsync());
        Assert.Equal(0, await _factory.DeliverWebhooksAsync()); // espera 30 s antes del reintento

        _factory.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await _factory.DeliverWebhooksAsync());
        _factory.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await _factory.DeliverWebhooksAsync()); // 3.er intento = MaxAttempts en pruebas

        var deliveries = await (await _orgAdmin.GetAsync($"api/admin/webhooks/{webhookId}/deliveries")).ReadJsonAsync();
        var delivery = deliveries.GetProperty("items")[0];
        Assert.Equal("Failed", delivery.GetProperty("status").GetString());
        Assert.Equal(3, delivery.GetProperty("attempts").GetInt32());
        Assert.Equal(500, delivery.GetProperty("lastStatusCode").GetInt32());

        // Arreglado el receptor, se reencola manualmente.
        _factory.Webhooks.StatusCode = HttpStatusCode.NoContent;
        var retry = await _orgAdmin.PostAsync($"api/admin/webhooks/{webhookId}/deliveries/{delivery.GetProperty("id").GetInt64()}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(1, await _factory.DeliverWebhooksAsync());

        var after = await (await _orgAdmin.GetAsync($"api/admin/webhooks/{webhookId}/deliveries?status=Succeeded")).ReadJsonAsync();
        Assert.Equal(1, after.GetProperty("total").GetInt32());
        Assert.Equal(4, _factory.Webhooks.Requests.Count);
    }

    [Fact]
    public async Task Deactivating_webhook_cancels_pending_deliveries()
    {
        var webhook = (await _orgAdmin.PostOkAsync("api/admin/webhooks", new
        {
            name = "Receptor", url = "https://hooks.acme.test/in", eventTypes = new[] { "sale.completed" },
        })).GetProperty("webhook");
        await _pos.PostOkAsync("api/events", new { type = "sale.completed", data = new { } });

        await _orgAdmin.PutOkAsync($"api/admin/webhooks/{webhook.GetProperty("id").GetGuid()}", new
        {
            name = "Receptor", url = "https://hooks.acme.test/in", eventTypes = new[] { "sale.completed" }, isActive = false,
        });
        Assert.Equal(0, await _factory.DeliverWebhooksAsync());
        Assert.Empty(_factory.Webhooks.Requests);
    }

    [Theory]
    [InlineData("http://erp.acme.test/hooks")]
    [InlineData("https://localhost/hooks")]
    [InlineData("https://127.0.0.1/hooks")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://10.0.0.5/hooks")]
    [InlineData("https://192.168.1.10/hooks")]
    [InlineData("https://[::1]/hooks")]
    [InlineData("https://user:pass@erp.acme.test/hooks")]
    [InlineData("not a url")]
    public async Task Webhook_urls_pointing_to_internal_networks_are_rejected(string url)
    {
        var response = await _orgAdmin.PostAsJsonAsync("api/admin/webhooks", new { name = "x", url, eventTypes = new[] { "*" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_webhook_url", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Connect_time_guard_blocks_hosts_that_resolve_to_private_addresses()
    {
        using var http = new HttpClient(NetworkGuard.CreateHandler(new WebhookOptions()));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://localhost:9/"));
        Assert.Contains("Destino no permitido", ex.Message);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::ffff:192.168.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void Private_address_detection(string ip, bool isPrivate) =>
        Assert.Equal(isPrivate, NetworkGuard.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public async Task Only_org_admin_manages_webhooks()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _pos.GetAsync("api/admin/webhooks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _platform.GetAsync("api/admin/webhooks")).StatusCode);
    }

    [Fact]
    public async Task Catalog_lists_known_events()
    {
        var catalog = await (await _erp.GetAsync("api/events/catalog")).ReadJsonAsync();
        Assert.Contains(catalog.EnumerateArray(), e => e.GetProperty("type").GetString() == "sale.completed");
    }

    private async Task<(HttpClient OrgAdmin, HttpClient Pos, HttpClient Erp)> CreateOrganizationAsync(string name, string slug)
    {
        var orgId = (await _platform.PostOkAsync("api/admin/organizations", new { name })).GetProperty("id").GetGuid();
        foreach (var product in new[] { "ERP", "POS" })
            await _platform.PutOkAsync($"api/admin/organizations/{orgId}/products/{product}", new { autoAssignUsers = true });
        await _platform.PostOkAsync("api/admin/users", new
        {
            organizationId = orgId, email = $"admin@{slug}.test", fullName = "Admin", password = Password, role = "OrgAdmin",
        });
        await _platform.PostOkAsync("api/admin/users", new { organizationId = orgId, email = $"caja@{slug}.test", fullName = "Caja", password = Password });

        return (await _factory.AdminClientAsync($"admin@{slug}.test", Password),
            await ProductHttpAsync("POS", $"caja@{slug}.test"), await ProductHttpAsync("ERP", $"caja@{slug}.test"));
    }

    private async Task<HttpClient> ProductHttpAsync(string product, string email)
    {
        var client = new VortexApiClient(_factory.CreateClient(),
            new VortexApiClientOptions { ProductCode = product, ProductKey = VortexApiFactory.ProductKeys[product] });
        var session = await client.LoginAsync(email, Password, $"device-{product}-{email}".PadRight(20, '0'));
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return http;
    }
}
