using System.Security.Claims;
using VortexApi.Domain;

namespace VortexApi.Security;

public static class ClaimsPrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(VortexClaims.Subject)!);

    public static Guid? OrganizationId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(VortexClaims.Organization), out var id) ? id : null;

    public static bool IsPlatformAdmin(this ClaimsPrincipal p) => p.IsInRole(nameof(UserRole.PlatformAdmin));

    public static Guid? DeviceActivationId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(VortexClaims.Device), out var id) ? id : null;

    /// <summary>
    /// Organización a la que el administrador puede acceder: cualquiera para PlatformAdmin,
    /// solo la propia para OrgAdmin.
    /// </summary>
    public static bool CanAccessOrganization(this ClaimsPrincipal p, Guid organizationId) =>
        p.IsPlatformAdmin() || p.OrganizationId() == organizationId;
}
