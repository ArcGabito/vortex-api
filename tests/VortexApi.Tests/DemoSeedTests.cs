using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VortexApi.Client;
using VortexApi.Data;

namespace VortexApi.Tests;

/// <summary>Organización demo de desarrollo: los sistemas en construcción pueden iniciar sesión sin configurar nada.</summary>
public sealed class DemoSeedTests : IDisposable
{
    private const string Password = "Suite-Local-2026!";

    private readonly VortexApiFactory _factory = new(new Dictionary<string, string>
    {
        ["Seed:Demo:Password"] = Password,
        ["Seed:Demo:Users:0:Email"] = "ana@vortex.test",
        ["Seed:Demo:Users:0:FullName"] = "Ana Demo",
        ["Seed:Demo:Users:0:Role"] = "OrgAdmin",
        ["Seed:Demo:Users:1:Email"] = "luis@vortex.test",
        ["Seed:Demo:Users:1:FullName"] = "Luis Demo",
    });

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("ERP")]
    [InlineData("CRM")]
    [InlineData("WMS")]
    [InlineData("POS")]
    public async Task Demo_user_can_log_in_to_every_system(string product)
    {
        var client = new VortexApiClient(_factory.CreateClient(),
            new VortexApiClientOptions { ProductCode = product, ProductKey = VortexApiFactory.ProductKeys[product] });

        var session = await client.LoginAsync("luis@vortex.test", Password, $"device-demo-{product}".PadRight(20, '0'));

        Assert.Equal(product, session.Access.Product);
        Assert.Equal("Vortex Demo", session.Access.OrganizationName);
        Assert.Equal(4, (await client.GetAvailableProductsAsync(session.AccessToken)).Count);
    }

    [Fact]
    public async Task Demo_org_admin_can_manage_the_organization_and_seeding_is_idempotent()
    {
        var admin = await _factory.AdminClientAsync("ana@vortex.test", Password);
        await admin.PutOkAsync("api/master/company", new { legalName = "Vortex Demo SAC", taxId = "20000000001" });

        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<SeedOptions>>().Value;
        await DbSeeder.SeedAsync(scope.ServiceProvider, options, NullLogger.Instance);

        var db = scope.ServiceProvider.GetRequiredService<VortexDbContext>();
        Assert.Equal(1, await db.Organizations.CountAsync(o => o.Name == "Vortex Demo"));
        Assert.Equal(2, await db.Users.CountAsync(u => u.OrganizationId != null));
    }
}
