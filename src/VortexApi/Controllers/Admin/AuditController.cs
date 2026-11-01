using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Security;

namespace VortexApi.Controllers.Admin;

[ApiController]
[Route("api/admin/audit")]
[Authorize(Policy = Policies.Admin)]
public sealed class AuditController(VortexDbContext db) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AuditLogDto>> List(Guid? organizationId, string? action, string? productCode, bool? success,
        DateTimeOffset? from, DateTimeOffset? to, int page = 1, int pageSize = 100)
    {
        var query = db.AuditLogs.AsQueryable();
        if (!User.IsPlatformAdmin())
            organizationId = User.OrganizationId();
        if (organizationId is not null)
            query = query.Where(a => a.OrganizationId == organizationId);
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(productCode))
            query = query.Where(a => a.ProductCode == productCode.ToUpperInvariant());
        if (success is not null)
            query = query.Where(a => a.Success == success);
        if (from is not null)
            query = query.Where(a => a.Timestamp >= from);
        if (to is not null)
            query = query.Where(a => a.Timestamp < to);

        return query.OrderByDescending(a => a.Id).ToPageAsync(page, pageSize, a => new AuditLogDto(
            a.Id, a.Timestamp, a.Action, a.Success, a.ActorUserId, a.OrganizationId, a.ProductCode, a.TargetId,
            a.Detail, a.IpAddress));
    }
}
