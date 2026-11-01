using Microsoft.EntityFrameworkCore;
using VortexApi.Data;
using VortexApi.Domain;

namespace VortexApi.Services;

/// <summary>Datos de quién publica un evento.</summary>
public sealed record EventActor(Guid OrganizationId, string Source, Guid? UserId, Guid? DeviceId);

/// <summary>
/// Agrega eventos al flujo de la organización y crea las entregas de webhooks que correspondan (patrón outbox:
/// todo en la misma transacción que el cambio que lo origina).
/// <para>
/// Igual que el feed de datos maestros, la secuencia sale de incrementar <see cref="Organization.EventSequence"/>:
/// el UPDATE bloquea la fila hasta el commit, así las secuencias son correlativas y en orden de commit.
/// </para>
/// </summary>
public sealed class EventPublisher(VortexDbContext db, TimeProvider clock)
{
    public const int MaxDataBytes = 64 * 1024;

    /// <summary>Debe ejecutarse dentro de una transacción; el llamador hace <c>SaveChanges</c> y commit.</summary>
    public async Task<IntegrationEvent> AppendAsync(EventActor actor, string type, string dataJson, Guid? id = null,
        DateTimeOffset? occurredAt = null, string? subjectType = null, string? subjectId = null, int schemaVersion = 1)
    {
        var orgId = actor.OrganizationId;
        await db.Organizations.Where(o => o.Id == orgId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.EventSequence, o => o.EventSequence + 1));
        var sequence = await db.Organizations.Where(o => o.Id == orgId).Select(o => o.EventSequence).SingleAsync();

        var now = clock.GetUtcNow();
        var e = new IntegrationEvent
        {
            Id = id ?? Guid.NewGuid(),
            OrganizationId = orgId,
            Sequence = sequence,
            Type = type,
            Category = EventTypePattern.CategoryOf(type),
            Source = actor.Source,
            SchemaVersion = schemaVersion,
            SubjectType = subjectType,
            SubjectId = subjectId,
            OccurredAt = occurredAt ?? now,
            RecordedAt = now,
            UserId = actor.UserId,
            DeviceId = actor.DeviceId,
            Data = dataJson,
        };
        db.IntegrationEvents.Add(e);

        var subscriptions = await db.WebhookSubscriptions.AsNoTracking()
            .Where(s => s.OrganizationId == orgId && s.IsActive).ToListAsync();
        foreach (var subscription in subscriptions.Where(s => s.Accepts(e)))
        {
            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                SubscriptionId = subscription.Id,
                EventId = e.Id,
                Status = DeliveryStatus.Pending,
                NextAttemptAt = now,
                CreatedAt = now,
            });
        }
        return e;
    }
}
