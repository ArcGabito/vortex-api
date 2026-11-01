namespace VortexApi.Services;

public static class AuditActions
{
    public const string ProductLogin = "product.login";
    public const string AdminLogin = "admin.login";
    public const string Refresh = "session.refresh";
    public const string RefreshReuse = "session.refresh_reuse";
    public const string Logout = "session.logout";
    public const string Heartbeat = "access.heartbeat";
    public const string DeviceActivated = "device.activated";
    public const string DeviceRevoked = "device.revoked";
    public const string PasswordChanged = "user.password_changed";
    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserPasswordReset = "user.password_reset";
    public const string UserUnlocked = "user.unlocked";
    public const string OrganizationCreated = "organization.created";
    public const string OrganizationUpdated = "organization.updated";
    public const string ProductEnabled = "organization_product.enabled";
    public const string ProductUpdated = "organization_product.updated";
    public const string MemberAdded = "member.added";
    public const string MemberUpdated = "member.updated";
    public const string MemberRemoved = "member.removed";
    public const string ProductKeyRotated = "product.key_rotated";
}
