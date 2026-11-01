namespace VortexApi.Infrastructure;

/// <summary>Error de negocio que se devuelve como ProblemDetails con un <c>code</c> estable para los clientes.</summary>
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static ApiException NotFound(string what) => new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"{what} no encontrado.");
    public static ApiException Forbidden() => new(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "No tiene acceso a este recurso.");
    public static ApiException BadRequest(string code, string message) => new(StatusCodes.Status400BadRequest, code, message);
    public static ApiException Conflict(string code, string message) => new(StatusCodes.Status409Conflict, code, message);
}

public static class ErrorCodes
{
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string Validation = "validation_error";

    public const string InvalidProduct = "invalid_product";
    public const string InvalidCredentials = "invalid_credentials";
    public const string AccountLocked = "account_locked";
    public const string AccountDisabled = "account_disabled";
    public const string OrganizationDisabled = "organization_disabled";
    public const string ProductNotEnabled = "product_not_enabled";
    public const string ProductDisabled = "product_disabled";
    public const string AccessExpired = "access_expired";
    public const string NoAccess = "no_access";
    public const string UserLimitReached = "user_limit_reached";
    public const string DeviceLimitReached = "device_limit_reached";
    public const string DeviceRevoked = "device_revoked";
    public const string DeviceMismatch = "device_mismatch";
    public const string InvalidRefreshToken = "invalid_refresh_token";
    public const string RefreshTokenReused = "refresh_token_reused";

    public const string EmailInUse = "email_in_use";
    public const string LimitInUse = "limit_in_use";
    public const string WeakPassword = "weak_password";
}
