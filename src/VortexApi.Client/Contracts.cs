namespace VortexApi.Client;

public sealed record VortexUser(Guid Id, string Email, string FullName, string Role, Guid? OrganizationId);

public sealed record VortexLicense(
    Guid LicenseId,
    string LicenseKey,
    string Product,
    string Plan,
    Guid OrganizationId,
    string OrganizationName,
    IReadOnlyList<string> Features,
    DateTimeOffset ExpiresAt,
    Guid DeviceActivationId)
{
    public bool HasFeature(string feature) => Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Sesión devuelta por login/refresh. La app debe guardar RefreshToken y LicenseToken en almacenamiento seguro.</summary>
public sealed record VortexSession(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string LicenseToken,
    DateTimeOffset LicenseTokenExpiresAt,
    VortexUser User,
    VortexLicense License);

public sealed record VortexHeartbeat(string LicenseToken, DateTimeOffset LicenseTokenExpiresAt, VortexLicense License);

/// <summary>Error devuelto por la API. <see cref="Code"/> es estable (p. ej. <c>license_expired</c>, <c>no_seat</c>).</summary>
public sealed class VortexApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    /// <summary>Errores que indican que la licencia ya no permite usar la app y no se arreglan reintentando.</summary>
    public bool IsLicenseDenied => Code is "no_license" or "no_seat" or "license_expired" or "license_not_started"
        or "license_suspended" or "license_revoked" or "device_limit_reached" or "device_revoked"
        or "account_disabled" or "organization_disabled";
}
