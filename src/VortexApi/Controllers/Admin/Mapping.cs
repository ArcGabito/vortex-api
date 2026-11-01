using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Domain;
using VortexApi.Services;

namespace VortexApi.Controllers.Admin;

internal static class Mapping
{
    public static UserDto ToDto(this User u, DateTimeOffset now) => new(
        u.Id, u.OrganizationId, u.Email, u.FullName, u.Role.ToString(), u.IsActive, u.LockoutEnd > now,
        u.CreatedAt, u.LastLoginAt);

    public static OrganizationProductDto ToDto(this OrganizationProduct p, int members, DateTimeOffset now) => new(
        p.Id, p.OrganizationId, p.Product.Code, p.Product.Name, p.IsEnabled, p.DisabledReason, p.AutoAssignUsers,
        p.MaxUsers, members, p.MaxDevicesPerUser, p.OfflineGraceDays, p.Features, p.ExpiresAt, p.IsUsableAt(now));

    public static DeviceDto ToDto(this Device d) => new(
        d.Id, d.DeviceName, d.Platform, d.ActivatedAt, d.LastSeenAt, d.LastIp, d.RevokedAt is not null);

    public static async Task<PagedResult<TOut>> ToPageAsync<TIn, TOut>(
        this IQueryable<TIn> query, int page, int pageSize, Func<TIn, TOut> map)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PagedResult<TOut>(items.Select(map).ToList(), page, pageSize, total);
    }
}
