namespace VortexApi.Client;

/// <summary>Tipos de comprobante que se piden al hub (<see cref="VortexApiClient.IssueFiscalDocumentAsync"/>).</summary>
public static class VortexFiscalTypes
{
    public const string Invoice = "invoice";
    public const string Receipt = "receipt";
    public const string CreditNote = "credit_note";
    public const string DebitNote = "debit_note";
}

/// <summary>Categoría del impuesto de una línea.</summary>
public static class VortexFiscalTaxCategories
{
    public const string Taxed = "taxed";
    public const string Exempt = "exempt";
    public const string Unaffected = "unaffected";
    public const string Export = "export";
}

/// <summary>
/// Comprobante neutro (igual para todos los países): las líneas traen el valor unitario sin impuesto y su categoría; el hub
/// calcula los montos con las reglas del país, lo firma y lo envía a la autoridad.
/// </summary>
/// <param name="SourceId">Id del documento en el sistema: pedirlo otra vez con el mismo no duplica.</param>
/// <param name="Series">Serie del equipo (en Perú F001 para facturas, B001 para boletas; las notas usan la serie de su comprobante).</param>
public sealed record VortexFiscalRequest(
    Guid SourceId, string Type, string Series, long Number, DateOnly IssueDate, string Currency, VortexFiscalParty Customer,
    IReadOnlyList<VortexFiscalLine> Lines, TimeOnly? IssueTime = null, DateOnly? DueDate = null, VortexFiscalPayment? Payment = null,
    VortexFiscalReference? Reference = null, string? Observation = null);

/// <param name="DocType">Catálogo 6 de SUNAT: 6 RUC, 1 DNI, 4 carné de extranjería, 7 pasaporte, 0 no domiciliado, - sin documento.</param>
public sealed record VortexFiscalParty(string DocType, string? DocNumber, string Name, string? Address = null);

/// <param name="UnitCode">Unidad UN/ECE (NIU, ZZ, KGM…).</param>
/// <param name="UnitValue">Valor unitario sin impuesto.</param>
/// <param name="TaxRate">Tasa en fracción (0.18) en las líneas gravadas.</param>
/// <param name="Total">Importe con impuesto y descuentos (lo cobrado): si viene, el hub deriva el valor de venta y el total cuadra al céntimo.</param>
public sealed record VortexFiscalLine(string Description, decimal Quantity, string UnitCode, decimal UnitValue, string TaxCategory,
    decimal TaxRate = 0, string? ItemCode = null, decimal? Total = null);

public sealed record VortexFiscalPayment(bool Credit, IReadOnlyList<VortexFiscalInstallment>? Installments = null);

public sealed record VortexFiscalInstallment(decimal Amount, DateOnly DueDate);

/// <param name="Type">Tipo del comprobante que modifica la nota (invoice o receipt).</param>
/// <param name="ReasonCode">Motivo (catálogo 9 para crédito, 10 para débito): 01 anulación, 07 devolución…</param>
public sealed record VortexFiscalReference(string Type, string Series, long Number, string ReasonCode, string Description);

public enum VortexFiscalStatus
{
    Pending,
    Accepted,
    Observed,
    Rejected,
    Failed,

    /// <summary>Anulado ante la autoridad (comunicación de baja o resumen diario aceptados).</summary>
    Voided,
}

public enum VortexFiscalVoidStatus
{
    Pending,
    Sent,
    Accepted,
    Rejected,
    Failed,
}

/// <summary>Anulación de un comprobante: <c>Kind</c> RA (comunicación de baja) o RC (resumen diario).</summary>
public sealed record VortexFiscalVoid(Guid Id, Guid DocumentId, Guid SourceId, string Kind, string SummaryId, DateOnly IssueDate, string Reason,
    VortexFiscalVoidStatus Status, string? Ticket, string? ResponseCode, string? ResponseDescription, string? LastError, DateTimeOffset CreatedAt);

/// <summary>Contenido de <see cref="VortexHubEvents.FiscalVoided"/> y <see cref="VortexHubEvents.FiscalVoidRejected"/>.</summary>
public sealed record VortexFiscalVoidResponded(Guid SourceId, string Product, string DocumentType, string Series, long Number, bool Voided,
    string SummaryId, string? ResponseCode, string? ResponseDescription);

/// <param name="Hash">Resumen de la firma (va en la representación impresa).</param>
/// <param name="QrText">Texto del código QR de la representación impresa.</param>
public sealed record VortexFiscalDocument(
    Guid Id, Guid SourceId, string Product, string DocumentType, string Series, long Number, DateOnly IssueDate, string CurrencyCode,
    decimal Total, VortexFiscalStatus Status, int Attempts, string? Hash, string? QrText, string? ResponseCode, string? ResponseDescription,
    string? Notes, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? SentAt)
{
    /// <summary>Válido ante la autoridad (aceptado u observado).</summary>
    public bool IsValid => Status is VortexFiscalStatus.Accepted or VortexFiscalStatus.Observed;
}

/// <summary>Contenido de <see cref="VortexHubEvents.FiscalAccepted"/> y <see cref="VortexHubEvents.FiscalRejected"/>.</summary>
public sealed record VortexFiscalResponded(
    Guid SourceId, string Product, string DocumentType, string Series, long Number, VortexFiscalStatus Status, string? Hash, string? QrText,
    string? ResponseCode, string? ResponseDescription, string? Notes);
