using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Controllers.Admin;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Services;

namespace VortexApi.Controllers.Master;

/// <summary>Sedes, tiendas y almacenes compartidos por ERP, WMS y POS.</summary>
[Route("api/master/sites")]
public sealed class SitesController(VortexDbContext db, MasterDataWriter writer)
    : MasterDataControllerBase<Site, SiteRequest, SiteDto>(db, writer)
{
    protected override string EntityType => MasterEntityTypes.Site;
    protected override string DisplayName => "Sede";

    [HttpGet]
    public Task<PagedResult<SiteDto>> List(SiteKind? kind, bool includeDeleted = false, int page = 1, int pageSize = 100)
    {
        var query = Scoped(includeDeleted);
        if (kind is not null)
            query = query.Where(s => s.Kind == kind);
        return query.OrderBy(s => s.Code).ToPageAsync(page, pageSize, ToDto);
    }

    protected override void Apply(Site s, SiteRequest r)
    {
        s.Code = r.Code.Trim().ToUpperInvariant();
        s.Name = r.Name.Trim();
        s.Kind = r.Kind;
        s.Phone = Clean(r.Phone);
        s.AddressLine = Clean(r.Address?.Line);
        s.District = Clean(r.Address?.District);
        s.City = Clean(r.Address?.City);
        s.Region = Clean(r.Address?.Region);
        s.PostalCode = Clean(r.Address?.PostalCode);
        s.CountryCode = Clean(r.Address?.CountryCode)?.ToUpperInvariant();
        s.IsActive = r.IsActive;
    }

    protected override async Task ValidateAsync(Site s)
    {
        var duplicate = await Db.Sites.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == s.OrganizationId && x.Id != s.Id && x.DeletedAt == null && x.Code == s.Code);
        if (duplicate)
            throw ApiException.Conflict(ErrorCodes.DuplicateCode, $"Ya existe una sede con código {s.Code}.");
    }

    protected override SiteDto ToDto(Site s) => new(
        s.Id, s.Code, s.Name, s.Kind, s.Phone,
        s.AddressLine is null && s.City is null && s.CountryCode is null
            ? null
            : new AddressDto(s.AddressLine, s.District, s.City, s.Region, s.PostalCode, s.CountryCode),
        s.IsActive, Meta(s));
}
