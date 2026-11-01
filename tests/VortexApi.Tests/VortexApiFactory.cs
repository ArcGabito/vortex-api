using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using VortexApi.Data;

namespace VortexApi.Tests;

public sealed class VortexApiFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "root@vortex.test";
    public const string AdminPassword = "Root-Pass-2026!";
    public const string Issuer = "https://licensing.vortex.test";
    public static readonly Dictionary<string, string> ProductKeys = new()
    {
        ["ERP"] = "test-erp-key", ["CRM"] = "test-crm-key", ["WMS"] = "test-wms-key", ["POS"] = "test-pos-key",
    };

    private static readonly string SigningKeyPem = RSA.Create(2048).ExportPkcs8PrivateKeyPem();
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Vortex", "unused");
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:SigningKeyPem", SigningKeyPem);
        builder.UseSetting("RateLimiting:AuthPermitsPerMinute", "10000");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        foreach (var (code, key) in ProductKeys)
            builder.UseSetting($"Seed:ProductKeys:{code}", key);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<VortexDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<VortexDbContext>>();
            services.AddDbContext<VortexDbContext>(o => o.UseSqlite(_connection));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }

    public async Task<HttpClient> AdminClientAsync(string email = AdminEmail, string password = AdminPassword)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("api/admin/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        return client;
    }
}

public static class HttpExtensions
{
    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(VortexApiFactory.Json);

    public static async Task<JsonElement> PostOkAsync(this HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, VortexApiFactory.Json);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.ReadJsonAsync();
    }

    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage response) =>
        (await response.ReadJsonAsync()).TryGetProperty("code", out var c) ? c.GetString() : null;
}
