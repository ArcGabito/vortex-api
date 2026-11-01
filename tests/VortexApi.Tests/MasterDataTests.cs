using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VortexApi.Client;

namespace VortexApi.Tests;

public sealed class MasterDataTests : IAsyncLifetime
{
    private const string Password = "User-Pass-2026!";

    private readonly VortexApiFactory _factory = new();
    private HttpClient _platform = null!;
    private Guid _orgId;
    private HttpClient _pos = null!;
    private HttpClient _erp = null!;
    private HttpClient _orgAdmin = null!;

    public async Task InitializeAsync()
    {
        _platform = await _factory.AdminClientAsync();
        (_orgId, _orgAdmin, _pos, _erp) = await CreateOrganizationAsync("Acme SA", "acme");
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Customer_created_offline_in_POS_reaches_ERP_through_change_feed()
    {
        // El POS genera el Id localmente (p. ej. sin conexión) y lo sincroniza con PUT.
        var id = Guid.NewGuid();
        var created = await _pos.PutOkAsync($"api/master/parties/{id}", new
        {
            name = "Bodega Don Pepe", documentType = "ruc", documentNumber = "20 123456789",
            address = new { line = "Av. Perú 123", city = "Lima", countryCode = "pe" },
        });
        Assert.Equal(1, created.GetProperty("meta").GetProperty("version").GetInt32());
        Assert.Equal("20123456789", created.GetProperty("documentNumber").GetString());

        // Reintentar la misma sincronización no duplica ni genera versión nueva.
        var retry = await _pos.PutOkAsync($"api/master/parties/{id}", new
        {
            name = "Bodega Don Pepe", documentType = "RUC", documentNumber = "20123456789",
            address = new { line = "Av. Perú 123", city = "Lima", countryCode = "PE" },
        });
        Assert.Equal(1, retry.GetProperty("meta").GetProperty("version").GetInt32());

        var feed = await GetFeedAsync(_erp, 0);
        var change = Assert.Single(feed.GetProperty("changes").EnumerateArray());
        Assert.Equal("party", change.GetProperty("entityType").GetString());
        Assert.Equal("Created", change.GetProperty("operation").GetString());
        Assert.Equal("POS", change.GetProperty("source").GetString());
        Assert.Equal("Bodega Don Pepe", change.GetProperty("data").GetProperty("name").GetString());

        // Sin cambios nuevos, el cursor no retrocede y la página viene vacía.
        var cursor = feed.GetProperty("nextCursor").GetInt64();
        Assert.Empty((await GetFeedAsync(_erp, cursor)).GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task If_match_prevents_overwriting_changes_from_another_system()
    {
        var item = await _erp.PostOkAsync("api/master/items", new { sku = "cafe-250", name = "Café 250g", price = 18.5m });
        var id = item.GetProperty("id").GetGuid();

        var erpUpdate = await PutWithVersionAsync(_erp, $"api/master/items/{id}", 1, new { sku = "CAFE-250", name = "Café tostado 250g", price = 19m });
        Assert.Equal(HttpStatusCode.OK, erpUpdate.StatusCode);
        Assert.Equal("\"2\"", erpUpdate.Headers.ETag?.Tag);

        // El POS todavía tenía la versión 1.
        var posUpdate = await PutWithVersionAsync(_pos, $"api/master/items/{id}", 1, new { sku = "CAFE-250", name = "Café 250 g", price = 17m });
        Assert.Equal(HttpStatusCode.PreconditionFailed, posUpdate.StatusCode);
        Assert.Equal("version_conflict", await posUpdate.ErrorCodeAsync());

        var current = await (await _pos.GetAsync($"api/master/items/{id}")).ReadJsonAsync();
        Assert.Equal("Café tostado 250g", current.GetProperty("name").GetString());
        Assert.Equal(19m, current.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Duplicates_and_invalid_documents_are_rejected()
    {
        var first = await _pos.PostOkAsync("api/master/parties", new { name = "Juan Pérez", kind = "Person", documentType = "DNI", documentNumber = "12345678" });

        var duplicate = await _erp.PostAsJsonAsync("api/master/parties", new { name = "J. Pérez", documentType = "DNI", documentNumber = "12345678" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("duplicate_document", await duplicate.ErrorCodeAsync());

        // Buscar por documento antes de crear permite reutilizar el registro existente.
        var found = await (await _erp.GetAsync("api/master/parties?documentNumber=12345678")).ReadJsonAsync();
        Assert.Equal(first.GetProperty("id").GetGuid(), found.GetProperty("items")[0].GetProperty("id").GetGuid());

        var badRuc = await _erp.PostAsJsonAsync("api/master/parties", new { name = "X", documentType = "RUC", documentNumber = "123" });
        Assert.Equal("invalid_document", await badRuc.ErrorCodeAsync());

        await _erp.PostOkAsync("api/master/items", new { sku = "A-1", name = "A" });
        var dupSku = await _pos.PostAsJsonAsync("api/master/items", new { sku = "a-1", name = "Otro" });
        Assert.Equal("duplicate_sku", await dupSku.ErrorCodeAsync());

        await _erp.PostOkAsync("api/master/sites", new { code = "T01", name = "Tienda Centro" });
        var dupSite = await _pos.PostAsJsonAsync("api/master/sites", new { code = "t01", name = "Otra" });
        Assert.Equal("duplicate_code", await dupSite.ErrorCodeAsync());
    }

    [Fact]
    public async Task Delete_is_logical_reported_in_feed_and_can_be_restored()
    {
        var site = await _erp.PostOkAsync("api/master/sites", new { code = "ALM-01", name = "Almacén central", kind = "Warehouse" });
        var id = site.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await _erp.DeleteAsync($"api/master/sites/{id}")).StatusCode);

        var list = await (await _pos.GetAsync("api/master/sites")).ReadJsonAsync();
        Assert.Equal(0, list.GetProperty("total").GetInt32());
        var byId = await (await _pos.GetAsync($"api/master/sites/{id}")).ReadJsonAsync();
        Assert.NotEqual(JsonValueKind.Null, byId.GetProperty("meta").GetProperty("deletedAt").ValueKind);

        var changes = (await GetFeedAsync(_pos, 0, "site")).GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(["Created", "Deleted"], changes.Select(c => c.GetProperty("operation").GetString()));

        var restored = await _erp.PutOkAsync($"api/master/sites/{id}", new { code = "ALM-01", name = "Almacén central", kind = "Warehouse" });
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("meta").GetProperty("deletedAt").ValueKind);
        Assert.Equal(3, restored.GetProperty("meta").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Change_feed_is_paged_ordered_and_filterable()
    {
        for (var i = 1; i <= 5; i++)
            await _erp.PostOkAsync("api/master/items", new { sku = $"SKU-{i}", name = $"Artículo {i}" });
        await _pos.PostOkAsync("api/master/parties", new { name = "Cliente varios" });

        var page1 = await GetFeedAsync(_pos, 0, "item", limit: 2);
        Assert.True(page1.GetProperty("hasMore").GetBoolean());
        var page2 = await GetFeedAsync(_pos, page1.GetProperty("nextCursor").GetInt64(), "item", limit: 10);
        Assert.False(page2.GetProperty("hasMore").GetBoolean());

        var skus = page1.GetProperty("changes").EnumerateArray().Concat(page2.GetProperty("changes").EnumerateArray())
            .Select(c => c.GetProperty("data").GetProperty("sku").GetString());
        Assert.Equal(["SKU-1", "SKU-2", "SKU-3", "SKU-4", "SKU-5"], skus);

        // Al terminar, el cursor llega a la cabeza aunque el último cambio sea de otro tipo.
        Assert.Equal(6, page2.GetProperty("nextCursor").GetInt64());

        var sequences = (await GetFeedAsync(_pos, 0)).GetProperty("changes").EnumerateArray()
            .Select(c => c.GetProperty("sequence").GetInt64());
        Assert.Equal([1L, 2, 3, 4, 5, 6], sequences);
    }

    [Fact]
    public async Task Organizations_are_isolated()
    {
        var (_, _, otherPos, _) = await CreateOrganizationAsync("Globex", "globex");
        var party = await _pos.PostOkAsync("api/master/parties", new { name = "Secreto de Acme" });
        var id = party.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await otherPos.GetAsync($"api/master/parties/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherPos.PutAsJsonAsync($"api/master/parties/{id}", new { name = "Hackeado" })).StatusCode);
        Assert.Empty((await GetFeedAsync(otherPos, 0)).GetProperty("changes").EnumerateArray());
        Assert.Equal(0, (await (await otherPos.GetAsync("api/master/parties")).ReadJsonAsync()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Company_profile_is_shared_and_only_org_admin_can_change_it()
    {
        var defaults = await (await _pos.GetAsync("api/master/company")).ReadJsonAsync();
        Assert.Equal("Acme SA", defaults.GetProperty("legalName").GetString());
        Assert.Equal(0, defaults.GetProperty("meta").GetProperty("version").GetInt32());

        var fromPos = await _pos.PutAsJsonAsync("api/master/company", new { legalName = "Acme" });
        Assert.Equal(HttpStatusCode.Forbidden, fromPos.StatusCode);

        await _orgAdmin.PutOkAsync("api/master/company", new
        {
            legalName = "Acme Sociedad Anónima", taxId = "20123456789", currencyCode = "usd", defaultTaxRate = 18,
        });
        var updated = await (await _erp.GetAsync("api/master/company")).ReadJsonAsync();
        Assert.Equal("USD", updated.GetProperty("currencyCode").GetString());

        // Los artículos nuevos sin moneda toman la de la empresa.
        var item = await _erp.PostOkAsync("api/master/items", new { sku = "S-1", name = "Asesoría", kind = "Service" });
        Assert.Equal("USD", item.GetProperty("currencyCode").GetString());
        Assert.False(item.GetProperty("trackInventory").GetBoolean());

        var badTz = await _orgAdmin.PutAsJsonAsync("api/master/company", new { legalName = "Acme", timeZone = "Marte/Olympus" });
        Assert.Equal(HttpStatusCode.BadRequest, badTz.StatusCode);
    }

    [Fact]
    public async Task Platform_admin_without_organization_cannot_use_master_data()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _platform.GetAsync("api/master/parties")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _platform.GetAsync("api/master/changes")).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_system_cuts_its_master_data_access()
    {
        await _platform.PutOkAsync($"api/admin/organizations/{_orgId}/products/POS", new { isEnabled = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await _pos.GetAsync("api/master/items")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _erp.GetAsync("api/master/items")).StatusCode);
    }

    private static Task<JsonElement> GetFeedAsync(HttpClient client, long since, string? types = null, int limit = 200) =>
        client.GetAsync($"api/master/changes?since={since}&limit={limit}" + (types is null ? "" : $"&types={types}"))
            .ContinueWith(t => t.Result.ReadJsonAsync()).Unwrap();

    private static Task<HttpResponseMessage> PutWithVersionAsync(HttpClient client, string url, int version, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return client.SendAsync(request);
    }

    /// <summary>Organización con ERP y POS habilitados para todos sus usuarios, un OrgAdmin y un usuario.</summary>
    private async Task<(Guid OrgId, HttpClient OrgAdmin, HttpClient Pos, HttpClient Erp)> CreateOrganizationAsync(string name, string slug)
    {
        var orgId = (await _platform.PostOkAsync("api/admin/organizations", new { name })).GetProperty("id").GetGuid();
        foreach (var product in new[] { "ERP", "POS" })
            await _platform.PutOkAsync($"api/admin/organizations/{orgId}/products/{product}", new { autoAssignUsers = true });

        await _platform.PostOkAsync("api/admin/users", new
        {
            organizationId = orgId, email = $"admin@{slug}.test", fullName = "Admin", password = Password, role = "OrgAdmin",
        });
        await _platform.PostOkAsync("api/admin/users", new
        {
            organizationId = orgId, email = $"caja@{slug}.test", fullName = "Caja", password = Password,
        });

        var orgAdmin = await _factory.AdminClientAsync($"admin@{slug}.test", Password);
        return (orgId, orgAdmin, await ProductHttpAsync("POS", $"caja@{slug}.test"), await ProductHttpAsync("ERP", $"caja@{slug}.test"));
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
