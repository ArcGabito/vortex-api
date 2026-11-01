using VortexApi.Data;
using VortexApi.Domain;

namespace VortexApi.Services;

public sealed class AuditService(VortexDbContext db, TimeProvider clock, IHttpContextAccessor http)
{
    /// <summary>Agrega una entrada al contexto; se persiste con el siguiente <c>SaveChanges</c>.</summary>
    public void Record(string action, bool success, Guid? actorUserId = null, Guid? organizationId = null,
        string? productCode = null, string? targetId = null, string? detail = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = clock.GetUtcNow(),
            Action = action,
            Success = success,
            ActorUserId = actorUserId,
            OrganizationId = organizationId,
            ProductCode = productCode,
            TargetId = targetId,
            Detail = detail is { Length: > 1000 } ? detail[..1000] : detail,
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        });
    }

    /// <summary>Registra y guarda de inmediato; útil antes de lanzar un error (que no persistiría nada más).</summary>
    public Task RecordNowAsync(string action, bool success, Guid? actorUserId = null, Guid? organizationId = null,
        string? productCode = null, string? targetId = null, string? detail = null)
    {
        Record(action, success, actorUserId, organizationId, productCode, targetId, detail);
        return db.SaveChangesAsync();
    }
}

