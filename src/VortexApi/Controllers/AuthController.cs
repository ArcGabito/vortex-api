using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VortexApi.Contracts;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers;

/// <summary>Autenticación de las apps Vortex (ERP, CRM, WMS, POS).</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    public const string ProductHeader = "X-Vortex-Product";
    public const string ProductKeyHeader = "X-Vortex-Product-Key";

    /// <summary>
    /// Inicia sesión desde una app Vortex. Valida la app, las credenciales, la licencia de la organización,
    /// el puesto del usuario y el límite de equipos. Devuelve access token, refresh token y license token.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public Task<ProductSessionResponse> Login(
        [FromHeader(Name = ProductHeader)] string? productCode,
        [FromHeader(Name = ProductKeyHeader)] string? productKey,
        ProductLoginRequest request) =>
        auth.ProductLoginAsync(productCode, productKey, request);

    /// <summary>Rota el refresh token y vuelve a validar licencia y equipo. Requiere el mismo <c>deviceId</c> del login.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public Task<ProductSessionResponse> Refresh(RefreshRequest request) => auth.ProductRefreshAsync(request);

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(LogoutRequest request)
    {
        await auth.LogoutAsync(request.RefreshToken);
        return NoContent();
    }

    /// <summary>Cambia la contraseña del usuario autenticado y cierra todas sus sesiones.</summary>
    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        await auth.ChangePasswordAsync(User.UserId(), request);
        return NoContent();
    }
}

[ApiController]
[Route("api/licensing")]
[Authorize(Policy = Policies.ProductClient)]
public sealed class LicensingController(AuthService auth) : ControllerBase
{
    /// <summary>
    /// Verificación periódica desde la app: confirma que licencia, usuario y equipo siguen vigentes y entrega un
    /// license token renovado para el modo sin conexión.
    /// </summary>
    [HttpPost("heartbeat")]
    public Task<HeartbeatResponse> Heartbeat() => auth.HeartbeatAsync(User.UserId(), User.DeviceActivationId()!.Value);
}

[ApiController]
[Route("api/admin/auth")]
public sealed class AdminAuthController(AuthService auth) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public Task<AdminSessionResponse> Login(AdminLoginRequest request) => auth.AdminLoginAsync(request);

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public Task<AdminSessionResponse> Refresh(RefreshRequest request) => auth.AdminRefreshAsync(request);
}
