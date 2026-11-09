using System.ComponentModel.DataAnnotations;
using VortexApi.Domain;

namespace VortexApi.Contracts;

/// <summary>
/// Copia del documento que ve el cliente en el enlace público. Los importes van ya calculados por el sistema (el hub no
/// recalcula impuestos); <see cref="CurrencyDecimals"/> dice con cuántos decimales se muestran.
/// </summary>
public sealed record PublicDocument(
    [Required, MaxLength(100)] string Title,
    [Required, MaxLength(64)] string Number,
    DateOnly IssuedOn,
    DateOnly? ValidUntil,
    [Required, StringLength(3, MinimumLength = 3)] string Currency,
    [Required] PublicDocumentParty Issuer,
    PublicDocumentParty? Customer,
    [Required, MinLength(1), MaxLength(500)] List<PublicDocumentLine> Lines,
    [Required, MinLength(1), MaxLength(20)] List<PublicDocumentTotal> Totals,
    [MaxLength(4000)] string? Notes = null,
    [Range(0, 4)] int CurrencyDecimals = 2,
    [MaxLength(200)] string? SellerName = null,
    [MaxLength(200)] string? SellerEmail = null,
    [MaxLength(50)] string? SellerPhone = null);

public sealed record PublicDocumentParty(
    [Required, MaxLength(300)] string Name,
    [MaxLength(50)] string? TaxId = null,
    [MaxLength(500)] string? Address = null,
    [MaxLength(200)] string? Email = null,
    [MaxLength(50)] string? Phone = null);

/// <param name="Optional">Se ofrece aparte: no suma al total.</param>
public sealed record PublicDocumentLine(
    [Required, MaxLength(500)] string Description,
    decimal Quantity,
    [MaxLength(20)] string? Unit,
    decimal UnitPrice,
    decimal Amount,
    bool Optional = false);

/// <param name="Grand">El total a pagar (se resalta).</param>
public sealed record PublicDocumentTotal([Required, MaxLength(100)] string Label, decimal Amount, bool Grand = false);

/// <summary>Crea el enlace público de un documento o, si ya tiene uno, actualiza su copia y su vencimiento (misma URL).</summary>
public sealed record PublicLinkRequest(
    [Required, MaxLength(50)] string DocumentType,
    Guid DocumentId,
    DateTimeOffset ExpiresAt,
    [Required] PublicDocument Document,
    [MaxLength(5)] string? Language = null);

public sealed record PublicLinkDto(
    Guid Id, string Url, string DocumentType, Guid DocumentId, string DocumentNumber, string Language, PublicLinkStatus Status,
    DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int ViewCount, DateTimeOffset? FirstViewedAt,
    DateTimeOffset? LastViewedAt, DateTimeOffset? RespondedAt, string? SignerName, string? SignerEmail, string? Comment,
    string? Signature);

// Lo que ve y envía el cliente (sin autenticación).

public sealed record PublicLinkView(
    PublicLinkStatus Status, bool Expired, DateTimeOffset ExpiresAt, string Language, PublicDocument Document,
    DateTimeOffset? RespondedAt, string? SignerName);

public sealed record PublicLinkAcceptRequest(
    [Required, StringLength(200, MinimumLength = 2)] string SignerName,
    [Required] string Signature,
    [EmailAddress, MaxLength(200)] string? SignerEmail = null,
    [MaxLength(1000)] string? Comment = null);

public sealed record PublicLinkRejectRequest([MaxLength(200)] string? SignerName = null, [MaxLength(1000)] string? Comment = null);

/// <summary>Contenido de <c>hub.public_link.accepted</c> y <c>hub.public_link.rejected</c>.</summary>
public sealed record PublicLinkResponded(
    Guid LinkId, string Product, string DocumentType, Guid DocumentId, string DocumentNumber, string Response,
    DateTimeOffset RespondedAt, string? SignerName, string? SignerEmail, string? Comment);
