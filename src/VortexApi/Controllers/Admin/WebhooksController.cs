using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services.Webhooks;

namespace VortexApi.Controllers.Admin;

/// <summary>Webhooks de la organización (solo su OrgAdmin).</summary>
[ApiController]
[Route("api/admin/webhooks")]
[Authorize(Policy = Policies.OrganizationAdmin)]
public sealed class WebhooksController(VortexDbContext db, TimeProvider clock, IOptions<WebhookOptions> options) : ControllerBase
{
    private Guid OrgId => User.OrganizationId()!.Value;

    [HttpGet]
    public async Task<IReadOnlyList<WebhookDto>> List()
    {
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking()
            .Where(s => s.OrganizationId == OrgId).OrderBy(s => s.Name).ToListAsync();
        var result = new List<WebhookDto>();
        foreach (var s in subscriptions)
            result.Add(await ToDtoAsync(s));
        return result;
    }

    [HttpGet("{id:guid}")]
    public async Task<WebhookDto> Get(Guid id) => await ToDtoAsync(await FindAsync(id));

    /// <summary>Crea la suscripción. La respuesta incluye el secreto de firma; no se vuelve a mostrar.</summary>
    [HttpPost]
    public async Task<ActionResult<WebhookSecretResponse>> Create(WebhookRequest request)
    {
        var subscription = new WebhookSubscription
        {
            OrganizationId = OrgId,
            Name = request.Name.Trim(),
            Url = request.Url.Trim(),
            Secret = NewSecret(),
            CreatedAt = clock.GetUtcNow(),
        };
        Apply(subscription, request);
        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = subscription.Id },
            new WebhookSecretResponse(await ToDtoAsync(subscription), subscription.Secret));
    }

    [HttpPut("{id:guid}")]
    public async Task<WebhookDto> Update(Guid id, WebhookRequest request)
    {
        var subscription = await FindAsync(id);
        Apply(subscription, request);
        if (!subscription.IsActive)
            await CancelPendingAsync(id);
        await db.SaveChangesAsync();
        return await ToDtoAsync(subscription);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        db.WebhookSubscriptions.Remove(await FindAsync(id));
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Genera un secreto nuevo; el anterior deja de ser válido de inmediato.</summary>
    [HttpPost("{id:guid}/rotate-secret")]
    public async Task<WebhookSecretResponse> RotateSecret(Guid id)
    {
        var subscription = await FindAsync(id);
        subscription.Secret = NewSecret();
        await db.SaveChangesAsync();
        return new WebhookSecretResponse(await ToDtoAsync(subscription), subscription.Secret);
    }

    [HttpGet("{id:guid}/deliveries")]
    public async Task<PagedResult<WebhookDeliveryDto>> Deliveries(Guid id, DeliveryStatus? status, int page = 1, int pageSize = 50)
    {
        await FindAsync(id);
        var query = db.WebhookDeliveries.AsNoTracking().Include(d => d.Event).Where(d => d.SubscriptionId == id);
        if (status is not null)
            query = query.Where(d => d.Status == status);
        return await query.OrderByDescending(d => d.Id).ToPageAsync(page, pageSize, d => new WebhookDeliveryDto(
            d.Id, d.EventId, d.Event.Type, d.Event.Sequence, d.Status, d.Attempts, d.NextAttemptAt, d.LastAttemptAt,
            d.LastStatusCode, d.LastError, d.CreatedAt, d.DeliveredAt));
    }

    /// <summary>Vuelve a poner en cola una entrega fallida o cancelada (p. ej. después de arreglar el receptor).</summary>
    [HttpPost("{id:guid}/deliveries/{deliveryId:long}/retry")]
    public async Task<IActionResult> Retry(Guid id, long deliveryId)
    {
        await FindAsync(id);
        var delivery = await db.WebhookDeliveries.SingleOrDefaultAsync(d => d.Id == deliveryId && d.SubscriptionId == id)
                       ?? throw ApiException.NotFound("Entrega");
        delivery.Status = DeliveryStatus.Pending;
        delivery.Attempts = 0;
        delivery.NextAttemptAt = clock.GetUtcNow();
        delivery.LockedUntil = null;
        await db.SaveChangesAsync();
        return Accepted();
    }

    private void Apply(WebhookSubscription s, WebhookRequest r)
    {
        if (NetworkGuard.ValidateUrl(r.Url.Trim(), options.Value) is { } error)
            throw ApiException.BadRequest(ErrorCodes.InvalidWebhookUrl, error);

        var patterns = r.EventTypes.Select(t => t.Trim().ToLowerInvariant()).Distinct().ToList();
        var invalid = patterns.FirstOrDefault(p => !EventTypePattern.IsValidPattern(p));
        if (invalid is not null)
            throw ApiException.BadRequest(ErrorCodes.InvalidEventType, $"Patrón inválido: {invalid}.");

        var receiver = r.ReceiverProduct?.Trim().ToUpperInvariant();
        if (receiver is not null && !ProductCodes.Defaults.ContainsKey(receiver))
            throw ApiException.BadRequest(ErrorCodes.Validation, $"Sistema desconocido: {receiver}.");

        s.Name = r.Name.Trim();
        s.Url = r.Url.Trim();
        s.EventTypes = patterns;
        s.ReceiverProduct = receiver;
        s.IsActive = r.IsActive;
    }

    private Task CancelPendingAsync(Guid id) => db.WebhookDeliveries
        .Where(d => d.SubscriptionId == id && d.Status == DeliveryStatus.Pending)
        .ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, DeliveryStatus.Cancelled)
            .SetProperty(d => d.LastError, "Suscripción desactivada."));

    private async Task<WebhookSubscription> FindAsync(Guid id) =>
        await db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == id && s.OrganizationId == OrgId)
        ?? throw ApiException.NotFound("Webhook");

    private async Task<WebhookDto> ToDtoAsync(WebhookSubscription s)
    {
        var counts = await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.SubscriptionId == s.Id && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Failed))
            .GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        return new WebhookDto(s.Id, s.Name, s.Url, s.EventTypes, s.ReceiverProduct, s.IsActive, s.CreatedAt,
            counts.FirstOrDefault(c => c.Key == DeliveryStatus.Pending)?.Count ?? 0,
            counts.FirstOrDefault(c => c.Key == DeliveryStatus.Failed)?.Count ?? 0);
    }

    private static string NewSecret() => $"whsec_{Security.Hashing.RandomToken(32)}";
}
