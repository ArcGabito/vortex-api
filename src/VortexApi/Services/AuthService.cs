using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Security;

namespace VortexApi.Services;

public sealed class LockoutOptions
{
    public const string Section = "Lockout";

    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}

/// <summary>
/// Inicio de sesión de las apps Vortex y de la consola de administración. Cada inicio de sesión y cada
/// renovación vuelve a validar usuario, organización, licencia, puesto y equipo.
/// </summary>
public sealed class AuthService(
    VortexDbContext db,
    IPasswordHasher hasher,
    TokenService tokens,
    AuditService audit,
    TimeProvider clock,
    IHttpContextAccessor http,
    IOptions<JwtOptions> jwtOptions,
    IOptions<LockoutOptions> lockoutOptions)
{
    // Se verifica contra este hash cuando el correo no existe para no revelar cuentas por tiempo de respuesta.
    private static readonly Lazy<string> DummyHash = new(() => new Pbkdf2PasswordHasher().Hash(Guid.NewGuid().ToString()));

    private readonly JwtOptions _jwt = jwtOptions.Value;
    private readonly LockoutOptions _lockout = lockoutOptions.Value;

    private string? ClientIp => http.HttpContext?.Connection.RemoteIpAddress?.ToString();

    // ---------- Apps Vortex ----------

    public async Task<ProductSessionResponse> ProductLoginAsync(string? productCode, string? productKey, ProductLoginRequest req)
    {
        Guid? userId = null, orgId = null;
        try
        {
            var product = await ResolveProductAsync(productCode, productKey);
            var user = await VerifyCredentialsAsync(req.Email, req.Password);
            (userId, orgId) = (user.Id, user.OrganizationId);
            var now = clock.GetUtcNow();

            if (user.OrganizationId is null)
                throw Forbidden(ErrorCodes.NoLicense, "Los administradores de plataforma no consumen licencias de producto.");

            var licenses = await db.Licenses
                .Include(l => l.Product)
                .Include(l => l.Organization)
                .Where(l => l.OrganizationId == user.OrganizationId && l.ProductId == product.Id)
                .ToListAsync();
            if (licenses.Count == 0)
                throw Forbidden(ErrorCodes.NoLicense, $"La organización no tiene licencia de {product.Name}.");

            var licenseIds = licenses.Select(l => l.Id).ToList();
            var seats = await db.LicenseSeats
                .Include(s => s.Devices)
                .Where(s => s.UserId == user.Id && licenseIds.Contains(s.LicenseId))
                .ToListAsync();
            if (seats.Count == 0)
                throw Forbidden(ErrorCodes.NoSeat, $"El usuario no tiene un puesto asignado en {product.Name}.");

            var ordered = seats.OrderByDescending(s => s.License.ExpiresAt).ToList();
            var seat = ordered.FirstOrDefault(s => s.License.IsValidAt(now));
            if (seat is null)
            {
                LicenseRules.EnsureValid(ordered[0].License, now);
                throw Forbidden(ErrorCodes.NoLicense, "No hay una licencia vigente.");
            }

            var device = ActivateDevice(seat, req, now);
            user.LastLoginAt = now;

            var session = IssueProductSession(user, seat.License, device, Guid.NewGuid(), now, out _);
            audit.Record(AuditActions.ProductLogin, true, user.Id, user.OrganizationId, product.Code, seat.LicenseId.ToString());
            await db.SaveChangesAsync();
            return session;
        }
        catch (ApiException ex)
        {
            await audit.RecordNowAsync(AuditActions.ProductLogin, false, userId, orgId, productCode?.ToUpperInvariant(),
                detail: $"{ex.Code}: {req.Email}");
            throw;
        }
    }

    public async Task<ProductSessionResponse> ProductRefreshAsync(RefreshRequest req)
    {
        var now = clock.GetUtcNow();
        var old = await ConsumeRefreshTokenAsync(req.RefreshToken, now);
        try
        {
            if (old.ProductId is null)
                throw InvalidRefresh();
            if (old.DeviceActivationId is null)
                throw Forbidden(ErrorCodes.DeviceRevoked, "El equipo ya no está activado.");

            var device = await db.DeviceActivations
                .Include(d => d.Seat).ThenInclude(s => s.License).ThenInclude(l => l.Product)
                .Include(d => d.Seat).ThenInclude(s => s.License).ThenInclude(l => l.Organization)
                .SingleAsync(d => d.Id == old.DeviceActivationId);

            EnsureDeviceMatches(device, req.DeviceId);
            LicenseRules.EnsureValid(device.Seat.License, now);

            device.LastSeenAt = now;
            device.LastIp = ClientIp;
            var session = IssueProductSession(old.User, device.Seat.License, device, old.FamilyId, now, out var next);
            old.ReplacedById = next.Id;
            await db.SaveChangesAsync();
            return session;
        }
        catch (ApiException ex)
        {
            await audit.RecordNowAsync(AuditActions.Refresh, false, old.UserId, old.User.OrganizationId, detail: ex.Code);
            throw;
        }
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(Guid userId, Guid deviceActivationId)
    {
        var now = clock.GetUtcNow();
        var device = await db.DeviceActivations
            .Include(d => d.Seat).ThenInclude(s => s.User).ThenInclude(u => u.Organization)
            .Include(d => d.Seat).ThenInclude(s => s.License).ThenInclude(l => l.Product)
            .Include(d => d.Seat).ThenInclude(s => s.License).ThenInclude(l => l.Organization)
            .SingleOrDefaultAsync(d => d.Id == deviceActivationId && d.Seat.UserId == userId);
        try
        {
            if (device is null || device.RevokedAt is not null)
                throw Forbidden(ErrorCodes.DeviceRevoked, "El equipo ya no está activado.");

            LicenseRules.EnsureUserCanSignIn(device.Seat.User, now);
            LicenseRules.EnsureValid(device.Seat.License, now);
        }
        catch (ApiException ex)
        {
            await audit.RecordNowAsync(AuditActions.Heartbeat, false, userId, device?.Seat.License.OrganizationId,
                device?.Seat.License.Product.Code, deviceActivationId.ToString(), ex.Code);
            throw;
        }

        device.LastSeenAt = now;
        device.LastIp = ClientIp;
        await db.SaveChangesAsync();

        var license = device.Seat.License;
        var licenseToken = tokens.CreateLicenseToken(device.Seat.User, license, device);
        return new HeartbeatResponse(licenseToken.Token, licenseToken.ExpiresAt, ToLicenseInfo(license, device));
    }

    // ---------- Consola de administración ----------

    public async Task<AdminSessionResponse> AdminLoginAsync(AdminLoginRequest req)
    {
        Guid? userId = null;
        try
        {
            var user = await VerifyCredentialsAsync(req.Email, req.Password);
            userId = user.Id;
            if (user.Role == UserRole.User)
                throw Forbidden(ErrorCodes.Forbidden, "El usuario no es administrador.");

            var now = clock.GetUtcNow();
            user.LastLoginAt = now;
            var session = IssueAdminSession(user, Guid.NewGuid(), now, out _);
            audit.Record(AuditActions.AdminLogin, true, user.Id, user.OrganizationId);
            await db.SaveChangesAsync();
            return session;
        }
        catch (ApiException ex)
        {
            await audit.RecordNowAsync(AuditActions.AdminLogin, false, userId, detail: $"{ex.Code}: {req.Email}");
            throw;
        }
    }

    public async Task<AdminSessionResponse> AdminRefreshAsync(RefreshRequest req)
    {
        var now = clock.GetUtcNow();
        var old = await ConsumeRefreshTokenAsync(req.RefreshToken, now);
        if (old.ProductId is not null || old.User.Role == UserRole.User)
        {
            await db.SaveChangesAsync();
            throw InvalidRefresh();
        }

        var session = IssueAdminSession(old.User, old.FamilyId, now, out var next);
        old.ReplacedById = next.Id;
        await db.SaveChangesAsync();
        return session;
    }

    // ---------- Común ----------

    public async Task LogoutAsync(string refreshToken)
    {
        var hash = Hashing.Sha256(refreshToken);
        var token = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash);
        if (token is null)
            return;

        await RevokeFamilyAsync(token.FamilyId);
        await audit.RecordNowAsync(AuditActions.Logout, true, token.UserId);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest req)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        if (!hasher.Verify(req.CurrentPassword, user.PasswordHash))
            throw new ApiException(StatusCodes.Status400BadRequest, ErrorCodes.InvalidCredentials, "La contraseña actual no es correcta.");

        await SetPasswordAsync(user, req.NewPassword);
        audit.Record(AuditActions.PasswordChanged, true, user.Id, user.OrganizationId, targetId: user.Id.ToString());
        await db.SaveChangesAsync();
    }

    /// <summary>Cambia la contraseña, invalida los access tokens (security stamp) y cierra todas las sesiones.</summary>
    public async Task SetPasswordAsync(User user, string newPassword)
    {
        PasswordPolicy.Ensure(newPassword, user.Email);
        user.PasswordHash = hasher.Hash(newPassword);
        await InvalidateUserSessionsAsync(user);
    }

    public async Task InvalidateUserSessionsAsync(User user)
    {
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
    }

    public async Task RevokeDeviceSessionsAsync(Guid deviceActivationId)
    {
        var now = clock.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.DeviceActivationId == deviceActivationId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
    }

    // ---------- Internos ----------

    private async Task<Product> ResolveProductAsync(string? productCode, string? productKey)
    {
        var code = productCode?.Trim().ToUpperInvariant();
        var product = code is null ? null : await db.Products.SingleOrDefaultAsync(p => p.Code == code);

        if (product is not { IsActive: true, ClientKeyHash: not null } ||
            string.IsNullOrEmpty(productKey) ||
            !Hashing.FixedTimeEqualsHex(Hashing.Sha256(productKey), product.ClientKeyHash))
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidProduct,
                "Aplicación no reconocida. Verifique los encabezados X-Vortex-Product y X-Vortex-Product-Key.");
        }
        return product;
    }

    private async Task<User> VerifyCredentialsAsync(string email, string password)
    {
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.Include(u => u.Organization).SingleOrDefaultAsync(u => u.NormalizedEmail == normalized);
        var now = clock.GetUtcNow();

        if (user is null)
        {
            hasher.Verify(password, DummyHash.Value);
            throw InvalidCredentials();
        }

        if (user.LockoutEnd > now)
            LicenseRules.EnsureUserCanSignIn(user, now);

        if (!hasher.Verify(password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= _lockout.MaxFailedAttempts)
            {
                user.LockoutEnd = now.AddMinutes(_lockout.LockoutMinutes);
                user.FailedLoginCount = 0;
            }
            await db.SaveChangesAsync();
            throw InvalidCredentials();
        }

        LicenseRules.EnsureUserCanSignIn(user, now);
        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        return user;
    }

    private DeviceActivation ActivateDevice(LicenseSeat seat, ProductLoginRequest req, DateTimeOffset now)
    {
        var hash = Hashing.Sha256(req.DeviceId);
        var active = seat.Devices.Where(d => d.RevokedAt is null).ToList();
        var device = active.FirstOrDefault(d => d.DeviceHash == hash);

        if (device is null)
        {
            if (active.Count >= seat.License.MaxDevicesPerUser)
                throw Forbidden(ErrorCodes.DeviceLimitReached,
                    $"Se alcanzó el máximo de {seat.License.MaxDevicesPerUser} equipo(s) por usuario. " +
                    "Un administrador debe liberar un equipo.");

            device = new DeviceActivation { DeviceHash = hash, ActivatedAt = now };
            seat.Devices.Add(device);
            audit.Record(AuditActions.DeviceActivated, true, seat.UserId, seat.License.OrganizationId,
                seat.License.Product.Code, device.Id.ToString(), req.DeviceName);
        }

        device.DeviceName = req.DeviceName ?? device.DeviceName;
        device.Platform = req.Platform ?? device.Platform;
        device.LastSeenAt = now;
        device.LastIp = ClientIp;
        return device;
    }

    private static void EnsureDeviceMatches(DeviceActivation device, string? deviceId)
    {
        if (device.RevokedAt is not null)
            throw Forbidden(ErrorCodes.DeviceRevoked, "El equipo ya no está activado.");
        if (string.IsNullOrEmpty(deviceId) || !Hashing.FixedTimeEqualsHex(Hashing.Sha256(deviceId), device.DeviceHash))
            throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.DeviceMismatch,
                "El token no corresponde a este equipo.");
    }

    /// <summary>Busca y revoca el refresh token. Si ya había sido rotado, se asume robo y se revoca toda la sesión.</summary>
    private async Task<RefreshToken> ConsumeRefreshTokenAsync(string plain, DateTimeOffset now)
    {
        var hash = Hashing.Sha256(plain);
        var token = await db.RefreshTokens
            .Include(t => t.User).ThenInclude(u => u.Organization)
            .SingleOrDefaultAsync(t => t.TokenHash == hash);

        if (token is null)
            throw InvalidRefresh();

        if (token.RevokedAt is not null)
        {
            if (token.ReplacedById is not null)
            {
                await RevokeFamilyAsync(token.FamilyId);
                await audit.RecordNowAsync(AuditActions.RefreshReuse, false, token.UserId, token.User.OrganizationId,
                    detail: $"familia {token.FamilyId} revocada");
                throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.RefreshTokenReused,
                    "El refresh token ya había sido usado; la sesión fue cerrada por seguridad.");
            }
            throw InvalidRefresh();
        }

        if (token.ExpiresAt <= now)
            throw InvalidRefresh();

        token.RevokedAt = now;
        try
        {
            LicenseRules.EnsureUserCanSignIn(token.User, now);
        }
        catch (ApiException ex)
        {
            await audit.RecordNowAsync(AuditActions.Refresh, false, token.UserId, token.User.OrganizationId, detail: ex.Code);
            throw;
        }
        return token;
    }

    private Task RevokeFamilyAsync(Guid familyId)
    {
        var now = clock.GetUtcNow();
        return db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
    }

    private ProductSessionResponse IssueProductSession(User user, License license, DeviceActivation device,
        Guid familyId, DateTimeOffset now, out RefreshToken refresh)
    {
        var access = tokens.CreateProductAccessToken(user, license, device);
        var licenseToken = tokens.CreateLicenseToken(user, license, device);
        var plain = Hashing.RandomToken();
        refresh = new RefreshToken
        {
            TokenHash = Hashing.Sha256(plain),
            FamilyId = familyId,
            UserId = user.Id,
            DeviceActivation = device,
            ProductId = license.ProductId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_jwt.ProductRefreshTokenDays),
        };
        db.RefreshTokens.Add(refresh);

        return new ProductSessionResponse(access.Token, access.ExpiresAt, plain, refresh.ExpiresAt,
            licenseToken.Token, licenseToken.ExpiresAt, ToUserInfo(user), ToLicenseInfo(license, device));
    }

    private AdminSessionResponse IssueAdminSession(User user, Guid familyId, DateTimeOffset now, out RefreshToken refresh)
    {
        var access = tokens.CreateAdminAccessToken(user);
        var plain = Hashing.RandomToken();
        refresh = new RefreshToken
        {
            TokenHash = Hashing.Sha256(plain),
            FamilyId = familyId,
            UserId = user.Id,
            CreatedAt = now,
            ExpiresAt = now.AddHours(_jwt.AdminRefreshTokenHours),
        };
        db.RefreshTokens.Add(refresh);
        return new AdminSessionResponse(access.Token, access.ExpiresAt, plain, refresh.ExpiresAt, ToUserInfo(user));
    }

    public static UserInfo ToUserInfo(User u) => new(u.Id, u.Email, u.FullName, u.Role.ToString(), u.OrganizationId);

    public static LicenseInfo ToLicenseInfo(License l, DeviceActivation d) => new(
        l.Id, l.LicenseKey, l.Product.Code, l.Plan, l.OrganizationId, l.Organization.Name, l.Features, l.ExpiresAt, d.Id);

    private static ApiException Forbidden(string code, string message) => new(StatusCodes.Status403Forbidden, code, message);

    private static ApiException InvalidCredentials() =>
        new(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials, "Correo o contraseña incorrectos.");

    private static ApiException InvalidRefresh() =>
        new(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidRefreshToken, "Sesión inválida o expirada. Inicie sesión de nuevo.");
}
