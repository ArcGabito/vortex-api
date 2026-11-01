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
    VortexMasterMeta Meta);

public sealed record VortexItem(
    Guid Id, string Sku, string Name, string Kind, string? Description, string Unit, string? Barcode, string? Category,
    decimal Price, string CurrencyCode, string? TaxCode, bool TrackInventory, bool IsActive, VortexMasterMeta Meta);

public sealed record VortexSite(
    Guid Id, string Code, string Name, string Kind, string? Phone, VortexAddress? Address, bool IsActive, VortexMasterMeta Meta);

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

    /// <summary>Tipo de entidad en el feed (<c>party</c>, <c>item</c>, <c>site</c>) para una ruta.</summary>
    public static string EntityTypeOf(string resource) => resource switch
    {
        Parties => "party",
        Items => "item",
        Sites => "site",
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
