using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers.Master;

/// <summary>
/// Lectura, alta, actualización y borrado lógico de un tipo de dato maestro de la organización del token.
/// <list type="bullet">
/// <item><c>PUT /{id}</c> crea o actualiza con un Id generado por el cliente: un sistema sin conexión puede crear el
/// registro localmente y sincronizarlo después sin duplicarlo.</item>
/// <item><c>If-Match: "versión"</c> (opcional) evita pisar cambios de otro sistema; si no coincide responde 412.</item>
/// <item>Las respuestas incluyen <c>ETag</c> con la versión.</item>
/// </list>
/// </summary>
[ApiController]
[Authorize(Policy = Policies.MasterRead)]
public abstract class MasterDataControllerBase<TEntity, TRequest, TDto>(VortexDbContext db, MasterDataWriter writer)
    : ControllerBase where TEntity : MasterEntity, new()
{
    protected VortexDbContext Db { get; } = db;
    protected abstract string EntityType { get; }
    protected abstract string DisplayName { get; }
    protected abstract TDto ToDto(TEntity entity);
    protected abstract void Apply(TEntity entity, TRequest request);

    /// <summary>Valida reglas que requieren la base de datos (unicidad), después de aplicar el request.</summary>
    protected virtual Task ValidateAsync(TEntity entity) => Task.CompletedTask;

    /// <summary>Valores por defecto de un registro nuevo, antes de aplicar el request.</summary>
    protected virtual Task OnCreatingAsync(TEntity entity) => Task.CompletedTask;

    private TimeProvider Clock => HttpContext.RequestServices.GetRequiredService<TimeProvider>();

    protected Guid OrgId => User.OrganizationId()!.Value;

    protected IQueryable<TEntity> Scoped(bool includeDeleted) =>
        Db.Set<TEntity>().AsNoTracking().Where(e => e.OrganizationId == OrgId && (includeDeleted || e.DeletedAt == null));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TDto>> Get(Guid id)
    {
        var entity = await Scoped(includeDeleted: true).SingleOrDefaultAsync(e => e.Id == id)
                     ?? throw ApiException.NotFound(DisplayName);
        return WithETag(entity);
    }

    [HttpPost]
    [Authorize(Policy = Policies.MasterWrite)]
    public async Task<ActionResult<TDto>> Create(TRequest request)
    {
        var entity = await UpsertAsync(Guid.NewGuid(), request, expectedVersion: null);
        SetETag(entity);
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, ToDto(entity));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.MasterWrite)]
    public async Task<ActionResult<TDto>> Upsert(Guid id, TRequest request)
    {
        var entity = await UpsertAsync(id, request, ExpectedVersion());
        return WithETag(entity);
    }

    /// <summary>Borrado lógico: el registro deja de listarse pero sigue disponible por Id para quien lo referencie.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.MasterWrite)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var expected = ExpectedVersion();
        await writer.WriteAsync(User, EntityType, async () =>
        {
            var entity = await FindForWriteAsync(id) ?? throw ApiException.NotFound(DisplayName);
            EnsureVersion(entity, expected);
            entity.DeletedAt ??= Clock.GetUtcNow();
            return (entity, ChangeOperation.Deleted);
        }, e => ToDto(e)!);
        return NoContent();
    }

    private Task<TEntity> UpsertAsync(Guid id, TRequest request, int? expectedVersion) =>
        writer.WriteAsync(User, EntityType, async () =>
        {
            var entity = await FindForWriteAsync(id);
            var operation = ChangeOperation.Updated;
            if (entity is null)
            {
                if (expectedVersion is not null and not 0)
                    throw MasterDataWriter.VersionConflict();
                entity = new TEntity { Id = id, OrganizationId = OrgId };
                Db.Set<TEntity>().Add(entity);
                await OnCreatingAsync(entity);
                operation = ChangeOperation.Created;
            }
            else
            {
                EnsureVersion(entity, expectedVersion);
                entity.DeletedAt = null; // Guardar un registro borrado lo restaura.
            }

            Apply(entity, request);
            await ValidateAsync(entity);
            return (entity, operation);
        }, e => ToDto(e)!);

    private async Task<TEntity?> FindForWriteAsync(Guid id)
    {
        var entity = await Db.Set<TEntity>().SingleOrDefaultAsync(e => e.Id == id);
        // Un Id de otra organización se trata como inexistente, pero no se puede reutilizar.
        if (entity is not null && entity.OrganizationId != OrgId)
            throw ApiException.NotFound(DisplayName);
        return entity;
    }

    private static void EnsureVersion(TEntity entity, int? expected)
    {
        if (expected is not null && entity.Version != expected)
            throw MasterDataWriter.VersionConflict();
    }

    private int? ExpectedVersion()
    {
        var raw = Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(raw) || raw == "*")
            return null;
        var value = raw.Trim().Replace("W/", "").Trim('"');
        return int.TryParse(value, out var v)
            ? v
            : throw ApiException.BadRequest(ErrorCodes.Validation, "If-Match debe contener la versión del registro.");
    }

    protected ActionResult<TDto> WithETag(TEntity entity)
    {
        SetETag(entity);
        return ToDto(entity);
    }

    private void SetETag(TEntity entity) => Response.Headers[HeaderNames.ETag] = $"\"{entity.Version}\"";

    protected static Contracts.MasterMeta Meta(MasterEntity e) => new(e.Version, e.CreatedAt, e.UpdatedAt, e.DeletedAt);

    protected static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
