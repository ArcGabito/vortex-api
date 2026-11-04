namespace VortexApi.Suite.Taxes;

/// <summary>
/// Categorías de impuesto de un artículo (<c>item.taxCode</c> en el hub). Son las mismas en todos los países; cada país
/// les da su tasa (<see cref="TaxRegime"/>). Así un artículo no cambia si la empresa opera en otro país.
/// </summary>
public static class TaxCategories
{
    /// <summary>Tasa general (IGV 18 % en Perú, IVA 16 % en México, VAT 20 % en el Reino Unido…).</summary>
    public const string Standard = "standard";

    /// <summary>Tasa reducida (alimentos, libros… según el país).</summary>
    public const string Reduced = "reduced";

    /// <summary>Segunda tasa reducida (UE: Francia 5,5 %, España 4 %…).</summary>
    public const string SuperReduced = "super_reduced";

    /// <summary>Tasa incrementada (Argentina 27 % en servicios públicos a empresas).</summary>
    public const string Increased = "increased";

    /// <summary>Gravado a tasa cero (exportaciones, ciertos alimentos): da derecho a crédito fiscal.</summary>
    public const string Zero = "zero";

    /// <summary>Exonerado o exento.</summary>
    public const string Exempt = "exempt";

    /// <summary>Inafecto o no sujeto (fuera del ámbito del impuesto).</summary>
    public const string OutOfScope = "out_of_scope";

    public static readonly string[] All = [Standard, Reduced, SuperReduced, Increased, Zero, Exempt, OutOfScope];

    /// <summary>
    /// Lleva un <c>taxCode</c> del hub a su categoría. Acepta los nombres usados antes de la base común (<c>igv</c>,
    /// <c>iva</c>, <c>vat</c>, <c>exonerado</c>, <c>inafecto</c>…) para no romper artículos ya cargados. Un artículo sin
    /// código está gravado a la tasa general.
    /// </summary>
    public static string Normalize(string? taxCode) => taxCode?.Trim().ToLowerInvariant() switch
    {
        null or "" or "igv" or "iva" or "vat" or "gst" or "itbms" or "itbis" or "sales_tax" or "general" => Standard,
        "reducido" or "reducida" => Reduced,
        "superreducido" or "super_reducido" => SuperReduced,
        "cero" or "0" or "exportacion" or "export" => Zero,
        "exonerado" or "exento" or "exenta" or "exempt" => Exempt,
        "inafecto" or "no_sujeto" or "no_gravado" => OutOfScope,
        var code when All.Contains(code) => code,
        _ => Standard,
    };
}

/// <summary>
/// Cómo funciona el impuesto al consumo en un país: su nombre, sus tasas por categoría y si los precios al público lo
/// incluyen (Perú, LATAM y la UE) o se suma al cobrar (EE. UU.).
/// </summary>
/// <param name="Country">Código ISO 3166-1 alfa-2.</param>
/// <param name="TaxName">Como aparece en los documentos: IGV, IVA, VAT, Sales tax…</param>
/// <param name="Rates">Tasa por categoría, en fracción (0.18 = 18 %). Las categorías sin tasa pagan 0.</param>
/// <param name="PricesIncludeTax">Si el precio de lista ya trae el impuesto.</param>
/// <param name="Reviewed">Fecha en que se verificaron las tasas; se revisan con cada cambio de ley.</param>
public sealed record TaxRegime(string Country, string TaxName, IReadOnlyDictionary<string, decimal> Rates, bool PricesIncludeTax,
    DateOnly Reviewed)
{
    public decimal StandardRate => RateOf(TaxCategories.Standard);

    public decimal RateOf(string? taxCode) => Rates.GetValueOrDefault(TaxCategories.Normalize(taxCode));

    /// <summary>Si la categoría paga impuesto (las de tasa cero, exentas e inafectas no).</summary>
    public bool IsTaxed(string? taxCode) => RateOf(taxCode) > 0;

    /// <summary>Copia con otra tasa general (la que configuró la empresa en el hub, <c>company.defaultTaxRate</c>).</summary>
    public TaxRegime WithStandardRate(decimal rate) =>
        this with { Rates = new Dictionary<string, decimal>(Rates) { [TaxCategories.Standard] = rate } };
}

/// <summary>
/// Regímenes de impuesto al consumo por país (Perú, el resto de LATAM, EE. UU., el Reino Unido y la UE). Las tasas son las
/// generales vigentes a la fecha de <see cref="TaxRegime.Reviewed"/>; la empresa puede corregir la general en el hub
/// (<c>company.defaultTaxRate</c>) y esa manda. Lo propio de cada país (detracciones, percepciones, impuestos locales)
/// va en los paquetes de localización, no aquí.
/// </summary>
public static class TaxCatalog
{
    private static readonly DateOnly Reviewed = new(2026, 11, 1);

    private static readonly Dictionary<string, TaxRegime> Regimes = Build(
        // ---------- Perú y LATAM: precios al público con impuesto ----------
        ("PE", "IGV", true, [(TaxCategories.Standard, 18m)]),   // 16 % IGV + 2 % IPM
        ("MX", "IVA", true, [(TaxCategories.Standard, 16m), (TaxCategories.Reduced, 8m)]),   // 8 %: región fronteriza
        ("CO", "IVA", true, [(TaxCategories.Standard, 19m), (TaxCategories.Reduced, 5m)]),
        ("CL", "IVA", true, [(TaxCategories.Standard, 19m)]),
        ("AR", "IVA", true, [(TaxCategories.Standard, 21m), (TaxCategories.Reduced, 10.5m), (TaxCategories.Increased, 27m)]),
        ("EC", "IVA", true, [(TaxCategories.Standard, 15m), (TaxCategories.Reduced, 5m)]),
        ("BO", "IVA", true, [(TaxCategories.Standard, 13m)]),
        ("UY", "IVA", true, [(TaxCategories.Standard, 22m), (TaxCategories.Reduced, 10m)]),
        ("PY", "IVA", true, [(TaxCategories.Standard, 10m), (TaxCategories.Reduced, 5m)]),
        ("VE", "IVA", true, [(TaxCategories.Standard, 16m), (TaxCategories.Reduced, 8m)]),
        ("CR", "IVA", true, [(TaxCategories.Standard, 13m), (TaxCategories.Reduced, 4m), (TaxCategories.SuperReduced, 2m)]),
        ("PA", "ITBMS", true, [(TaxCategories.Standard, 7m)]),   // 10 % y 15 % (alcohol, tabaco) van en el paquete local
        ("GT", "IVA", true, [(TaxCategories.Standard, 12m)]),
        ("SV", "IVA", true, [(TaxCategories.Standard, 13m)]),
        ("HN", "ISV", true, [(TaxCategories.Standard, 15m), (TaxCategories.Increased, 18m)]),
        ("NI", "IVA", true, [(TaxCategories.Standard, 15m)]),
        ("DO", "ITBIS", true, [(TaxCategories.Standard, 18m), (TaxCategories.Reduced, 16m)]),
        // Brasil: ICMS, PIS/COFINS e IPI dependen del estado y del producto. Se usa una tasa general configurable
        // (company.defaultTaxRate) hasta el paquete de localización.
        ("BR", "Impostos", true, [(TaxCategories.Standard, 17m)]),

        // ---------- EE. UU.: sales tax sumado al cobrar; la tasa depende del estado y la ciudad ----------
        ("US", "Sales tax", false, [(TaxCategories.Standard, 0m)]),

        // ---------- Europa: IVA incluido en el precio al consumidor ----------
        ("GB", "VAT", true, [(TaxCategories.Standard, 20m), (TaxCategories.Reduced, 5m)]),
        ("ES", "IVA", true, [(TaxCategories.Standard, 21m), (TaxCategories.Reduced, 10m), (TaxCategories.SuperReduced, 4m)]),
        ("PT", "IVA", true, [(TaxCategories.Standard, 23m), (TaxCategories.Reduced, 13m), (TaxCategories.SuperReduced, 6m)]),
        ("FR", "TVA", true, [(TaxCategories.Standard, 20m), (TaxCategories.Reduced, 10m), (TaxCategories.SuperReduced, 5.5m)]),
        ("DE", "MwSt.", true, [(TaxCategories.Standard, 19m), (TaxCategories.Reduced, 7m)]),
        ("IT", "IVA", true, [(TaxCategories.Standard, 22m), (TaxCategories.Reduced, 10m), (TaxCategories.SuperReduced, 4m)]),
        ("NL", "btw", true, [(TaxCategories.Standard, 21m), (TaxCategories.Reduced, 9m)]),
        ("BE", "TVA/btw", true, [(TaxCategories.Standard, 21m), (TaxCategories.Reduced, 12m), (TaxCategories.SuperReduced, 6m)]),
        ("IE", "VAT", true, [(TaxCategories.Standard, 23m), (TaxCategories.Reduced, 13.5m), (TaxCategories.SuperReduced, 9m)]),
        ("AT", "USt.", true, [(TaxCategories.Standard, 20m), (TaxCategories.Reduced, 13m), (TaxCategories.SuperReduced, 10m)]),
        ("PL", "VAT", true, [(TaxCategories.Standard, 23m), (TaxCategories.Reduced, 8m), (TaxCategories.SuperReduced, 5m)]),
        ("SE", "moms", true, [(TaxCategories.Standard, 25m), (TaxCategories.Reduced, 12m), (TaxCategories.SuperReduced, 6m)]),
        ("DK", "moms", true, [(TaxCategories.Standard, 25m)]),
        ("GR", "ΦΠΑ", true, [(TaxCategories.Standard, 24m), (TaxCategories.Reduced, 13m), (TaxCategories.SuperReduced, 6m)]),
        ("LU", "TVA", true, [(TaxCategories.Standard, 17m), (TaxCategories.Reduced, 8m), (TaxCategories.SuperReduced, 3m)]),
        ("CZ", "DPH", true, [(TaxCategories.Standard, 21m), (TaxCategories.Reduced, 12m)]));

    /// <summary>Países con régimen conocido.</summary>
    public static IReadOnlyCollection<string> Countries => Regimes.Keys;

    /// <summary>
    /// Régimen del país. Si la empresa configuró en el hub una tasa general distinta (<paramref name="companyDefaultRate"/>,
    /// en porcentaje como la guarda el hub: 18 = 18 %), esa manda. Para un país sin tabla se usa un régimen genérico con
    /// esa tasa, IVA incluido.
    /// </summary>
    public static TaxRegime For(string? countryCode, decimal? companyDefaultRate = null)
    {
        var country = (countryCode ?? "").Trim().ToUpperInvariant();
        var regime = Regimes.GetValueOrDefault(country)
            ?? new TaxRegime(country, "Tax", new Dictionary<string, decimal> { [TaxCategories.Standard] = 0m }, true, Reviewed);
        return companyDefaultRate is { } percent && percent >= 0 && percent / 100m != regime.StandardRate
            ? regime.WithStandardRate(percent / 100m)
            : regime;
    }

    private static Dictionary<string, TaxRegime> Build(params (string Country, string Name, bool Inclusive, (string Category, decimal Percent)[] Rates)[] rows) =>
        rows.ToDictionary(r => r.Country, r => new TaxRegime(r.Country, r.Name,
            r.Rates.ToDictionary(x => x.Category, x => x.Percent / 100m), r.Inclusive, Reviewed), StringComparer.OrdinalIgnoreCase);
}
