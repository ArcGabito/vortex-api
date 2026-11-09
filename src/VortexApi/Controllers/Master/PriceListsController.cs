using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Controllers.Admin;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Services;

namespace VortexApi.Controllers.Master;

/// <summary>
/// Listas de precios compartidas por POS, ERP y CRM (por cliente y por tienda, con escalas por cantidad). La lista se
/// manda completa en cada cambio y llega por el feed como <c>price_list</c>; cada sistema resuelve el precio en el equipo
/// (también sin conexión): la lista del cliente, si no la de la tienda, si no el precio del artículo.
/// </summary>
[Route("api/master/price-lists")]
public sealed class PriceListsController(VortexDbContext db, MasterDataWriter writer)
    : MasterDataControllerBase<PriceList, PriceListRequest, PriceListDto>(db, writer)
{
    protected override string EntityType => MasterEntityTypes.PriceList;
    protected override string DisplayName => "Lista de precios";

    [HttpGet]
    public Task<PagedResult<PriceListDto>> List(string? search, bool includeDeleted = false, int page = 1, int pageSize = 50)
    {
        var query = Scoped(includeDeleted);
        if (Clean(search) is { } term)
            query = query.Where(l => l.Code.Contains(term) || l.Name.Contains(term));
        return query.OrderBy(l => l.Code).ToPageAsync(page, pageSize, ToDto);
    }

    protected override void Apply(PriceList l, PriceListRequest r)
    {
        l.Code = r.Code.Trim().ToUpperInvariant();
        l.Name = r.Name.Trim();
        l.CurrencyCode = r.CurrencyCode.Trim().ToUpperInvariant();
        (l.ValidFrom, l.ValidTo) = (r.ValidFrom, r.ValidTo);
        l.SiteIds = (r.SiteIds ?? []).Distinct().ToList();
        l.IsActive = r.IsActive;
        l.Lines = (r.Lines ?? []).Select(x => new PriceListLine
        {
            ItemId = x.ItemId, Price = decimal.Round(x.Price, 6), MinQuantity = x.MinQuantity <= 0 ? 1 : decimal.Round(x.MinQuantity, 6),
        }).ToList();
    }

    protected override async Task ValidateAsync(PriceList l)
    {
        if (l.ValidFrom is { } from && l.ValidTo is { } to && to < from)
            throw ApiException.BadRequest(ErrorCodes.Validation, "La vigencia termina antes de empezar.");
        if (l.Lines.GroupBy(x => (x.ItemId, x.MinQuantity)).Any(g => g.Count() > 1))
            throw ApiException.BadRequest(ErrorCodes.Validation, "Un artículo tiene dos precios para la misma cantidad.");
        var items = l.Lines.Select(x => x.ItemId).Distinct().ToList();
        var known = await Db.Items.AsNoTracking()
            .CountAsync(i => i.OrganizationId == l.OrganizationId && i.DeletedAt == null && items.Contains(i.Id));
        if (known != items.Count)
            throw ApiException.BadRequest(ErrorCodes.Validation, "La lista tiene artículos que no existen.");
        var duplicate = await Db.PriceLists.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == l.OrganizationId && x.Id != l.Id && x.DeletedAt == null && x.Code == l.Code);
        if (duplicate)
            throw ApiException.Conflict(ErrorCodes.DuplicateCode, $"Ya hay una lista de precios con el código {l.Code}.");
    }

    protected override PriceListDto ToDto(PriceList l) => new(
        l.Id, l.Code, l.Name, l.CurrencyCode, l.ValidFrom, l.ValidTo, l.SiteIds,
        l.IsActive, l.Lines.Select(x => new PriceListLineDto(x.ItemId, x.Price, x.MinQuantity)).ToList(), Meta(l));
}
