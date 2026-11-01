using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using VortexApi.Domain;

namespace VortexApi.Infrastructure;

/// <summary>
/// Describe la seguridad en el documento OpenAPI: Bearer (tokens de los sistemas Vortex y de la consola) y OAuth2
/// <c>client_credentials</c> para aplicaciones de integración, con sus scopes. Así se puede probar desde /docs y
/// generar clientes en cualquier lenguaje.
/// </summary>
internal sealed class BearerSecurityTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info.Title = "Vortex Hub API";
        document.Info.Description =
            "Hub de la suite Vortex (ERP, CRM, WMS, POS): identidad, datos maestros, eventos e integraciones. " +
            "Las aplicaciones externas obtienen tokens en POST /connect/token (OAuth2 client_credentials).";
        document.Info.License = new OpenApiLicense { Name = "AGPL-3.0", Url = new Uri("https://www.gnu.org/licenses/agpl-3.0.html") };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Token de un sistema Vortex (/api/auth/login) o de la consola (/api/admin/auth/login).",
        };
        document.Components.SecuritySchemes["OAuth2"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description = "Aplicaciones de integración registradas por el OrgAdmin en /api/admin/integrations.",
            Flows = new OpenApiOAuthFlows
            {
                ClientCredentials = new OpenApiOAuthFlow
                {
                    TokenUrl = new Uri("/connect/token", UriKind.Relative),
                    Scopes = IntegrationScopes.Descriptions.ToDictionary(d => d.Key, d => d.Value),
                },
            },
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] });
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("OAuth2", document)] = [] });
        return Task.CompletedTask;
    }
}
