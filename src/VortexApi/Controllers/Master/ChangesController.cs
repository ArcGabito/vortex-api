using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Security;

namespace VortexApi.Controllers.Master;

/// <summary>
/// Feed de cambios de datos maestros de la organización. Cada sistema guarda el último <c>nextCursor</c> y consulta
/// periódicamente (o al recuperar la conexión) lo posterior a él. Los cambios llegan en orden y sin huecos.
/// </summary>
[ApiController]
[Route("api/master/changes")]
[Authorize(Policy = Policies.OrganizationData)]
public sealed class ChangesController(VortexDbContext db) : ControllerBase
{
    /// <param name="since">Cursor de la consulta anterior; 0 para recibir todo desde el inicio.</param>
    /// <param name="types">Tipos separados por coma: <c>party</c>, <c>item</c>, <c>site</c>, <c>company</c>. Vacío = todos.</param>
    /// <param name="limit">Máximo de cambios por página (1–1000).</param>
    [HttpGet]
    public async Task<ChangeFeedResponse> Get(long since = 0, string? types = null, int limit = 200)
    {
        var orgId = User.OrganizationId()!.Value;
        limit = Math.Clamp(limit, 1, 1000);

        var typeFilter = (types ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).Distinct().ToList();
        var unknown = typeFilter.FirstOrDefault(t => !MasterEntityTypes.All.Contains(t));
        if (unknown is not null)
            throw ApiException.BadRequest(ErrorCodes.Validation, $"Tipo desconocido: {unknown}.");

        // Solo se leen secuencias ya confirmadas: el contador se lee antes que las entradas.
        var head = await db.Organizations.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.ChangeSequence).SingleAsync();

        var query = db.ChangeEntries.AsNoTracking()
            .Where(c => c.OrganizationId == orgId && c.Sequence > since && c.Sequence <= head);
        if (typeFilter.Count > 0)
            query = query.Where(c => typeFilter.Contains(c.EntityType));

        var entries = await query.OrderBy(c => c.Sequence).Take(limit + 1).ToListAsync();
        var hasMore = entries.Count > limit;
        if (hasMore)
            entries.RemoveAt(limit);

        var changes = entries.Select(c => new ChangeDto(c.Sequence, c.EntityType, c.EntityId, c.Operation, c.Version,
            c.ChangedAt, c.ChangedByUserId, c.Source, JsonDocument.Parse(c.Payload).RootElement.Clone())).ToList();

        // Sin más resultados, el cursor avanza hasta la cabeza aunque los últimos cambios fueran de otros tipos.
        var next = hasMore ? entries[^1].Sequence : Math.Max(since, head);
        return new ChangeFeedResponse(changes, next, hasMore);
    }
}
