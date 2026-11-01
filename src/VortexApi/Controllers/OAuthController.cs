using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers;

/// <summary>
/// Endpoint de token OAuth 2.0 (RFC 6749) para aplicaciones de integración, con el flujo
/// <c>client_credentials</c>. Las credenciales pueden ir en <c>Authorization: Basic</c> o en el cuerpo.
/// Funciona con cualquier cliente OAuth estándar (n8n, Make, Zapier, Power Automate, Postman, librerías OAuth).
/// </summary>
[ApiController]
[Route("connect")]
[AllowAnonymous]
public sealed class OAuthController(VortexDbContext db, TokenService tokens, AuditService audit, TimeProvider clock) : ControllerBase
{
    public sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string Scope);

    public sealed record TokenError(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("error_description")] string Description);

    /// <param name="grantType">Debe ser <c>client_credentials</c>.</param>
    /// <param name="scope">Scopes separados por espacio (subconjunto de los de la aplicación); vacío = todos.</param>
    [HttpPost("token")]
    [Consumes("application/x-www-form-urlencoded")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TokenError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<TokenError>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Token(
        [FromForm(Name = "grant_type")] string? grantType,
        [FromForm(Name = "client_id")] string? clientId,
        [FromForm(Name = "client_secret")] string? clientSecret,
        [FromForm(Name = "scope")] string? scope)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        var usedBasic = TryReadBasic(out var basicId, out var basicSecret);
        if (usedBasic)
            (clientId, clientSecret) = (basicId, basicSecret);

        if (grantType != "client_credentials")
            return Error(400, "unsupported_grant_type", "Solo se admite grant_type=client_credentials.");
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return Error(401, "invalid_client", "Faltan client_id y client_secret.", usedBasic);

        var now = clock.GetUtcNow();
        var client = await db.IntegrationClients.Include(c => c.Organization).SingleOrDefaultAsync(c => c.ClientId == clientId);
        var secretOk = Hashing.FixedTimeEqualsHex(Hashing.Sha256(clientSecret), client?.SecretHash ?? new string('0', 64));
        if (client is null || !secretOk || !client.IsActive || !client.Organization.IsActive || client.ExpiresAt <= now)
        {
            await audit.RecordNowAsync(AuditActions.IntegrationTokenFailed, false, organizationId: client?.OrganizationId,
                targetId: client?.Id.ToString(), detail: clientId);
            return Error(401, "invalid_client", "Credenciales inválidas o aplicación desactivada.", usedBasic);
        }

        var requested = (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        var notAllowed = requested.FirstOrDefault(s => !client.Scopes.Contains(s));
        if (notAllowed is not null)
            return Error(400, "invalid_scope", $"La aplicación no tiene el scope {notAllowed}.");
        var granted = requested.Count > 0 ? requested : client.Scopes;

        // Evita escribir en cada token: basta con una marca cada pocos minutos.
        if (client.LastUsedAt is null || now - client.LastUsedAt > TimeSpan.FromMinutes(5))
        {
            client.LastUsedAt = now;
            await db.SaveChangesAsync();
        }

        var token = tokens.CreateIntegrationToken(client, granted);
        return Ok(new TokenResponse(token.Token, "Bearer", (int)(token.ExpiresAt - now).TotalSeconds, string.Join(' ', granted)));
    }

    private bool TryReadBasic(out string? id, out string? secret)
    {
        id = secret = null;
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) ||
            !"Basic".Equals(header.Scheme, StringComparison.OrdinalIgnoreCase) || header.Parameter is null)
            return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
            var separator = decoded.IndexOf(':');
            if (separator < 0)
                return false;
            // RFC 6749 §2.3.1: ambos valores van codificados como application/x-www-form-urlencoded.
            id = Uri.UnescapeDataString(decoded[..separator].Replace('+', ' '));
            secret = Uri.UnescapeDataString(decoded[(separator + 1)..].Replace('+', ' '));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private ObjectResult Error(int status, string error, string description, bool challengeBasic = false)
    {
        if (status == 401 && challengeBasic)
            Response.Headers.WWWAuthenticate = "Basic realm=\"vortex\"";
        return StatusCode(status, new TokenError(error, description));
    }
}
