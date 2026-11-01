using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Controllers.Admin;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Services;

namespace VortexApi.Controllers.Master;

/// <summary>Productos y servicios compartidos por ERP, WMS y POS.</summary>
[Route("api/master/items")]
public sealed class ItemsController(VortexDbContext db, MasterDataWriter writer)
    : MasterDataControllerBase<Item, ItemRequest, ItemDto>(db, writer)
{
    protected override string EntityType => MasterEntityTypes.Item;
    protected override string DisplayName => "Artículo";

    [HttpGet]
    public Task<PagedResult<ItemDto>> List(string? search, string? sku, string? barcode, string? category,
        bool includeDeleted = false, int page = 1, int pageSize = 50)
    {
        var query = Scoped(includeDeleted);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(i => i.Name.Contains(search) || i.Sku.Contains(search));
        if (!string.IsNullOrWhiteSpace(sku))
            query = query.Where(i => i.Sku == sku.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(barcode))
            query = query.Where(i => i.Barcode == barcode.Trim());
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(i => i.Category == category.Trim());
        return query.OrderBy(i => i.Sku).ToPageAsync(page, pageSize, ToDto);
    }

    protected override void Apply(Item i, ItemRequest r)
    {
        i.Sku = r.Sku.Trim().ToUpperInvariant();
        i.Name = r.Name.Trim();
        i.Kind = r.Kind;
        i.Description = Clean(r.Description);
        i.Unit = Clean(r.Unit)?.ToLowerInvariant() ?? (r.Kind == ItemKind.Service ? "servicio" : "unidad");
        i.Barcode = Clean(r.Barcode);
        i.Category = Clean(r.Category);
        i.Price = decimal.Round(r.Price, 4);
        i.TaxCode = Clean(r.TaxCode)?.ToLowerInvariant();
        i.TrackInventory = r.Kind == ItemKind.Product && (r.TrackInventory ?? true);
        i.IsActive = r.IsActive;
        if (Clean(r.CurrencyCode) is { } currency)
            i.CurrencyCode = currency.ToUpperInvariant();
    }

    /// <summary>Sin moneda explícita, un artículo nuevo usa la moneda de la empresa.</summary>
    protected override async Task OnCreatingAsync(Item i) =>
        i.CurrencyCode = await Db.CompanyProfiles.AsNoTracking()
            .Where(c => c.OrganizationId == i.OrganizationId).Select(c => c.CurrencyCode).FirstOrDefaultAsync() ?? "PEN";

    protected override async Task ValidateAsync(Item i)
    {
        var duplicate = await Db.Items.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == i.OrganizationId && x.Id != i.Id && x.DeletedAt == null && x.Sku == i.Sku);
        if (duplicate)
            throw ApiException.Conflict(ErrorCodes.DuplicateSku, $"Ya existe un artículo con SKU {i.Sku}.");
    }

    protected override ItemDto ToDto(Item i) => new(
        i.Id, i.Sku, i.Name, i.Kind, i.Description, i.Unit, i.Barcode, i.Category, i.Price, i.CurrencyCode, i.TaxCode,
        i.TrackInventory, i.IsActive, Meta(i));
}
