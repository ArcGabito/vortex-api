using System.Text.RegularExpressions;

namespace VortexApi.Fiscal.Peru;

/// <summary>
/// Reglas de SUNAT que se revisan antes de encolar un comprobante (así el sistema se entera al instante y no tras el
/// rechazo): series, documento del cliente, líneas, forma de pago y documento de referencia de las notas.
/// </summary>
public static partial class PeruRules
{
    /// <summary>Monto desde el que una boleta debe identificar al cliente (S/ 700).</summary>
    public const decimal ReceiptIdentifyFrom = 700m;

    /// <summary>Motivos de nota de crédito (catálogo 9) y de débito (catálogo 10).</summary>
    public static readonly string[] CreditNoteReasons = ["01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12", "13"];
    public static readonly string[] DebitNoteReasons = ["01", "02", "03", "11", "12"];

    /// <summary>Tipos de documento del cliente (catálogo 6).</summary>
    public static readonly string[] CustomerDocTypes = ["0", "1", "4", "6", "7", "A", "B", "C", "D", "E", "-"];

    /// <summary>RUC: 11 dígitos, empieza en 10, 15, 16, 17 o 20, y dígito verificador módulo 11.</summary>
    public static bool IsValidRuc(string? ruc)
    {
        if (ruc is not { Length: 11 } || !ruc.All(char.IsAsciiDigit) || ruc[..2] is not ("10" or "15" or "16" or "17" or "20"))
            return false;
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = weights.Select((w, i) => w * (ruc[i] - '0')).Sum();
        var check = 11 - sum % 11;
        check = check switch { 10 => 0, 11 => 1, _ => check };
        return ruc[10] - '0' == check;
    }

    public static List<string> Validate(FiscalDocumentRequest r)
    {
        var errors = new List<string>();
        if (!FiscalDocumentTypes.All.Contains(r.Type))
        {
            errors.Add($"type debe ser uno de: {string.Join(", ", FiscalDocumentTypes.All)}.");
            return errors;
        }
        var isNote = r.Type is FiscalDocumentTypes.CreditNote or FiscalDocumentTypes.DebitNote;

        // Serie: F### para facturas y sus notas, B### para boletas y sus notas.
        var letter = r.Type switch
        {
            FiscalDocumentTypes.Invoice => 'F',
            FiscalDocumentTypes.Receipt => 'B',
            _ => r.Reference?.Type == FiscalDocumentTypes.Receipt ? 'B' : 'F',
        };
        if (!SeriesPattern().IsMatch(r.Series) || r.Series[0] != letter)
            errors.Add($"series debe tener 4 caracteres (letra {letter} y tres letras o números en mayúsculas).");

        if (!CurrencyPattern().IsMatch(r.Currency))
            errors.Add("currency debe ser un código ISO 4217 en mayúsculas.");

        // Líneas
        for (var i = 0; i < r.Lines.Count; i++)
        {
            var l = r.Lines[i];
            if (l.Quantity <= 0)
                errors.Add($"lines[{i}].quantity debe ser mayor que cero.");
            if (l.UnitValue < 0 || l.Total < 0)
                errors.Add($"lines[{i}] no puede tener valor ni total negativos.");
            if (!FiscalTaxCategories.All.Contains(l.TaxCategory))
                errors.Add($"lines[{i}].taxCategory debe ser uno de: {string.Join(", ", FiscalTaxCategories.All)}.");
            else if (l.TaxCategory == FiscalTaxCategories.Taxed && l.TaxRate is <= 0 or >= 1)
                errors.Add($"lines[{i}].taxRate debe ser la tasa en fracción (0.18 para el IGV).");
            if (!UnitPattern().IsMatch(l.UnitCode))
                errors.Add($"lines[{i}].unitCode debe ser un código UN/ECE (NIU, ZZ, KGM…).");
        }
        var (_, totals) = PeruUbl.Compute(r.Lines);

        // Cliente
        var customer = r.Customer;
        if (!CustomerDocTypes.Contains(customer.DocType))
            errors.Add("customer.docType debe ser un código del catálogo 6 (6 RUC, 1 DNI, 4 carné de extranjería, 7 pasaporte, 0, -).");
        else if (customer.DocType == "6" && !IsValidRuc(customer.DocNumber))
            errors.Add("customer.docNumber no es un RUC válido.");
        else if (customer.DocType == "1" && (customer.DocNumber is not { Length: 8 } || !customer.DocNumber.All(char.IsAsciiDigit)))
            errors.Add("customer.docNumber debe ser un DNI de 8 dígitos.");
        else if (customer.DocType is not ("-" or "0") && string.IsNullOrWhiteSpace(customer.DocNumber))
            errors.Add("customer.docNumber es obligatorio para ese tipo de documento.");

        var invoiceLike = r.Type == FiscalDocumentTypes.Invoice || (isNote && r.Reference?.Type == FiscalDocumentTypes.Invoice);
        if (invoiceLike && customer.DocType is not ("6" or "0" or "4" or "7"))
            errors.Add("Una factura (y sus notas) va a un cliente con RUC (o no domiciliado, carné de extranjería o pasaporte).");
        if (r.Type == FiscalDocumentTypes.Receipt && customer.DocType == "-" && totals.Total >= ReceiptIdentifyFrom && r.Currency == "PEN")
            errors.Add($"Una boleta desde S/ {ReceiptIdentifyFrom:0} debe identificar al cliente (DNI u otro documento).");

        // Forma de pago
        if (r.Payment is { Credit: true } payment)
        {
            if (r.Type != FiscalDocumentTypes.Invoice)
                errors.Add("La venta al crédito con cuotas solo se informa en facturas.");
            foreach (var cuota in payment.Installments ?? [])
            {
                if (cuota.Amount <= 0)
                    errors.Add("Cada cuota debe tener un monto mayor que cero.");
                if (cuota.DueDate <= r.IssueDate)
                    errors.Add("Cada cuota debe vencer después de la fecha de emisión.");
            }
            if (payment.Installments is { Count: > 0 } list && list.Sum(c => c.Amount) > totals.Total)
                errors.Add("Las cuotas no pueden sumar más que el total.");
            if (payment.Installments is not { Count: > 0 } && (r.DueDate ?? r.IssueDate) <= r.IssueDate)
                errors.Add("Al crédito sin cuotas, dueDate debe ser posterior a la emisión (es la única cuota).");
        }

        // Notas
        if (isNote)
        {
            if (r.Reference is not { } reference)
            {
                errors.Add("Las notas deben traer reference (el comprobante que modifican).");
            }
            else
            {
                if (reference.Type is not (FiscalDocumentTypes.Invoice or FiscalDocumentTypes.Receipt))
                    errors.Add("reference.type debe ser invoice o receipt.");
                var reasons = r.Type == FiscalDocumentTypes.CreditNote ? CreditNoteReasons : DebitNoteReasons;
                if (!reasons.Contains(reference.ReasonCode))
                    errors.Add($"reference.reasonCode debe ser uno de: {string.Join(", ", reasons)}.");
                if (!SeriesPattern().IsMatch(reference.Series) || reference.Number < 1)
                    errors.Add("reference.series y reference.number deben identificar el comprobante (F001-123).");
            }
        }
        else if (r.Reference is not null)
        {
            errors.Add("reference solo va en las notas de crédito y de débito.");
        }
        return errors;
    }

    [GeneratedRegex("^[A-Z][A-Z0-9]{3}$")]
    private static partial Regex SeriesPattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex("^[A-Z0-9]{2,3}$")]
    private static partial Regex UnitPattern();
}
