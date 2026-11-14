using System.Net;
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
using VortexApi.Fiscal;
using VortexApi.Fiscal.Peru;
using VortexApi.Services.Webhooks;

namespace VortexApi.Tests;

public sealed class VortexApiFactory(IReadOnlyDictionary<string, string>? settings = null) : WebApplicationFactory<Program>
{
    public const string AdminEmail = "root@vortex.test";
    public const string AdminPassword = "Root-Pass-2026!";
    public const string Issuer = "https://hub.vortex.test";
    public static readonly Dictionary<string, string> ProductKeys = new()
    {
        ["ERP"] = "test-erp-key", ["CRM"] = "test-crm-key", ["WMS"] = "test-wms-key", ["POS"] = "test-pos-key",
    };

    private static readonly string SigningKeyPem = RSA.Create(2048).ExportPkcs8PrivateKeyPem();
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>Receptor de webhooks falso: registra cada POST y responde con <see cref="FakeWebhookReceiver.StatusCode"/>.</summary>
    public FakeWebhookReceiver Webhooks { get; } = new();

    /// <summary>SUNAT falsa: registra cada envío y responde lo que se le encole (por defecto, CDR aceptado).</summary>
    public FakeSunat Sunat { get; } = new();

    /// <summary>Ejecuta una pasada del procesador de comprobantes electrónicos.</summary>
    public async Task<int> ProcessFiscalAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FiscalService>().ProcessDueAsync();
    }

    /// <summary>Ejecuta una pasada del despachador de webhooks.</summary>
    public async Task<int> DeliverWebhooksAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WebhookDeliveryService>().DeliverDueAsync();
    }

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
        builder.UseSetting("RateLimiting:PublicPermitsPerMinute", "10000");
        builder.UseSetting("Webhooks:DispatcherEnabled", "false");
        builder.UseSetting("Webhooks:MaxAttempts", "3");
        builder.UseSetting("Fiscal:ProcessorEnabled", "false");
        builder.UseSetting("Fiscal:MaxAttempts", "3");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        foreach (var (code, key) in ProductKeys)
            builder.UseSetting($"Seed:ProductKeys:{code}", key);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            builder.UseSetting(key, value);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<VortexDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<VortexDbContext>>();
            services.AddDbContext<VortexDbContext>(o => o.UseSqlite(_connection));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddHttpClient(WebhookDeliveryService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Webhooks);
            services.RemoveAll<ISunatClient>();
            services.AddSingleton<ISunatClient>(Sunat);
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

public static class HttpPutExtensions
{
    public static async Task<JsonElement> PutOkAsync(this HttpClient client, string url, object body)
    {
        var response = await client.PutAsJsonAsync(url, body, VortexApiFactory.Json);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.ReadJsonAsync();
    }
}

public sealed class FakeWebhookReceiver : HttpMessageHandler
{
    public sealed record Received(Uri Url, Dictionary<string, string> Headers, string Body);

    public List<Received> Requests { get; } = [];
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        Requests.Add(new Received(request.RequestUri!, headers, await request.Content!.ReadAsStringAsync(ct)));
        return new HttpResponseMessage(StatusCode);
    }

    // El HttpClientFactory rota handlers; este es compartido y no debe liberarse.
    protected override void Dispose(bool disposing) { }
}

public sealed class FakeSunat : ISunatClient
{
    public sealed record Sent(FiscalEnvironment Environment, string Username, string Password, string FileName, string Xml);

    public List<Sent> Requests { get; } = [];
    public Queue<SendResult> Responses { get; } = new();

    public static SendResult Accepted(params string[] notes) =>
        new(SendOutcome.Responded, "0", "La Factura ha sido aceptada", notes, "<ApplicationResponse>CDR</ApplicationResponse>");

    public Task<SendResult> SendBillAsync(FiscalEnvironment environment, string username, string password, string fileName, byte[] zip,
        CancellationToken ct = default)
    {
        Requests.Add(new Sent(environment, username, password, fileName, SunatClient.ReadZip(zip)));
        return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : Accepted());
    }

    /// <summary>Respuestas de getStatus por encolar (por defecto, procesado con CDR aceptado).</summary>
    public Queue<TicketStatus> Tickets { get; } = new();
    public Queue<SendResult> CdrQueries { get; } = new();
    public List<string> StatusQueries { get; } = [];
    public List<(string Ruc, string Type, string Series, long Number)> ConsultedCdrs { get; } = [];

    public Task<SummaryResult> SendSummaryAsync(FiscalEnvironment environment, string username, string password, string fileName, byte[] zip,
        CancellationToken ct = default)
    {
        Requests.Add(new Sent(environment, username, password, fileName, SunatClient.ReadZip(zip)));
        return Task.FromResult(new SummaryResult(SendOutcome.Responded, $"T{Requests.Count:0000}", null, null));
    }

    public Task<TicketStatus> GetStatusAsync(FiscalEnvironment environment, string username, string password, string ticket, CancellationToken ct = default)
    {
        StatusQueries.Add(ticket);
        return Task.FromResult(Tickets.Count > 0 ? Tickets.Dequeue()
            : new TicketStatus(SendOutcome.Responded, "0", new SendResult(SendOutcome.Responded, "0", "La Comunicacion de baja ha sido aceptada", [], "<cdr/>"), null));
    }

    public Task<SendResult> GetStatusCdrAsync(FiscalEnvironment environment, string username, string password, string ruc, string typeCode, string series,
        long number, CancellationToken ct = default)
    {
        if (environment != FiscalEnvironment.Production)
            return Task.FromResult(new SendResult(SendOutcome.Error, null, "SUNAT solo ofrece la consulta del CDR en producción.", [], null));
        ConsultedCdrs.Add((ruc, typeCode, series, number));
        return Task.FromResult(CdrQueries.Count > 0 ? CdrQueries.Dequeue() : Accepted());
    }
}
