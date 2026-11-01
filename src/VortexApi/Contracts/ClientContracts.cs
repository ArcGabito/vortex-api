using System.ComponentModel.DataAnnotations;

namespace VortexApi.Contracts;

// Contratos usados por los sistemas Vortex (ERP, CRM, WMS, POS).

public sealed record ProductLoginRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(256)] string Password,
    [Required, MinLength(16), MaxLength(512)] string DeviceId,
    [MaxLength(200)] string? DeviceName,
    [MaxLength(50)] string? Platform);

public sealed record RefreshRequest(
    [Required] string RefreshToken,
    [MaxLength(512)] string? DeviceId);

public sealed record LogoutRequest([Required] string RefreshToken);

public sealed record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(10), MaxLength(256)] string NewPassword);

/// <summary>Acceso del usuario a un sistema en un equipo concreto.</summary>
public sealed record ProductAccessInfo(
    Guid AccessId,
    string Product,
    Guid OrganizationId,
    string OrganizationName,
    string? Role,
    IReadOnlyList<string> Features,
    DateTimeOffset? ExpiresAt,
    Guid DeviceId);

public sealed record ProductSessionResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string OfflineToken,
    DateTimeOffset OfflineTokenExpiresAt,
    UserInfo User,
    ProductAccessInfo Access);

public sealed record HeartbeatResponse(
    string OfflineToken,
    DateTimeOffset OfflineTokenExpiresAt,
    ProductAccessInfo Access);

/// <summary>Sistema de la suite al que el usuario puede entrar (para un selector de sistemas).</summary>
public sealed record AvailableProduct(string Code, string Name, string? Role, IReadOnlyList<string> Features);

public sealed record UserInfo(Guid Id, string Email, string FullName, string Role, Guid? OrganizationId);
