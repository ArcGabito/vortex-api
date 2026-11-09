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
    public async Task Exchange_rate_in_force_on_a_date_is_shared_through_the_feed()
    {
        await _erp.PostOkAsync("api/master/exchange-rates", new
        {
            date = "2026-11-05", fromCurrency = "usd", toCurrency = "pen", rate = 3.765m, buyRate = 3.761m, sellRate = 3.769m, source = "sunat",
        });
        await _erp.PostOkAsync("api/master/exchange-rates", new { date = "2026-11-06", fromCurrency = "USD", toCurrency = "PEN", rate = 3.772m });

        // El sábado no hay publicación: vale la del viernes.
        var saturday = await (await _pos.GetAsync("api/master/exchange-rates/latest?from=USD&to=PEN&date=2026-11-07")).ReadJsonAsync();
        Assert.Equal(3.772m, saturday.GetProperty("rate").GetDecimal());
        var thursday = (await (await _pos.GetAsync("api/master/exchange-rates/latest?from=usd&to=pen&date=2026-11-05")).ReadJsonAsync())
            .Deserialize<VortexExchangeRate>(VortexJson.Options)!;
        Assert.Equal("SUNAT", thursday.Source);
        Assert.Equal(376.9m, thursday.Convert(100, VortexRateSide.Sell));
        Assert.Equal(100m, decimal.Round(thursday.ConvertBack(376.5m), 2));

        var none = await _pos.GetAsync("api/master/exchange-rates/latest?from=EUR&to=PEN");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        // Un solo tipo de cambio por par y día; corregirlo es actualizar el existente.
        var duplicate = await _erp.PostAsJsonAsync("api/master/exchange-rates", new { date = "2026-11-06", fromCurrency = "USD", toCurrency = "PEN", rate = 3.8m });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var same = await _erp.PostAsJsonAsync("api/master/exchange-rates", new { date = "2026-11-06", fromCurrency = "PEN", toCurrency = "PEN", rate = 1m });
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);

        var feed = await GetFeedAsync(_pos, 0, types: "exchange_rate");
        Assert.Equal(2, feed.GetProperty("changes").GetArrayLength());
        Assert.All(feed.GetProperty("changes").EnumerateArray(), c => Assert.Equal("exchange_rate", c.GetProperty("entityType").GetString()));
    }

    [Fact]
    public async Task Credit_terms_and_account_owner_survive_updates_from_systems_that_do_not_send_them()
    {
        var owner = Guid.NewGuid();
        var created = await _erp.PostOkAsync("api/master/parties", new
        {
            name = "Ferretería Lima", documentType = "RUC", documentNumber = "20111222333",
            credit = new { paymentTermDays = 30, creditLimit = 5000m, discountPercent = 5m },
        });
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(30, created.GetProperty("credit").GetProperty("paymentTermDays").GetInt32());

        // El POS (que no conoce las condiciones) corrige el teléfono: el crédito queda como estaba.
        await _pos.PutOkAsync($"api/master/parties/{id}", new { name = "Ferretería Lima", documentType = "RUC", documentNumber = "20111222333", phone = "999 111 222" });
        // El CRM asigna el responsable sin tocar el crédito.
        var updated = await _erp.PutOkAsync($"api/master/parties/{id}", new
        {
            name = "Ferretería Lima", documentType = "RUC", documentNumber = "20111222333", phone = "999 111 222",
            account = new { ownerUserId = owner, ownerName = "Luis Demo" },
        });
        Assert.Equal(5000m, updated.GetProperty("credit").GetProperty("creditLimit").GetDecimal());
        Assert.Equal(owner, updated.GetProperty("account").GetProperty("ownerUserId").GetGuid());

        // Llega así por el feed a los SDK.
        var party = (await (await _pos.GetAsync($"api/master/parties/{id}")).ReadJsonAsync()).Deserialize<VortexParty>(VortexJson.Options)!;
        Assert.Equal(new VortexPartyCredit(30, 5000m, 5m), party.Credit);
        Assert.Equal("Luis Demo", party.Account!.OwnerName);

        // Mandar el bloque vacío sí lo limpia (sin límite, sin descuento, contado sin definir).
        var cleared = await _erp.PutOkAsync($"api/master/parties/{id}", new { name = "Ferretería Lima", documentType = "RUC", documentNumber = "20111222333", credit = new { } });
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("credit").ValueKind);

        var bad = await _erp.PostAsJsonAsync("api/master/parties", new { name = "X", credit = new { discountPercent = 120m } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Price_lists_resolve_by_customer_then_store_with_quantity_breaks()
    {
        var coffee = (await _erp.PostOkAsync("api/master/items", new { sku = "cafe-pl", name = "Café", price = 20m })).GetProperty("id").GetGuid();
        var sugar = (await _erp.PostOkAsync("api/master/items", new { sku = "azucar-pl", name = "Azúcar", price = 5m })).GetProperty("id").GetGuid();
        var store = Guid.NewGuid();
        var wholesale = await _erp.PostOkAsync("api/master/price-lists", new
        {
            code = "mayorista", name = "Mayorista", currencyCode = "pen",
            lines = new[] { new { itemId = coffee, price = 18m, minQuantity = 1m }, new { itemId = coffee, price = 16.5m, minQuantity = 12m } },
        });
        Assert.Equal("MAYORISTA", wholesale.GetProperty("code").GetString());
        var storeList = await _erp.PostOkAsync("api/master/price-lists", new
        {
            code = "tienda-sur", name = "Tienda Sur", currencyCode = "PEN", siteIds = new[] { store },
            validFrom = "2026-11-01", lines = new[] { new { itemId = sugar, price = 4.8m } },
        });

        // El cliente lleva su lista en el bloque de condiciones comerciales.
        var listId = wholesale.GetProperty("id").GetGuid();
        var party = await _erp.PostOkAsync("api/master/parties", new { name = "Bodega Sur", credit = new { priceListId = listId } });
        Assert.Equal(listId, party.GetProperty("credit").GetProperty("priceListId").GetGuid());
        var unknown = await _erp.PostAsJsonAsync("api/master/parties", new { name = "X", credit = new { priceListId = Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // Errores: código repetido, artículo inexistente, dos precios para la misma cantidad.
        Assert.Equal(HttpStatusCode.Conflict, (await _pos.PostAsJsonAsync("api/master/price-lists", new { code = "MAYORISTA", name = "Otra", currencyCode = "PEN" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _pos.PostAsJsonAsync("api/master/price-lists",
            new { code = "x1", name = "X", currencyCode = "PEN", lines = new[] { new { itemId = Guid.NewGuid(), price = 1m } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _pos.PostAsJsonAsync("api/master/price-lists",
            new { code = "x2", name = "X", currencyCode = "PEN", lines = new[] { new { itemId = coffee, price = 1m }, new { itemId = coffee, price = 2m } } })).StatusCode);

        // Llegan por el feed y el SDK resuelve: cliente > tienda > artículo, con escalas.
        var feed = await GetFeedAsync(_pos, 0, types: "price_list");
        var lists = feed.GetProperty("changes").EnumerateArray()
            .Select(c => c.GetProperty("data").Deserialize<VortexPriceList>(VortexJson.Options)!).ToList();
        Assert.Equal(2, lists.Count);
        var today = new DateOnly(2026, 11, 8);
        var forCustomer = VortexPriceLists.Choose(lists, listId, store, today)!;
        Assert.Equal((18m, 16.5m, (decimal?)null), (forCustomer.PriceFor(coffee, 2)!.Value, forCustomer.PriceFor(coffee, 12)!.Value, forCustomer.PriceFor(sugar)));
        Assert.Equal(4.8m, VortexPriceLists.Choose(lists, null, store, today)!.PriceFor(sugar, 0.5m));
        Assert.Null(VortexPriceLists.Choose(lists, null, store, new DateOnly(2026, 10, 31)));   // aún no vigente
        Assert.Equal(storeList.GetProperty("id").GetGuid(), VortexPriceLists.Choose(lists, Guid.NewGuid(), store, today)!.Id);
    }

    [Fact]
    public async Task Item_presentations_are_found_by_any_barcode_and_reach_the_SDK()
    {
        var item = await _erp.PostOkAsync("api/master/items", new
        {
            sku = "agua-625", name = "Agua 625 ml", barcode = "7750000000017", unitCode = "niu", weightKg = 0.65m,
            tracking = "Lot",
            presentations = new[]
            {
                new { code = "pack6", name = "Paquete × 6", factor = 6m, barcode = "17750000000014", unitCode = "pk" },
                new { code = "caja", name = "Caja × 24", factor = 24m, barcode = "27750000000011", unitCode = "box" },
            },
        });
        Assert.Equal("NIU", item.GetProperty("unitCode").GetString());
        Assert.Equal("Lot", item.GetProperty("tracking").GetString());
        Assert.Equal("PACK6", item.GetProperty("presentations")[0].GetProperty("code").GetString());

        // El escáner del WMS lee la caja: el hub devuelve el artículo y el SDK reconoce la presentación.
        var page = await (await _pos.GetAsync("api/master/items?barcode=27750000000011")).ReadJsonAsync();
        var found = Assert.Single(page.GetProperty("items").Deserialize<List<VortexItem>>(VortexJson.Options)!);
        Assert.Equal(item.GetProperty("id").GetGuid(), found.Id);
        Assert.Equal(24m, found.PresentationFor("27750000000011")!.Factor);
        Assert.Null(found.PresentationFor("7750000000017"));
        Assert.Equal("Lot", found.Tracking);

        // Un servicio no lleva inventario, así que tampoco lote ni serie.
        var service = await _erp.PostOkAsync("api/master/items", new { sku = "flete", name = "Flete", kind = "Service", trackInventory = false, tracking = "Serial" });
        Assert.Equal("None", service.GetProperty("tracking").GetString());

        var repeated = await _erp.PostAsJsonAsync("api/master/items", new
        {
            sku = "x-2", name = "X", barcode = "111",
            presentations = new[] { new { code = "caja", name = "Caja", factor = 12m, barcode = "111" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
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
