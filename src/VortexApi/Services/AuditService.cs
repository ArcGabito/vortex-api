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

public static class AuditActions
{
    public const string ProductLogin = "product.login";
    public const string AdminLogin = "admin.login";
    public const string Refresh = "session.refresh";
    public const string RefreshReuse = "session.refresh_reuse";
    public const string Logout = "session.logout";
    public const string Heartbeat = "license.heartbeat";
    public const string DeviceActivated = "device.activated";
    public const string DeviceRevoked = "device.revoked";
    public const string PasswordChanged = "user.password_changed";
    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserPasswordReset = "user.password_reset";
    public const string UserUnlocked = "user.unlocked";
    public const string OrganizationCreated = "organization.created";
    public const string OrganizationUpdated = "organization.updated";
    public const string LicenseCreated = "license.created";
    public const string LicenseUpdated = "license.updated";
    public const string LicenseStatusChanged = "license.status_changed";
    public const string SeatAssigned = "seat.assigned";
    public const string SeatRemoved = "seat.removed";
    public const string ProductKeyRotated = "product.key_rotated";
}
