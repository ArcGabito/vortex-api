using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VortexApi.Client;

namespace VortexApi.Tests;

public sealed class LicensingFlowTests : IAsyncLifetime
{
    private const string UserPassword = "User-Pass-2026!";
    private const string DeviceA = "device-aaaaaaaaaaaaaaaa";
    private const string DeviceB = "device-bbbbbbbbbbbbbbbb";

    private readonly VortexApiFactory _factory = new();
    private HttpClient _admin = null!;
    private Guid _orgId;
    private Guid _licenseId;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        _admin = await _factory.AdminClientAsync();
        _orgId = (await _admin.PostOkAsync("api/admin/organizations", new { name = "Acme SA", taxId = "J-123" }))
            .GetProperty("id").GetGuid();
        _licenseId = (await CreateLicenseAsync(_orgId, "ERP", seats: 1, devices: 1)).GetProperty("license").GetProperty("id").GetGuid();
        _userId = await CreateUserAsync(_orgId, "ana@acme.test");
        await _admin.PostOkAsync($"api/admin/licenses/{_licenseId}/seats", new { userId = _userId });
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Login_with_valid_license_issues_tokens_usable_online_and_offline()
    {
        var client = ProductClient("ERP");
        var session = await client.LoginAsync("ana@acme.test", UserPassword, DeviceA, "Caja 1", "windows");

        Assert.Equal("ERP", session.License.Product);
        Assert.Contains("ventas", session.License.Features);

        var heartbeat = await client.HeartbeatAsync(session.AccessToken);
        Assert.Equal(_licenseId, heartbeat.License.LicenseId);

        var jwks = await client.GetSigningKeysAsync();
        var validator = new OfflineLicenseValidator(jwks, VortexApiFactory.Issuer);

        var ok = await validator.ValidateAsync(heartbeat.LicenseToken, "ERP", DeviceA);
        Assert.True(ok.IsValid, ok.Error);
        Assert.Equal(_licenseId, ok.License!.LicenseId);
        Assert.True(ok.License.HasFeature("ventas"));

        Assert.False((await validator.ValidateAsync(heartbeat.LicenseToken, "ERP", DeviceB)).IsValid);
        Assert.False((await validator.ValidateAsync(heartbeat.LicenseToken, "CRM", DeviceA)).IsValid);
        Assert.False((await validator.ValidateAsync(heartbeat.LicenseToken[..^4] + "AAAA", "ERP", DeviceA)).IsValid);
    }

    [Fact]
    public async Task License_token_cannot_be_used_as_access_token()
    {
        var session = await ProductClient("ERP").LoginAsync("ana@acme.test", UserPassword, DeviceA);
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.LicenseToken);
        var response = await http.PostAsync("api/licensing/heartbeat", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Product_token_cannot_call_admin_endpoints()
    {
        var session = await ProductClient("ERP").LoginAsync("ana@acme.test", UserPassword, DeviceA);
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var response = await http.GetAsync("api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("ERP", "wrong-key")]
    [InlineData("ERP", null)]
    [InlineData("XYZ", "test-erp-key")]
    public async Task Login_rejects_unknown_application(string product, string? key)
    {
        var client = new VortexLicensingClient(_factory.CreateClient(),
            new VortexLicensingOptions { ProductCode = product, ProductKey = key ?? "" });
        var ex = await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync("ana@acme.test", UserPassword, DeviceA));
        Assert.Equal("invalid_product", ex.Code);
    }

    [Fact]
    public async Task Login_to_product_without_license_is_denied()
    {
        var ex = await Assert.ThrowsAsync<VortexApiException>(() =>
            ProductClient("CRM").LoginAsync("ana@acme.test", UserPassword, DeviceA));
        Assert.Equal("no_license", ex.Code);
    }

    [Fact]
    public async Task Seats_are_limited_and_users_without_seat_are_denied()
    {
        var other = await CreateUserAsync(_orgId, "beto@acme.test");

        var assign = await _admin.PostAsJsonAsync($"api/admin/licenses/{_licenseId}/seats", new { userId = other });
        Assert.Equal(HttpStatusCode.Conflict, assign.StatusCode);
        Assert.Equal("seat_limit_reached", await assign.ErrorCodeAsync());

        var ex = await Assert.ThrowsAsync<VortexApiException>(() =>
            ProductClient("ERP").LoginAsync("beto@acme.test", UserPassword, DeviceA));
        Assert.Equal("no_seat", ex.Code);

        // Liberar el puesto de Ana permite asignarlo a Beto.
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"api/admin/licenses/{_licenseId}/seats/{_userId}")).StatusCode);
        await _admin.PostOkAsync($"api/admin/licenses/{_licenseId}/seats", new { userId = other });
        await ProductClient("ERP").LoginAsync("beto@acme.test", UserPassword, DeviceA);
    }

    [Fact]
    public async Task Device_limit_is_enforced_until_admin_releases_a_device()
    {
        var client = ProductClient("ERP");
        var first = await client.LoginAsync("ana@acme.test", UserPassword, DeviceA);
        await client.LoginAsync("ana@acme.test", UserPassword, DeviceA); // mismo equipo: no consume cupo

        var ex = await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync("ana@acme.test", UserPassword, DeviceB));
        Assert.Equal("device_limit_reached", ex.Code);

        var revoke = await _admin.DeleteAsync($"api/admin/licenses/{_licenseId}/devices/{first.License.DeviceActivationId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // El equipo revocado pierde la sesión de inmediato.
        var hb = await Assert.ThrowsAsync<VortexApiException>(() => client.HeartbeatAsync(first.AccessToken));
        Assert.Equal(401, hb.StatusCode);
        var refresh = await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(first.RefreshToken, DeviceA));
        Assert.Equal(401, refresh.StatusCode);

        await client.LoginAsync("ana@acme.test", UserPassword, DeviceB);
    }

    [Fact]
    public async Task Suspending_license_blocks_existing_sessions_and_new_logins()
    {
        var client = ProductClient("ERP");
        var session = await client.LoginAsync("ana@acme.test", UserPassword, DeviceA);

        await _admin.PostOkAsync($"api/admin/licenses/{_licenseId}/suspend", new { reason = "Falta de pago" });

        Assert.Equal(401, (await Assert.ThrowsAsync<VortexApiException>(() => client.HeartbeatAsync(session.AccessToken))).StatusCode);
        Assert.Equal("license_suspended",
            (await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(session.RefreshToken, DeviceA))).Code);
        Assert.Equal("license_suspended",
            (await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync("ana@acme.test", UserPassword, DeviceA))).Code);

        await _admin.PostOkAsync($"api/admin/licenses/{_licenseId}/reactivate", new { });
        await client.LoginAsync("ana@acme.test", UserPassword, DeviceA);
    }

    [Fact]
    public async Task Expired_license_is_denied_and_offline_token_never_outlives_license()
    {
        var client = ProductClient("ERP");
        var session = await client.LoginAsync("ana@acme.test", UserPassword, DeviceA);
        Assert.True(session.LicenseTokenExpiresAt <= session.License.ExpiresAt);

        _factory.Clock.Advance(TimeSpan.FromDays(400));
        var ex = await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync("ana@acme.test", UserPassword, DeviceA));
        Assert.Equal("license_expired", ex.Code);

        var validator = new OfflineLicenseValidator(await client.GetSigningKeysAsync(), VortexApiFactory.Issuer);
        var offline = await validator.ValidateAsync(session.LicenseToken, "ERP", DeviceA, _factory.Clock);
        Assert.False(offline.IsValid);
    }

    [Fact]
    public async Task Refresh_rotates_tokens_and_reuse_revokes_the_session()
    {
        var client = ProductClient("ERP");
        var s1 = await client.LoginAsync("ana@acme.test", UserPassword, DeviceA);

        var mismatch = await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(s1.RefreshToken, DeviceB));
        Assert.Equal("device_mismatch", mismatch.Code);

        var s0 = await client.LoginAsync("ana@acme.test", UserPassword, DeviceA);
        var s2 = await client.RefreshAsync(s0.RefreshToken, DeviceA);
        Assert.NotEqual(s0.RefreshToken, s2.RefreshToken);

        var reuse = await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(s0.RefreshToken, DeviceA));
        Assert.Equal("refresh_token_reused", reuse.Code);
        var afterReuse = await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(s2.RefreshToken, DeviceA));
        Assert.Equal("invalid_refresh_token", afterReuse.Code);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        var client = ProductClient("ERP");
        for (var i = 0; i < 5; i++)
            Assert.Equal("invalid_credentials",
                (await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync("ana@acme.test", "Wrong-Pass-1!", DeviceA))).Code);

        var locked = await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync("ana@acme.test", UserPassword, DeviceA));
        Assert.Equal("account_locked", locked.Code);

        _factory.Clock.Advance(TimeSpan.FromMinutes(16));
        await client.LoginAsync("ana@acme.test", UserPassword, DeviceA);
    }

    [Fact]
    public async Task Deactivating_user_invalidates_access_and_refresh_tokens()
    {
        var client = ProductClient("ERP");
        var session = await client.LoginAsync("ana@acme.test", UserPassword, DeviceA);

        var update = await _admin.PutAsJsonAsync($"api/admin/users/{_userId}",
            new { fullName = "Ana", role = "User", isActive = false }, VortexApiFactory.Json);
        update.EnsureSuccessStatusCode();

        Assert.Equal(401, (await Assert.ThrowsAsync<VortexApiException>(() => client.HeartbeatAsync(session.AccessToken))).StatusCode);
        Assert.Equal(401, (await Assert.ThrowsAsync<VortexApiException>(() => client.RefreshAsync(session.RefreshToken, DeviceA))).StatusCode);
        Assert.Equal("account_disabled",
            (await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync("ana@acme.test", UserPassword, DeviceA))).Code);
    }

    [Fact]
    public async Task Org_admin_is_scoped_to_own_organization()
    {
        var otherOrg = (await _admin.PostOkAsync("api/admin/organizations", new { name = "Globex" })).GetProperty("id").GetGuid();
        var otherLicense = (await CreateLicenseAsync(otherOrg, "POS", 5, 1)).GetProperty("license").GetProperty("id").GetGuid();

        await _admin.PostOkAsync("api/admin/users", new
        {
            organizationId = _orgId, email = "admin@acme.test", fullName = "Admin Acme", password = UserPassword, role = "OrgAdmin",
        });
        var orgAdmin = await _factory.AdminClientAsync("admin@acme.test", UserPassword);

        var licenses = await (await orgAdmin.GetAsync("api/admin/licenses")).ReadJsonAsync();
        Assert.Equal(1, licenses.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await orgAdmin.GetAsync($"api/admin/licenses/{otherLicense}")).StatusCode);

        var create = await orgAdmin.PostAsJsonAsync("api/admin/licenses", new
        {
            organizationId = _orgId, productCode = "CRM", plan = "pro", maxSeats = 100, maxDevicesPerUser = 5,
            offlineGraceDays = 3, expiresAt = DateTimeOffset.UtcNow.AddYears(5),
        });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        var escalate = await orgAdmin.PostAsJsonAsync("api/admin/users", new
        {
            email = "evil@acme.test", fullName = "Evil", password = UserPassword, role = "PlatformAdmin",
        });
        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);

        // Usuario creado por el OrgAdmin queda en su organización aunque indique otra.
        var created = await orgAdmin.PostOkAsync("api/admin/users", new
        {
            organizationId = otherOrg, email = "carla@acme.test", fullName = "Carla", password = UserPassword,
        });
        Assert.Equal(_orgId, created.GetProperty("organizationId").GetGuid());

        // Un usuario normal no puede entrar a la consola.
        var userLogin = await _factory.CreateClient().PostAsJsonAsync("api/admin/auth/login",
            new { email = "ana@acme.test", password = UserPassword });
        Assert.Equal(HttpStatusCode.Forbidden, userLogin.StatusCode);
    }

    [Fact]
    public async Task Audit_log_records_successful_and_failed_logins()
    {
        var client = ProductClient("ERP");
        await client.LoginAsync("ana@acme.test", UserPassword, DeviceA);
        await Assert.ThrowsAsync<VortexApiException>(() => client.LoginAsync("ana@acme.test", "Wrong-Pass-1!", DeviceA));

        var audit = await (await _admin.GetAsync("api/admin/audit?action=product.login")).ReadJsonAsync();
        var items = audit.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("success").GetBoolean());
        Assert.Contains(items, i => !i.GetProperty("success").GetBoolean());
    }

    private VortexLicensingClient ProductClient(string product) =>
        new(_factory.CreateClient(), new VortexLicensingOptions
        {
            ProductCode = product, ProductKey = VortexApiFactory.ProductKeys[product],
        });

    private Task<JsonElement> CreateLicenseAsync(Guid orgId, string product, int seats, int devices) =>
        _admin.PostOkAsync("api/admin/licenses", new
        {
            organizationId = orgId, productCode = product, plan = "business", maxSeats = seats,
            maxDevicesPerUser = devices, offlineGraceDays = 3,
            expiresAt = _factory.Clock.GetUtcNow().AddYears(1), features = new[] { "ventas", "Inventario" },
        });

    private async Task<Guid> CreateUserAsync(Guid orgId, string email) =>
        (await _admin.PostOkAsync("api/admin/users", new
        {
            organizationId = orgId, email, fullName = email.Split('@')[0], password = UserPassword,
        })).GetProperty("id").GetGuid();
}
