using System.Globalization;
using System.Xml.Linq;

namespace VortexApi.Fiscal.Peru;

/// <summary>Emisor para el XML: los datos de la empresa en el hub y el ubigeo del perfil fiscal.</summary>
public sealed record PeruIssuer(string Ruc, string LegalName, string? TradeName, string? Ubigeo, string? Address, string? District, string? Province,
    string? Department);

/// <summary>Montos de una línea ya calculados.</summary>
public sealed record PeruLine(FiscalLine Source, decimal LineExtension, decimal Tax, decimal UnitPrice, string AffectationCode, string SchemeId,
    string SchemeName, string SchemeType);

/// <summary>Totales del comprobante (en Perú, gravadas, exoneradas, inafectas, exportación, IGV y total).</summary>
public sealed record PeruTotals(decimal Taxed, decimal Exempt, decimal Unaffected, decimal Export, decimal Tax, decimal LineExtension, decimal Total);

/// <summary>
/// Arma el XML UBL 2.1 de SUNAT para facturas (01), boletas (03), notas de crédito (07) y de débito (08): versión 2.1,
/// personalización 2.0, tipo de operación 0101 (venta interna), afectación del IGV por línea (catálogo 7), tributos
/// (catálogo 5), forma de pago en facturas y documento de referencia en las notas. Deja vacío
/// <c>ext:ExtensionContent</c> para la firma.
/// </summary>
public static class PeruUbl
{
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    public static string TypeCode(string type) => type switch
    {
        FiscalDocumentTypes.Invoice => "01",
        FiscalDocumentTypes.Receipt => "03",
        FiscalDocumentTypes.CreditNote => "07",
        FiscalDocumentTypes.DebitNote => "08",
        _ => throw new ArgumentException($"Tipo de comprobante desconocido: {type}"),
    };

    /// <summary>Nombre del archivo que pide SUNAT: RUC-TIPO-SERIE-NUMERO (sin extensión).</summary>
    public static string FileName(string ruc, string typeCode, string series, long number) => $"{ruc}-{typeCode}-{series}-{number}";

    /// <summary>Calcula cada línea: valor de venta, IGV (redondeado por línea) y precio con impuesto.</summary>
    public static (List<PeruLine> Lines, PeruTotals Totals) Compute(IReadOnlyList<FiscalLine> lines)
    {
        var result = new List<PeruLine>();
        foreach (var l in lines)
        {
            var (code, id, name, type, taxed) = l.TaxCategory switch
            {
                FiscalTaxCategories.Taxed => ("10", "1000", "IGV", "VAT", true),
                FiscalTaxCategories.Exempt => ("20", "9997", "EXO", "VAT", false),
                FiscalTaxCategories.Unaffected => ("30", "9998", "INA", "FRE", false),
                _ => ("40", "9995", "EXP", "FRE", false),
            };
            var rate = taxed ? l.TaxRate : 0;
            decimal extension, tax, price;
            var source = l;
            if (l.Total is { } total)
            {
                // Desde lo cobrado: valor de venta = total / (1 + tasa) y el impuesto es la diferencia.
                extension = Round(total / (1 + rate));
                tax = total - extension;
                price = l.Quantity > 0 ? Math.Round(total / l.Quantity, 10, MidpointRounding.AwayFromZero) : 0;
                source = l with { UnitValue = l.Quantity > 0 ? Math.Round(extension / l.Quantity, 10, MidpointRounding.AwayFromZero) : 0 };
            }
            else
            {
                extension = Round(l.Quantity * l.UnitValue);
                tax = Round(extension * rate);
                price = Math.Round(l.UnitValue * (1 + rate), 2, MidpointRounding.AwayFromZero);
            }
            result.Add(new PeruLine(source, extension, tax, price, code, id, name, type));
        }
        decimal Sum(string code) => result.Where(r => r.AffectationCode == code).Sum(r => r.LineExtension);
        var totals = new PeruTotals(Sum("10"), Sum("20"), Sum("30"), Sum("40"), result.Sum(r => r.Tax), result.Sum(r => r.LineExtension),
            result.Sum(r => r.LineExtension + r.Tax));
        return (result, totals);
    }

    public static string Build(FiscalDocumentRequest r, PeruIssuer issuer)
    {
        var (lines, totals) = Compute(r.Lines);
        var typeCode = TypeCode(r.Type);
        var isNote = r.Type is FiscalDocumentTypes.CreditNote or FiscalDocumentTypes.DebitNote;
        XNamespace root = r.Type switch
        {
            FiscalDocumentTypes.CreditNote => "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2",
            FiscalDocumentTypes.DebitNote => "urn:oasis:names:specification:ubl:schema:xsd:DebitNote-2",
            _ => "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2",
        };
        var rootName = r.Type switch
        {
            FiscalDocumentTypes.CreditNote => "CreditNote",
            FiscalDocumentTypes.DebitNote => "DebitNote",
            _ => "Invoice",
        };
        var currency = r.Currency.ToUpperInvariant();
        XElement Money(XName name, decimal value) => new(name, new XAttribute("currencyID", currency), Fmt(value));

        var doc = new XElement(root + rootName,
            new XAttribute(XNamespace.Xmlns + "cac", Cac), new XAttribute(XNamespace.Xmlns + "cbc", Cbc),
            new XAttribute(XNamespace.Xmlns + "ds", Ds), new XAttribute(XNamespace.Xmlns + "ext", Ext),
            new XElement(Ext + "UBLExtensions", new XElement(Ext + "UBLExtension", new XElement(Ext + "ExtensionContent"))),
            new XElement(Cbc + "UBLVersionID", "2.1"),
            new XElement(Cbc + "CustomizationID", "2.0"),
            new XElement(Cbc + "ID", $"{r.Series}-{r.Number}"),
            new XElement(Cbc + "IssueDate", r.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new XElement(Cbc + "IssueTime", (r.IssueTime ?? new TimeOnly(0, 0)).ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        if (!isNote && r.DueDate is { } due)
            doc.Add(new XElement(Cbc + "DueDate", due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (!isNote)
            doc.Add(new XElement(Cbc + "InvoiceTypeCode", new XAttribute("listID", "0101"), typeCode));
        doc.Add(new XElement(Cbc + "Note", new XAttribute("languageLocaleID", "1000"), new XCData(AmountInWords.Spanish(totals.Total, currency))));
        if (r.Observation is { Length: > 0 } observation)
            doc.Add(new XElement(Cbc + "Note", new XCData(observation)));
        doc.Add(new XElement(Cbc + "DocumentCurrencyCode", currency));

        if (isNote && r.Reference is { } reference)
        {
            doc.Add(new XElement(Cac + "DiscrepancyResponse",
                new XElement(Cbc + "ReferenceID", $"{reference.Series}-{reference.Number}"),
                new XElement(Cbc + "ResponseCode", reference.ReasonCode),
                new XElement(Cbc + "Description", new XCData(reference.Description))));
            doc.Add(new XElement(Cac + "BillingReference",
                new XElement(Cac + "InvoiceDocumentReference",
                    new XElement(Cbc + "ID", $"{reference.Series}-{reference.Number}"),
                    new XElement(Cbc + "DocumentTypeCode", TypeCode(reference.Type)))));
        }

        doc.Add(new XElement(Cac + "Signature",
            new XElement(Cbc + "ID", issuer.Ruc),
            new XElement(Cac + "SignatoryParty",
                new XElement(Cac + "PartyIdentification", new XElement(Cbc + "ID", issuer.Ruc)),
                new XElement(Cac + "PartyName", new XElement(Cbc + "Name", new XCData(issuer.LegalName)))),
            new XElement(Cac + "DigitalSignatureAttachment",
                new XElement(Cac + "ExternalReference", new XElement(Cbc + "URI", "#" + XmlSigner.SignatureId)))));

        var address = new XElement(Cac + "RegistrationAddress",
            new XElement(Cbc + "ID", issuer.Ubigeo ?? "-"),
            new XElement(Cbc + "AddressTypeCode", "0000"));
        if (issuer.Province is { } province)
            address.Add(new XElement(Cbc + "CityName", province));
        if (issuer.Department is { } department)
            address.Add(new XElement(Cbc + "CountrySubentity", department));
        if (issuer.District is { } district)
            address.Add(new XElement(Cbc + "District", district));
        address.Add(new XElement(Cac + "AddressLine", new XElement(Cbc + "Line", new XCData(issuer.Address ?? "-"))),
            new XElement(Cac + "Country", new XElement(Cbc + "IdentificationCode", "PE")));
        doc.Add(new XElement(Cac + "AccountingSupplierParty", new XElement(Cac + "Party",
            new XElement(Cac + "PartyIdentification", new XElement(Cbc + "ID", new XAttribute("schemeID", "6"), issuer.Ruc)),
            new XElement(Cac + "PartyName", new XElement(Cbc + "Name", new XCData(issuer.TradeName ?? issuer.LegalName))),
            new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", new XCData(issuer.LegalName)), address))));

        var customer = new XElement(Cac + "Party",
            new XElement(Cac + "PartyIdentification", new XElement(Cbc + "ID", new XAttribute("schemeID", r.Customer.DocType), r.Customer.DocNumber ?? "-")),
            new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", new XCData(r.Customer.Name))));
        if (r.Customer.Address is { Length: > 0 } customerAddress)
            customer.Element(Cac + "PartyLegalEntity")!.Add(new XElement(Cac + "RegistrationAddress",
                new XElement(Cac + "AddressLine", new XElement(Cbc + "Line", new XCData(customerAddress)))));
        doc.Add(new XElement(Cac + "AccountingCustomerParty", customer));

        // Forma de pago: obligatoria en facturas (contado, o crédito con el monto pendiente y sus cuotas).
        if (r.Type == FiscalDocumentTypes.Invoice)
        {
            var payment = r.Payment ?? new FiscalPayment(false);
            if (!payment.Credit)
            {
                doc.Add(new XElement(Cac + "PaymentTerms", new XElement(Cbc + "ID", "FormaPago"), new XElement(Cbc + "PaymentMeansID", "Contado")));
            }
            else
            {
                var installments = payment.Installments is { Count: > 0 } i ? i : [new FiscalInstallment(totals.Total, r.DueDate ?? r.IssueDate)];
                doc.Add(new XElement(Cac + "PaymentTerms", new XElement(Cbc + "ID", "FormaPago"), new XElement(Cbc + "PaymentMeansID", "Credito"),
                    Money(Cbc + "Amount", installments.Sum(x => x.Amount))));
                var n = 0;
                foreach (var cuota in installments)
                    doc.Add(new XElement(Cac + "PaymentTerms", new XElement(Cbc + "ID", "FormaPago"),
                        new XElement(Cbc + "PaymentMeansID", $"Cuota{++n:000}"), Money(Cbc + "Amount", cuota.Amount),
                        new XElement(Cbc + "PaymentDueDate", cuota.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
            }
        }

        var taxTotal = new XElement(Cac + "TaxTotal", Money(Cbc + "TaxAmount", totals.Tax));
        foreach (var g in lines.GroupBy(l => (l.SchemeId, l.SchemeName, l.SchemeType)))
            taxTotal.Add(new XElement(Cac + "TaxSubtotal",
                Money(Cbc + "TaxableAmount", g.Sum(l => l.LineExtension)), Money(Cbc + "TaxAmount", g.Sum(l => l.Tax)),
                new XElement(Cac + "TaxCategory", Scheme(g.Key.SchemeId, g.Key.SchemeName, g.Key.SchemeType))));
        doc.Add(taxTotal);

        doc.Add(new XElement(Cac + (r.Type == FiscalDocumentTypes.DebitNote ? "RequestedMonetaryTotal" : "LegalMonetaryTotal"),
            Money(Cbc + "LineExtensionAmount", totals.LineExtension),
            Money(Cbc + "TaxInclusiveAmount", totals.Total),
            Money(Cbc + "PayableAmount", totals.Total)));

        var (lineName, quantityName) = r.Type switch
        {
            FiscalDocumentTypes.CreditNote => ("CreditNoteLine", "CreditedQuantity"),
            FiscalDocumentTypes.DebitNote => ("DebitNoteLine", "DebitedQuantity"),
            _ => ("InvoiceLine", "InvoicedQuantity"),
        };
        var no = 0;
        foreach (var l in lines)
        {
            var item = new XElement(Cac + "Item", new XElement(Cbc + "Description", new XCData(l.Source.Description)));
            if (l.Source.ItemCode is { Length: > 0 } code)
                item.Add(new XElement(Cac + "SellersItemIdentification", new XElement(Cbc + "ID", code)));
            doc.Add(new XElement(Cac + lineName,
                new XElement(Cbc + "ID", ++no),
                new XElement(Cbc + quantityName, new XAttribute("unitCode", l.Source.UnitCode), Fmt(l.Source.Quantity, 10)),
                Money(Cbc + "LineExtensionAmount", l.LineExtension),
                new XElement(Cac + "PricingReference", new XElement(Cac + "AlternativeConditionPrice",
                    new XElement(Cbc + "PriceAmount", new XAttribute("currencyID", currency), Fmt(l.UnitPrice, 10)),
                    new XElement(Cbc + "PriceTypeCode", "01"))),
                new XElement(Cac + "TaxTotal", Money(Cbc + "TaxAmount", l.Tax),
                    new XElement(Cac + "TaxSubtotal", Money(Cbc + "TaxableAmount", l.LineExtension), Money(Cbc + "TaxAmount", l.Tax),
                        new XElement(Cac + "TaxCategory",
                            new XElement(Cbc + "Percent", Fmt(l.AffectationCode == "10" ? l.Source.TaxRate * 100 : 0)),
                            new XElement(Cbc + "TaxExemptionReasonCode", l.AffectationCode),
                            Scheme(l.SchemeId, l.SchemeName, l.SchemeType)))),
                item,
                new XElement(Cac + "Price", new XElement(Cbc + "PriceAmount", new XAttribute("currencyID", currency), Fmt(l.Source.UnitValue, 10)))));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", "no"), doc).Declaration + doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Texto del QR de la representación impresa: RUC | tipo | serie | número | IGV | total | fecha | tipo doc. del
    /// cliente | número del cliente | resumen de la firma.
    /// </summary>
    public static string QrText(string ruc, FiscalDocumentRequest r, string? hash)
    {
        var (_, totals) = Compute(r.Lines);
        return string.Join('|', ruc, TypeCode(r.Type), r.Series, r.Number, Fmt(totals.Tax), Fmt(totals.Total),
            r.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Customer.DocType, r.Customer.DocNumber ?? "-", hash ?? "") + "|";
    }

    private static XElement Scheme(string id, string name, string type) => new(Cac + "TaxScheme",
        new XElement(Cbc + "ID", id), new XElement(Cbc + "Name", name), new XElement(Cbc + "TaxTypeCode", type));

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Fmt(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Fmt(decimal value, int maxDecimals) =>
        Math.Round(value, maxDecimals, MidpointRounding.AwayFromZero).ToString("0.00########", CultureInfo.InvariantCulture);
}

/// <summary>Monto en letras para la leyenda 1000 de SUNAT: "CIENTO OCHENTA Y CINCO CON 00/100 SOLES".</summary>
public static class AmountInWords
{
    private static readonly string[] Units = ["", "UNO", "DOS", "TRES", "CUATRO", "CINCO", "SEIS", "SIETE", "OCHO", "NUEVE", "DIEZ", "ONCE", "DOCE",
        "TRECE", "CATORCE", "QUINCE", "DIECISÉIS", "DIECISIETE", "DIECIOCHO", "DIECINUEVE", "VEINTE", "VEINTIUNO", "VEINTIDÓS", "VEINTITRÉS",
        "VEINTICUATRO", "VEINTICINCO", "VEINTISÉIS", "VEINTISIETE", "VEINTIOCHO", "VEINTINUEVE"];
    private static readonly string[] Tens = ["", "", "", "TREINTA", "CUARENTA", "CINCUENTA", "SESENTA", "SETENTA", "OCHENTA", "NOVENTA"];
    private static readonly string[] Hundreds = ["", "CIENTO", "DOSCIENTOS", "TRESCIENTOS", "CUATROCIENTOS", "QUINIENTOS", "SEISCIENTOS",
        "SETECIENTOS", "OCHOCIENTOS", "NOVECIENTOS"];

    public static string Spanish(decimal amount, string currency)
    {
        var integer = (long)Math.Floor(Math.Abs(amount));
        var cents = (int)Math.Round((Math.Abs(amount) - integer) * 100, MidpointRounding.AwayFromZero);
        var name = currency switch { "PEN" => "SOLES", "USD" => "DÓLARES AMERICANOS", "EUR" => "EUROS", _ => currency };
        return $"{(integer == 0 ? "CERO" : Words(integer))} CON {cents:00}/100 {name}";
    }

    /// <summary>Delante de MIL y MILLONES: "UNO" pasa a "UN" ("VEINTIÚN MIL", "CIENTO UN MIL").</summary>
    private static string Apocope(string words) =>
        words.EndsWith("VEINTIUNO", StringComparison.Ordinal) ? words[..^9] + "VEINTIÚN"
        : words.EndsWith("UNO", StringComparison.Ordinal) ? words[..^1]
        : words;

    private static string Words(long n)
    {
        if (n >= 1_000_000)
        {
            var millions = n / 1_000_000;
            var rest = n % 1_000_000;
            return (millions == 1 ? "UN MILLÓN" : $"{Apocope(Words(millions))} MILLONES") + (rest > 0 ? " " + Words(rest) : "");
        }
        if (n >= 1000)
        {
            var thousands = n / 1000;
            var rest = n % 1000;
            return (thousands == 1 ? "MIL" : $"{Apocope(Words(thousands))} MIL") + (rest > 0 ? " " + Words(rest) : "");
        }
        if (n == 100)
            return "CIEN";
        if (n >= 100)
            return Hundreds[n / 100] + (n % 100 > 0 ? " " + Words(n % 100) : "");
        if (n < 30)
            return Units[n];
        return Tens[n / 10] + (n % 10 > 0 ? " Y " + Units[n % 10] : "");
    }
}
