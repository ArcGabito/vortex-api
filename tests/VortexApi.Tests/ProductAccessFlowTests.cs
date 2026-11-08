using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VortexApi.Client;

namespace VortexApi.Tests;

public sealed class ProductAccessFlowTests : IAsyncLifetime
{
    private const string UserPassword = "User-Pass-2026!";
    private const string DeviceA = "device-aaaaaaaaaaaaaaaa";
    private const string DeviceB = "device-bbbbbbbbbbbbbbbb";
    private const string Ana = "ana@acme.test";

    private readonly VortexApiFactory _factory = new();
    private HttpClient _admin = null!;
    private Guid _orgId;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        _admin = await _factory.AdminClientAsync();
        _orgId = (await _admin.PostOkAsync("api/admin/organizations", new { name = "Acme SA", taxId = "J-123" }))
            .GetProperty("id").GetGuid();
        await EnableAsync("ERP", new { features = new[] { "contabilidad", "Nomina" } });
        _userId = await CreateUserAsync(Ana);
        await AddMemberAsync("ERP", _userId, "contador");
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Login_issues_tokens_usable_online_and_offline()
    {
        var client = Client("ERP");
        var session = await client.LoginAsync(Ana, UserPassword, DeviceA, "Laptop Ana", "windows");

        Assert.Equal("ERP", session.Access.Product);
        Assert.Equal("contador", session.Access.Role);
        Assert.True(session.Access.HasFeature("nomina"));
        Assert.Null(session.Access.ExpiresAt);

        var heartbeat = await client.HeartbeatAsync(session.AccessToken);
        var validator = new OfflineAccessValidator(await client.GetSigningKeysAsync(), VortexApiFactory.Issuer);

        var ok = await validator.ValidateAsync(heartbeat.OfflineToken, "ERP", DeviceA);
        Assert.True(ok.IsValid, ok.Error);
        Assert.Equal("contador", ok.Access!.Role);
        Assert.True(ok.Access.HasFeature("contabilidad"));

        Assert.False((await validator.ValidateAsync(heartbeat.OfflineToken, "ERP", DeviceB)).IsValid);
        Assert.False((await validator.ValidateAsync(heartbeat.OfflineToken, "CRM", DeviceA)).IsValid);
        Assert.False((await validator.ValidateAsync(heartbeat.OfflineToken[..^4] + "AAAA", "ERP", DeviceA)).IsValid);
    }

    [Fact]
    public async Task Preferred_language_is_saved_in_the_hub_and_travels_in_the_session()
    {
        var erp = Client("ERP");
        var session = await erp.LoginAsync(Ana, UserPassword, DeviceA);
        Assert.Null(session.User.Language);

        // Ana elige portugués en el ERP; no se cierra su sesión y el próximo login ya lo trae.
        var user = await erp.SetLanguageAsync(session.AccessToken, "pt-BR");
        Assert.Equal("pt", user.Language);
        Assert.NotNull(await erp.HeartbeatAsync(session.AccessToken));
        Assert.Equal("pt", (await erp.RefreshAsync(session.RefreshToken, DeviceA)).User.Language);

        var invalid = await Assert.ThrowsAsync<VortexApiException>(() => erp.SetLanguageAsync(session.AccessToken, "fr"));
        Assert.Equal("validation_error", invalid.Code);

        Assert.Null((await erp.SetLanguageAsync(session.AccessToken, null)).Language);
    }

    [Fact]
    public async Task Pin_is_hashed_and_its_verifier_reaches_the_other_members_of_the_system()
    {
        var bobId = await CreateUserAsync("bob@acme.test");
        await AddMemberAsync("ERP", bobId, "facturador");
        var erp = Client("ERP");
        var ana = await erp.LoginAsync(Ana, UserPassword, DeviceA);
        var bob = await erp.LoginAsync("bob@acme.test", UserPassword, DeviceB);

        var weak = await Assert.ThrowsAsync<VortexApiException>(() => erp.SetPinAsync(ana.AccessToken, UserPassword, "1234"));
        Assert.Equal("validation_error", weak.Code);
        var wrongPassword = await Assert.ThrowsAsync<VortexApiException>(() => erp.SetPinAsync(ana.AccessToken, "otra", "482915"));
        Assert.Equal("invalid_credentials", wrongPassword.Code);

        await erp.SetPinAsync(ana.AccessToken, UserPassword, "482915");
        Assert.DoesNotContain(await erp.GetPinVerifiersAsync(ana.AccessToken), v => v.UserId == bobId);   // Bob no tiene PIN

        // Bob, desde su equipo, recibe el verificador de Ana (nunca el PIN) y lo valida sin conexión.
        var verifier = Assert.Single(await erp.GetPinVerifiersAsync(bob.AccessToken));
        Assert.Equal((_userId, "contador"), (verifier.UserId, verifier.Role));
        Assert.DoesNotContain("482915", verifier.PinHash);
        Assert.True(VortexPin.Verify("482915", verifier.PinHash));
        Assert.False(VortexPin.Verify("482916", verifier.PinHash));

        await erp.SetPinAsync(ana.AccessToken, UserPassword, null);
        Assert.Empty(await erp.GetPinVerifiersAsync(bob.AccessToken));
    }

    [Fact]
    public async Task Offline_token_cannot_be_used_as_access_token()
    {
        var session = await Client("ERP").LoginAsync(Ana, UserPassword, DeviceA);
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.OfflineToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("api/access/heartbeat", null)).StatusCode);
    }

    [Fact]
    public async Task Product_token_cannot_call_admin_endpoints()
    {
        var session = await Client("ERP").LoginAsync(Ana, UserPassword, DeviceA);
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("api/admin/users")).StatusCode);
    }

    [Theory]
    [InlineData("ERP", "wrong-key")]
    [InlineData("ERP", "")]
    [InlineData("XYZ", "test-erp-key")]
    public async Task Login_rejects_unknown_system(string product, string key)
    {
        var client = new VortexApiClient(_factory.CreateClient(), new VortexApiClientOptions { ProductCode = product, ProductKey = key });
        var ex = await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync(Ana, UserPassword, DeviceA));
        Assert.Equal("invalid_product", ex.Code);
    }

    [Fact]
    public async Task Login_to_system_not_enabled_for_organization_is_denied()
    {
        var ex = await Assert.ThrowsAsync<VortexApiException>(() => Client("CRM").LoginAsync(Ana, UserPassword, DeviceA));
        Assert.Equal("product_not_enabled", ex.Code);
    }

    [Fact]
    public async Task Organization_can_use_several_systems_and_switch_between_them()
    {
        await EnableAsync("CRM", new { });
        await AddMemberAsync("CRM", _userId, "vendedor");

        var erp = await Client("ERP").LoginAsync(Ana, UserPassword, DeviceA);
        var crm = await Client("CRM").LoginAsync(Ana, UserPassword, DeviceA);
        Assert.Equal("vendedor", crm.Access.Role);

        var available = await Client("ERP").GetAvailableProductsAsync(erp.AccessToken);
        Assert.Equal(["CRM", "ERP"], available.Select(p => p.Code));

        // Reemplazar ERP por CRM: se deshabilita ERP; el usuario y su acceso a CRM siguen intactos.
        await EnableAsync("ERP", new { isEnabled = false, disabledReason = "Migró a CRM" });

        Assert.Equal(401, (await Assert.ThrowsAsync<VortexApiException>(() => Client("ERP").HeartbeatAsync(erp.AccessToken))).StatusCode);
        Assert.Equal("product_disabled",
            (await Assert.ThrowsAsync<VortexApiException>(() => Client("ERP").LoginAsync(Ana, UserPassword, DeviceA))).Code);
        await Client("CRM").HeartbeatAsync(crm.AccessToken);
        Assert.Equal(["CRM"], (await Client("CRM").GetAvailableProductsAsync(crm.AccessToken)).Select(p => p.Code));

        // Volver a habilitar ERP conserva miembros y roles.
        await EnableAsync("ERP", new { });
        var back = await Client("ERP").LoginAsync(Ana, UserPassword, DeviceA);
        Assert.Equal("contador", back.Access.Role);
    }

    [Fact]
    public async Task Users_need_access_unless_auto_assign_is_enabled()
    {
        await CreateUserAsync("beto@acme.test");
        var ex = await Assert.ThrowsAsync<VortexApiException>(() => Client("ERP").LoginAsync("beto@acme.test", UserPassword, DeviceA));
        Assert.Equal("no_access", ex.Code);

        await EnableAsync("ERP", new { autoAssignUsers = true, maxUsers = 2 });
        var session = await Client("ERP").LoginAsync("beto@acme.test", UserPassword, DeviceA);
        Assert.Null(session.Access.Role);

        await CreateUserAsync("carla@acme.test");
        var limit = await Assert.ThrowsAsync<VortexApiException>(() => Client("ERP").LoginAsync("carla@acme.test", UserPassword, DeviceA));
        Assert.Equal("user_limit_reached", limit.Code);
    }

    [Fact]
    public async Task Limits_are_optional_and_enforced_only_when_configured()
    {
        var client = Client("ERP");
        for (var i = 0; i < 5; i++)
            await client.LoginAsync(Ana, UserPassword, $"device-unlimited-{i:0000000000}");

        var reduce = await _admin.PutAsJsonAsync($"api/admin/organizations/{_orgId}/products/ERP",
            new { maxDevicesPerUser = 2 }, VortexApiFactory.Json);
        Assert.Equal(HttpStatusCode.Conflict, reduce.StatusCode);
        Assert.Equal("limit_in_use", await reduce.ErrorCodeAsync());

        await EnableAsync("ERP", new { maxUsers = 1 });
        var other = await CreateUserAsync("beto@acme.test");
        var add = await _admin.PutAsJsonAsync($"api/admin/organizations/{_orgId}/products/ERP/members/{other}",
            new { role = "cajero" }, VortexApiFactory.Json);
        Assert.Equal(HttpStatusCode.Conflict, add.StatusCode);
        Assert.Equal("user_limit_reached", await add.ErrorCodeAsync());
    }

    [Fact]
    public async Task Device_limit_is_enforced_until_admin_releases_a_device()
    {
        await EnableAsync("ERP", new { maxDevicesPerUser = 1 });
        var client = Client("ERP");
        var first = await client.LoginAsync(Ana, UserPassword, DeviceA);
        await client.LoginAsync(Ana, UserPassword, DeviceA); // mismo equipo: no consume cupo

        var ex = await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync(Ana, UserPassword, DeviceB));
        Assert.Equal("device_limit_reached", ex.Code);

        var revoke = await _admin.DeleteAsync($"api/admin/organizations/{_orgId}/products/ERP/devices/{first.Access.DeviceId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        Assert.Equal(401, (await Assert.ThrowsAsync<VortexApiException>(() => client.HeartbeatAsync(first.AccessToken))).StatusCode);
        Assert.Equal(401, (await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(first.RefreshToken, DeviceA))).StatusCode);

        await client.LoginAsync(Ana, UserPassword, DeviceB);
    }

    [Fact]
    public async Task Access_without_expiration_never_expires_but_temporary_access_does()
    {
        var client = Client("ERP");
        _factory.Clock.Advance(TimeSpan.FromDays(3650));
        await client.LoginAsync(Ana, UserPassword, DeviceA);

        await EnableAsync("ERP", new { expiresAt = _factory.Clock.GetUtcNow().AddDays(1), offlineGraceDays = 7 });
        var session = await client.LoginAsync(Ana, UserPassword, DeviceA);
        Assert.True(session.OfflineTokenExpiresAt <= session.Access.ExpiresAt);

        _factory.Clock.Advance(TimeSpan.FromDays(2));
        var ex = await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync(Ana, UserPassword, DeviceA));
        Assert.Equal("access_expired", ex.Code);

        var validator = new OfflineAccessValidator(await client.GetSigningKeysAsync(), VortexApiFactory.Issuer);
        Assert.False((await validator.ValidateAsync(session.OfflineToken, "ERP", DeviceA, _factory.Clock)).IsValid);
    }

    [Fact]
    public async Task Changing_member_role_closes_sessions_so_new_role_applies()
    {
        var client = Client("ERP");
        var session = await client.LoginAsync(Ana, UserPassword, DeviceA);

        await AddMemberAsync("ERP", _userId, "gerente");
        Assert.Equal(401, (await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(session.RefreshToken, DeviceA))).StatusCode);
        Assert.Equal("gerente", (await client.LoginAsync(Ana, UserPassword, DeviceA)).Access.Role);
    }

    [Fact]
    public async Task Removing_member_revokes_access()
    {
        var client = Client("ERP");
        var session = await client.LoginAsync(Ana, UserPassword, DeviceA);

        var remove = await _admin.DeleteAsync($"api/admin/organizations/{_orgId}/products/ERP/members/{_userId}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        Assert.Equal(401, (await Assert.ThrowsAsync<VortexApiException>(() => client.HeartbeatAsync(session.AccessToken))).StatusCode);
        Assert.Equal("no_access", (await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync(Ana, UserPassword, DeviceA))).Code);
    }

    [Fact]
    public async Task Refresh_rotates_tokens_and_reuse_revokes_the_session()
    {
        var client = Client("ERP");
        var s1 = await client.LoginAsync(Ana, UserPassword, DeviceA);
        Assert.Equal("device_mismatch",
            (await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(s1.RefreshToken, DeviceB))).Code);

        var s0 = await client.LoginAsync(Ana, UserPassword, DeviceA);
        var s2 = await client.RefreshAsync(s0.RefreshToken, DeviceA);
        Assert.NotEqual(s0.RefreshToken, s2.RefreshToken);

        Assert.Equal("refresh_token_reused",
            (await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(s0.RefreshToken, DeviceA))).Code);
        Assert.Equal("invalid_refresh_token",
            (await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(s2.RefreshToken, DeviceA))).Code);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        var client = Client("ERP");
        for (var i = 0; i < 5; i++)
            Assert.Equal("invalid_credentials",
                (await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync(Ana, "Wrong-Pass-1!", DeviceA))).Code);

        Assert.Equal("account_locked",
            (await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync(Ana, UserPassword, DeviceA))).Code);

        _factory.Clock.Advance(TimeSpan.FromMinutes(16));
        await client.LoginAsync(Ana, UserPassword, DeviceA);
    }

    [Fact]
    public async Task Deactivating_user_invalidates_access_and_refresh_tokens()
    {
        var client = Client("ERP");
        var session = await client.LoginAsync(Ana, UserPassword, DeviceA);

        await _admin.PutOkAsync($"api/admin/users/{_userId}", new { fullName = "Ana", role = "User", isActive = false });

        Assert.Equal(401, (await Assert.ThrowsAsync<VortexApiException>(() => client.HeartbeatAsync(session.AccessToken))).StatusCode);
        Assert.Equal(401, (await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(session.RefreshToken, DeviceA))).StatusCode);
        Assert.Equal("account_disabled",
            (await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync(Ana, UserPassword, DeviceA))).Code);
    }

    [Fact]
    public async Task Org_admin_is_scoped_to_own_organization()
    {
        var otherOrg = (await _admin.PostOkAsync("api/admin/organizations", new { name = "Globex" })).GetProperty("id").GetGuid();
        await _admin.PutOkAsync($"api/admin/organizations/{otherOrg}/products/POS", new { });

        await _admin.PostOkAsync("api/admin/users", new
        {
            organizationId = _orgId, email = "admin@acme.test", fullName = "Admin Acme", password = UserPassword, role = "OrgAdmin",
        });
        var orgAdmin = await _factory.AdminClientAsync("admin@acme.test", UserPassword);

        var products = await (await orgAdmin.GetAsync($"api/admin/organizations/{_orgId}/products")).ReadJsonAsync();
        Assert.Equal(1, products.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await orgAdmin.GetAsync($"api/admin/organizations/{otherOrg}/products/POS")).StatusCode);

        // Habilitar sistemas es de PlatformAdmin; administrar miembros sí lo puede hacer el OrgAdmin.
        var enable = await orgAdmin.PutAsJsonAsync($"api/admin/organizations/{_orgId}/products/CRM", new { });
        Assert.Equal(HttpStatusCode.Forbidden, enable.StatusCode);

        var created = await orgAdmin.PostOkAsync("api/admin/users", new
        {
            organizationId = otherOrg, email = "carla@acme.test", fullName = "Carla", password = UserPassword,
        });
        Assert.Equal(_orgId, created.GetProperty("organizationId").GetGuid());
        await orgAdmin.PutOkAsync($"api/admin/organizations/{_orgId}/products/ERP/members/{created.GetProperty("id").GetGuid()}",
            new { role = "auxiliar" });

        var escalate = await orgAdmin.PostAsJsonAsync("api/admin/users", new
        {
            email = "evil@acme.test", fullName = "Evil", password = UserPassword, role = "PlatformAdmin",
        });
        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);

        var userLogin = await _factory.CreateClient().PostAsJsonAsync("api/admin/auth/login", new { email = Ana, password = UserPassword });
        Assert.Equal(HttpStatusCode.Forbidden, userLogin.StatusCode);
    }

    [Fact]
    public async Task Member_sites_travel_in_the_session_and_the_offline_token()
    {
        var client = Client("ERP");
        var session = await client.LoginAsync(Ana, UserPassword, DeviceA);
        Assert.Empty(session.Access.SiteIds!);
        Assert.True(session.Access.CanUseSite(Guid.NewGuid()));

        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var central = (await http.PostOkAsync("api/master/sites", new { code = "ALM-01", name = "Central", kind = "Warehouse" }))
            .GetProperty("id").GetGuid();
        var other = (await http.PostOkAsync("api/master/sites", new { code = "TDA-01", name = "Tienda" })).GetProperty("id").GetGuid();

        var detail = await _admin.PutOkAsync($"api/admin/organizations/{_orgId}/products/ERP/members/{_userId}",
            new { role = "contador", siteIds = new[] { central } });
        Assert.Equal(central, detail.GetProperty("members")[0].GetProperty("siteIds")[0].GetGuid());

        var unknown = await _admin.PutAsJsonAsync($"api/admin/organizations/{_orgId}/products/ERP/members/{_userId}",
            new { role = "contador", siteIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // Cambiar las sedes cierra las sesiones: la nueva trae las sedes.
        var again = await client.LoginAsync(Ana, UserPassword, DeviceA);
        Assert.Equal([central], again.Access.SiteIds);
        Assert.True(again.Access.CanUseSite(central));
        Assert.False(again.Access.CanUseSite(other));

        var offline = await new OfflineAccessValidator(await client.GetSigningKeysAsync(), VortexApiFactory.Issuer)
            .ValidateAsync(again.OfflineToken, "ERP", DeviceA);
        Assert.True(offline.IsValid, offline.Error);
        Assert.Equal([central], offline.Access!.SiteIds);
    }

    [Fact]
    public async Task Audit_log_records_successful_and_failed_logins()
    {
        var client = Client("ERP");
        await client.LoginAsync(Ana, UserPassword, DeviceA);
        await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync(Ana, "Wrong-Pass-1!", DeviceA));

        var audit = await (await _admin.GetAsync("api/admin/audit?action=product.login")).ReadJsonAsync();
        var items = audit.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("success").GetBoolean());
        Assert.Contains(items, i => !i.GetProperty("success").GetBoolean());
    }

    private VortexApiClient Client(string product) =>
        new(_factory.CreateClient(), new VortexApiClientOptions { ProductCode = product, ProductKey = VortexApiFactory.ProductKeys[product] });

    private Task<JsonElement> EnableAsync(string product, object settings) =>
        _admin.PutOkAsync($"api/admin/organizations/{_orgId}/products/{product}", settings);

    private Task<JsonElement> AddMemberAsync(string product, Guid userId, string? role) =>
        _admin.PutOkAsync($"api/admin/organizations/{_orgId}/products/{product}/members/{userId}", new { role });

    private async Task<Guid> CreateUserAsync(string email) =>
        (await _admin.PostOkAsync("api/admin/users", new
        {
            organizationId = _orgId, email, fullName = email.Split('@')[0], password = UserPassword,
        })).GetProperty("id").GetGuid();
}
