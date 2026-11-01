using VortexApi.Domain;

namespace VortexApi.Security;

public static class Policies
{
    /// <summary>Token de la consola de administración con rol OrgAdmin o PlatformAdmin.</summary>
    public const string Admin = "admin";

    /// <summary>Token de administración con rol PlatformAdmin (quien opera el hub).</summary>
    public const string PlatformAdmin = "platform-admin";

    /// <summary>Token emitido a un sistema Vortex (ERP/CRM/WMS/POS) con acceso y equipo validados.</summary>
    public const string ProductClient = "product-client";

    /// <summary>Datos de la organización: lo usan los sistemas Vortex y el OrgAdmin de la organización.</summary>
    public const string OrganizationData = "organization-data";

    /// <summary>Configuración de la organización (p. ej. datos de la empresa): solo su OrgAdmin.</summary>
    public const string OrganizationAdmin = "organization-admin";

    public static void Configure(Microsoft.AspNetCore.Authorization.AuthorizationOptions o)
    {
        o.AddPolicy(Admin, p => p
            .RequireClaim(VortexClaims.TokenUse, VortexClaims.TokenUseAdmin)
            .RequireRole(nameof(UserRole.OrgAdmin), nameof(UserRole.PlatformAdmin)));
        o.AddPolicy(PlatformAdmin, p => p
            .RequireClaim(VortexClaims.TokenUse, VortexClaims.TokenUseAdmin)
            .RequireRole(nameof(UserRole.PlatformAdmin)));
        o.AddPolicy(ProductClient, p => p
            .RequireClaim(VortexClaims.TokenUse, VortexClaims.TokenUseProduct));
        o.AddPolicy(OrganizationData, p => p
            .RequireClaim(VortexClaims.Organization)
            .RequireAssertion(c =>
                c.User.HasClaim(VortexClaims.TokenUse, VortexClaims.TokenUseProduct) ||
                (c.User.HasClaim(VortexClaims.TokenUse, VortexClaims.TokenUseAdmin) && c.User.IsInRole(nameof(UserRole.OrgAdmin)))));
        o.AddPolicy(OrganizationAdmin, p => p
            .RequireClaim(VortexClaims.Organization)
            .RequireClaim(VortexClaims.TokenUse, VortexClaims.TokenUseAdmin)
            .RequireRole(nameof(UserRole.OrgAdmin)));
    }
}
