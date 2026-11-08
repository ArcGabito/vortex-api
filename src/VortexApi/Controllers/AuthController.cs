using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VortexApi.Contracts;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers;

/// <summary>Autenticación de los sistemas Vortex (ERP, CRM, WMS, POS).</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    public const string ProductHeader = "X-Vortex-Product";
    public const string ProductKeyHeader = "X-Vortex-Product-Key";

    /// <summary>
    /// Inicia sesión desde un sistema Vortex. Valida el sistema, las credenciales, que el sistema esté habilitado
    /// para la organización, el acceso del usuario y el límite de equipos (si lo hay). Devuelve access token,
    /// refresh token y offline token.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public Task<ProductSessionResponse> Login(
        [FromHeader(Name = ProductHeader)] string? productCode,
        [FromHeader(Name = ProductKeyHeader)] string? productKey,
        ProductLoginRequest request) =>
        auth.ProductLoginAsync(productCode, productKey, request);

    /// <summary>Rota el refresh token y vuelve a validar el acceso. Requiere el mismo <c>deviceId</c> del login.</summary>
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

/// <summary>
    /// Preferencias del usuario autenticado (hoy, el idioma). No cierra sesiones: el idioma nuevo llega en el próximo
    /// login o refresh, y la app que lo cambió ya lo está usando.
    /// </summary>
    [HttpPut("me/preferences")]
    [Authorize]
    public Task<UserInfo> Preferences(PreferencesRequest request) => auth.SetPreferencesAsync(User.UserId(), request);

    /// <summary>
    /// Fija o quita el PIN de caja del usuario autenticado (pide la contraseña actual). No cierra sesiones: los equipos del
    /// sistema reciben el verificador nuevo en su próxima consulta de <c>api/access/pins</c>.
    /// </summary>
    [HttpPut("me/pin")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> SetPin(SetPinRequest request)
    {
        await auth.SetPinAsync(User.UserId(), request);
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
[Route("api/access")]
[Authorize(Policy = Policies.ProductClient)]
public sealed class AccessController(AuthService auth) : ControllerBase
{
    /// <summary>
    /// Verificación periódica desde el sistema: confirma que usuario, acceso y equipo siguen vigentes y entrega un
    /// offline token renovado para trabajar sin conexión.
    /// </summary>
    [HttpPost("heartbeat")]
    public Task<HeartbeatResponse> Heartbeat() => auth.HeartbeatAsync(User.UserId(), User.DeviceId()!.Value);

    /// <summary>
    /// Verificadores de PIN de los miembros de este sistema en la organización del equipo (para el cambio rápido de
    /// operador y las autorizaciones sin conexión). Solo los usuarios activos con PIN.
    /// </summary>
    [HttpGet("pins")]
    public Task<IReadOnlyList<PinVerifier>> Pins() => auth.GetPinVerifiersAsync(User.UserId(), User.DeviceId()!.Value);

    /// <summary>Sistemas de la suite a los que el usuario puede entrar (para mostrar un selector de sistemas).</summary>
    [HttpGet("products")]
    public Task<IReadOnlyList<AvailableProduct>> Products() => auth.GetAvailableProductsAsync(User.UserId());
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
