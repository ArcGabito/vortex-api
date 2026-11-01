using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace VortexApi.Client;

public sealed record OfflineLicense(
    Guid LicenseId,
    string LicenseKey,
    string Product,
    string Plan,
    Guid UserId,
    Guid OrganizationId,
    string OrganizationName,
    IReadOnlyList<string> Features,
    DateTimeOffset LicenseExpiresAt,
    DateTimeOffset ValidUntil)
{
    public bool HasFeature(string feature) => Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
}

public sealed record OfflineValidationResult(bool IsValid, OfflineLicense? License, string? Error);

/// <summary>
/// Valida el license token sin conexión: firma RS256 con la llave pública (JWKS) del servidor, emisor,
/// audiencia, vigencia, producto y equipo. Un token copiado de otro equipo o manipulado es rechazado.
/// </summary>
public sealed class OfflineLicenseValidator(string jwksJson, string issuer)
{
    private readonly JsonWebKeySet _keys = new(jwksJson);
    private readonly JsonWebTokenHandler _handler = new();

    public async Task<OfflineValidationResult> ValidateAsync(string licenseToken, string productCode, string deviceId,
        TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var result = await _handler.ValidateTokenAsync(licenseToken, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = "vortex-license",
            ValidTypes = ["vortex-license+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = _keys.GetSigningKeys(),
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
            LifetimeValidator = (notBefore, expires, _, _) =>
                (notBefore is null || notBefore.Value <= now.UtcDateTime.AddMinutes(5)) &&
                expires is not null && expires.Value > now.UtcDateTime,
        });

        if (!result.IsValid)
            return new(false, null, result.Exception?.Message ?? "Token inválido.");

        var claims = result.ClaimsIdentity;
        string Get(string type) => claims.FindFirst(type)?.Value ?? "";

        if (!string.Equals(Get("prd"), productCode, StringComparison.OrdinalIgnoreCase))
            return new(false, null, "El license token corresponde a otro producto.");

        var deviceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)));
        if (!string.Equals(Get("dev_hash"), deviceHash, StringComparison.OrdinalIgnoreCase))
            return new(false, null, "El license token corresponde a otro equipo.");

        var token = (JsonWebToken)result.SecurityToken;
        var license = new OfflineLicense(
            Guid.Parse(Get("lic")),
            Get("lic_key"),
            Get("prd"),
            Get("plan"),
            Guid.Parse(Get("sub")),
            Guid.Parse(Get("org")),
            Get("org_name"),
            claims.FindAll("feat").Select(c => c.Value).ToList(),
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(Get("lic_exp"))),
            new DateTimeOffset(token.ValidTo, TimeSpan.Zero));
        return new(true, license, null);
    }
}
