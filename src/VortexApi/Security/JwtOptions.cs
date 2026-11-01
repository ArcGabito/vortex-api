namespace VortexApi.Security;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "https://licensing.vortex.local";
    public int AccessTokenMinutes { get; set; } = 15;
    public int ProductRefreshTokenDays { get; set; } = 30;
    public int AdminRefreshTokenHours { get; set; } = 12;

    /// <summary>Llave RSA privada en PEM (p. ej. desde Azure Key Vault). Tiene prioridad sobre <see cref="SigningKeyPath"/>.</summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>Ruta de un archivo PEM con la llave RSA privada.</summary>
    public string? SigningKeyPath { get; set; }

    /// <summary>Si no existe la llave en <see cref="SigningKeyPath"/>, generarla. Solo para desarrollo.</summary>
    public bool GenerateKeyIfMissing { get; set; }
}

public static class TokenAudiences
{
    public const string Admin = "vortex-admin";
    public const string License = "vortex-license";

    public static string ForProduct(string productCode) => $"vortex-{productCode.ToLowerInvariant()}";
}

public static class VortexClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string TokenUse = "tu";
    public const string Organization = "org";
    public const string OrganizationName = "org_name";
    public const string Product = "prd";
    public const string License = "lic";
    public const string LicenseKey = "lic_key";
    public const string LicenseExpires = "lic_exp";
    public const string Plan = "plan";
    public const string Features = "feat";
    public const string Device = "dev";
    public const string DeviceHash = "dev_hash";
    public const string SecurityStamp = "sst";

    public const string TokenUseAdmin = "admin";
    public const string TokenUseProduct = "product";
}
