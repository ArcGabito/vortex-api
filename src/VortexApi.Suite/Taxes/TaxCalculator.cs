using VortexApi.Suite.Money;

namespace VortexApi.Suite.Taxes;

/// <summary>Una línea a gravar: su importe (con o sin impuesto, según se calcule) y la categoría del artículo.</summary>
public readonly record struct TaxableLine(decimal Amount, string? TaxCode);

/// <summary>Base e impuesto de una tasa.</summary>
public sealed record TaxGroup(string Category, decimal Rate, decimal Base, decimal Tax);

/// <summary>Totales de un documento con el impuesto desglosado por tasa (como lo exigen las facturas en todos los países).</summary>
public sealed record TaxBreakdown(decimal Net, decimal Tax, decimal Total, IReadOnlyList<TaxGroup> Groups)
{
    /// <summary>Base de lo que no paga impuesto (tasa cero, exento, inafecto).</summary>
    public decimal Untaxed => Groups.Where(g => g.Rate == 0).Sum(g => g.Base);

    public static readonly TaxBreakdown Empty = new(0, 0, 0, []);
}

/// <summary>
/// Calcula el impuesto de un documento. Agrupa las líneas por tasa y redondea una vez por tasa a los decimales de la
/// moneda (así lo hacen la factura electrónica de Perú, el CFDI y el IVA europeo), lo que evita los céntimos de
/// diferencia de redondear línea por línea.
/// </summary>
public static class TaxCalculator
{
    /// <param name="pricesIncludeTax">
    /// Si los importes de las líneas ya traen el impuesto (se desglosa) o no (se suma). Normalmente
    /// <see cref="TaxRegime.PricesIncludeTax"/>, pero una cotización B2B puede ir "más IGV".
    /// </param>
    public static TaxBreakdown Calculate(IEnumerable<TaxableLine> lines, TaxRegime regime, string currencyCode, bool? pricesIncludeTax = null)
    {
        var inclusive = pricesIncludeTax ?? regime.PricesIncludeTax;
        var currency = Currencies.Get(currencyCode);
        var groups = new List<TaxGroup>();
        foreach (var group in lines.GroupBy(l => TaxCategories.Normalize(l.TaxCode)).OrderBy(g => Array.IndexOf(TaxCategories.All, g.Key)))
        {
            var rate = regime.RateOf(group.Key);
            var amount = currency.Round(group.Sum(l => l.Amount));
            decimal baseAmount, tax;
            if (inclusive)
            {
                baseAmount = currency.Round(amount / (1 + rate));
                tax = amount - baseAmount;
            }
            else
            {
                baseAmount = amount;
                tax = currency.Round(amount * rate);
            }
            groups.Add(new TaxGroup(group.Key, rate, baseAmount, tax));
        }

        var net = groups.Sum(g => g.Base);
        var totalTax = groups.Sum(g => g.Tax);
        return new TaxBreakdown(net, totalTax, net + totalTax, groups);
    }

    /// <summary>Precio con impuesto a partir del precio sin impuesto (y al revés), redondeado a la moneda.</summary>
    public static decimal AddTax(decimal netPrice, decimal rate, string currencyCode) => Currencies.Get(currencyCode).Round(netPrice * (1 + rate));

    public static decimal RemoveTax(decimal grossPrice, decimal rate, string currencyCode) => Currencies.Get(currencyCode).Round(grossPrice / (1 + rate));
}
