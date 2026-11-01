using System.Security.Claims;
using VortexApi.Domain;

namespace VortexApi.Security;

public static class ClaimsPrincipalExtensions
{
    /// <summary>Usuario del token; en tokens de integración es el Id de la aplicación.</summary>
    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(VortexClaims.Subject)!);

    /// <summary>Usuario humano detrás de la acción, o null si actúa una aplicación de integración.</summary>
    public static Guid? ActingUserId(this ClaimsPrincipal p) => p.IsIntegration() ? null : p.UserId();

    public static Guid? OrganizationId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(VortexClaims.Organization), out var id) ? id : null;

    public static bool IsPlatformAdmin(this ClaimsPrincipal p) => p.IsInRole(nameof(UserRole.PlatformAdmin));

    public static bool IsIntegration(this ClaimsPrincipal p) =>
        p.HasClaim(VortexClaims.TokenUse, VortexClaims.TokenUseIntegration);

    public static Guid? IntegrationClientId(this ClaimsPrincipal p) => p.IsIntegration() ? p.UserId() : null;

    public static IReadOnlyList<string> Scopes(this ClaimsPrincipal p) =>
        (p.FindFirstValue(VortexClaims.Scope) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static bool HasScope(this ClaimsPrincipal p, string scope) => p.Scopes().Contains(scope);

    public static Guid? DeviceId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(VortexClaims.Device), out var id) ? id : null;

    /// <summary>
    /// Organización a la que el administrador puede acceder: cualquiera para PlatformAdmin,
    /// solo la propia para OrgAdmin.
    /// </summary>
    public static bool CanAccessOrganization(this ClaimsPrincipal p, Guid organizationId) =>
        p.IsPlatformAdmin() || p.OrganizationId() == organizationId;
}
