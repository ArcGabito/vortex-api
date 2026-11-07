namespace VortexApi.Client;

/// <summary>Usuario de la sesión. <c>Language</c>: idioma preferido (<c>es</c>, <c>en</c>, <c>pt</c>); null = el del país de la empresa.</summary>
public sealed record VortexUser(Guid Id, string Email, string FullName, string Role, Guid? OrganizationId, string? Language = null);

/// <summary>Acceso del usuario al sistema en este equipo.</summary>
public sealed record VortexAccess(
    Guid AccessId,
    string Product,
    Guid OrganizationId,
    string OrganizationName,
    string? Role,
    IReadOnlyList<string> Features,
    DateTimeOffset? ExpiresAt,
    Guid DeviceId,
    IReadOnlyList<Guid>? SiteIds = null)
{
    public bool HasFeature(string feature) => Features.Contains(feature, StringComparer.OrdinalIgnoreCase);

    /// <summary>Sedes asignadas al usuario en este sistema. Vacío: puede trabajar en todas.</summary>
    public bool CanUseSite(Guid siteId) => SiteIds is not { Count: > 0 } || SiteIds.Contains(siteId);
}

/// <summary>Sesión devuelta por login/refresh. El sistema debe guardar RefreshToken y OfflineToken en almacenamiento seguro.</summary>
public sealed record VortexSession(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string OfflineToken,
    DateTimeOffset OfflineTokenExpiresAt,
    VortexUser User,
    VortexAccess Access);

public sealed record VortexHeartbeat(string OfflineToken, DateTimeOffset OfflineTokenExpiresAt, VortexAccess Access);

/// <summary>Sistema de la suite al que el usuario puede entrar.</summary>
public sealed record VortexAvailableProduct(string Code, string Name, string? Role, IReadOnlyList<string> Features);

/// <summary>Error devuelto por la API. <see cref="Code"/> es estable (p. ej. <c>no_access</c>, <c>device_revoked</c>).</summary>
public sealed class VortexApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    /// <summary>Errores que indican que el usuario ya no puede usar el sistema y no se arreglan reintentando.</summary>
    public bool IsAccessDenied => Code is "product_not_enabled" or "product_disabled" or "access_expired"
        or "no_access" or "user_limit_reached" or "device_limit_reached" or "device_revoked"
        or "account_disabled" or "organization_disabled";
}
