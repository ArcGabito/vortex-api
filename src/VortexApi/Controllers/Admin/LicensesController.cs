using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers.Admin;

/// <summary>
/// Licencias por organización y producto. Crear, modificar y cambiar el estado es exclusivo de PlatformAdmin;
/// un OrgAdmin puede consultar sus licencias, asignar puestos y liberar equipos.
/// </summary>
[ApiController]
[Route("api/admin/licenses")]
[Authorize(Policy = Policies.Admin)]
public sealed class LicensesController(
    VortexDbContext db, AuthService auth, AuditService audit, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<PagedResult<LicenseDto>> List(Guid? organizationId, string? productCode, LicenseStatus? status,
        int page = 1, int pageSize = 50)
    {
        var query = db.Licenses.Include(l => l.Organization).Include(l => l.Product).AsQueryable();
        if (!User.IsPlatformAdmin())
            organizationId = User.OrganizationId();
        if (organizationId is not null)
            query = query.Where(l => l.OrganizationId == organizationId);
        if (!string.IsNullOrWhiteSpace(productCode))
            query = query.Where(l => l.Product.Code == productCode.ToUpperInvariant());
        if (status is not null)
            query = query.Where(l => l.Status == status);

        var now = clock.GetUtcNow();
        var result = await query.OrderBy(l => l.Organization.Name).ThenBy(l => l.Product.Code)
            .Select(l => new { License = l, l.Organization, l.Product, Used = l.Seats.Count })
            .ToPageAsync(page, pageSize, x => x.License.ToDto(x.Used, now));
        return result;
    }

    [HttpGet("{id:guid}")]
    public async Task<LicenseDetailDto> Get(Guid id)
    {
        var license = await db.Licenses
            .Include(l => l.Organization).Include(l => l.Product)
            .Include(l => l.Seats).ThenInclude(s => s.User)
            .Include(l => l.Seats).ThenInclude(s => s.Devices)
            .AsSplitQuery()
            .SingleOrDefaultAsync(l => l.Id == id);
        if (license is null || !User.CanAccessOrganization(license.OrganizationId))
            throw ApiException.NotFound("Licencia");

        var seats = license.Seats.OrderBy(s => s.User.Email)
            .Select(s => new SeatDto(s.Id, s.UserId, s.User.Email, s.User.FullName, s.AssignedAt,
                s.Devices.OrderByDescending(d => d.LastSeenAt).Select(d => d.ToDto()).ToList()))
            .ToList();
        return new LicenseDetailDto(license.ToDto(seats.Count, clock.GetUtcNow()), seats);
    }

    [HttpPost]
    [Authorize(Policy = Policies.PlatformAdmin)]
    public async Task<ActionResult<LicenseDetailDto>> Create(CreateLicenseRequest request)
    {
        var org = await db.Organizations.FindAsync(request.OrganizationId) ?? throw ApiException.NotFound("Organización");
        var product = await db.Products.SingleOrDefaultAsync(p => p.Code == request.ProductCode.ToUpperInvariant())
                      ?? throw ApiException.NotFound("Producto");

        var now = clock.GetUtcNow();
        var startsAt = request.StartsAt ?? now;
        EnsureDates(startsAt, request.ExpiresAt);

        var license = new License
        {
            LicenseKey = LicenseRules.GenerateLicenseKey(product.Code),
            Organization = org,
            Product = product,
            Plan = request.Plan.Trim(),
            MaxSeats = request.MaxSeats,
            MaxDevicesPerUser = request.MaxDevicesPerUser,
            OfflineGraceDays = request.OfflineGraceDays,
            Features = NormalizeFeatures(request.Features),
            StartsAt = startsAt,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = now,
        };
        db.Licenses.Add(license);
        audit.Record(AuditActions.LicenseCreated, true, User.UserId(), org.Id, product.Code, license.Id.ToString(),
            $"{license.Plan}, {license.MaxSeats} puestos, vence {license.ExpiresAt:O}");
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = license.Id }, await Get(license.Id));
    }

    /// <summary>Cambia plan, puestos, equipos, vigencia o funcionalidades (renovaciones, upgrades).</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.PlatformAdmin)]
    public async Task<LicenseDetailDto> Update(Guid id, UpdateLicenseRequest request)
    {
        var license = await db.Licenses.Include(l => l.Product).Include(l => l.Seats).ThenInclude(s => s.Devices)
                          .SingleOrDefaultAsync(l => l.Id == id) ?? throw ApiException.NotFound("Licencia");
        EnsureDates(request.StartsAt, request.ExpiresAt);

        if (request.MaxSeats < license.Seats.Count)
            throw ApiException.Conflict(ErrorCodes.SeatsInUse,
                $"Hay {license.Seats.Count} puestos asignados; libere puestos antes de reducir a {request.MaxSeats}.");
        var maxDevicesInUse = license.Seats.Select(s => s.Devices.Count(d => d.RevokedAt == null)).DefaultIfEmpty(0).Max();
        if (request.MaxDevicesPerUser < maxDevicesInUse)
            throw ApiException.Conflict(ErrorCodes.SeatsInUse,
                $"Un usuario tiene {maxDevicesInUse} equipos activos; libere equipos antes de reducir a {request.MaxDevicesPerUser}.");

        license.Plan = request.Plan.Trim();
        license.MaxSeats = request.MaxSeats;
        license.MaxDevicesPerUser = request.MaxDevicesPerUser;
        license.OfflineGraceDays = request.OfflineGraceDays;
        license.StartsAt = request.StartsAt;
        license.ExpiresAt = request.ExpiresAt;
        license.Features = NormalizeFeatures(request.Features);
        audit.Record(AuditActions.LicenseUpdated, true, User.UserId(), license.OrganizationId, license.Product.Code,
            license.Id.ToString(), $"{license.Plan}, {license.MaxSeats} puestos, vence {license.ExpiresAt:O}");
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>Suspende temporalmente (p. ej. falta de pago). Bloquea login, renovación y heartbeat.</summary>
    [HttpPost("{id:guid}/suspend")]
    [Authorize(Policy = Policies.PlatformAdmin)]
    public Task<LicenseDetailDto> Suspend(Guid id, LicenseStatusRequest request) => SetStatus(id, LicenseStatus.Suspended, request.Reason);

    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Policy = Policies.PlatformAdmin)]
    public Task<LicenseDetailDto> Reactivate(Guid id) => SetStatus(id, LicenseStatus.Active, null);

    /// <summary>Revoca de forma definitiva. Una licencia revocada no se puede reactivar.</summary>
    [HttpPost("{id:guid}/revoke")]
    [Authorize(Policy = Policies.PlatformAdmin)]
    public Task<LicenseDetailDto> Revoke(Guid id, LicenseStatusRequest request) => SetStatus(id, LicenseStatus.Revoked, request.Reason);

    [HttpPost("{id:guid}/seats")]
    public async Task<LicenseDetailDto> AssignSeat(Guid id, AssignSeatRequest request)
    {
        var license = await FindScopedAsync(id, includeSeats: true);
        var user = await db.Users.FindAsync(request.UserId);
        if (user is null || user.OrganizationId != license.OrganizationId)
            throw ApiException.NotFound("Usuario");
        if (license.Status == LicenseStatus.Revoked)
            throw ApiException.Conflict(ErrorCodes.LicenseRevoked, "La licencia fue revocada.");

        if (license.Seats.All(s => s.UserId != user.Id))
        {
            if (license.Seats.Count >= license.MaxSeats)
                throw ApiException.Conflict(ErrorCodes.SeatLimitReached,
                    $"La licencia tiene sus {license.MaxSeats} puestos ocupados.");

            license.Seats.Add(new LicenseSeat { UserId = user.Id, AssignedAt = clock.GetUtcNow() });
            audit.Record(AuditActions.SeatAssigned, true, User.UserId(), license.OrganizationId, license.Product.Code,
                license.Id.ToString(), user.Email);
            await db.SaveChangesAsync();
        }
        return await Get(id);
    }

    /// <summary>Libera el puesto del usuario; sus equipos quedan desactivados y sus sesiones del producto se cierran.</summary>
    [HttpDelete("{id:guid}/seats/{userId:guid}")]
    public async Task<IActionResult> RemoveSeat(Guid id, Guid userId)
    {
        var license = await FindScopedAsync(id, includeSeats: true);
        var seat = license.Seats.SingleOrDefault(s => s.UserId == userId) ?? throw ApiException.NotFound("Puesto");

        foreach (var device in seat.Devices)
            await auth.RevokeDeviceSessionsAsync(device.Id);
        db.LicenseSeats.Remove(seat);
        audit.Record(AuditActions.SeatRemoved, true, User.UserId(), license.OrganizationId, license.Product.Code,
            license.Id.ToString(), userId.ToString());
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Desactiva un equipo y libera su cupo. Las sesiones abiertas en ese equipo se cierran.</summary>
    [HttpDelete("{id:guid}/devices/{deviceId:guid}")]
    public async Task<IActionResult> RevokeDevice(Guid id, Guid deviceId)
    {
        var license = await FindScopedAsync(id, includeSeats: true);
        var device = license.Seats.SelectMany(s => s.Devices).SingleOrDefault(d => d.Id == deviceId)
                     ?? throw ApiException.NotFound("Equipo");

        if (device.RevokedAt is null)
        {
            device.RevokedAt = clock.GetUtcNow();
            await auth.RevokeDeviceSessionsAsync(device.Id);
            audit.Record(AuditActions.DeviceRevoked, true, User.UserId(), license.OrganizationId, license.Product.Code,
                device.Id.ToString(), device.DeviceName);
            await db.SaveChangesAsync();
        }
        return NoContent();
    }

    private async Task<LicenseDetailDto> SetStatus(Guid id, LicenseStatus status, string? reason)
    {
        var license = await db.Licenses.Include(l => l.Product).SingleOrDefaultAsync(l => l.Id == id)
                      ?? throw ApiException.NotFound("Licencia");
        if (license.Status == LicenseStatus.Revoked && status != LicenseStatus.Revoked)
            throw ApiException.Conflict(ErrorCodes.LicenseRevoked, "Una licencia revocada no se puede reactivar.");

        license.Status = status;
        license.StatusReason = reason;
        audit.Record(AuditActions.LicenseStatusChanged, true, User.UserId(), license.OrganizationId, license.Product.Code,
            license.Id.ToString(), $"{status}: {reason}");
        await db.SaveChangesAsync();
        return await Get(id);
    }

    private async Task<License> FindScopedAsync(Guid id, bool includeSeats)
    {
        var query = db.Licenses.Include(l => l.Product).AsQueryable();
        if (includeSeats)
            query = query.Include(l => l.Seats).ThenInclude(s => s.Devices);
        var license = await query.SingleOrDefaultAsync(l => l.Id == id);
        if (license is null || !User.CanAccessOrganization(license.OrganizationId))
            throw ApiException.NotFound("Licencia");
        return license;
    }

    private static void EnsureDates(DateTimeOffset startsAt, DateTimeOffset expiresAt)
    {
        if (expiresAt <= startsAt)
            throw ApiException.BadRequest(ErrorCodes.Validation, "expiresAt debe ser posterior a startsAt.");
    }

    private static List<string> NormalizeFeatures(List<string>? features) =>
        (features ?? []).Select(f => f.Trim().ToLowerInvariant()).Where(f => f.Length > 0).Distinct().Order().ToList();
}
