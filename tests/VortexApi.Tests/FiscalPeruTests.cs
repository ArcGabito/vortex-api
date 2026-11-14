using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using VortexApi.Fiscal;
using VortexApi.Fiscal.Peru;

namespace VortexApi.Tests;

/// <summary>Piezas de Perú sin base de datos: RUC, montos, XML UBL, firma y respuestas de SUNAT.</summary>
public sealed class FiscalPeruTests
{
    public const string IssuerRuc = "20123456786";
    public const string CustomerRuc = "20111222330";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    public static X509Certificate2 SelfSigned(string subject = "CN=EMPRESA DE PRUEBA SAC, SERIALNUMBER=20123456786")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public static FiscalDocumentRequest Invoice(long number = 1, Guid? sourceId = null, string series = "F001") => new(
        sourceId ?? Guid.NewGuid(), FiscalDocumentTypes.Invoice, series, number, new DateOnly(2026, 11, 11), "PEN",
        new FiscalParty("6", CustomerRuc, "Bodega El Sur SAC", "Av. Grau 456, Lima"),
        [
            new FiscalLine("Café molido 250 g", 10, "NIU", 18.5m, FiscalTaxCategories.Taxed, 0.18m, "CAF-250"),
            new FiscalLine("Libro de recetas", 1, "NIU", 50m, FiscalTaxCategories.Exempt),
        ],
        new TimeOnly(10, 30));

    private static PeruIssuer Issuer => new(IssuerRuc, "EMPRESA DE PRUEBA SAC", "Café Andino", "150101", "Av. Lima 123", "LIMA", "LIMA", "LIMA");

    [Theory]
    [InlineData("20123456786", true)]
    [InlineData("20111222330", true)]
    [InlineData("20123456787", false)]
    [InlineData("30123456786", false)]
    [InlineData("2012345678", false)]
    [InlineData(null, false)]
    public void El_RUC_se_valida_con_su_digito_verificador(string? ruc, bool valid) => Assert.Equal(valid, PeruRules.IsValidRuc(ruc));

    [Fact]
    public void Los_montos_se_calculan_por_linea_con_el_IGV_redondeado()
    {
        var (lines, totals) = PeruUbl.Compute(Invoice().Lines);
        Assert.Equal((185m, 33.30m, 21.83m, "10"), (lines[0].LineExtension, lines[0].Tax, lines[0].UnitPrice, lines[0].AffectationCode));
        Assert.Equal((50m, 0m, "20"), (lines[1].LineExtension, lines[1].Tax, lines[1].AffectationCode));
        Assert.Equal(new PeruTotals(185m, 50m, 0, 0, 33.30m, 235m, 268.30m), totals);
    }

    [Fact]
    public void Con_el_total_cobrado_el_comprobante_cuadra_al_centimo_con_la_caja()
    {
        // Precios con IGV de la caja: 3 x 3.50 y 1 x 10.00 = 20.50 cobrados.
        var (lines, totals) = PeruUbl.Compute([
            new FiscalLine("Pan", 3, "NIU", 0, FiscalTaxCategories.Taxed, 0.18m, Total: 10.50m),
            new FiscalLine("Leche", 1, "NIU", 0, FiscalTaxCategories.Taxed, 0.18m, Total: 10.00m),
            new FiscalLine("Libro", 1, "NIU", 0, FiscalTaxCategories.Exempt, Total: 25m),
        ]);
        Assert.Equal((8.90m, 1.60m, 3.5m), (lines[0].LineExtension, lines[0].Tax, lines[0].UnitPrice));
        Assert.Equal((8.47m, 1.53m), (lines[1].LineExtension, lines[1].Tax));
        Assert.Equal(2.9666666667m, lines[0].Source.UnitValue);
        Assert.Equal((17.37m, 25m, 3.13m, 45.50m), (totals.Taxed, totals.Exempt, totals.Tax, totals.Total));
    }

    [Theory]
    [InlineData(268.30, "PEN", "DOSCIENTOS SESENTA Y OCHO CON 30/100 SOLES")]
    [InlineData(100, "PEN", "CIEN CON 00/100 SOLES")]
    [InlineData(0.5, "USD", "CERO CON 50/100 DÓLARES AMERICANOS")]
    [InlineData(1001, "PEN", "MIL UNO CON 00/100 SOLES")]
    [InlineData(21500, "PEN", "VEINTIÚN MIL QUINIENTOS CON 00/100 SOLES")]
    [InlineData(101000, "PEN", "CIENTO UN MIL CON 00/100 SOLES")]
    [InlineData(2_031_000.99, "EUR", "DOS MILLONES TREINTA Y UN MIL CON 99/100 EUROS")]
    public void El_monto_va_en_letras(decimal amount, string currency, string expected) =>
        Assert.Equal(expected, AmountInWords.Spanish(amount, currency));

    [Fact]
    public void La_factura_UBL_se_firma_y_la_firma_se_rompe_si_cambia_el_XML()
    {
        using var certificate = SelfSigned();
        var xml = PeruUbl.Build(Invoice(), Issuer);
        var (signed, digest) = XmlSigner.Sign(xml, certificate);

        Assert.True(XmlSigner.Verify(signed, certificate));
        Assert.Matches("^[A-Za-z0-9+/]{27}=$", digest);
        var doc = XDocument.Parse(signed);
        Assert.Equal("F001-1", doc.Root!.Element(Cbc + "ID")!.Value);
        Assert.Equal("01", doc.Root.Element(Cbc + "InvoiceTypeCode")!.Value);
        Assert.Equal("DOSCIENTOS SESENTA Y OCHO CON 30/100 SOLES", doc.Root.Element(Cbc + "Note")!.Value);
        Assert.Contains(doc.Descendants(Cbc + "PaymentMeansID"), e => e.Value == "Contado");
        Assert.Equal(["33.30", "33.30", "0.00"], doc.Descendants(Cbc + "TaxAmount").Select(e => e.Value).Take(3));
        Assert.Equal("268.30", doc.Descendants(Cbc + "PayableAmount").Single().Value);

        Assert.False(XmlSigner.Verify(signed.Replace("268.30", "26.83"), certificate));
        using var other = SelfSigned("CN=OTRO");
        Assert.False(XmlSigner.Verify(signed, other));
    }

    [Fact]
    public void La_nota_de_credito_referencia_su_factura_y_el_credito_lleva_cuotas()
    {
        var note = Invoice() with
        {
            Type = FiscalDocumentTypes.CreditNote, Series = "F001", Number = 7,
            Reference = new FiscalReference(FiscalDocumentTypes.Invoice, "F001", 1, "07", "Devolución de 2 unidades"),
        };
        var doc = XDocument.Parse(PeruUbl.Build(note, Issuer));
        Assert.Equal("CreditNote", doc.Root!.Name.LocalName);
        Assert.Equal("F001-1", doc.Descendants(Cbc + "ReferenceID").Single().Value);
        Assert.Equal("07", doc.Descendants(Cbc + "ResponseCode").Single().Value);
        Assert.Empty(doc.Descendants(Cbc + "InvoiceTypeCode"));

        var credit = Invoice() with
        {
            DueDate = new DateOnly(2026, 12, 11),
            Payment = new FiscalPayment(true, [new FiscalInstallment(134.15m, new DateOnly(2026, 11, 26)), new FiscalInstallment(134.15m, new DateOnly(2026, 12, 11))]),
        };
        Assert.Empty(PeruRules.Validate(credit));
        var ids = XDocument.Parse(PeruUbl.Build(credit, Issuer)).Descendants(Cbc + "PaymentMeansID").Select(e => e.Value);
        Assert.Equal(["Credito", "Cuota001", "Cuota002"], ids);
    }

    [Fact]
    public void Las_reglas_de_SUNAT_se_revisan_antes_de_encolar()
    {
        Assert.Empty(PeruRules.Validate(Invoice()));
        Assert.Contains(PeruRules.Validate(Invoice() with { Series = "B001" }), e => e.Contains("letra F"));
        Assert.Contains(PeruRules.Validate(Invoice() with { Customer = new FiscalParty("1", "12345678", "Ana") }), e => e.Contains("RUC"));
        Assert.Contains(PeruRules.Validate(Invoice() with { Customer = new FiscalParty("6", "20123456787", "X") }), e => e.Contains("RUC válido"));

        var receipt = Invoice() with { Type = FiscalDocumentTypes.Receipt, Series = "B001", Customer = new FiscalParty("-", null, "Clientes varios") };
        Assert.Empty(PeruRules.Validate(receipt));
        var big = receipt with { Lines = [new FiscalLine("TV", 1, "NIU", 1000, FiscalTaxCategories.Taxed, 0.18m)] };
        Assert.Contains(PeruRules.Validate(big), e => e.Contains("identificar al cliente"));

        var note = Invoice() with { Type = FiscalDocumentTypes.DebitNote };
        Assert.Contains(PeruRules.Validate(note), e => e.Contains("reference"));
        var badReason = note with { Reference = new FiscalReference(FiscalDocumentTypes.Invoice, "F001", 1, "07", "x") };
        Assert.Contains(PeruRules.Validate(badReason), e => e.Contains("reasonCode"));
        Assert.Contains(PeruRules.Validate(Invoice() with { Lines = [new FiscalLine("X", 0, "NIU", 1, FiscalTaxCategories.Taxed, 18)] }),
            e => e.Contains("quantity"));
    }

    [Fact]
    public void Las_respuestas_de_SUNAT_se_interpretan_con_su_codigo()
    {
        static string Fault(string code, string text) =>
            $"""<soap-env:Envelope xmlns:soap-env="http://schemas.xmlsoap.org/soap/envelope/"><soap-env:Body><soap-env:Fault><faultcode>{code}</faultcode><faultstring>{text}</faultstring></soap-env:Fault></soap-env:Body></soap-env:Envelope>""";

        var rejected = SunatClient.ParseResponse(Fault("soap-env:Client.2335", "El documento electrónico ingresado ha sido alterado"));
        Assert.Equal((SendOutcome.Rejected, "2335"), (rejected.Outcome, rejected.Code));
        Assert.Equal(SendOutcome.Error, SunatClient.ParseResponse(Fault("soap-env:Client.0102", "Usuario o contraseña incorrectos")).Outcome);
        Assert.Equal(SendOutcome.Transient, SunatClient.ParseResponse(Fault("soap-env:Server", "0109 El sistema no puede responder")).Outcome);
        Assert.Equal(SendOutcome.Transient, SunatClient.ParseResponse("<html>502</html>").Outcome);

        const string cdr = """
            <ar:ApplicationResponse xmlns:ar="urn:oasis:names:specification:ubl:schema:xsd:ApplicationResponse-2" xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2" xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
              <cbc:Note>4252 - El dato ingresado como atributo @listName es incorrecto.</cbc:Note>
              <cac:DocumentResponse><cac:Response><cbc:ResponseCode>0</cbc:ResponseCode><cbc:Description>La Factura numero F001-1, ha sido aceptada</cbc:Description></cac:Response></cac:DocumentResponse>
            </ar:ApplicationResponse>
            """;
        var zip = Convert.ToBase64String(SunatClient.Zip($"R-{IssuerRuc}-01-F001-1.xml", cdr));
        var ok = SunatClient.ParseResponse(
            $"""<S:Envelope xmlns:S="http://schemas.xmlsoap.org/soap/envelope/"><S:Body><br:sendBillResponse xmlns:br="http://service.sunat.gob.pe"><applicationResponse>{zip}</applicationResponse></br:sendBillResponse></S:Body></S:Envelope>""");
        Assert.Equal((SendOutcome.Responded, "0"), (ok.Outcome, ok.Code));
        Assert.Single(ok.Notes);

        Assert.Equal(FiscalStatus.Observed, FiscalService.StatusOf(ok, false));
        Assert.Equal(FiscalStatus.Accepted, FiscalService.StatusOf(ok with { Notes = [] }, false));
        Assert.Equal(FiscalStatus.Rejected, FiscalService.StatusOf(ok with { Code = "2017" }, false));
        Assert.Equal(FiscalStatus.Rejected, FiscalService.StatusOf(rejected, false));
        Assert.Equal(FiscalStatus.Pending, FiscalService.StatusOf(SendResult.Transient("x"), false));
        Assert.Equal(FiscalStatus.Failed, FiscalService.StatusOf(SendResult.Transient("x"), true));
    }

    [Fact]
    public void Los_resumenes_de_anulacion_se_arman_y_se_firman()
    {
        using var certificate = SelfSigned();
        Assert.Equal("RA", PeruSummaries.KindFor(FiscalDocumentTypes.Invoice, "F001"));
        Assert.Equal("RA", PeruSummaries.KindFor(FiscalDocumentTypes.CreditNote, "F001"));
        Assert.Equal("RC", PeruSummaries.KindFor(FiscalDocumentTypes.Receipt, "B001"));
        Assert.Equal("RC", PeruSummaries.KindFor(FiscalDocumentTypes.CreditNote, "B001"));

        var item = new SummaryItem(FiscalDocumentTypes.Invoice, "F001", 12, "PEN", 268.30m, "6", CustomerRuc, null, null);
        var ra = PeruSummaries.Voided("RA-20261114-1", new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 14), Issuer, [(item, "Error en el RUC")]);
        var (signed, _) = XmlSigner.Sign(ra, certificate);
        Assert.True(XmlSigner.Verify(signed, certificate));
        var doc = XDocument.Parse(signed);
        Assert.Equal("VoidedDocuments", doc.Root!.Name.LocalName);
        Assert.Equal(("RA-20261114-1", "2026-11-13"), (doc.Root.Element(Cbc + "ID")!.Value, doc.Root.Element(Cbc + "ReferenceDate")!.Value));

        var note = new SummaryItem(FiscalDocumentTypes.CreditNote, "B001", 2, "PEN", 11.80m, "1", "45678912",
            new FiscalReference(FiscalDocumentTypes.Receipt, "B001", 5, "01", "Anulación"), new PeruTotals(10m, 0, 0, 0, 1.80m, 10m, 11.80m));
        var rc = PeruSummaries.VoidSummary("RC-20261114-1", new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 14), Issuer, [note]);
        Assert.Contains("<cbc:DocumentTypeCode>07</cbc:DocumentTypeCode>", rc);
        Assert.Contains("<cbc:ID>B001-5</cbc:ID>", rc);
        Assert.Contains("<cbc:InstructionID>01</cbc:InstructionID>", rc);
        Assert.Contains("<cbc:ConditionCode>3</cbc:ConditionCode>", rc);
    }

    [Fact]
    public void Las_respuestas_de_ticket_y_de_consulta_se_interpretan()
    {
        Assert.Equal("202611140001", SunatClient.ParseSummary("""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>
            <br:sendSummaryResponse xmlns:br="http://service.sunat.gob.pe"><ticket>202611140001</ticket></br:sendSummaryResponse>
            </soap:Body></soap:Envelope>
            """).Ticket);
        var cdr = Convert.ToBase64String(SunatClient.Zip("R-x.xml", """
            <ar:ApplicationResponse xmlns:ar="urn:oasis:names:specification:ubl:schema:xsd:ApplicationResponse-2" xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2" xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
            <cac:DocumentResponse><cac:Response><cbc:ResponseCode>0</cbc:ResponseCode><cbc:Description>La Comunicacion de baja RA-20261114-1, ha sido aceptada</cbc:Description></cac:Response></cac:DocumentResponse>
            </ar:ApplicationResponse>
            """));
        var status = SunatClient.ParseTicket($"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>
            <br:getStatusResponse xmlns:br="http://service.sunat.gob.pe"><status><statusCode>0</statusCode><content>{cdr}</content></status></br:getStatusResponse>
            </soap:Body></soap:Envelope>
            """);
        Assert.Equal(("0", "0", false), (status.StatusCode, status.Cdr!.Code, status.InProcess));
        Assert.True(SunatClient.ParseTicket("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><status><statusCode>98</statusCode></status></s:Body></s:Envelope>").InProcess);

        var consulted = SunatClient.ParseStatusCdr($"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>
            <ns2:getStatusCdrResponse xmlns:ns2="http://service.sunat.gob.pe"><statusCdr><content>{cdr}</content><statusCode>0004</statusCode><statusMessage>La constancia existe</statusMessage></statusCdr></ns2:getStatusCdrResponse>
            </soap:Body></soap:Envelope>
            """);
        Assert.Equal((SendOutcome.Responded, "0"), (consulted.Outcome, consulted.Code));
        var missing = SunatClient.ParseStatusCdr("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><statusCdr><statusCode>0127</statusCode><statusMessage>El ticket no existe</statusMessage></statusCdr></s:Body></s:Envelope>");
        Assert.Equal((SendOutcome.Error, "0127"), (missing.Outcome, missing.Code));
    }
}
