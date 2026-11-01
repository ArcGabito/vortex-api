using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VortexApi.Domain;
using VortexApi.Security;

namespace VortexApi.Controllers;

/// <summary>
/// Llaves públicas para validar los tokens. Los backends de los sistemas Vortex pueden configurar
/// JwtBearer con <c>Authority</c> apuntando a esta API; las apps MAUI usan el JWKS para validar el
/// offline token sin conexión.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route(".well-known")]
public sealed class WellKnownController(SigningKeyProvider keys, IOptions<JwtOptions> options) : ControllerBase
{
    [HttpGet("jwks.json")]
    [ResponseCache(Duration = 3600)]
    public IActionResult Jwks() => Ok(new { keys = new[] { keys.PublicJwk } });

    [HttpGet("openid-configuration")]
    public IActionResult Discovery() => Ok(new Dictionary<string, object>
    {
        ["issuer"] = options.Value.Issuer,
        ["jwks_uri"] = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/.well-known/jwks.json",
        ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
        ["token_endpoint"] = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/connect/token",
        ["grant_types_supported"] = new[] { "client_credentials" },
        ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post" },
        ["scopes_supported"] = IntegrationScopes.Descriptions.Keys.ToArray(),
    });
}
