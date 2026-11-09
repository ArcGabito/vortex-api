namespace VortexApi.Client;

/// <summary>
/// Copia del documento que el cliente ve en el enlace público (<see cref="VortexApiClient.CreatePublicLinkAsync"/>). Los
/// importes van ya calculados; <see cref="CurrencyDecimals"/> dice con cuántos decimales se muestran.
/// </summary>
public sealed record VortexPublicDocument(
    string Title, string Number, DateOnly IssuedOn, DateOnly? ValidUntil, string Currency, VortexPublicDocumentParty Issuer,
    VortexPublicDocumentParty? Customer, IReadOnlyList<VortexPublicDocumentLine> Lines, IReadOnlyList<VortexPublicDocumentTotal> Totals,
    string? Notes = null, int CurrencyDecimals = 2, string? SellerName = null, string? SellerEmail = null, string? SellerPhone = null);

public sealed record VortexPublicDocumentParty(string Name, string? TaxId = null, string? Address = null, string? Email = null,
    string? Phone = null);

/// <param name="Optional">Se ofrece aparte: no suma al total.</param>
public sealed record VortexPublicDocumentLine(string Description, decimal Quantity, string? Unit, decimal UnitPrice, decimal Amount,
    bool Optional = false);

/// <param name="Grand">El total a pagar (se resalta).</param>
public sealed record VortexPublicDocumentTotal(string Label, decimal Amount, bool Grand = false);

/// <param name="DocumentType">Tipo de documento en minúsculas (<c>quote</c>).</param>
/// <param name="Language"><c>es</c>, <c>en</c> o <c>pt</c>: idioma de la página.</param>
public sealed record VortexPublicLinkRequest(string DocumentType, Guid DocumentId, DateTimeOffset ExpiresAt, VortexPublicDocument Document,
    string? Language = null);

public enum VortexPublicLinkStatus
{
    Open,
    Accepted,
    Rejected,
    Revoked,
}

/// <param name="Url">Página pública para el cliente (siempre la misma para el mismo documento).</param>
/// <param name="Signature">Firma del cliente (PNG en data URL); solo en <see cref="VortexApiClient.GetPublicLinkAsync"/>.</param>
public sealed record VortexPublicLink(
    Guid Id, string Url, string DocumentType, Guid DocumentId, string DocumentNumber, string Language, VortexPublicLinkStatus Status,
    DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int ViewCount, DateTimeOffset? FirstViewedAt,
    DateTimeOffset? LastViewedAt, DateTimeOffset? RespondedAt, string? SignerName, string? SignerEmail, string? Comment,
    string? Signature);

/// <summary>Contenido de <see cref="VortexHubEvents.PublicLinkAccepted"/> y <see cref="VortexHubEvents.PublicLinkRejected"/>.</summary>
/// <param name="Response"><c>accepted</c> o <c>rejected</c>.</param>
public sealed record VortexPublicLinkResponded(
    Guid LinkId, string Product, string DocumentType, Guid DocumentId, string DocumentNumber, string Response,
    DateTimeOffset RespondedAt, string? SignerName, string? SignerEmail, string? Comment)
{
    public bool Accepted => Response == "accepted";
}

/// <summary>Eventos que publica el propio hub.</summary>
public static class VortexHubEvents
{
    public const string PublicLinkAccepted = "hub.public_link.accepted";
    public const string PublicLinkRejected = "hub.public_link.rejected";

    /// <summary>Patrón para <c>OnEvent</c>: las dos respuestas.</summary>
    public const string PublicLinkResponses = "hub.public_link.*";
}
