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
    int UserCount, IReadOnlyList<string> EnabledProducts);

// Usuarios

public sealed record CreateUserRequest(
    Guid? OrganizationId,
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(200)] string FullName,
    [Required, MinLength(10), MaxLength(256)] string Password,
    UserRole Role = UserRole.User,
    [MaxLength(10)] string? Language = null);

public sealed record UpdateUserRequest(
    [Required, MaxLength(200)] string FullName,
    UserRole Role,
    bool IsActive,
    [MaxLength(10)] string? Language = null);

public sealed record ResetPasswordRequest([Required, MinLength(10), MaxLength(256)] string NewPassword);

public sealed record UserDto(
    Guid Id, Guid? OrganizationId, string Email, string FullName, string Role, bool IsActive,
    bool IsLockedOut, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, string? Language);

// Sistemas habilitados por organización

/// <summary>Habilita o configura un sistema para la organización. Límites y vencimiento son opcionales.</summary>
public sealed record OrganizationProductRequest(
    bool IsEnabled = true,
    [MaxLength(500)] string? DisabledReason = null,
    bool AutoAssignUsers = false,
    [Range(1, 1_000_000)] int? MaxUsers = null,
    [Range(1, 1_000)] int? MaxDevicesPerUser = null,
    [Range(0, 365)] int OfflineGraceDays = 7,
    List<string>? Features = null,
    DateTimeOffset? ExpiresAt = null);

/// <param name="SiteIds">Sedes en las que trabaja el usuario en este sistema; vacío o null: todas.</param>
public sealed record MemberRequest([MaxLength(50)] string? Role, [MaxLength(200)] List<Guid>? SiteIds = null);

public sealed record OrganizationProductDto(
    Guid Id, Guid OrganizationId, string ProductCode, string ProductName, bool IsEnabled, string? DisabledReason,
    bool AutoAssignUsers, int? MaxUsers, int Members, int? MaxDevicesPerUser, int OfflineGraceDays,
    IReadOnlyList<string> Features, DateTimeOffset? ExpiresAt, bool IsUsableNow);

public sealed record MemberDto(Guid MemberId, Guid UserId, string Email, string FullName, string? Role,
    DateTimeOffset AddedAt, IReadOnlyList<DeviceDto> Devices, IReadOnlyList<Guid> SiteIds);

public sealed record DeviceDto(Guid Id, string? DeviceName, string? Platform, DateTimeOffset ActivatedAt,
    DateTimeOffset LastSeenAt, string? LastIp, bool IsRevoked);

public sealed record OrganizationProductDetailDto(OrganizationProductDto Product, IReadOnlyList<MemberDto> Members);

// Auditoría

public sealed record AuditLogDto(long Id, DateTimeOffset Timestamp, string Action, bool Success, Guid? ActorUserId,
    Guid? OrganizationId, string? ProductCode, string? TargetId, string? Detail, string? IpAddress);
