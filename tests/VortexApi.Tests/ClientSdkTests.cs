using System.Net;
using System.Net.Http.Headers;
using VortexApi.Client;
using VortexApi.Client.Sessions;
using VortexApi.Client.Sync;

namespace VortexApi.Tests;

/// <summary>Pruebas de la librería cliente (sesión y sincronización) contra el hub real en memoria.</summary>
public sealed class ClientSdkTests : IAsyncLifetime
{
    private const string Password = "User-Pass-2026!";

    private readonly VortexApiFactory _factory = new();
    private HttpClient _platform = null!;
    private Guid _orgId;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        _platform = await _factory.AdminClientAsync();
        _orgId = (await _platform.PostOkAsync("api/admin/organizations", new { name = "Acme SA" })).GetProperty("id").GetGuid();
        foreach (var product in new[] { "ERP", "POS" })
            await _platform.PutOkAsync($"api/admin/organizations/{_orgId}/products/{product}", new { autoAssignUsers = true });
        _userId = (await _platform.PostOkAsync("api/admin/users", new
        {
            organizationId = _orgId, email = "caja@acme.test", fullName = "Caja", password = Password,
        })).GetProperty("id").GetGuid();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Session_manager_refreshes_once_even_with_concurrent_callers()
    {
        var app = await AppAsync("POS");
        var first = await app.Session.GetAccessTokenAsync();
        Assert.Equal(first, await app.Session.GetAccessTokenAsync()); // vigente: no renueva

        _factory.Clock.Advance(TimeSpan.FromMinutes(14.5));
        var tokens = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => app.Session.GetAccessTokenAsync()));

        Assert.Single(tokens.Distinct());
        Assert.NotEqual(first, tokens[0]);
        // Si se hubieran hecho varios refresh con el mismo token, el hub habría detectado reutilización y revocado
        // la sesión: el siguiente refresh fallaría.
        _factory.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.NotEqual(tokens[0], await app.Session.GetAccessTokenAsync());
        Assert.NotNull(await app.Store.LoadAsync());
    }

    [Fact]
    public async Task Rejected_access_token_is_refreshed_and_call_retried_once()
    {
        var app = await AppAsync("POS");
        var calls = 0;
        var result = await app.Session.WithAccessTokenAsync(token =>
        {
            calls++;
            return calls == 1 ? throw new VortexApiException(401, "invalid_token", "rechazado") : Task.FromResult(token);
        });
        Assert.Equal(2, calls);
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public async Task Session_ends_when_user_is_deactivated()
    {
        var app = await AppAsync("POS");
        VortexApiException? ended = null;
        app.Session.SessionEnded += (_, ex) => ended = ex;

        await _platform.PutOkAsync($"api/admin/users/{_userId}", new { fullName = "Caja", role = "User", isActive = false });
        _factory.Clock.Advance(TimeSpan.FromMinutes(15));

        await Assert.ThrowsAsync<VortexApiException>(() => app.Session.GetAccessTokenAsync());
        Assert.NotNull(ended);
        Assert.Null(await app.Store.LoadAsync());
    }

    [Fact]
    public async Task Offline_validation_uses_stored_keys_and_token()
    {
        var app = await AppAsync("POS");
        var result = await app.Session.ValidateOfflineAsync(VortexApiFactory.Issuer);
        Assert.True(result.IsValid, result.Error);
        Assert.Equal("POS", result.Access!.Product);
    }

    [Fact]
    public async Task Customer_and_sale_created_offline_reach_ERP_in_order()
    {
        var pos = await AppAsync("POS");
        var erp = await AppAsync("ERP");

        // Sin conexión: el POS crea un cliente y registra una venta que lo referencia.
        pos.Network.Offline = true;
        var customerId = Guid.NewGuid();
        await pos.Sync.EnqueueUpsertAsync(VortexMasterResource.Parties, customerId,
            new { name = "Bodega Don Pepe", documentType = "RUC", documentNumber = "20123456789" });
        var saleId = await pos.Sync.EnqueueEventAsync("sale.completed", new { customerId, total = 59.90m }, "sale", "B001-1");

        var offline = await pos.Sync.SyncAsync();
        Assert.False(offline.Completed);
        Assert.Equal(2, offline.PendingOutbox);

        // Vuelve la conexión.
        pos.Network.Offline = false;
        var online = await pos.Sync.SyncAsync();
        Assert.True(online.Completed, online.InterruptedBy);
        Assert.Equal((1, 1, 0), (online.PushedMasterWrites, online.PushedEvents, online.PendingOutbox));

        // El ERP recibe el cliente y luego la venta.
        var received = new List<string>();
        VortexParty? party = null;
        erp.Sync
            .OnParties((c, _) => { party = c.Data; received.Add($"party:{c.Operation}"); return Task.CompletedTask; })
            .OnEvent("sale.*", (e, _) => { received.Add($"event:{e.Type}:{e.Id == saleId}"); return Task.CompletedTask; });

        var pulled = await erp.Sync.SyncAsync();
        Assert.Equal(["party:Created", "event:sale.completed:True"], received);
        Assert.Equal("Bodega Don Pepe", party!.Name);
        Assert.Equal((1, 1), (pulled.PulledChanges, pulled.PulledEvents));

        // Los cursores quedaron guardados: una segunda pasada no repite nada.
        var again = await erp.Sync.SyncAsync();
        Assert.Equal((0, 0), (again.PulledChanges, again.PulledEvents));
        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task Conflicting_offline_edit_is_resolved_by_handler()
    {
        var pos = await AppAsync("POS");
        var erp = await AppAsync("ERP");
        var id = Guid.NewGuid();
        await erp.Sync.EnqueueUpsertAsync(VortexMasterResource.Items, id, new { sku = "CAFE-250", name = "Café", price = 18m });
        await erp.Sync.SyncAsync();

        // El ERP sube el precio (v2) mientras el POS, con la v1, cambia el nombre.
        await erp.Sync.EnqueueUpsertAsync(VortexMasterResource.Items, id, new { sku = "CAFE-250", name = "Café", price = 20m }, expectedVersion: 1);
        await erp.Sync.SyncAsync();
        await pos.Sync.EnqueueUpsertAsync(VortexMasterResource.Items, id, new { sku = "CAFE-250", name = "Café 250 g", price = 18m }, expectedVersion: 1);

        VortexConflict? conflict = null;
        pos.Sync.OnConflict((c, _) => { conflict = c; return Task.FromResult(VortexConflictResolution.DiscardLocal); });
        var result = await pos.Sync.SyncAsync();

        Assert.Equal(1, result.Conflicts);
        Assert.Equal(0, result.PendingOutbox);
        Assert.Equal(20m, conflict!.Server.GetProperty("price").GetDecimal());

        var item = (await ProductHttpAsync(erp)).GetAsync($"api/master/items/{id}");
        Assert.Equal("Café", (await (await item).ReadJsonAsync()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Rejected_entries_go_to_failed_list_without_blocking_the_queue()
    {
        var pos = await AppAsync("POS");
        VortexFailedEntry? notified = null;
        pos.Sync.OnPushFailed((f, _) => { notified = f; return Task.CompletedTask; });

        await pos.Sync.EnqueueUpsertAsync(VortexMasterResource.Parties, Guid.NewGuid(), new { name = "X", documentType = "RUC", documentNumber = "123" });
        await pos.Sync.EnqueueEventAsync("Venta Mal Escrita", new { });
        await pos.Sync.EnqueueEventAsync("sale.completed", new { total = 1 });

        var result = await pos.Sync.SyncAsync();
        Assert.True(result.Completed);
        Assert.Equal((2, 1, 0), (result.Failed, result.PushedEvents, result.PendingOutbox));

        var failed = await pos.SyncStore.GetFailedAsync();
        Assert.Equal(["invalid_document", "invalid_event_type"], failed.Select(f => f.ErrorCode));
        Assert.NotNull(notified);
    }

    [Fact]
    public async Task Handler_failure_keeps_cursor_at_last_applied_change()
    {
        var erp = await AppAsync("ERP");
        var pos = await AppAsync("POS");
        for (var i = 1; i <= 3; i++)
            await erp.Sync.EnqueueUpsertAsync(VortexMasterResource.Sites, Guid.NewGuid(), new { code = $"T{i}", name = $"Tienda {i}" });
        await erp.Sync.SyncAsync();

        var applied = new List<string>();
        var failOn = "T2";
        pos.Sync.OnSites((c, _) =>
        {
            if (c.Data.Code == failOn)
                throw new InvalidOperationException("disco lleno");
            applied.Add(c.Data.Code);
            return Task.CompletedTask;
        });

        var ex = await Assert.ThrowsAsync<VortexSyncException>(() => pos.Sync.SyncAsync());
        Assert.Equal(2, ex.Sequence);
        Assert.Equal(1, await pos.SyncStore.GetCursorAsync(VortexSync.MasterStream));

        failOn = "";
        await pos.Sync.SyncAsync();
        Assert.Equal(["T1", "T2", "T3"], applied);
    }

    [Fact]
    public async Task Json_file_store_survives_restarts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vortex-sync-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonFileSyncStore(path);
            await store.SaveCursorAsync("events", 42);
            await store.EnqueueAsync(new VortexOutboxEntry(Guid.NewGuid(), VortexOutboxKind.Event, DateTimeOffset.UtcNow,
                EventType: "sale.completed", Payload: System.Text.Json.JsonSerializer.SerializeToElement(new { total = 5 })));

            var reopened = new JsonFileSyncStore(path);
            Assert.Equal(42, await reopened.GetCursorAsync("events"));
            var entry = Assert.Single(await reopened.PeekAsync(10));
            Assert.Equal(5, entry.Payload!.Value.GetProperty("total").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("*", "sale.completed", true)]
    [InlineData("sale.*", "sale.completed", true)]
    [InlineData("sale.*", "sales.completed", false)]
    [InlineData("master.item.*", "master.item.updated", true)]
    [InlineData("master.item.*", "master.items.updated", false)]
    [InlineData("sale.completed", "sale.completed", true)]
    public void Client_pattern_matching_matches_hub_rules(string pattern, string type, bool expected) =>
        Assert.Equal(expected, VortexSync.Matches(pattern, type));

    // ---------- Infraestructura ----------

    private sealed class NetworkSwitch : DelegatingHandler
    {
        public bool Offline { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Offline ? throw new HttpRequestException("Sin conexión") : base.SendAsync(request, ct);
    }

    private sealed record App(VortexApiClient Client, VortexSessionManager Session, InMemorySessionStore Store,
        VortexSync Sync, InMemorySyncStore SyncStore, NetworkSwitch Network);

    private async Task<App> AppAsync(string product)
    {
        var network = new NetworkSwitch();
        var client = new VortexApiClient(_factory.CreateDefaultClient(network),
            new VortexApiClientOptions { ProductCode = product, ProductKey = VortexApiFactory.ProductKeys[product] });
        var store = new InMemorySessionStore();
        var session = new VortexSessionManager(client, store, $"device-{product}-0000000000000", _factory.Clock);
        await session.LoginAsync("caja@acme.test", Password);
        var syncStore = new InMemorySyncStore();
        return new App(client, session, store, new VortexSync(client, session, syncStore, clock: _factory.Clock), syncStore, network);
    }

    private async Task<HttpClient> ProductHttpAsync(App app)
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await app.Session.GetAccessTokenAsync());
        return http;
    }
}
