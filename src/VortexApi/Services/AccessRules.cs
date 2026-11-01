using VortexApi.Domain;
using VortexApi.Infrastructure;

namespace VortexApi.Services;

public static class AccessRules
{
    public static bool IsUsableAt(this OrganizationProduct access, DateTimeOffset now) =>
        access.IsEnabled && (access.ExpiresAt is null || now < access.ExpiresAt);

    /// <summary>Lanza el error que impide usar el sistema ahora, si lo hay.</summary>
    public static void EnsureUsable(OrganizationProduct access, DateTimeOffset now)
    {
        if (!access.IsEnabled)
            throw new ApiException(StatusCodes.Status403Forbidden, ErrorCodes.ProductDisabled,
                $"{access.Product.Name} está deshabilitado para la organización.");
        if (access.ExpiresAt <= now)
            throw new ApiException(StatusCodes.Status403Forbidden, ErrorCodes.AccessExpired,
                $"El acceso a {access.Product.Name} venció.");
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
}
