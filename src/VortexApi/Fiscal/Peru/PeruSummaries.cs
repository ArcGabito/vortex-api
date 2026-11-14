using System.Globalization;
using System.Xml.Linq;

namespace VortexApi.Fiscal.Peru;

/// <summary>Un comprobante dentro de una comunicación de baja o de un resumen diario.</summary>
/// <param name="Reference">En una nota de una boleta, la boleta que modifica.</param>
public sealed record SummaryItem(string DocumentType, string Series, long Number, string Currency, decimal Total, string? CustomerDocType,
    string? CustomerDocNumber, FiscalReference? Reference, PeruTotals? Totals);

/// <summary>
/// XML de los resúmenes de SUNAT (UBL 2.0, fase 7b): <c>VoidedDocuments</c> para la comunicación de baja de facturas y sus
/// notas (RA) y <c>SummaryDocuments</c> 1.1 con estado 3 para anular boletas y sus notas (RC). Se firman igual que los
/// comprobantes (en <c>ext:ExtensionContent</c>) y se envían con <c>sendSummary</c>.
/// </summary>
public static class PeruSummaries
{
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace Sac = "urn:sunat:names:specification:ubl:peru:schema:xsd:SunatAggregateComponents-1";

    public const string VoidedKind = "RA";
    public const string SummaryKind = "RC";

    /// <summary>
    /// Por dónde se anula: una factura, o una nota cuya serie empieza con F, va por comunicación de baja; una boleta, o una
    /// nota de boleta (serie B), por resumen diario.
    /// </summary>
    public static string KindFor(string documentType, string series) =>
        documentType == FiscalDocumentTypes.Receipt || (documentType is FiscalDocumentTypes.CreditNote or FiscalDocumentTypes.DebitNote && series.StartsWith('B'))
            ? SummaryKind
            : VoidedKind;

    /// <summary>Nombre del archivo: <c>RUC-RA-20261114-1</c>.</summary>
    public static string FileName(string ruc, string summaryId) => $"{ruc}-{summaryId}";

    public static string SummaryId(string kind, DateOnly issued, int correlative) =>
        $"{kind}-{issued.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{correlative}";

    /// <summary>Comunicación de baja: la fecha de referencia es la de emisión de los comprobantes.</summary>
    public static string Voided(string summaryId, DateOnly referenceDate, DateOnly issueDate, PeruIssuer issuer,
        IReadOnlyList<(SummaryItem Item, string Reason)> items)
    {
        XNamespace root = "urn:sunat:names:specification:ubl:peru:schema:xsd:VoidedDocuments-1";
        var doc = Header(root + "VoidedDocuments", "1.0", summaryId, referenceDate, issueDate, issuer);
        var line = 0;
        foreach (var (item, reason) in items)
            doc.Add(new XElement(Sac + "VoidedDocumentsLine",
                new XElement(Cbc + "LineID", ++line),
                new XElement(Cbc + "DocumentTypeCode", PeruUbl.TypeCode(item.DocumentType)),
                new XElement(Sac + "DocumentSerialID", item.Series),
                new XElement(Sac + "DocumentNumberID", item.Number),
                new XElement(Sac + "VoidReasonDescription", new XCData(reason.Length > 100 ? reason[..100] : reason))));
        return Serialize(doc);
    }

    /// <summary>Resumen diario con los comprobantes de boleta que se anulan (estado 3), con sus montos por tipo de operación.</summary>
    public static string VoidSummary(string summaryId, DateOnly referenceDate, DateOnly issueDate, PeruIssuer issuer, IReadOnlyList<SummaryItem> items)
    {
        XNamespace root = "urn:sunat:names:specification:ubl:peru:schema:xsd:SummaryDocuments-1";
        var doc = Header(root + "SummaryDocuments", "1.1", summaryId, referenceDate, issueDate, issuer);
        var line = 0;
        foreach (var item in items)
        {
            var currency = item.Currency.ToUpperInvariant();
            XElement Money(XName name, decimal value) => new(name, new XAttribute("currencyID", currency), Fmt(value));
            var totals = item.Totals ?? new PeruTotals(0, 0, 0, 0, 0, 0, item.Total);
            var element = new XElement(Sac + "SummaryDocumentsLine",
                new XElement(Cbc + "LineID", ++line),
                new XElement(Cbc + "DocumentTypeCode", PeruUbl.TypeCode(item.DocumentType)),
                new XElement(Cbc + "ID", $"{item.Series}-{item.Number}"),
                new XElement(Cac + "AccountingCustomerParty",
                    new XElement(Cbc + "CustomerAssignedAccountID", item.CustomerDocNumber ?? "-"),
                    new XElement(Cbc + "AdditionalAccountID", item.CustomerDocType ?? "-")));
            if (item.Reference is { } reference)
                element.Add(new XElement(Cac + "BillingReference",
                    new XElement(Cac + "InvoiceDocumentReference",
                        new XElement(Cbc + "ID", $"{reference.Series}-{reference.Number}"),
                        new XElement(Cbc + "DocumentTypeCode", PeruUbl.TypeCode(reference.Type)))));
            element.Add(new XElement(Cac + "Status", new XElement(Cbc + "ConditionCode", "3")));
            element.Add(Money(Sac + "TotalAmount", totals.Total));
            foreach (var (amount, code) in new[] { (totals.Taxed, "01"), (totals.Exempt, "02"), (totals.Unaffected, "03"), (totals.Export, "04") })
                if (amount > 0)
                    element.Add(new XElement(Sac + "BillingPayment", Money(Cbc + "PaidAmount", amount), new XElement(Cbc + "InstructionID", code)));
            element.Add(new XElement(Cac + "TaxTotal",
                Money(Cbc + "TaxAmount", totals.Tax),
                new XElement(Cac + "TaxSubtotal",
                    Money(Cbc + "TaxAmount", totals.Tax),
                    new XElement(Cac + "TaxCategory",
                        new XElement(Cac + "TaxScheme",
                            new XElement(Cbc + "ID", "1000"), new XElement(Cbc + "Name", "IGV"), new XElement(Cbc + "TaxTypeCode", "VAT"))))));
            doc.Add(element);
        }
        return Serialize(doc);
    }

    private static XElement Header(XName name, string customization, string summaryId, DateOnly referenceDate, DateOnly issueDate, PeruIssuer issuer) =>
        new(name,
            new XAttribute(XNamespace.Xmlns + "cac", Cac), new XAttribute(XNamespace.Xmlns + "cbc", Cbc),
            new XAttribute(XNamespace.Xmlns + "ds", Ds), new XAttribute(XNamespace.Xmlns + "ext", Ext), new XAttribute(XNamespace.Xmlns + "sac", Sac),
            new XElement(Ext + "UBLExtensions", new XElement(Ext + "UBLExtension", new XElement(Ext + "ExtensionContent"))),
            new XElement(Cbc + "UBLVersionID", "2.0"),
            new XElement(Cbc + "CustomizationID", customization),
            new XElement(Cbc + "ID", summaryId),
            new XElement(Cbc + "ReferenceDate", referenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new XElement(Cbc + "IssueDate", issueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new XElement(Cac + "Signature",
                new XElement(Cbc + "ID", issuer.Ruc),
                new XElement(Cac + "SignatoryParty",
                    new XElement(Cac + "PartyIdentification", new XElement(Cbc + "ID", issuer.Ruc)),
                    new XElement(Cac + "PartyName", new XElement(Cbc + "Name", new XCData(issuer.LegalName)))),
                new XElement(Cac + "DigitalSignatureAttachment",
                    new XElement(Cac + "ExternalReference", new XElement(Cbc + "URI", "#" + XmlSigner.SignatureId)))),
            new XElement(Cac + "AccountingSupplierParty",
                new XElement(Cbc + "CustomerAssignedAccountID", issuer.Ruc),
                new XElement(Cbc + "AdditionalAccountID", "6"),
                new XElement(Cac + "Party", new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", new XCData(issuer.LegalName))))));

    private static string Serialize(XElement root) =>
        new XDocument(new XDeclaration("1.0", "utf-8", "no"), root).Declaration + root.ToString(SaveOptions.DisableFormatting);

    private static string Fmt(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
