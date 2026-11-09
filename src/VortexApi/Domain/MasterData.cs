namespace VortexApi.Domain;

/// <summary>
/// Dato maestro compartido por los sistemas de la suite. Vive en el hub con un Id único; cada sistema lo
/// referencia por ese Id. Cada cambio incrementa <see cref="Version"/> y queda en el feed de cambios.
/// </summary>
public abstract class MasterEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Borrado lógico: otros sistemas pueden seguir referenciando el registro.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}

public enum PartyKind
{
    Company = 0,
    Person = 1,
}

/// <summary>Cliente, proveedor o contacto (CRM, ERP, POS).</summary>
public class Party : MasterEntity
{
    public bool IsCustomer { get; set; }
    public bool IsSupplier { get; set; }
    public PartyKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string? TradeName { get; set; }

    /// <summary>Tipo de documento (p. ej. <c>RUC</c>, <c>DNI</c>, <c>CE</c>, <c>PASAPORTE</c>).</summary>
    public string? DocumentType { get; set; }
    public string? DocumentNumber { get; set; }

    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? AddressLine { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    // Condiciones comerciales (las maneja el ERP).

    /// <summary>Días de plazo para pagar (0 = contado; null = sin definir).</summary>
    public int? PaymentTermDays { get; set; }

    /// <summary>Crédito máximo en la moneda de la empresa (null = sin límite).</summary>
    public decimal? CreditLimit { get; set; }

    /// <summary>Descuento habitual del cliente, en porcentaje (0 a 100).</summary>
    public decimal? DiscountPercent { get; set; }

    /// <summary>Lista de precios del cliente (<see cref="PriceList"/>); gana a la de la tienda y al precio del artículo.</summary>
    public Guid? PriceListId { get; set; }

    // Cuenta (la maneja el CRM): quién del equipo atiende al cliente.
    public Guid? OwnerUserId { get; set; }
    public string? OwnerName { get; set; }
}

public enum ItemKind
{
    Product = 0,
    Service = 1,
}

/// <summary>Producto o servicio (ERP, WMS, POS).</summary>
public class Item : MasterEntity
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ItemKind Kind { get; set; }
    public string Unit { get; set; } = "unidad";
    public string? Barcode { get; set; }
    public string? Category { get; set; }
    public decimal Price { get; set; }
    public string CurrencyCode { get; set; } = "PEN";

    /// <summary>Categoría tributaria (p. ej. <c>igv</c>, <c>exonerado</c>, <c>inafecto</c>). La interpreta cada sistema.</summary>
    public string? TaxCode { get; set; }

    /// <summary>Si el WMS/POS controla stock del artículo. Los servicios no llevan inventario.</summary>
    public bool TrackInventory { get; set; } = true;
    public bool IsActive { get; set; } = true;

    /// <summary>Código de la unidad base (UN/ECE Rec. 20; en Perú, Tabla 6 de SUNAT): <c>NIU</c>, <c>KGM</c>, <c>LTR</c>…</summary>
    public string? UnitCode { get; set; }

    /// <summary>Peso bruto de una unidad base en kg (guías de remisión, fletes, picking).</summary>
    public decimal? WeightKg { get; set; }

    /// <summary>Si el stock se controla por lote (con vencimiento) o por número de serie.</summary>
    public ItemTracking Tracking { get; set; }

    /// <summary>Presentaciones de compra y venta (caja × 12, paquete × 6). El stock siempre se lleva en la unidad base.</summary>
    public List<ItemPresentation> Presentations { get; set; } = [];
}

public enum ItemTracking
{
    None = 0,
    Lot = 1,
    Serial = 2,
}

/// <summary>Presentación de un artículo: cuántas unidades base trae y con qué código de barras se reconoce (DUN-14, EAN).</summary>
public class ItemPresentation
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Factor { get; set; }
    public string? Barcode { get; set; }
    public string? UnitCode { get; set; }
}

/// <summary>
/// Lista de precios compartida por POS, ERP y CRM. Se aplica a los clientes que la tienen (<c>Party.PriceListId</c>) y,
/// en las tiendas de <see cref="SiteIds"/>, a toda venta sin lista propia del cliente. Los precios siguen el mismo
/// criterio que el precio del artículo (con o sin impuesto, según cada sistema) y están en <see cref="CurrencyCode"/>.
/// Un artículo sin línea en la lista se vende a su precio de siempre.
/// </summary>
public class PriceList : MasterEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string CurrencyCode { get; set; } = "PEN";

    /// <summary>Vigencia (inclusive). Sin fechas, siempre vigente.</summary>
    public DateOnly? ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }

    /// <summary>Tiendas donde es la lista por defecto (vacío: ninguna; solo para los clientes que la tienen).</summary>
    public List<Guid> SiteIds { get; set; } = [];

    public bool IsActive { get; set; } = true;
    public List<PriceListLine> Lines { get; set; } = [];
}

/// <summary>Precio de un artículo en la lista desde una cantidad (escalas: 1 a 11 a 10, desde 12 a 9.50).</summary>
public class PriceListLine
{
    public Guid ItemId { get; set; }
    public decimal Price { get; set; }
    public decimal MinQuantity { get; set; } = 1;
}

public enum SiteKind
{
    Store = 0,
    Warehouse = 1,
    Office = 2,
    Other = 3,
}

/// <summary>
/// Tipo de cambio de un día: 1 <see cref="FromCurrency"/> = <see cref="Rate"/> <see cref="ToCurrency"/>. Lo publica el
/// ERP o el administrador (p. ej. el de SUNAT o el BCE) y lo usan todos los sistemas para convertir igual.
/// </summary>
public class ExchangeRate : MasterEntity
{
    public DateOnly Date { get; set; }
    public string FromCurrency { get; set; } = "";
    public string ToCurrency { get; set; } = "";

    /// <summary>Tasa de referencia (promedio o la oficial).</summary>
    public decimal Rate { get; set; }

    /// <summary>Compra y venta, si la fuente las publica (en Perú, SUNAT publica ambas).</summary>
    public decimal? BuyRate { get; set; }
    public decimal? SellRate { get; set; }

    /// <summary>De dónde salió: <c>SUNAT</c>, <c>SBS</c>, <c>BCE</c>, <c>manual</c>…</summary>
    public string? Source { get; set; }
}

/// <summary>Sede, tienda o almacén (ERP, WMS, POS).</summary>
public class Site : MasterEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public SiteKind Kind { get; set; }
    public string? Phone { get; set; }
    public string? AddressLine { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryCode { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Datos de la empresa usados por todos los sistemas. Uno por organización (<c>Id = OrganizationId</c>).</summary>
public class CompanyProfile : MasterEntity
{
    public string LegalName { get; set; } = "";
    public string? TradeName { get; set; }
    public string? TaxId { get; set; }
    public string CountryCode { get; set; } = "PE";
    public string CurrencyCode { get; set; } = "PEN";
    public string TimeZone { get; set; } = "America/Lima";

    /// <summary>Tasa de impuesto general en porcentaje (p. ej. 18 para el IGV).</summary>
    public decimal DefaultTaxRate { get; set; } = 18m;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? AddressLine { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
}

public enum ChangeOperation
{
    Created = 0,
    Updated = 1,
    Deleted = 2,
}

/// <summary>
/// Entrada del feed de cambios. <see cref="Sequence"/> es correlativa por organización y se asigna en orden de
/// commit, así un sistema que lee "todo lo posterior a N" nunca se salta un cambio.
/// </summary>
public class ChangeEntry
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public long Sequence { get; set; }
    public required string EntityType { get; set; }
    public Guid EntityId { get; set; }
    public ChangeOperation Operation { get; set; }
    public int Version { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public Guid? ChangedByUserId { get; set; }

    /// <summary>Sistema que hizo el cambio (ERP, CRM, WMS, POS) o <c>ADMIN</c>.</summary>
    public required string Source { get; set; }

    /// <summary>Estado completo del registro en esa versión (JSON).</summary>
    public required string Payload { get; set; }
}

public static class MasterEntityTypes
{
    public const string Party = "party";
    public const string Item = "item";
    public const string Site = "site";
    public const string Company = "company";
    public const string ExchangeRate = "exchange_rate";
    public const string PriceList = "price_list";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Party, Item, Site, Company, ExchangeRate, PriceList };
}
