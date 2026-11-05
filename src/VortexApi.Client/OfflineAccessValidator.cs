using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace VortexApi.Client;

public sealed record OfflineAccess(
    Guid AccessId,
    string Product,
    Guid UserId,
    Guid OrganizationId,
    string OrganizationName,
    string? Role,
    IReadOnlyList<string> Features,
    DateTimeOffset? AccessExpiresAt,
    DateTimeOffset ValidUntil,
    IReadOnlyList<Guid>? SiteIds = null)
{
    public bool HasFeature(string feature) => Features.Contains(feature, StringComparer.OrdinalIgnoreCase);

    /// <summary>Sedes asignadas al usuario en este sistema. Vacío: puede trabajar en todas.</summary>
    public bool CanUseSite(Guid siteId) => SiteIds is not { Count: > 0 } || SiteIds.Contains(siteId);
}

public sealed record OfflineValidationResult(bool IsValid, OfflineAccess? Access, string? Error);

/// <summary>
/// Valida el offline token sin conexión: firma RS256 con la llave pública (JWKS) del hub, emisor, audiencia,
/// vigencia, sistema y equipo. Un token copiado de otro equipo o manipulado es rechazado.
/// </summary>
public sealed class OfflineAccessValidator(string jwksJson, string issuer)
{
    private readonly JsonWebKeySet _keys = new(jwksJson);
    private readonly JsonWebTokenHandler _handler = new();

    public async Task<OfflineValidationResult> ValidateAsync(string offlineToken, string productCode, string deviceId,
        TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var result = await _handler.ValidateTokenAsync(offlineToken, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = "vortex-offline",
            ValidTypes = ["vortex-offline+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = _keys.GetSigningKeys(),
            RequireExpirationTime = true,
            LifetimeValidator = (notBefore, expires, _, _) =>
                (notBefore is null || notBefore.Value <= now.UtcDateTime.AddMinutes(5)) &&
                expires is not null && expires.Value > now.UtcDateTime,
        });

        if (!result.IsValid)
            return new(false, null, result.Exception?.Message ?? "Token inválido.");

        var claims = result.ClaimsIdentity;
        string? Get(string type) => claims.FindFirst(type)?.Value;

        if (!string.Equals(Get("prd"), productCode, StringComparison.OrdinalIgnoreCase))
            return new(false, null, "El offline token corresponde a otro sistema.");

        var deviceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)));
        if (!string.Equals(Get("dev_hash"), deviceHash, StringComparison.OrdinalIgnoreCase))
            return new(false, null, "El offline token corresponde a otro equipo.");

        var token = (JsonWebToken)result.SecurityToken;
        var access = new OfflineAccess(
            Guid.Parse(Get("acc")!),
            Get("prd")!,
            Guid.Parse(Get("sub")!),
            Guid.Parse(Get("org")!),
            Get("org_name") ?? "",
            Get("prd_role"),
            claims.FindAll("feat").Select(c => c.Value).ToList(),
            Get("acc_exp") is { } exp ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(exp)) : null,
            new DateTimeOffset(token.ValidTo, TimeSpan.Zero),
            claims.FindAll("prd_sites").Select(c => Guid.TryParse(c.Value, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList());
        return new(true, access, null);
    }
}
