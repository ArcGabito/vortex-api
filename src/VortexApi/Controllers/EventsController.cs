using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers;

/// <summary>
/// Eventos de integración entre los sistemas de la suite. Un sistema publica lo que pasa (<c>sale.completed</c>) y
/// los demás lo consumen con el feed (<c>GET /api/events?since=</c>) o por webhook.
/// </summary>
[ApiController]
[Route("api/events")]
[Authorize(Policy = Policies.OrganizationData)]
public sealed class EventsController(VortexDbContext db, EventPublisher publisher, TimeProvider clock) : ControllerBase
{
    /// <summary>Publica un evento. Devuelve 201 si es nuevo y 200 si ese <c>id</c> ya estaba registrado.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ProductClient)]
    public async Task<ActionResult<EventDto>> Publish(PublishEventRequest request)
    {
        var (e, created) = await PublishOneAsync(request);
        return created ? CreatedAtAction(nameof(Get), new { id = e.Id }, ToDto(e)) : Ok(ToDto(e));
    }

    /// <summary>
    /// Publica hasta 100 eventos en orden (p. ej. las ventas que un POS acumuló sin conexión). Cada uno se procesa
    /// por separado: un error no impide registrar los demás.
    /// </summary>
    [HttpPost("batch")]
    [Authorize(Policy = Policies.ProductClient)]
    public async Task<IReadOnlyList<PublishResult>> PublishBatch(PublishBatchRequest request)
    {
        var results = new List<PublishResult>(request.Events.Count);
        foreach (var item in request.Events)
        {
            try
            {
                var (e, created) = await PublishOneAsync(item);
                results.Add(new PublishResult(e.Id, e.Sequence, created, null, null));
            }
            catch (ApiException ex)
            {
                results.Add(new PublishResult(item.Id ?? Guid.Empty, null, false, ex.Code, ex.Message));
            }
        }
        return results;
    }

    /// <param name="since">Cursor de la consulta anterior; 0 para recibir todo desde el inicio.</param>
    /// <param name="types">Patrones separados por coma: tipo exacto (<c>sale.completed</c>), categoría (<c>sale.*</c>) o <c>*</c>.</param>
    /// <param name="excludeSource">No devolver los eventos publicados por este sistema (normalmente, el propio).</param>
    [HttpGet]
    public async Task<EventFeedResponse> Feed(long since = 0, string? types = null, string? excludeSource = null, int limit = 200)
    {
        var orgId = User.OrganizationId()!.Value;
        limit = Math.Clamp(limit, 1, 1000);

        var patterns = (types ?? "*").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant()).Distinct().ToList();
        var invalid = patterns.FirstOrDefault(p => !EventTypePattern.IsValidPattern(p));
        if (invalid is not null)
            throw ApiException.BadRequest(ErrorCodes.InvalidEventType, $"Patrón inválido: {invalid}.");

        // Solo se leen secuencias ya confirmadas: el contador se lee antes que los eventos.
        var head = await db.Organizations.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.EventSequence).SingleAsync();
        var query = db.IntegrationEvents.AsNoTracking()
            .Where(e => e.OrganizationId == orgId && e.Sequence > since && e.Sequence <= head);

        if (!patterns.Contains("*"))
            query = query.Where(MatchesAny(patterns));
        if (!string.IsNullOrWhiteSpace(excludeSource))
        {
            var source = excludeSource.Trim().ToUpperInvariant();
            query = query.Where(e => e.Source != source);
        }

        var events = await query.OrderBy(e => e.Sequence).Take(limit + 1).ToListAsync();
        var hasMore = events.Count > limit;
        if (hasMore)
            events.RemoveAt(limit);

        var next = hasMore ? events[^1].Sequence : Math.Max(since, head);
        return new EventFeedResponse(events.Select(ToDto).ToList(), next, hasMore);
    }

    [HttpGet("{id:guid}")]
    public async Task<EventDto> Get(Guid id)
    {
        var orgId = User.OrganizationId()!.Value;
        var e = await db.IntegrationEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == orgId)
                ?? throw ApiException.NotFound("Evento");
        return ToDto(e);
    }

    /// <summary>Eventos conocidos de la suite, con quién los publica y quién los consume.</summary>
    [HttpGet("catalog")]
    public IReadOnlyList<EventCatalog.Entry> Catalog() => EventCatalog.Entries;

    private async Task<(IntegrationEvent Event, bool Created)> PublishOneAsync(PublishEventRequest request)
    {
        var orgId = User.OrganizationId()!.Value;
        var type = request.Type.Trim().ToLowerInvariant();
        if (!EventTypePattern.IsValidType(type))
            throw ApiException.BadRequest(ErrorCodes.InvalidEventType, "El tipo debe tener el formato categoria.accion (p. ej. sale.completed).");
        if (EventTypePattern.ReservedCategories.Contains(EventTypePattern.CategoryOf(type)))
            throw ApiException.BadRequest(ErrorCodes.InvalidEventType, "Las categorías master.* y hub.* las publica solo el hub.");
        if (request.Data.ValueKind != JsonValueKind.Object)
            throw ApiException.BadRequest(ErrorCodes.InvalidEventData, "data debe ser un objeto JSON.");

        var data = request.Data.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(data) > EventPublisher.MaxDataBytes)
            throw ApiException.BadRequest(ErrorCodes.InvalidEventData, $"data supera {EventPublisher.MaxDataBytes / 1024} KB.");

        var now = clock.GetUtcNow();
        if (request.OccurredAt > now.AddMinutes(5))
            throw ApiException.BadRequest(ErrorCodes.InvalidEventData, "occurredAt no puede estar en el futuro.");

        if (request.Id is { } id && await FindExistingAsync(id, orgId) is { } existing)
            return (existing, false);

        var actor = new EventActor(orgId, MasterDataWriter.SourceOf(User), User.UserId(), User.DeviceId());
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            var e = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync();
                var appended = await publisher.AppendAsync(actor, type, data, request.Id, request.OccurredAt,
                    request.SubjectType?.Trim(), request.SubjectId?.Trim(), request.SchemaVersion);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return appended;
            });
            return (e, true);
        }
        catch (DbUpdateException) when (request.Id is { } duplicateId)
        {
            // Dos envíos simultáneos con el mismo Id: el segundo devuelve el registrado por el primero.
            db.ChangeTracker.Clear();
            return (await FindExistingAsync(duplicateId, orgId) ?? throw ApiException.Conflict(ErrorCodes.EventIdInUse, "El id ya está en uso."), false);
        }
    }

    private async Task<IntegrationEvent?> FindExistingAsync(Guid id, Guid orgId)
    {
        var existing = await db.IntegrationEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id);
        if (existing is not null && existing.OrganizationId != orgId)
            throw ApiException.Conflict(ErrorCodes.EventIdInUse, "El id ya está en uso.");
        return existing;
    }

    /// <summary>
    /// OR de los patrones traducible a SQL: exactos con <c>IN</c>, categorías (<c>sale.*</c>) por la columna indexada
    /// <c>Category</c> y prefijos más largos (<c>master.item.*</c>) con <c>LIKE 'master.item.%'</c>.
    /// </summary>
    private static Expression<Func<IntegrationEvent, bool>> MatchesAny(IReadOnlyCollection<string> patterns)
    {
        var e = Expression.Parameter(typeof(IntegrationEvent), "e");
        var type = Expression.Property(e, nameof(IntegrationEvent.Type));
        var category = Expression.Property(e, nameof(IntegrationEvent.Category));

        var exact = patterns.Where(p => !p.EndsWith(".*")).ToList();
        var prefixes = patterns.Where(p => p.EndsWith(".*")).Select(p => p[..^1]).ToList();
        var categories = prefixes.Where(p => p.Count(c => c == '.') == 1).Select(p => p[..^1]).ToList();

        var conditions = new List<Expression>();
        if (exact.Count > 0)
            conditions.Add(Expression.Call(Expression.Constant(exact), nameof(List<string>.Contains), null, type));
        if (categories.Count > 0)
            conditions.Add(Expression.Call(Expression.Constant(categories), nameof(List<string>.Contains), null, category));
        foreach (var prefix in prefixes.Where(p => p.Count(c => c == '.') > 1))
            conditions.Add(Expression.Call(type, nameof(string.StartsWith), null, Expression.Constant(prefix)));

        return Expression.Lambda<Func<IntegrationEvent, bool>>(conditions.Aggregate(Expression.OrElse), e);
    }

    private static EventDto ToDto(IntegrationEvent e) => new(
        e.Id, e.Sequence, e.Type, e.Source, e.SchemaVersion, e.SubjectType, e.SubjectId, e.OccurredAt, e.RecordedAt,
        e.UserId, JsonDocument.Parse(e.Data).RootElement.Clone());
}
