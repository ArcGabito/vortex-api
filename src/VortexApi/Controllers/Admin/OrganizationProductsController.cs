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
/// Sistemas de la suite habilitados para una organización, sus miembros y equipos. Habilitar, deshabilitar y
/// configurar un sistema es exclusivo de PlatformAdmin; un OrgAdmin administra miembros y equipos de su organización.
/// </summary>
[ApiController]
[Route("api/admin/organizations/{orgId:guid}/products")]
[Authorize(Policy = Policies.Admin)]
public sealed class OrganizationProductsController(
    VortexDbContext db, AuthService auth, AuditService audit, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<OrganizationProductDto>> List(Guid orgId)
    {
        EnsureOrgAccess(orgId);
        var now = clock.GetUtcNow();
        var items = await db.OrganizationProducts.Include(p => p.Product)
            .Where(p => p.OrganizationId == orgId)
            .OrderBy(p => p.Product.Code)
            .Select(p => new { Access = p, p.Product, Members = p.Members.Count })
            .ToListAsync();
        return items.Select(x => x.Access.ToDto(x.Members, now)).ToList();
    }

    [HttpGet("{code}")]
    public async Task<OrganizationProductDetailDto> Get(Guid orgId, string code)
    {
        var access = await FindAsync(orgId, code, includeMembers: true);
        var members = access.Members.OrderBy(m => m.User.Email)
            .Select(m => new MemberDto(m.Id, m.UserId, m.User.Email, m.User.FullName, m.Role, m.AddedAt,
                m.Devices.OrderByDescending(d => d.LastSeenAt).Select(d => d.ToDto()).ToList(), m.SiteIds))
            .ToList();
        return new OrganizationProductDetailDto(access.ToDto(members.Count, clock.GetUtcNow()), members);
    }

    /// <summary>
    /// Habilita o configura un sistema para la organización. Para cambiar de sistema (p. ej. de CRM a ERP)
    /// se habilita el nuevo y se deshabilita el anterior con <c>isEnabled=false</c>; usuarios y miembros se conservan.
    /// </summary>
    [HttpPut("{code}")]
    [Authorize(Policy = Policies.PlatformAdmin)]
    public async Task<OrganizationProductDetailDto> Upsert(Guid orgId, string code, OrganizationProductRequest request)
    {
        if (!await db.Organizations.AnyAsync(o => o.Id == orgId))
            throw ApiException.NotFound("Organización");
        var product = await db.Products.SingleOrDefaultAsync(p => p.Code == code.ToUpperInvariant())
                      ?? throw ApiException.NotFound("Sistema");

        var now = clock.GetUtcNow();
        var access = await db.OrganizationProducts
            .Include(p => p.Members).ThenInclude(m => m.Devices)
            .SingleOrDefaultAsync(p => p.OrganizationId == orgId && p.ProductId == product.Id);
        var isNew = access is null;
        access ??= db.OrganizationProducts.Add(new OrganizationProduct
        {
            OrganizationId = orgId, Product = product, CreatedAt = now,
        }).Entity;

        if (request.MaxUsers < access.Members.Count)
            throw ApiException.Conflict(ErrorCodes.LimitInUse,
                $"Hay {access.Members.Count} usuarios con acceso; quite usuarios antes de reducir a {request.MaxUsers}.");
        var maxDevicesInUse = access.Members.Select(m => m.Devices.Count(d => d.RevokedAt == null)).DefaultIfEmpty(0).Max();
        if (request.MaxDevicesPerUser < maxDevicesInUse)
            throw ApiException.Conflict(ErrorCodes.LimitInUse,
                $"Un usuario tiene {maxDevicesInUse} equipos activos; libere equipos antes de reducir a {request.MaxDevicesPerUser}.");

        access.IsEnabled = request.IsEnabled;
        access.DisabledReason = request.IsEnabled ? null : request.DisabledReason;
        access.AutoAssignUsers = request.AutoAssignUsers;
        access.MaxUsers = request.MaxUsers;
        access.MaxDevicesPerUser = request.MaxDevicesPerUser;
        access.OfflineGraceDays = request.OfflineGraceDays;
        access.Features = NormalizeFeatures(request.Features);
        access.ExpiresAt = request.ExpiresAt;

        audit.Record(isNew ? AuditActions.ProductEnabled : AuditActions.ProductUpdated, true, User.UserId(), orgId,
            product.Code, access.Id.ToString(),
            $"enabled={access.IsEnabled}, maxUsers={access.MaxUsers?.ToString() ?? "∞"}, expires={access.ExpiresAt?.ToString("O") ?? "nunca"}");
        await db.SaveChangesAsync();
        return await Get(orgId, code);
    }

    /// <summary>Da acceso a un usuario de la organización, o cambia su rol dentro del sistema.</summary>
    [HttpPut("{code}/members/{userId:guid}")]
    public async Task<OrganizationProductDetailDto> UpsertMember(Guid orgId, string code, Guid userId, MemberRequest request)
    {
        var access = await FindAsync(orgId, code, includeMembers: true);
        var user = await db.Users.FindAsync(userId);
        if (user is null || user.OrganizationId != orgId)
            throw ApiException.NotFound("Usuario");

        var role = string.IsNullOrWhiteSpace(request.Role) ? null : request.Role.Trim().ToLowerInvariant();
        var siteIds = (request.SiteIds ?? []).Where(s => s != Guid.Empty).Distinct().Order().ToList();
        if (siteIds.Count > 0)
        {
            var known = await db.Sites.CountAsync(s => s.OrganizationId == orgId && s.DeletedAt == null && siteIds.Contains(s.Id));
            if (known != siteIds.Count)
                throw ApiException.BadRequest(ErrorCodes.Validation, "Una de las sedes no existe en la organización.");
        }
        var member = access.Members.SingleOrDefault(m => m.UserId == userId);
        if (member is null)
        {
            if (access.MaxUsers is { } max && access.Members.Count >= max)
                throw ApiException.Conflict(ErrorCodes.UserLimitReached,
                    $"{access.Product.Name} alcanzó su máximo de {max} usuario(s).");

            member = new ProductMember { UserId = userId, Role = role, SiteIds = siteIds, AddedAt = clock.GetUtcNow() };
            access.Members.Add(member);
            audit.Record(AuditActions.MemberAdded, true, User.UserId(), orgId, access.Product.Code, member.Id.ToString(),
                $"{user.Email} ({role ?? "sin rol"}; sedes: {(siteIds.Count == 0 ? "todas" : siteIds.Count.ToString())})");
        }
        else if (member.Role != role || !member.SiteIds.Order().SequenceEqual(siteIds))
        {
            member.Role = role;
            member.SiteIds = siteIds;
            // El rol y las sedes viajan en el token: cerrar las sesiones del usuario en este sistema para que tome los nuevos.
            foreach (var device in member.Devices)
                await auth.RevokeDeviceSessionsAsync(device.Id);
            audit.Record(AuditActions.MemberUpdated, true, User.UserId(), orgId, access.Product.Code, member.Id.ToString(),
                $"{user.Email} ({role ?? "sin rol"}; sedes: {(siteIds.Count == 0 ? "todas" : siteIds.Count.ToString())})");
        }

        await db.SaveChangesAsync();
        return await Get(orgId, code);
    }

    /// <summary>Quita el acceso del usuario a este sistema; sus equipos quedan desactivados y sus sesiones se cierran.</summary>
    [HttpDelete("{code}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid orgId, string code, Guid userId)
    {
        var access = await FindAsync(orgId, code, includeMembers: true);
        var member = access.Members.SingleOrDefault(m => m.UserId == userId) ?? throw ApiException.NotFound("Miembro");

        foreach (var device in member.Devices)
            await auth.RevokeDeviceSessionsAsync(device.Id);
        db.ProductMembers.Remove(member);
        audit.Record(AuditActions.MemberRemoved, true, User.UserId(), orgId, access.Product.Code, member.Id.ToString(),
            member.User.Email);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Desactiva un equipo y libera su cupo. Las sesiones abiertas en ese equipo se cierran.</summary>
    [HttpDelete("{code}/devices/{deviceId:guid}")]
    public async Task<IActionResult> RevokeDevice(Guid orgId, string code, Guid deviceId)
    {
        var access = await FindAsync(orgId, code, includeMembers: true);
        var device = access.Members.SelectMany(m => m.Devices).SingleOrDefault(d => d.Id == deviceId)
                     ?? throw ApiException.NotFound("Equipo");

        if (device.RevokedAt is null)
        {
            device.RevokedAt = clock.GetUtcNow();
            await auth.RevokeDeviceSessionsAsync(device.Id);
            audit.Record(AuditActions.DeviceRevoked, true, User.UserId(), orgId, access.Product.Code,
                device.Id.ToString(), device.DeviceName);
            await db.SaveChangesAsync();
        }
        return NoContent();
    }

    private async Task<OrganizationProduct> FindAsync(Guid orgId, string code, bool includeMembers)
    {
        EnsureOrgAccess(orgId);
        var query = db.OrganizationProducts.Include(p => p.Product).AsQueryable();
        if (includeMembers)
            query = query
                .Include(p => p.Members).ThenInclude(m => m.User)
                .Include(p => p.Members).ThenInclude(m => m.Devices)
                .AsSplitQuery();
        return await query.SingleOrDefaultAsync(p => p.OrganizationId == orgId && p.Product.Code == code.ToUpperInvariant())
               ?? throw ApiException.NotFound("Sistema habilitado");
    }

    private void EnsureOrgAccess(Guid orgId)
    {
        if (!User.CanAccessOrganization(orgId))
            throw ApiException.NotFound("Organización");
    }

    private static List<string> NormalizeFeatures(List<string>? features) =>
        (features ?? []).Select(f => f.Trim().ToLowerInvariant()).Where(f => f.Length > 0).Distinct().Order().ToList();
}
