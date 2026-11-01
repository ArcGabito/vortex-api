using System.ComponentModel.DataAnnotations;

namespace VortexApi.Contracts;

// Contratos usados por las apps Vortex (ERP, CRM, WMS, POS).

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

public sealed record LicenseInfo(
    Guid LicenseId,
    string LicenseKey,
    string Product,
    string Plan,
    Guid OrganizationId,
    string OrganizationName,
    IReadOnlyList<string> Features,
    DateTimeOffset ExpiresAt,
    Guid DeviceActivationId);

public sealed record ProductSessionResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string LicenseToken,
    DateTimeOffset LicenseTokenExpiresAt,
    UserInfo User,
    LicenseInfo License);

public sealed record HeartbeatResponse(
    string LicenseToken,
    DateTimeOffset LicenseTokenExpiresAt,
    LicenseInfo License);

public sealed record UserInfo(Guid Id, string Email, string FullName, string Role, Guid? OrganizationId);
