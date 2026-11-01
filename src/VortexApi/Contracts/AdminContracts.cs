using System.ComponentModel.DataAnnotations;
using VortexApi.Domain;

namespace VortexApi.Contracts;

public sealed record AdminLoginRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(256)] string Password);

public sealed record AdminSessionResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserInfo User);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

// Productos

public sealed record ProductDto(Guid Id, string Code, string Name, bool IsActive, bool HasClientKey);

public sealed record ProductKeyResponse(string Code, string ClientKey);

// Organizaciones

public sealed record OrganizationRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(50)] string? TaxId,
    [EmailAddress, MaxLength(256)] string? ContactEmail);

public sealed record OrganizationDto(
    Guid Id, string Name, string? TaxId, string? ContactEmail, bool IsActive, DateTimeOffset CreatedAt,
    int UserCount, int LicenseCount);

// Usuarios

public sealed record CreateUserRequest(
    Guid? OrganizationId,
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(200)] string FullName,
    [Required, MinLength(10), MaxLength(256)] string Password,
    UserRole Role = UserRole.User);

public sealed record UpdateUserRequest(
    [Required, MaxLength(200)] string FullName,
    UserRole Role,
    bool IsActive);

public sealed record ResetPasswordRequest([Required, MinLength(10), MaxLength(256)] string NewPassword);

public sealed record UserDto(
    Guid Id, Guid? OrganizationId, string Email, string FullName, string Role, bool IsActive,
    bool IsLockedOut, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

// Licencias

public sealed record CreateLicenseRequest(
    [Required] Guid OrganizationId,
    [Required, MaxLength(16)] string ProductCode,
    [Required, MaxLength(50)] string Plan,
    [Range(1, 100_000)] int MaxSeats,
    [Range(1, 100)] int MaxDevicesPerUser,
    [Range(0, 30)] int OfflineGraceDays,
    DateTimeOffset? StartsAt,
    [Required] DateTimeOffset ExpiresAt,
    List<string>? Features);

public sealed record UpdateLicenseRequest(
    [Required, MaxLength(50)] string Plan,
    [Range(1, 100_000)] int MaxSeats,
    [Range(1, 100)] int MaxDevicesPerUser,
    [Range(0, 30)] int OfflineGraceDays,
    [Required] DateTimeOffset StartsAt,
    [Required] DateTimeOffset ExpiresAt,
    List<string>? Features);

public sealed record LicenseStatusRequest([MaxLength(500)] string? Reason);

public sealed record AssignSeatRequest([Required] Guid UserId);

public sealed record LicenseDto(
    Guid Id, string LicenseKey, Guid OrganizationId, string OrganizationName, string ProductCode, string Plan,
    int MaxSeats, int UsedSeats, int MaxDevicesPerUser, int OfflineGraceDays, IReadOnlyList<string> Features,
    DateTimeOffset StartsAt, DateTimeOffset ExpiresAt, string Status, string? StatusReason, bool IsValidNow);

public sealed record SeatDto(Guid SeatId, Guid UserId, string Email, string FullName, DateTimeOffset AssignedAt,
    IReadOnlyList<DeviceDto> Devices);

public sealed record DeviceDto(Guid Id, string? DeviceName, string? Platform, DateTimeOffset ActivatedAt,
    DateTimeOffset LastSeenAt, string? LastIp, bool IsRevoked);

public sealed record LicenseDetailDto(LicenseDto License, IReadOnlyList<SeatDto> Seats);

// Auditoría

public sealed record AuditLogDto(long Id, DateTimeOffset Timestamp, string Action, bool Success, Guid? ActorUserId,
    Guid? OrganizationId, string? ProductCode, string? TargetId, string? Detail, string? IpAddress);
