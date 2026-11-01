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

[ApiController]
[Route("api/admin/organizations")]
[Authorize(Policy = Policies.Admin)]
public sealed class OrganizationsController(VortexDbContext db, AuditService audit, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<OrganizationDto>> List(string? search, int page = 1, int pageSize = 50)
    {
        var query = Scoped();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(o => o.Name.Contains(search) || (o.TaxId != null && o.TaxId.Contains(search)));

        return query.OrderBy(o => o.Name)
            .Select(o => new OrganizationDto(o.Id, o.Name, o.TaxId, o.ContactEmail, o.IsActive, o.CreatedAt,
                o.Users.Count, o.Products.Where(p => p.IsEnabled).Select(p => p.Product.Code).OrderBy(c => c).ToList()))
            .ToPageAsync(page, pageSize, x => x);
    }

    [HttpGet("{id:guid}")]
    public async Task<OrganizationDto> Get(Guid id) =>
        await Scoped().Where(o => o.Id == id)
            .Select(o => new OrganizationDto(o.Id, o.Name, o.TaxId, o.ContactEmail, o.IsActive, o.CreatedAt,
                o.Users.Count, o.Products.Where(p => p.IsEnabled).Select(p => p.Product.Code).OrderBy(c => c).ToList()))
            .SingleOrDefaultAsync() ?? throw ApiException.NotFound("Organización");

    [HttpPost]
    [Authorize(Policy = Policies.PlatformAdmin)]
    public async Task<ActionResult<OrganizationDto>> Create(OrganizationRequest request)
    {
        var org = new Organization
        {
            Name = request.Name.Trim(),
            TaxId = request.TaxId?.Trim(),
            ContactEmail = request.ContactEmail?.Trim(),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Organizations.Add(org);
        audit.Record(AuditActions.OrganizationCreated, true, User.UserId(), org.Id, targetId: org.Id.ToString(), detail: org.Name);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = org.Id }, await Get(org.Id));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.PlatformAdmin)]
    public async Task<OrganizationDto> Update(Guid id, OrganizationRequest request)
    {
        var org = await db.Organizations.FindAsync(id) ?? throw ApiException.NotFound("Organización");
        org.Name = request.Name.Trim();
        org.TaxId = request.TaxId?.Trim();
        org.ContactEmail = request.ContactEmail?.Trim();
        audit.Record(AuditActions.OrganizationUpdated, true, User.UserId(), org.Id, targetId: org.Id.ToString());
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>Deshabilita la organización: ninguno de sus usuarios puede iniciar sesión ni renovar sesión.</summary>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = Policies.PlatformAdmin)]
    public Task<OrganizationDto> Deactivate(Guid id) => SetActive(id, false);

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = Policies.PlatformAdmin)]
    public Task<OrganizationDto> Activate(Guid id) => SetActive(id, true);

    private async Task<OrganizationDto> SetActive(Guid id, bool active)
    {
        var org = await db.Organizations.FindAsync(id) ?? throw ApiException.NotFound("Organización");
        org.IsActive = active;
        if (!active)
        {
            var now = clock.GetUtcNow();
            await db.RefreshTokens
                .Where(t => t.User.OrganizationId == id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
        }
        audit.Record(AuditActions.OrganizationUpdated, true, User.UserId(), org.Id, targetId: org.Id.ToString(),
            detail: active ? "activada" : "desactivada");
        await db.SaveChangesAsync();
        return await Get(id);
    }

    private IQueryable<Organization> Scoped()
    {
        var query = db.Organizations.AsQueryable();
        if (!User.IsPlatformAdmin())
        {
            var orgId = User.OrganizationId();
            query = query.Where(o => o.Id == orgId);
        }
        return query;
    }
}
