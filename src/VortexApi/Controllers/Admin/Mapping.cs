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

    public static LicenseDto ToDto(this License l, int usedSeats, DateTimeOffset now) => new(
        l.Id, l.LicenseKey, l.OrganizationId, l.Organization.Name, l.Product.Code, l.Plan, l.MaxSeats, usedSeats,
        l.MaxDevicesPerUser, l.OfflineGraceDays, l.Features, l.StartsAt, l.ExpiresAt, l.Status.ToString(),
        l.StatusReason, l.IsValidAt(now));

    public static DeviceDto ToDto(this DeviceActivation d) => new(
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
