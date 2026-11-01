using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using VortexApi.Data;

namespace VortexApi.Security;

/// <summary>
/// En cada petición autenticada confirma contra la base de datos que el token sigue vigente: usuario activo,
/// security stamp sin cambios, organización activa y, para tokens de sistema, equipo no revocado y sistema
/// habilitado. Así una revocación surte efecto de inmediato en esta API sin esperar a que expire el access token.
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

        if (principal.IsIntegration())
        {
            var client = await db.IntegrationClients.AsNoTracking()
                .Where(c => c.Id == userId)
                .Select(c => new { c.IsActive, c.SecurityStamp, c.ExpiresAt, OrgActive = c.Organization.IsActive })
                .SingleOrDefaultAsync();
            if (client is null || !client.IsActive || !client.OrgActive || client.SecurityStamp != stamp || client.ExpiresAt <= now)
                context.Fail("Aplicación de integración revocada.");
            return;
        }

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

        var deviceId = principal.DeviceId();
        var device = await db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId && d.Member.UserId == userId)
            .Select(d => new { d.RevokedAt, d.Member.OrganizationProduct.IsEnabled, d.Member.OrganizationProduct.ExpiresAt })
            .SingleOrDefaultAsync();

        if (device is null || device.RevokedAt is not null || !device.IsEnabled || device.ExpiresAt <= now)
            context.Fail("Acceso o equipo revocado.");
    }
}
