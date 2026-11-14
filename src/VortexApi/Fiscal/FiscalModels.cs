using System.ComponentModel.DataAnnotations;

namespace VortexApi.Fiscal;

public enum FiscalEnvironment
{
    /// <summary>Ambiente de pruebas de la autoridad tributaria (en Perú, beta de SUNAT).</summary>
    Test = 0,
    Production = 1,
}

/// <summary>
/// Configuración de facturación electrónica de una organización: ambiente, ubigeo del domicilio fiscal, credenciales de la
/// autoridad (en Perú, el usuario y la clave SOL) y certificado digital. Los secretos se guardan cifrados
/// (<see cref="FiscalSecrets"/>) y nunca se devuelven. Los datos del emisor (RUC, razón social, dirección) son los de la
/// empresa en el hub.
/// </summary>
public class FiscalProfile
{
    public Guid OrganizationId { get; set; }
    public bool Enabled { get; set; }
    public FiscalEnvironment Environment { get; set; }

    /// <summary>Código de ubigeo INEI del domicilio fiscal (Perú).</summary>
    public string? Ubigeo { get; set; }

    /// <summary>Usuario secundario SOL (sin el RUC delante).</summary>
    public string? SolUser { get; set; }
    public byte[]? SolPassword { get; set; }

    /// <summary>Certificado digital (PFX) y su contraseña, cifrados.</summary>
    public byte[]? Certificate { get; set; }
    public byte[]? CertificatePassword { get; set; }
    public string? CertificateSubject { get; set; }
    public DateTimeOffset? CertificateExpiresAt { get; set; }

    /// <summary>Algoritmo de la firma: <c>sha1</c> (el que acepta SUNAT desde siempre) o <c>sha256</c>.</summary>
    public string SignatureAlgorithm { get; set; } = "sha1";

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum FiscalStatus
{
    /// <summary>En cola: se firma y se envía en el próximo ciclo.</summary>
    Pending = 0,

    /// <summary>La autoridad lo aceptó.</summary>
    Accepted = 1,

    /// <summary>Aceptado con observaciones (en Perú, códigos 4000 en adelante): es válido, conviene corregir para la próxima.</summary>
    Observed = 2,

    /// <summary>Rechazado: no es válido y hay que emitir otro (o corregir y volver a enviar con otro número).</summary>
    Rejected = 3,

    /// <summary>No se pudo enviar después de todos los reintentos (sin conexión con la autoridad, credenciales).</summary>
    Failed = 4,

    /// <summary>Anulado ante la autoridad (comunicación de baja o resumen diario aceptados).</summary>
    Voided = 5,
}

/// <summary>Estado de una anulación (comunicación de baja o resumen diario).</summary>
public enum FiscalVoidStatus
{
    /// <summary>En cola: falta firmarla y enviarla.</summary>
    Pending = 0,

    /// <summary>Enviada: SUNAT dio el ticket y la está procesando; se consulta hasta tener el CDR.</summary>
    Sent = 1,

    Accepted = 2,
    Rejected = 3,

    /// <summary>No se pudo enviar o consultar tras los reintentos.</summary>
    Failed = 4,
}

/// <summary>
/// Anulación de un comprobante aceptado (fase 7b): las facturas y sus notas por <b>comunicación de baja</b> (RA), las boletas
/// y sus notas por <b>resumen diario</b> con estado 3 (RC). Se envía con <c>sendSummary</c> (devuelve un ticket) y se consulta
/// con <c>getStatus</c> hasta tener el CDR.
/// </summary>
public class FiscalVoid
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public required string Product { get; set; }

    /// <summary>El comprobante que se anula.</summary>
    public Guid DocumentId { get; set; }

    /// <summary><c>RA</c> (comunicación de baja) o <c>RC</c> (resumen diario).</summary>
    public required string Kind { get; set; }

    /// <summary>Identificador del resumen (<c>RA-20261114-1</c>): único por día y tipo en la organización.</summary>
    public required string SummaryId { get; set; }
    public DateOnly IssueDate { get; set; }
    public required string Reason { get; set; }

    public FiscalVoidStatus Status { get; set; }
    public string? Ticket { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? SignedXml { get; set; }
    public string? ResponseCode { get; set; }
    public string? ResponseDescription { get; set; }
    public string? CdrXml { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record FiscalVoidRequest([Required, MinLength(3), MaxLength(100)] string Reason);

public sealed record FiscalVoidDto(Guid Id, Guid DocumentId, Guid SourceId, string Kind, string SummaryId, DateOnly IssueDate, string Reason,
    FiscalVoidStatus Status, string? Ticket, string? ResponseCode, string? ResponseDescription, string? LastError, DateTimeOffset CreatedAt);

/// <summary>Contenido de <c>hub.fiscal.voided</c> y <c>hub.fiscal.void_rejected</c>.</summary>
public sealed record FiscalVoidResponded(Guid SourceId, string Product, string DocumentType, string Series, long Number, bool Voided, string SummaryId,
    string? ResponseCode, string? ResponseDescription);

/// <summary>Un comprobante pedido por un sistema y su recorrido hasta la autoridad tributaria.</summary>
public class FiscalDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }

    /// <summary>Sistema que lo pidió (ERP, POS, WMS) y su Id en ese sistema.</summary>
    public required string Product { get; set; }
    public Guid SourceId { get; set; }

    /// <summary>Tipo de comprobante de SUNAT (catálogo 1): 01 factura, 03 boleta, 07 nota de crédito, 08 nota de débito.</summary>
    public required string DocumentType { get; set; }
    public required string Series { get; set; }
    public long Number { get; set; }
    public DateOnly IssueDate { get; set; }
    public required string CurrencyCode { get; set; }
    public decimal Total { get; set; }
    public string? CustomerDocType { get; set; }
    public string? CustomerDocNumber { get; set; }

    /// <summary>El pedido tal como llegó (JSON).</summary>
    public required string Request { get; set; }

    public FiscalStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>XML firmado que se envió y el resumen de la firma (va en la representación impresa).</summary>
    public string? SignedXml { get; set; }
    public string? Hash { get; set; }

    /// <summary>Texto del código QR de la representación impresa.</summary>
    public string? QrText { get; set; }

    /// <summary>Respuesta de la autoridad: código, descripción, observaciones y el comprobante de recepción (CDR).</summary>
    public string? ResponseCode { get; set; }
    public string? ResponseDescription { get; set; }
    public string? Notes { get; set; }
    public string? CdrXml { get; set; }
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string FullNumber => $"{Series}-{Number}";
}

// ---------- Contratos ----------

public static class FiscalDocumentTypes
{
    public const string Invoice = "invoice";
    public const string Receipt = "receipt";
    public const string CreditNote = "credit_note";
    public const string DebitNote = "debit_note";

    public static readonly string[] All = [Invoice, Receipt, CreditNote, DebitNote];
}

public static class FiscalTaxCategories
{
    /// <summary>Gravado con el impuesto general (IGV).</summary>
    public const string Taxed = "taxed";

    /// <summary>Exonerado.</summary>
    public const string Exempt = "exempt";

    /// <summary>Inafecto.</summary>
    public const string Unaffected = "unaffected";

    /// <summary>Exportación.</summary>
    public const string Export = "export";

    public static readonly string[] All = [Taxed, Exempt, Unaffected, Export];
}

/// <summary>
/// Comprobante neutro que piden los sistemas (lo mismo para todos los países). Las líneas traen el valor unitario sin
/// impuesto y su categoría; el hub calcula los montos con las reglas del país.
/// </summary>
/// <param name="SourceId">Id del documento en el sistema que lo pide (reintentar con el mismo no duplica).</param>
/// <param name="Series">Serie del equipo (en Perú, 4 caracteres: F… para facturas, B… para boletas).</param>
public sealed record FiscalDocumentRequest(
    Guid SourceId,
    [Required] string Type,
    [Required, StringLength(4, MinimumLength = 4)] string Series,
    [Range(1, 99_999_999)] long Number,
    DateOnly IssueDate,
    [Required, StringLength(3, MinimumLength = 3)] string Currency,
    [Required] FiscalParty Customer,
    [Required, MinLength(1), MaxLength(500)] List<FiscalLine> Lines,
    TimeOnly? IssueTime = null,
    DateOnly? DueDate = null,
    FiscalPayment? Payment = null,
    FiscalReference? Reference = null,
    [MaxLength(500)] string? Observation = null);

/// <param name="DocType">Tipo de documento del adquirente (catálogo 6 de SUNAT): 6 RUC, 1 DNI, 4 carné de extranjería, 7 pasaporte, 0 no domiciliado, - sin documento.</param>
public sealed record FiscalParty([Required, MaxLength(1)] string DocType, [MaxLength(15)] string? DocNumber, [Required, MaxLength(300)] string Name,
    [MaxLength(300)] string? Address = null);

/// <param name="UnitCode">Unidad UN/ECE (NIU productos, ZZ servicios, KGM kilos…).</param>
/// <param name="UnitValue">Valor unitario sin impuesto.</param>
/// <param name="TaxRate">Tasa del impuesto (0.18 para el IGV) en las líneas gravadas.</param>
/// <param name="Total">
/// Importe de la línea con impuesto, ya con descuentos (lo que cobró la caja). Si viene, el hub deriva de él el valor de venta
/// y el impuesto (y <paramref name="UnitValue"/> se ignora), así el total del comprobante es exactamente lo cobrado.
/// </param>
public sealed record FiscalLine([Required, MaxLength(500)] string Description, decimal Quantity, [Required, MaxLength(3)] string UnitCode,
    decimal UnitValue, [Required] string TaxCategory, decimal TaxRate = 0, [MaxLength(30)] string? ItemCode = null, decimal? Total = null);

/// <summary>Forma de pago (obligatoria en facturas de Perú): al contado o al crédito con sus cuotas.</summary>
public sealed record FiscalPayment(bool Credit, List<FiscalInstallment>? Installments = null);

public sealed record FiscalInstallment(decimal Amount, DateOnly DueDate);

/// <summary>Comprobante que modifica una nota: tipo (invoice o receipt), serie-número, motivo (catálogo 9 o 10) y sustento.</summary>
public sealed record FiscalReference([Required] string Type, [Required] string Series, long Number, [Required, MaxLength(2)] string ReasonCode,
    [Required, MaxLength(250)] string Description);

/// <summary>Estado de un comprobante para el sistema que lo pidió.</summary>
public sealed record FiscalDocumentDto(
    Guid Id, Guid SourceId, string Product, string DocumentType, string Series, long Number, DateOnly IssueDate, string CurrencyCode, decimal Total,
    FiscalStatus Status, int Attempts, string? Hash, string? QrText, string? ResponseCode, string? ResponseDescription, string? Notes,
    string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);

/// <summary>Contenido de <c>hub.fiscal.accepted</c> y <c>hub.fiscal.rejected</c>.</summary>
public sealed record FiscalDocumentResponded(Guid SourceId, string Product, string DocumentType, string Series, long Number, FiscalStatus Status,
    string? Hash, string? QrText, string? ResponseCode, string? ResponseDescription, string? Notes);

public sealed record FiscalProfileRequest(
    bool Enabled,
    FiscalEnvironment Environment,
    [MaxLength(6)] string? Ubigeo,
    [MaxLength(30)] string? SolUser,
    [MaxLength(100)] string? SolPassword,
    string? CertificateBase64,
    [MaxLength(100)] string? CertificatePassword,
    [MaxLength(10)] string? SignatureAlgorithm = null);

public sealed record FiscalProfileDto(bool Enabled, FiscalEnvironment Environment, string? Ubigeo, string? SolUser, bool HasSolPassword,
    bool HasCertificate, string? CertificateSubject, DateTimeOffset? CertificateExpiresAt, string SignatureAlgorithm, string? Country, string? TaxId);

public static class FiscalEvents
{
    /// <summary>Módulo (<c>features</c> del acceso de la organización al sistema) que habilita la emisión.</summary>
    public const string Feature = "facturacion_electronica";

    public const string Accepted = "hub.fiscal.accepted";
    public const string Rejected = "hub.fiscal.rejected";

    /// <summary>La autoridad aceptó la anulación (comunicación de baja o resumen diario).</summary>
    public const string Voided = "hub.fiscal.voided";

    /// <summary>La autoridad rechazó la anulación o no se pudo enviar: el comprobante sigue vigente.</summary>
    public const string VoidRejected = "hub.fiscal.void_rejected";
}
