using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Security;

namespace VortexApi.Services;

/// <summary>
/// Escribe datos maestros y registra cada cambio en el feed dentro de la misma transacción.
/// <para>
/// La secuencia del feed se obtiene incrementando <see cref="Organization.ChangeSequence"/>: el UPDATE bloquea la fila
/// de la organización hasta el commit, así las escrituras concurrentes de una organización se serializan y las
/// secuencias quedan correlativas y en orden de commit. Un lector que pide "lo posterior a N" nunca se salta cambios.
/// </para>
/// </summary>
public sealed class MasterDataWriter(
    VortexDbContext db, EventPublisher events, TimeProvider clock, IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> json)
{
    public delegate Task<(TEntity Entity, ChangeOperation Operation)> Mutation<TEntity>();

    /// <summary>
    /// Ejecuta <paramref name="mutate"/> (que carga y modifica la entidad) y, si cambió algo, incrementa la versión y
    /// publica el cambio. Guardar los mismos valores no genera versión ni cambio nuevo: así dos sistemas que
    /// sincronizan entre sí no entran en un bucle.
    /// </summary>
    public async Task<TEntity> WriteAsync<TEntity>(ClaimsPrincipal actor, string entityType, Mutation<TEntity> mutate,
        Func<TEntity, object> toPayload) where TEntity : MasterEntity
    {
        var orgId = actor.OrganizationId()!.Value;
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync();

                var (entity, operation) = await mutate();
                if (!db.ChangeTracker.HasChanges())
                {
                    await tx.CommitAsync();
                    return entity;
                }

                var now = clock.GetUtcNow();
                if (operation == ChangeOperation.Created)
                {
                    entity.OrganizationId = orgId;
                    entity.CreatedAt = now;
                }
                entity.Version++;
                entity.UpdatedAt = now;

                await db.Organizations.Where(o => o.Id == orgId)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.ChangeSequence, o => o.ChangeSequence + 1));
                var sequence = await db.Organizations.Where(o => o.Id == orgId).Select(o => o.ChangeSequence).SingleAsync();

                var payload = JsonSerializer.Serialize(toPayload(entity), json.Value.JsonSerializerOptions);
                db.ChangeEntries.Add(new ChangeEntry
                {
                    OrganizationId = orgId,
                    Sequence = sequence,
                    EntityType = entityType,
                    EntityId = entity.Id,
                    Operation = operation,
                    Version = entity.Version,
                    ChangedAt = now,
                    ChangedByUserId = actor.ActingUserId(),
                    Source = SourceOf(actor),
                    Payload = payload,
                });

                // El mismo cambio como evento (master.item.updated, …) para quienes reciben webhooks.
                await events.AppendAsync(
                    new EventActor(orgId, SourceOf(actor), actor.ActingUserId(), actor.DeviceId()),
                    $"master.{entityType}.{operation.ToString().ToLowerInvariant()}", payload,
                    subjectType: entityType, subjectId: entity.Id.ToString());

                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return entity;
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            throw VersionConflict();
        }
        catch (DbUpdateException)
        {
            // Índice único violado por una escritura concurrente que pasó la validación previa.
            throw ApiException.Conflict(ErrorCodes.DuplicateValue, "Ya existe un registro con esos datos.");
        }
    }

    /// <summary>Sistema Vortex (ERP, CRM, WMS, POS), aplicación externa (<c>EXT:slug</c>) o <c>ADMIN</c>.</summary>
    public static string SourceOf(ClaimsPrincipal actor) =>
        actor.FindFirstValue(VortexClaims.Product) ?? actor.FindFirstValue(VortexClaims.Source) ?? "ADMIN";

    public static ApiException VersionConflict() => new(StatusCodes.Status412PreconditionFailed, ErrorCodes.VersionConflict,
        "El registro fue modificado por otro sistema o usuario. Vuelva a leerlo y reintente.");
}
