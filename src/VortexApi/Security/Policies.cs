using VortexApi.Domain;

namespace VortexApi.Security;

public static class Policies
{
    /// <summary>Token de la consola de administración con rol OrgAdmin o PlatformAdmin.</summary>
    public const string Admin = "admin";

    /// <summary>Token de administración con rol PlatformAdmin (personal de Vortex).</summary>
    public const string PlatformAdmin = "platform-admin";

    /// <summary>Token emitido a un sistema Vortex (ERP/CRM/WMS/POS) con acceso y equipo validados.</summary>
    public const string ProductClient = "product-client";

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
    }
}
