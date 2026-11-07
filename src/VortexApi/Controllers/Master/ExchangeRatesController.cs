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
/// Tipos de cambio por día, compartidos por ERP, CRM y POS para que todos conviertan igual. Uno por par de monedas y
/// fecha; cargar otra vez el mismo día lo corrige (versión nueva en el feed).
/// </summary>
[Route("api/master/exchange-rates")]
public sealed class ExchangeRatesController(VortexDbContext db, MasterDataWriter writer)
    : MasterDataControllerBase<ExchangeRate, ExchangeRateRequest, ExchangeRateDto>(db, writer)
{
    protected override string EntityType => MasterEntityTypes.ExchangeRate;
    protected override string DisplayName => "Tipo de cambio";

    [HttpGet]
    public Task<PagedResult<ExchangeRateDto>> List(string? from, string? to, DateOnly? dateFrom, DateOnly? dateTo,
        bool includeDeleted = false, int page = 1, int pageSize = 100)
    {
        var query = Scoped(includeDeleted);
        if (Currency(from) is { } f)
            query = query.Where(r => r.FromCurrency == f);
        if (Currency(to) is { } t)
            query = query.Where(r => r.ToCurrency == t);
        if (dateFrom is { } start)
            query = query.Where(r => r.Date >= start);
        if (dateTo is { } end)
            query = query.Where(r => r.Date <= end);
        return query.OrderByDescending(r => r.Date).ThenBy(r => r.FromCurrency).ThenBy(r => r.ToCurrency)
            .ToPageAsync(page, pageSize, ToDto);
    }

    /// <summary>
    /// El tipo de cambio vigente en una fecha: el de ese día o, si no se publicó (fin de semana, feriado), el último
    /// anterior. Sin <paramref name="date"/>, el más reciente.
    /// </summary>
    [HttpGet("latest")]
    public async Task<ActionResult<ExchangeRateDto>> Latest(string from, string to, DateOnly? date)
    {
        var (f, t) = (Currency(from), Currency(to));
        if (f is null || t is null)
            throw ApiException.BadRequest(ErrorCodes.Validation, "Indica las monedas from y to (ISO 4217).");
        var query = Scoped(includeDeleted: false).Where(r => r.FromCurrency == f && r.ToCurrency == t);
        if (date is { } d)
            query = query.Where(r => r.Date <= d);
        var rate = await query.OrderByDescending(r => r.Date).FirstOrDefaultAsync()
                   ?? throw ApiException.NotFound(DisplayName);
        return WithETag(rate);
    }

    protected override void Apply(ExchangeRate r, ExchangeRateRequest q)
    {
        r.Date = q.Date;
        r.FromCurrency = q.FromCurrency.Trim().ToUpperInvariant();
        r.ToCurrency = q.ToCurrency.Trim().ToUpperInvariant();
        r.Rate = decimal.Round(q.Rate, 8);
        r.BuyRate = q.BuyRate is { } buy ? decimal.Round(buy, 8) : null;
        r.SellRate = q.SellRate is { } sell ? decimal.Round(sell, 8) : null;
        r.Source = Clean(q.Source)?.ToUpperInvariant();
    }

    protected override async Task ValidateAsync(ExchangeRate r)
    {
        if (r.FromCurrency == r.ToCurrency)
            throw ApiException.BadRequest(ErrorCodes.Validation, "Las dos monedas deben ser distintas.");
        var duplicate = await Db.ExchangeRates.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == r.OrganizationId && x.Id != r.Id && x.DeletedAt == null &&
            x.FromCurrency == r.FromCurrency && x.ToCurrency == r.ToCurrency && x.Date == r.Date);
        if (duplicate)
            throw ApiException.Conflict(ErrorCodes.DuplicateCode,
                $"Ya hay un tipo de cambio {r.FromCurrency}/{r.ToCurrency} del {r.Date:yyyy-MM-dd}; actualízalo en vez de crear otro.");
    }

    protected override ExchangeRateDto ToDto(ExchangeRate r) => new(
        r.Id, r.Date, r.FromCurrency, r.ToCurrency, r.Rate, r.BuyRate, r.SellRate, r.Source, Meta(r));

    private static string? Currency(string? code) => Clean(code)?.ToUpperInvariant();
}
