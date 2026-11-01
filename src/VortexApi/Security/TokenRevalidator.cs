using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using VortexApi.Data;
using VortexApi.Domain;

namespace VortexApi.Security;

/// <summary>
/// En cada petición autenticada confirma contra la base de datos que el token sigue vigente: usuario activo,
/// security stamp sin cambios, organización activa y, para tokens de producto, equipo no revocado y licencia
/// válida. Así una revocación surte efecto de inmediato en esta API sin esperar a que expire el access token.
/// </summary>
public static class TokenRevalidator
{
    public static async Task OnTokenValidated(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var db = context.HttpContext.RequestServices.GetRequiredService<VortexDbContext>();
        var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var userId = principal.UserId();
        var stamp = principal.FindFirst(VortexClaims.SecurityStamp)?.Value;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsActive, u.SecurityStamp, OrgActive = u.Organization == null || u.Organization.IsActive })
            .SingleOrDefaultAsync();

        if (user is null || !user.IsActive || !user.OrgActive || user.SecurityStamp != stamp)
        {
            context.Fail("Sesión revocada.");
            return;
        }

        if (principal.FindFirst(VortexClaims.TokenUse)?.Value != VortexClaims.TokenUseProduct)
            return;

        var deviceId = principal.DeviceActivationId();
        var device = await db.DeviceActivations.AsNoTracking()
            .Where(d => d.Id == deviceId && d.Seat.UserId == userId)
            .Select(d => new { d.RevokedAt, d.Seat.License.Status, d.Seat.License.StartsAt, d.Seat.License.ExpiresAt })
            .SingleOrDefaultAsync();

        if (device is null || device.RevokedAt is not null || device.Status != LicenseStatus.Active ||
            now < device.StartsAt || now >= device.ExpiresAt)
        {
            context.Fail("Licencia o equipo revocado.");
        }
    }
}
