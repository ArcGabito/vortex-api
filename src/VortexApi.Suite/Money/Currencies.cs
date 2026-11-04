using System.Globalization;

namespace VortexApi.Suite.Money;

/// <summary>Una moneda ISO 4217: cuántos decimales usa y cómo se escribe.</summary>
/// <param name="Symbol">Símbolo local (S/, $, €, R$…).</param>
/// <param name="Decimals">Decimales con que se cobra y se factura (0 en pesos chilenos, guaraníes o yenes).</param>
public sealed record Currency(string Code, string Name, string Symbol, int Decimals)
{
    public decimal Round(decimal amount) => Math.Round(amount, Decimals, MidpointRounding.AwayFromZero);

    /// <summary>
    /// <c>S/ 1,234.50</c>, <c>$ 1.234</c> (CLP), <c>1.234,50 €</c>: el número con la cultura indicada (o la actual) y el
    /// símbolo delante, salvo en las culturas que lo ponen detrás.
    /// </summary>
    public string Format(decimal amount, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var number = Round(amount).ToString("N" + Decimals, culture);
        // CurrencyPositivePattern 1 y 3 ponen el símbolo detrás (es-ES, de-DE, fr-FR, pt-PT…).
        return culture.NumberFormat.CurrencyPositivePattern is 1 or 3 ? $"{number} {Symbol}" : $"{Symbol} {number}";
    }
}

/// <summary>Monedas de los mercados de la suite (Perú, LATAM, EE. UU., Europa) más las de comercio internacional.</summary>
public static class Currencies
{
    private static readonly Dictionary<string, Currency> All = new Currency[]
    {
        new("PEN", "Sol peruano", "S/", 2),
        new("USD", "Dólar estadounidense", "US$", 2),
        new("EUR", "Euro", "€", 2),
        new("MXN", "Peso mexicano", "$", 2),
        new("COP", "Peso colombiano", "$", 2),
        new("CLP", "Peso chileno", "$", 0),
        new("ARS", "Peso argentino", "$", 2),
        new("BOB", "Boliviano", "Bs", 2),
        new("UYU", "Peso uruguayo", "$U", 2),
        new("PYG", "Guaraní", "₲", 0),
        new("VES", "Bolívar", "Bs.", 2),
        new("CRC", "Colón costarricense", "₡", 2),
        new("PAB", "Balboa", "B/.", 2),
        new("GTQ", "Quetzal", "Q", 2),
        new("HNL", "Lempira", "L", 2),
        new("NIO", "Córdoba", "C$", 2),
        new("DOP", "Peso dominicano", "RD$", 2),
        new("BRL", "Real", "R$", 2),
        new("GBP", "Libra esterlina", "£", 2),
        new("CHF", "Franco suizo", "CHF", 2),
        new("PLN", "Esloti", "zł", 2),
        new("SEK", "Corona sueca", "kr", 2),
        new("DKK", "Corona danesa", "kr", 2),
        new("CZK", "Corona checa", "Kč", 2),
        new("CAD", "Dólar canadiense", "CA$", 2),
        new("CNY", "Yuan", "¥", 2),
        new("JPY", "Yen", "¥", 0),
    }.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Moneda de cada país (la que propone el hub al crear la empresa y los sistemas usan por defecto).</summary>
    private static readonly Dictionary<string, string> ByCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PE"] = "PEN", ["MX"] = "MXN", ["CO"] = "COP", ["CL"] = "CLP", ["AR"] = "ARS", ["EC"] = "USD", ["BO"] = "BOB",
        ["UY"] = "UYU", ["PY"] = "PYG", ["VE"] = "VES", ["CR"] = "CRC", ["PA"] = "USD", ["GT"] = "GTQ", ["SV"] = "USD",
        ["HN"] = "HNL", ["NI"] = "NIO", ["DO"] = "DOP", ["BR"] = "BRL", ["US"] = "USD", ["GB"] = "GBP", ["CH"] = "CHF",
        ["PL"] = "PLN", ["SE"] = "SEK", ["DK"] = "DKK", ["CZ"] = "CZK", ["CA"] = "CAD",
        ["ES"] = "EUR", ["PT"] = "EUR", ["FR"] = "EUR", ["DE"] = "EUR", ["IT"] = "EUR", ["NL"] = "EUR", ["BE"] = "EUR",
        ["IE"] = "EUR", ["AT"] = "EUR", ["GR"] = "EUR", ["LU"] = "EUR", ["FI"] = "EUR",
    };

    public static IReadOnlyCollection<Currency> List => All.Values;

    /// <summary>La moneda del código; uno desconocido se trata con 2 decimales y el código como símbolo.</summary>
    public static Currency Get(string? code)
    {
        var key = (code ?? "").Trim().ToUpperInvariant();
        return All.GetValueOrDefault(key) ?? new Currency(key.Length == 0 ? "XXX" : key, key, key, 2);
    }

    public static bool IsKnown(string? code) => code is not null && All.ContainsKey(code.Trim());

    public static Currency ForCountry(string? countryCode) => Get(ByCountry.GetValueOrDefault(countryCode ?? "", "USD"));
}
