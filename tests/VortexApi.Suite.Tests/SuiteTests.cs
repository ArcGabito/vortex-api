using System.Globalization;
using VortexApi.Suite.Money;
using VortexApi.Suite.Regional;
using VortexApi.Suite.Security;
using VortexApi.Suite.Taxes;
using VortexApi.Suite.Text;

namespace VortexApi.Suite.Tests;

public sealed class TaxTests
{
    [Fact]
    public void Peru_desglosa_el_igv_por_tasa_y_deja_aparte_lo_exonerado()
    {
        var totals = TaxCalculator.Calculate([new(36m, "igv"), new(2m, "exonerado"), new(1m, "inafecto")], TaxCatalog.For("PE"), "PEN");

        Assert.Equal(30.51m + 3m, totals.Net);
        Assert.Equal(5.49m, totals.Tax);
        Assert.Equal(39m, totals.Total);
        Assert.Equal(3m, totals.Untaxed);
        Assert.Equal([TaxCategories.Standard, TaxCategories.Exempt, TaxCategories.OutOfScope], totals.Groups.Select(g => g.Category));
    }

    [Fact]
    public void En_EEUU_el_sales_tax_se_suma_con_la_tasa_que_configuro_la_empresa()
    {
        var regime = TaxCatalog.For("US", companyDefaultRate: 8.875m);   // Nueva York
        Assert.False(regime.PricesIncludeTax);

        var totals = TaxCalculator.Calculate([new(100m, "standard"), new(19.99m, "standard")], regime, "USD");
        Assert.Equal(119.99m, totals.Net);
        Assert.Equal(10.65m, totals.Tax);   // 119.99 × 8.875 % = 10.649…
        Assert.Equal(130.64m, totals.Total);
    }

    [Theory]
    [InlineData("MX", 16)]
    [InlineData("CO", 19)]
    [InlineData("CL", 19)]
    [InlineData("AR", 21)]
    [InlineData("BR", 17)]
    [InlineData("ES", 21)]
    [InlineData("DE", 19)]
    [InlineData("PT", 23)]
    [InlineData("GB", 20)]
    public void Cada_pais_tiene_su_tasa_general(string country, decimal percent) =>
        Assert.Equal(percent / 100m, TaxCatalog.For(country).StandardRate);

    [Fact]
    public void La_tasa_del_hub_manda_y_un_pais_sin_tabla_usa_la_de_la_empresa()
    {
        Assert.Equal(0.20m, TaxCatalog.For("PE", 20m).StandardRate);
        Assert.Equal(0.18m, TaxCatalog.For("PE", 18m).StandardRate);
        Assert.Equal(0.11m, TaxCatalog.For("ZZ", 11m).StandardRate);
        Assert.Equal(0.10m, TaxCatalog.For("ES").RateOf("reduced"));
    }

    [Fact]
    public void Precios_sin_impuesto_en_una_cotizacion_b2b_suman_el_impuesto()
    {
        var totals = TaxCalculator.Calculate([new(1000m, null)], TaxCatalog.For("PE"), "PEN", pricesIncludeTax: false);
        Assert.Equal(new TaxBreakdown(1000m, 180m, 1180m, totals.Groups), totals);
    }

    [Fact]
    public void El_peso_chileno_se_redondea_sin_decimales()
    {
        var totals = TaxCalculator.Calculate([new(9990m, "standard")], TaxCatalog.For("CL"), "CLP");
        Assert.Equal(8395m, totals.Net);   // 9990 / 1.19 = 8394.96
        Assert.Equal(1595m, totals.Tax);
    }

    [Theory]
    [InlineData(null, TaxCategories.Standard)]
    [InlineData("IGV", TaxCategories.Standard)]
    [InlineData("exonerado", TaxCategories.Exempt)]
    [InlineData("inafecto", TaxCategories.OutOfScope)]
    [InlineData("reduced", TaxCategories.Reduced)]
    [InlineData("algo-raro", TaxCategories.Standard)]
    public void Los_codigos_antiguos_del_hub_se_normalizan(string? code, string expected) =>
        Assert.Equal(expected, TaxCategories.Normalize(code));
}

public sealed class MoneyAndRegionalTests
{
    [Fact]
    public void Las_monedas_formatean_con_su_simbolo_y_decimales()
    {
        Assert.Equal("S/ 1,234.50", Currencies.Get("PEN").Format(1234.5m, CultureInfo.GetCultureInfo("es-PE")));
        Assert.Equal("$ 1.235", Currencies.Get("CLP").Format(1234.5m, CultureInfo.GetCultureInfo("es-CL")));
        Assert.Equal("1.234,50 €", Currencies.Get("EUR").Format(1234.5m, CultureInfo.GetCultureInfo("es-ES")));
        Assert.Equal("CLP", Currencies.ForCountry("CL").Code);
        Assert.Equal("USD", Currencies.ForCountry("EC").Code);
        Assert.Equal(2, Currencies.Get("XYZ").Decimals);
    }

    [Theory]
    [InlineData("es", "PE", "es-PE")]
    [InlineData("en", "US", "en-US")]
    [InlineData("pt", "BR", "pt-BR")]
    [InlineData("en", "PE", "en-US")]   // no existe en-PE: inglés de EE. UU.
    [InlineData("en", "GB", "en-GB")]
    [InlineData("pt", "ZZ", "pt-BR")]
    [InlineData("fr", "MX", "es-MX")]
    public void La_cultura_sale_del_idioma_y_el_pais(string language, string country, string expected) =>
        Assert.Equal(expected, RegionalSettings.CultureFor(language, country).Name);

    [Fact]
    public void El_idioma_por_defecto_depende_del_pais_y_la_hora_de_la_zona_de_la_empresa()
    {
        Assert.Equal("pt", Languages.DefaultFor("BR"));
        Assert.Equal("en", Languages.DefaultFor("US"));
        Assert.Equal("es", Languages.DefaultFor("PE"));
        var utc = new DateTimeOffset(2026, 11, 2, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 1), DateOnly.FromDateTime(RegionalSettings.ToCompanyTime(utc, "America/Lima").DateTime));
        Assert.Equal(TimeSpan.FromHours(-5), RegionalSettings.ToCompanyTime(utc, "America/Lima").Offset);
        Assert.Equal(utc, RegionalSettings.ToCompanyTime(utc, "No/Existe"));
    }
}

public sealed class PermissionAndTextTests
{
    private static readonly PermissionMatrix Pos = PermissionMatrix.Create("cajero",
        ("cajero", ["sale.create"]),
        ("supervisor", ["sale.create", "sale.void"]));

    [Fact]
    public void Cada_rol_puede_lo_suyo_y_los_administradores_todo()
    {
        Assert.True(Pos.Can("cajero", "User", "sale.create"));
        Assert.False(Pos.Can("cajero", "User", "sale.void"));
        Assert.True(Pos.Can("Supervisor", "User", "sale.void"));
        Assert.True(Pos.Can("administrador", "User", "cash.close"));
        Assert.True(Pos.Can(null, "OrgAdmin", "sale.void"));
        Assert.False(Pos.Can(null, "User", "sale.void"));               // sin rol: el rol por defecto
        Assert.Equal("cajero", Pos.EffectiveRole("rol-que-no-existe", "User"));
    }

    [Fact]
    public void Los_textos_caen_al_espanol_y_luego_a_la_clave()
    {
        var texts = new TextCatalog().Add("es", new Dictionary<string, string> { ["pos.sell"] = "Vender {0}" });
        Assert.Equal("Save", texts.Get("en", "common.save"));
        Assert.Equal("Salvar", texts.Get("pt-BR", "common.save"));
        Assert.Equal("Vender 2", texts.Get("en", "pos.sell", 2));
        Assert.Equal("no.existe", texts.Get("en", "no.existe"));
        Assert.Equal(["pos.sell"], texts.Missing("en"));
    }

    [Fact]
    public void Los_textos_comunes_estan_en_los_tres_idiomas()
    {
        var texts = new TextCatalog();
        Assert.Empty(texts.Missing("en"));
        Assert.Empty(texts.Missing("pt"));
    }
}

public sealed class XlsxTests
{
    [Fact]
    public void El_libro_trae_titulo_encabezados_filas_tipadas_y_totales()
    {
        var sheet = new VortexApi.Suite.Export.XlsxSheet("Kardex: nov/2026",
            [new("SKU"), new("Cantidad", VortexApi.Suite.Export.XlsxColumnType.Number), new("Valor", VortexApi.Suite.Export.XlsxColumnType.Money),
             new("Exactitud", VortexApi.Suite.Export.XlsxColumnType.Percent), new("Fecha", VortexApi.Suite.Export.XlsxColumnType.Date)],
            [["CAFE-250 <&>", 60m, 360.5m, 50m, new DateOnly(2026, 11, 2)], ["=SUMA(A1)", null, 1, null, null]],
            Totals: ["Total", 60m, 361.5m, null, null], Title: "Vortex SAC", Subtitle: "Central · 2026-11");
        var bytes = VortexApi.Suite.Export.XlsxWriter.Write(sheet, sheet with { Name = "Kardex: nov/2026" });
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(bytes, 0, 2));

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        var workbook = System.Xml.Linq.XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
        Assert.Equal(["Kardex nov2026", "Kardex nov2026 (2)"],
            workbook.Descendants().Where(e => e.Name.LocalName == "sheet").Select(e => e.Attribute("name")!.Value));
        var xml = System.Xml.Linq.XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var cells = xml.Descendants().Where(e => e.Name.LocalName == "c").ToDictionary(e => e.Attribute("r")!.Value);
        Assert.Equal("CAFE-250 <&>", cells["A5"].Value);                       // título, subtítulo, blanco, encabezado en 4
        Assert.Equal(("60", "360.5"), (cells["B5"].Value, cells["C5"].Value));
        Assert.Equal("46328", cells["E5"].Value);                              // 2 de noviembre de 2026 como serie de Excel
        Assert.Equal("=SUMA(A1)", cells["A6"].Value);                          // texto, nunca fórmula
        Assert.Equal("inlineStr", cells["A6"].Attribute("t")!.Value);
        Assert.Equal("361.5", cells["C7"].Value);
        Assert.Equal("AA1", VortexApi.Suite.Export.XlsxWriter.Ref(26, 1));
    }
}
