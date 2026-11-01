using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Domain;

namespace VortexApi.Services.Webhooks;

/// <summary>
/// Envía las entregas pendientes. Garantía <b>al menos una vez</b>: el receptor debe descartar duplicados por
/// <c>id</c> del evento. Los reintentos usan espera exponencial (30 s, 1 min, 2 min… hasta 6 h).
/// </summary>
public sealed class WebhookDeliveryService(
    VortexDbContext db,
    IHttpClientFactory httpFactory,
    TimeProvider clock,
    IOptions<WebhookOptions> options,
    IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> json,
    ILogger<WebhookDeliveryService> logger)
{
    public const string HttpClientName = "webhooks";
    public const string SignatureHeader = "X-Vortex-Signature";

    private readonly WebhookOptions _options = options.Value;

    /// <summary>Procesa un lote de entregas vencidas. Devuelve cuántas intentó.</summary>
    public async Task<int> DeliverDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAt <= now && (d.LockedUntil == null || d.LockedUntil < now))
            .OrderBy(d => d.Id).Select(d => d.Id).Take(_options.BatchSize).ToListAsync(ct);

        var attempted = 0;
        foreach (var id in due)
        {
            // Lease: solo una réplica gana el UPDATE condicional.
            var lockUntil = now.AddSeconds(_options.TimeoutSeconds * 3 + 30);
            var claimed = await db.WebhookDeliveries
                .Where(d => d.Id == id && d.Status == DeliveryStatus.Pending && (d.LockedUntil == null || d.LockedUntil < now))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LockedUntil, lockUntil), ct);
            if (claimed == 0)
                continue;

            var delivery = await db.WebhookDeliveries.Include(d => d.Subscription).Include(d => d.Event)
                .SingleAsync(d => d.Id == id, ct);
            await AttemptAsync(delivery, ct);
            delivery.LockedUntil = null;
            await db.SaveChangesAsync(ct);
            attempted++;
        }

        db.ChangeTracker.Clear();
        return attempted;
    }

    private async Task AttemptAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!delivery.Subscription.IsActive)
        {
            delivery.Status = DeliveryStatus.Cancelled;
            delivery.LastError = "Suscripción desactivada.";
            return;
        }

        delivery.Attempts++;
        delivery.LastAttemptAt = now;
        try
        {
            using var request = BuildRequest(delivery, now);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, timeout.Token);

            delivery.LastStatusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                delivery.Status = DeliveryStatus.Succeeded;
                delivery.DeliveredAt = now;
                delivery.LastError = null;
                return;
            }
            delivery.LastError = $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            delivery.LastStatusCode = null;
            delivery.LastError = ex is OperationCanceledException ? "Tiempo de espera agotado." : Truncate(ex.Message, 500);
        }

        if (delivery.Attempts >= _options.MaxAttempts)
        {
            delivery.Status = DeliveryStatus.Failed;
            logger.LogWarning("Webhook {Subscription} falló definitivamente para el evento {Event}: {Error}",
                delivery.SubscriptionId, delivery.EventId, delivery.LastError);
        }
        else
        {
            delivery.NextAttemptAt = now + Backoff(delivery.Attempts);
        }
    }

    private HttpRequestMessage BuildRequest(WebhookDelivery delivery, DateTimeOffset now)
    {
        var e = delivery.Event;
        var payload = new WebhookPayload(e.Id, e.Sequence, e.Type, e.Source, e.OrganizationId, e.SchemaVersion,
            e.SubjectType, e.SubjectId, e.OccurredAt, e.RecordedAt, JsonDocument.Parse(e.Data).RootElement);
        var body = JsonSerializer.Serialize(payload, json.Value.JsonSerializerOptions);
        var timestamp = now.ToUnixTimeSeconds().ToString();

        var request = new HttpRequestMessage(HttpMethod.Post, delivery.Subscription.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Vortex-Event", e.Type);
        request.Headers.Add("X-Vortex-Event-Id", e.Id.ToString());
        request.Headers.Add("X-Vortex-Delivery", delivery.Id.ToString());
        request.Headers.Add(SignatureHeader, $"t={timestamp},v1={Sign(delivery.Subscription.Secret, timestamp, body)}");
        request.Headers.UserAgent.ParseAdd("VortexHub-Webhooks/1.0");
        return request;
    }

    /// <summary>HMAC-SHA256 en hex de <c>"{timestamp}.{body}"</c> con el secreto de la suscripción.</summary>
    public static string Sign(string secret, string timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, attempts - 1), TimeSpan.FromHours(6).TotalSeconds));

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}

/// <summary>Ejecuta <see cref="WebhookDeliveryService"/> periódicamente. Puede correr en varias réplicas a la vez.</summary>
public sealed class WebhookDispatcher(IServiceScopeFactory scopes, IOptions<WebhookOptions> options, ILogger<WebhookDispatcher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DispatcherEnabled)
            return;

        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var attempted = 0;
            try
            {
                using var scope = scopes.CreateScope();
                attempted = await scope.ServiceProvider.GetRequiredService<WebhookDeliveryService>().DeliverDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Error en el despachador de webhooks.");
            }

            // Si el lote vino lleno, seguir sin esperar.
            if (attempted < options.Value.BatchSize)
                await Task.Delay(interval, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
