using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
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

    /// <summary>Configuración de la organización (datos de la empresa, aplicaciones de integración): solo su OrgAdmin.</summary>
    public const string OrganizationAdmin = "organization-admin";

    // Datos de la organización: sistemas Vortex, OrgAdmin y aplicaciones de integración con el scope indicado.
    public const string MasterRead = "master-read";
    public const string MasterWrite = "master-write";
    public const string EventsRead = "events-read";

    /// <summary>Publicar eventos: sistemas Vortex y aplicaciones con algún scope <c>events.publish</c>.</summary>
    public const string EventsPublish = "events-publish";

    /// <summary>Webhooks: OrgAdmin (todos los de la organización) o aplicación con <c>webhooks.manage</c> (los suyos).</summary>
    public const string WebhooksManage = "webhooks-manage";

    public static void Configure(AuthorizationOptions o)
    {
        o.AddPolicy(Admin, p => p
            .RequireClaim(VortexClaims.TokenUse, VortexClaims.TokenUseAdmin)
            .RequireRole(nameof(UserRole.OrgAdmin), nameof(UserRole.PlatformAdmin)));
        o.AddPolicy(PlatformAdmin, p => p
            .RequireClaim(VortexClaims.TokenUse, VortexClaims.TokenUseAdmin)
            .RequireRole(nameof(UserRole.PlatformAdmin)));
        o.AddPolicy(ProductClient, p => p
            .RequireClaim(VortexClaims.TokenUse, VortexClaims.TokenUseProduct));
        o.AddPolicy(OrganizationAdmin, p => p
            .RequireClaim(VortexClaims.Organization)
            .RequireAssertion(c => IsOrgAdmin(c.User)));

        o.AddPolicy(MasterRead, p => OrganizationData(p, u =>
            u.HasScope(IntegrationScopes.MasterRead) || u.HasScope(IntegrationScopes.MasterWrite)));
        o.AddPolicy(MasterWrite, p => OrganizationData(p, u => u.HasScope(IntegrationScopes.MasterWrite)));
        o.AddPolicy(EventsRead, p => OrganizationData(p, u => u.HasScope(IntegrationScopes.EventsRead)));
        o.AddPolicy(EventsPublish, p => p
            .RequireClaim(VortexClaims.Organization)
            .RequireAssertion(c => IsProduct(c.User) || (c.User.IsIntegration() && IntegrationScopes.CanPublishAny(c.User.Scopes()))));
        o.AddPolicy(WebhooksManage, p => p
            .RequireClaim(VortexClaims.Organization)
            .RequireAssertion(c => IsOrgAdmin(c.User) || (c.User.IsIntegration() && c.User.HasScope(IntegrationScopes.WebhooksManage))));
    }

    private static void OrganizationData(AuthorizationPolicyBuilder p, Func<ClaimsPrincipal, bool> integrationAllowed) => p
        .RequireClaim(VortexClaims.Organization)
        .RequireAssertion(c => IsProduct(c.User) || IsOrgAdmin(c.User) || (c.User.IsIntegration() && integrationAllowed(c.User)));

    private static bool IsProduct(ClaimsPrincipal u) => u.HasClaim(VortexClaims.TokenUse, VortexClaims.TokenUseProduct);

    private static bool IsOrgAdmin(ClaimsPrincipal u) =>
        u.HasClaim(VortexClaims.TokenUse, VortexClaims.TokenUseAdmin) && u.IsInRole(nameof(UserRole.OrgAdmin));
}
