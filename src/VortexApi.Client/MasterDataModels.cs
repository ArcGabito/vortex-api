using System.Text.Json;

namespace VortexApi.Client;

// Modelos de datos maestros y eventos tal como los devuelve el hub. Los campos de tipo "kind" se exponen como string
// para que una versión nueva del hub con valores nuevos no rompa apps antiguas.

public sealed record VortexAddress(string? Line, string? District, string? City, string? Region, string? PostalCode, string? CountryCode);

public sealed record VortexMasterMeta(int Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt)
{
    public bool IsDeleted => DeletedAt is not null;
}

public sealed record VortexParty(
    Guid Id, bool IsCustomer, bool IsSupplier, string Kind, string Name, string? TradeName, string? DocumentType,
    string? DocumentNumber, string? Email, string? Phone, VortexAddress? Address, string? Notes, bool IsActive,
    VortexMasterMeta Meta, VortexPartyCredit? Credit = null, VortexPartyAccount? Account = null);

/// <summary>Condiciones comerciales del cliente (las maneja el ERP): plazo, límite de crédito en la moneda de la empresa y descuento.</summary>
public sealed record VortexPartyCredit(int? PaymentTermDays = null, decimal? CreditLimit = null, decimal? DiscountPercent = null,
    Guid? PriceListId = null);

/// <summary>
/// Lista de precios compartida por POS, ERP y CRM. Precio de un artículo: la lista del cliente (<c>credit.priceListId</c>),
/// si no la de la tienda (<see cref="SiteIds"/>), si no el precio del artículo. Ver <see cref="PriceFor"/> y
/// <see cref="VortexPriceLists.Choose"/>.
/// </summary>
public sealed record VortexPriceList(
    Guid Id, string Code, string Name, string CurrencyCode, DateOnly? ValidFrom, DateOnly? ValidTo, IReadOnlyList<Guid> SiteIds,
    bool IsActive, IReadOnlyList<VortexPriceListLine> Lines, VortexMasterMeta Meta)
{
    /// <summary>Activa y dentro de su vigencia en <paramref name="date"/>.</summary>
    public bool AppliesOn(DateOnly date) => IsActive && Meta.DeletedAt is null && (ValidFrom is null || ValidFrom <= date) &&
                                             (ValidTo is null || date <= ValidTo);

    /// <summary>
    /// Precio del artículo para <paramref name="quantity"/> (la escala con la mayor cantidad mínima que no la supera), o
    /// null si el artículo no está en la lista.
    /// </summary>
    /// <remarks>Las cantidades menores a 1 (0.5 kg) toman la escala "desde 1".</remarks>
    public decimal? PriceFor(Guid itemId, decimal quantity = 1) =>
        Lines.Where(l => l.ItemId == itemId && l.MinQuantity <= Math.Max(quantity, 1)).OrderByDescending(l => l.MinQuantity)
            .Select(l => (decimal?)l.Price).FirstOrDefault();
}

public sealed record VortexPriceListLine(Guid ItemId, decimal Price, decimal MinQuantity = 1);

public static class VortexPriceLists
{
    /// <summary>
    /// La lista que rige una venta: la del cliente si aplica en la fecha; si no, la primera (por código) de la tienda que
    /// aplique. Null: precio del artículo.
    /// </summary>
    public static VortexPriceList? Choose(IEnumerable<VortexPriceList> lists, Guid? customerListId, Guid? siteId, DateOnly date)
    {
        var all = lists.Where(l => l.AppliesOn(date)).ToList();
        return all.FirstOrDefault(l => l.Id == customerListId)
               ?? (siteId is { } site ? all.Where(l => l.SiteIds.Contains(site)).OrderBy(l => l.Code, StringComparer.Ordinal).FirstOrDefault() : null);
    }
}

/// <summary>Quién del equipo atiende al cliente (lo maneja el CRM).</summary>
public sealed record VortexPartyAccount(Guid? OwnerUserId = null, string? OwnerName = null);

/// <summary>
/// Artículo del hub. <c>UnitCode</c> es la unidad base (UN/ECE Rec. 20; Tabla 6 de SUNAT); <c>Tracking</c> es
/// <c>None</c>, <c>Lot</c> (con vencimiento) o <c>Serial</c>; <c>Presentations</c> trae el factor en unidades base (caja × 12).
/// </summary>
public sealed record VortexItem(
    Guid Id, string Sku, string Name, string Kind, string? Description, string Unit, string? Barcode, string? Category,
    decimal Price, string CurrencyCode, string? TaxCode, bool TrackInventory, bool IsActive, VortexMasterMeta Meta,
    string? UnitCode = null, decimal? WeightKg = null, string Tracking = "None", IReadOnlyList<VortexItemPresentation>? Presentations = null)
{
    /// <summary>La presentación con ese código de barras (null si es el de la unidad o no es de este artículo).</summary>
    public VortexItemPresentation? PresentationFor(string barcode) =>
        Presentations?.FirstOrDefault(p => string.Equals(p.Barcode, barcode.Trim(), StringComparison.Ordinal));
}

public sealed record VortexItemPresentation(string Code, string Name, decimal Factor, string? Barcode = null, string? UnitCode = null);

public sealed record VortexSite(
    Guid Id, string Code, string Name, string Kind, string? Phone, VortexAddress? Address, bool IsActive, VortexMasterMeta Meta);

/// <summary>Tipo de cambio de un día: 1 <c>FromCurrency</c> = <c>Rate</c> <c>ToCurrency</c>.</summary>
public sealed record VortexExchangeRate(
    Guid Id, DateOnly Date, string FromCurrency, string ToCurrency, decimal Rate, decimal? BuyRate, decimal? SellRate,
    string? Source, VortexMasterMeta Meta)
{
    /// <summary>
    /// Convierte un monto de <c>FromCurrency</c> a <c>ToCurrency</c> (sin redondear: redondee con
    /// <c>VortexApi.Suite.Money.Currencies</c> según la moneda destino). Use <paramref name="side"/> para compra o venta.
    /// </summary>
    public decimal Convert(decimal amount, VortexRateSide side = VortexRateSide.Reference) => amount * RateFor(side);

    /// <summary>Convierte de <c>ToCurrency</c> a <c>FromCurrency</c>.</summary>
    public decimal ConvertBack(decimal amount, VortexRateSide side = VortexRateSide.Reference) => amount / RateFor(side);

    public decimal RateFor(VortexRateSide side) => side switch
    {
        VortexRateSide.Buy => BuyRate ?? Rate,
        VortexRateSide.Sell => SellRate ?? Rate,
        _ => Rate,
    };
}

public enum VortexRateSide
{
    Reference,
    Buy,
    Sell,
}

public sealed record VortexCompany(
    Guid OrganizationId, string LegalName, string? TradeName, string? TaxId, string CountryCode, string CurrencyCode,
    string TimeZone, decimal DefaultTaxRate, string? Email, string? Phone, VortexAddress? Address, VortexMasterMeta Meta);

public enum VortexChangeOperation
{
    Created,
    Updated,
    Deleted,
}

/// <summary>Cambio de datos maestros sin tipar; <see cref="Data"/> es el registro completo en esa versión.</summary>
public sealed record VortexMasterChange(
    long Sequence, string EntityType, Guid EntityId, VortexChangeOperation Operation, int Version,
    DateTimeOffset ChangedAt, Guid? ChangedByUserId, string Source, JsonElement Data)
{
    public T DataAs<T>() => Data.Deserialize<T>(VortexJson.Options)!;
}

/// <summary>Cambio de datos maestros con el registro deserializado.</summary>
public sealed record VortexMasterChange<T>(
    long Sequence, Guid EntityId, VortexChangeOperation Operation, int Version, DateTimeOffset ChangedAt, string Source, T Data);

public sealed record VortexChangePage(IReadOnlyList<VortexMasterChange> Changes, long NextCursor, bool HasMore);

public sealed record VortexEvent(
    Guid Id, long Sequence, string Type, string Source, int SchemaVersion, string? SubjectType, string? SubjectId,
    DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, Guid? UserId, JsonElement Data)
{
    public T DataAs<T>() => Data.Deserialize<T>(VortexJson.Options)!;
}

public sealed record VortexEventPage(IReadOnlyList<VortexEvent> Events, long NextCursor, bool HasMore);

public sealed record VortexEventToPublish(
    Guid Id, string Type, JsonElement Data, DateTimeOffset? OccurredAt = null, string? SubjectType = null,
    string? SubjectId = null, int SchemaVersion = 1);

public sealed record VortexPublishResult(Guid Id, long? Sequence, bool Created, string? ErrorCode, string? Error);

/// <summary>Rutas de datos maestros para <see cref="VortexApiClient.PutMasterAsync"/>.</summary>
public static class VortexMasterResource
{
    public const string Parties = "parties";
    public const string Items = "items";
    public const string Sites = "sites";
    public const string ExchangeRates = "exchange-rates";
    public const string PriceLists = "price-lists";

    /// <summary>Tipo de entidad en el feed (<c>party</c>, <c>item</c>, <c>site</c>, <c>exchange_rate</c>) para una ruta.</summary>
    public static string EntityTypeOf(string resource) => resource switch
    {
        Parties => "party",
        Items => "item",
        Sites => "site",
        ExchangeRates => "exchange_rate",
        PriceLists => "price_list",
        _ => throw new ArgumentException($"Recurso desconocido: {resource}", nameof(resource)),
    };
}

public static class VortexJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
