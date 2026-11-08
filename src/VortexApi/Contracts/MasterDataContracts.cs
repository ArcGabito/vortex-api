using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using VortexApi.Domain;

namespace VortexApi.Contracts;

// Datos maestros compartidos entre los sistemas de la suite.

public sealed record AddressDto(
    [MaxLength(300)] string? Line,
    [MaxLength(100)] string? District,
    [MaxLength(100)] string? City,
    [MaxLength(100)] string? Region,
    [MaxLength(20)] string? PostalCode,
    [StringLength(2, MinimumLength = 2)] string? CountryCode);

/// <summary>Metadatos comunes: la versión se usa en <c>If-Match</c> para evitar pisar cambios de otro sistema.</summary>
public sealed record MasterMeta(int Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt);

public sealed record PartyRequest(
    [Required, MaxLength(200)] string Name,
    bool IsCustomer = true,
    bool IsSupplier = false,
    PartyKind Kind = PartyKind.Company,
    [MaxLength(200)] string? TradeName = null,
    [MaxLength(20)] string? DocumentType = null,
    [MaxLength(20)] string? DocumentNumber = null,
    [EmailAddress, MaxLength(256)] string? Email = null,
    [MaxLength(50)] string? Phone = null,
    AddressDto? Address = null,
    [MaxLength(2000)] string? Notes = null,
    bool IsActive = true,
    PartyCreditDto? Credit = null,
    PartyAccountDto? Account = null);

/// <summary>
/// Condiciones comerciales del cliente (las maneja el ERP). En un PUT, si no se manda, quedan como estaban: así un
/// sistema que no las conoce no las borra al guardar el cliente.
/// </summary>
/// <param name="PaymentTermDays">Días de plazo para pagar (0 = contado).</param>
/// <param name="CreditLimit">Crédito máximo en la moneda de la empresa (null = sin límite).</param>
/// <param name="DiscountPercent">Descuento habitual del cliente (0 a 100).</param>
public sealed record PartyCreditDto(
    [Range(0, 365)] int? PaymentTermDays = null,
    [Range(0, 999_999_999)] decimal? CreditLimit = null,
    [Range(0, 100)] decimal? DiscountPercent = null);

/// <summary>Quién del equipo atiende al cliente (lo maneja el CRM). En un PUT, si no se manda, queda como estaba.</summary>
public sealed record PartyAccountDto(Guid? OwnerUserId = null, [MaxLength(200)] string? OwnerName = null);

public sealed record PartyDto(
    Guid Id, bool IsCustomer, bool IsSupplier, PartyKind Kind, string Name, string? TradeName,
    string? DocumentType, string? DocumentNumber, string? Email, string? Phone, AddressDto? Address,
    string? Notes, bool IsActive, MasterMeta Meta, PartyCreditDto? Credit = null, PartyAccountDto? Account = null);

public sealed record ItemRequest(
    [Required, MaxLength(64)] string Sku,
    [Required, MaxLength(200)] string Name,
    ItemKind Kind = ItemKind.Product,
    [MaxLength(2000)] string? Description = null,
    [MaxLength(20)] string? Unit = null,
    [MaxLength(64)] string? Barcode = null,
    [MaxLength(100)] string? Category = null,
    [Range(0, 999_999_999)] decimal Price = 0,
    [StringLength(3, MinimumLength = 3)] string? CurrencyCode = null,
    [MaxLength(20)] string? TaxCode = null,
    bool? TrackInventory = null,
    bool IsActive = true,
    [MaxLength(10)] string? UnitCode = null,
    [Range(0, 1_000_000)] decimal? WeightKg = null,
    ItemTracking Tracking = ItemTracking.None,
    [MaxLength(20)] List<ItemPresentationDto>? Presentations = null);

/// <param name="Factor">Unidades base que trae la presentación (caja × 12 = 12).</param>
public sealed record ItemPresentationDto(
    [Required, MaxLength(20)] string Code,
    [Required, MaxLength(100)] string Name,
    [Range(0.000001, 1_000_000)] decimal Factor,
    [MaxLength(64)] string? Barcode = null,
    [MaxLength(10)] string? UnitCode = null);

public sealed record ItemDto(
    Guid Id, string Sku, string Name, ItemKind Kind, string? Description, string Unit, string? Barcode,
    string? Category, decimal Price, string CurrencyCode, string? TaxCode, bool TrackInventory, bool IsActive,
    MasterMeta Meta, string? UnitCode, decimal? WeightKg, ItemTracking Tracking, IReadOnlyList<ItemPresentationDto> Presentations);

public sealed record SiteRequest(
    [Required, MaxLength(20)] string Code,
    [Required, MaxLength(200)] string Name,
    SiteKind Kind = SiteKind.Store,
    [MaxLength(50)] string? Phone = null,
    AddressDto? Address = null,
    bool IsActive = true);

public sealed record SiteDto(
    Guid Id, string Code, string Name, SiteKind Kind, string? Phone, AddressDto? Address, bool IsActive, MasterMeta Meta);

/// <param name="Rate">1 <c>FromCurrency</c> = <c>Rate</c> <c>ToCurrency</c>.</param>
public sealed record ExchangeRateRequest(
    DateOnly Date,
    [Required, StringLength(3, MinimumLength = 3)] string FromCurrency,
    [Required, StringLength(3, MinimumLength = 3)] string ToCurrency,
    [Range(0.0000001, 1_000_000_000)] decimal Rate,
    [Range(0.0000001, 1_000_000_000)] decimal? BuyRate = null,
    [Range(0.0000001, 1_000_000_000)] decimal? SellRate = null,
    [MaxLength(20)] string? Source = null);

public sealed record ExchangeRateDto(
    Guid Id, DateOnly Date, string FromCurrency, string ToCurrency, decimal Rate, decimal? BuyRate, decimal? SellRate,
    string? Source, MasterMeta Meta);

public sealed record CompanyProfileRequest(
    [Required, MaxLength(200)] string LegalName,
    [MaxLength(200)] string? TradeName = null,
    [MaxLength(20)] string? TaxId = null,
    [StringLength(2, MinimumLength = 2)] string CountryCode = "PE",
    [StringLength(3, MinimumLength = 3)] string CurrencyCode = "PEN",
    [MaxLength(64)] string TimeZone = "America/Lima",
    [Range(0, 100)] decimal DefaultTaxRate = 18m,
    [EmailAddress, MaxLength(256)] string? Email = null,
    [MaxLength(50)] string? Phone = null,
    AddressDto? Address = null);

public sealed record CompanyProfileDto(
    Guid OrganizationId, string LegalName, string? TradeName, string? TaxId, string CountryCode, string CurrencyCode,
    string TimeZone, decimal DefaultTaxRate, string? Email, string? Phone, AddressDto? Address, MasterMeta Meta);

/// <summary>Cambio en el feed. <see cref="Data"/> es el estado completo del registro en esa versión.</summary>
public sealed record ChangeDto(
    long Sequence, string EntityType, Guid EntityId, ChangeOperation Operation, int Version,
    DateTimeOffset ChangedAt, Guid? ChangedByUserId, string Source, JsonElement Data);

/// <summary>
/// Página del feed. Guarde <see cref="NextCursor"/> y úselo como <c>since</c> en la siguiente consulta;
/// si <see cref="HasMore"/> es true, consulte de nuevo de inmediato.
/// </summary>
public sealed record ChangeFeedResponse(IReadOnlyList<ChangeDto> Changes, long NextCursor, bool HasMore);
