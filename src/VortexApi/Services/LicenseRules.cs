using VortexApi.Domain;
using VortexApi.Infrastructure;

namespace VortexApi.Services;

public static class LicenseRules
{
    public static bool IsValidAt(this License license, DateTimeOffset now) =>
        license.Status == LicenseStatus.Active && license.StartsAt <= now && now < license.ExpiresAt;

    /// <summary>Lanza el error específico que impide usar la licencia ahora, si lo hay.</summary>
    public static void EnsureValid(License license, DateTimeOffset now)
    {
        var (code, message) = license switch
        {
            { Status: LicenseStatus.Revoked } => (ErrorCodes.LicenseRevoked, "La licencia fue revocada."),
            { Status: LicenseStatus.Suspended } => (ErrorCodes.LicenseSuspended, "La licencia está suspendida."),
            _ when now < license.StartsAt => (ErrorCodes.LicenseNotStarted, "La licencia aún no está vigente."),
            _ when now >= license.ExpiresAt => (ErrorCodes.LicenseExpired, "La licencia expiró."),
            _ => (null, null),
        };
        if (code is not null)
            throw new ApiException(StatusCodes.Status403Forbidden, code, message!);
    }

    public static void EnsureUserCanSignIn(User user, DateTimeOffset now)
    {
        if (user.LockoutEnd > now)
            throw new ApiException(StatusCodes.Status423Locked, ErrorCodes.AccountLocked,
                "La cuenta está bloqueada temporalmente por intentos fallidos.");
        if (!user.IsActive)
            throw new ApiException(StatusCodes.Status403Forbidden, ErrorCodes.AccountDisabled, "La cuenta está deshabilitada.");
        if (user.Organization is { IsActive: false })
            throw new ApiException(StatusCodes.Status403Forbidden, ErrorCodes.OrganizationDisabled,
                "La organización está deshabilitada.");
    }

    public static string GenerateLicenseKey(string productCode)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(alphabet, 16);
        var s = new string(chars);
        return $"VX-{productCode}-{s[..4]}-{s[4..8]}-{s[8..12]}-{s[12..]}";
    }
}
