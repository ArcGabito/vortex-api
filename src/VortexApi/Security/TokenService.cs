using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using VortexApi.Domain;

namespace VortexApi.Security;

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt);

public sealed class TokenService(SigningKeyProvider keys, IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly JwtOptions _options = options.Value;

    public IssuedToken CreateAdminAccessToken(User user)
    {
        var claims = BaseUserClaims(user);
        claims.Add(new(VortexClaims.TokenUse, VortexClaims.TokenUseAdmin));
        return Create(TokenAudiences.Admin, claims, clock.GetUtcNow().AddMinutes(_options.AccessTokenMinutes));
    }

    public IssuedToken CreateProductAccessToken(User user, ProductMember member, Device device)
    {
        var access = member.OrganizationProduct;
        var claims = BaseUserClaims(user);
        claims.Add(new(VortexClaims.TokenUse, VortexClaims.TokenUseProduct));
        claims.Add(new(VortexClaims.Product, access.Product.Code));
        claims.Add(new(VortexClaims.Access, access.Id.ToString()));
        claims.Add(new(VortexClaims.Device, device.Id.ToString()));
        if (member.Role is not null)
            claims.Add(new(VortexClaims.ProductRole, member.Role));
        claims.AddRange(access.Features.Select(f => new Claim(VortexClaims.Features, f)));

        var expires = Cap(clock.GetUtcNow().AddMinutes(_options.AccessTokenMinutes), access.ExpiresAt);
        return Create(TokenAudiences.ForProduct(access.Product.Code), claims, expires);
    }

    /// <summary>
    /// Comprobante firmado que el sistema guarda localmente para operar sin conexión durante
    /// <see cref="OrganizationProduct.OfflineGraceDays"/>. Nunca vence después que el acceso.
    /// </summary>
    public IssuedToken CreateOfflineToken(User user, ProductMember member, Device device)
    {
        var access = member.OrganizationProduct;
        var claims = new List<Claim>
        {
            new(VortexClaims.Subject, user.Id.ToString()),
            new(VortexClaims.Organization, access.OrganizationId.ToString()),
            new(VortexClaims.OrganizationName, access.Organization.Name),
            new(VortexClaims.Product, access.Product.Code),
            new(VortexClaims.Access, access.Id.ToString()),
            new(VortexClaims.DeviceHash, device.DeviceHash),
        };
        if (member.Role is not null)
            claims.Add(new(VortexClaims.ProductRole, member.Role));
        if (access.ExpiresAt is { } accessExpires)
            claims.Add(new(VortexClaims.AccessExpires, accessExpires.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
        claims.AddRange(access.Features.Select(f => new Claim(VortexClaims.Features, f)));

        var expires = Cap(clock.GetUtcNow().AddDays(access.OfflineGraceDays), access.ExpiresAt);
        return Create(TokenAudiences.Offline, claims, expires, tokenType: "vortex-offline+jwt");
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = _options.Issuer,
        ValidateAudience = true,
        AudienceValidator = (audiences, _, _) => audiences.Any(IsAccessAudience),
        IssuerSigningKey = keys.PublicKey,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ValidTypes = ["at+jwt"],
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = VortexClaims.Name,
        RoleClaimType = VortexClaims.Role,
    };

    private static bool IsAccessAudience(string audience) =>
        audience == TokenAudiences.Admin ||
        ProductCodes.Defaults.Keys.Any(code => audience == TokenAudiences.ForProduct(code));

    private static List<Claim> BaseUserClaims(User user)
    {
        var claims = new List<Claim>
        {
            new(VortexClaims.Subject, user.Id.ToString()),
            new(VortexClaims.Email, user.Email),
            new(VortexClaims.Name, user.FullName),
            new(VortexClaims.Role, user.Role.ToString()),
            new(VortexClaims.SecurityStamp, user.SecurityStamp),
        };
        if (user.OrganizationId is { } orgId)
            claims.Add(new(VortexClaims.Organization, orgId.ToString()));
        return claims;
    }

    private IssuedToken Create(string audience, List<Claim> claims, DateTimeOffset expires, string tokenType = "at+jwt")
    {
        var now = clock.GetUtcNow();
        claims.Add(new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")));

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = keys.Credentials,
            TokenType = tokenType,
        });
        return new IssuedToken(token, expires);
    }

    private static DateTimeOffset Cap(DateTimeOffset value, DateTimeOffset? max) =>
        max is { } m && m < value ? m : value;
}
