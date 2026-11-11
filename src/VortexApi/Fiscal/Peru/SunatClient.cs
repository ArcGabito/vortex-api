using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace VortexApi.Fiscal.Peru;

/// <summary>Resultado de enviar un comprobante a la autoridad.</summary>
public enum SendOutcome
{
    /// <summary>Respondió con el CDR (aceptado, observado o rechazado según su código).</summary>
    Responded = 0,

    /// <summary>Rechazado en la recepción (en SUNAT, códigos 2000 a 3999 como fault).</summary>
    Rejected = 1,

    /// <summary>Error que no se arregla reintentando (credenciales, XML mal formado, ya registrado con otros datos).</summary>
    Error = 2,

    /// <summary>Error pasajero (sin conexión, servicio no disponible): se reintenta.</summary>
    Transient = 3,
}

public sealed record SendResult(SendOutcome Outcome, string? Code, string? Description, IReadOnlyList<string> Notes, string? CdrXml)
{
    public static SendResult Transient(string description) => new(SendOutcome.Transient, null, description, [], null);
}

/// <summary>Envía el ZIP del comprobante firmado. La implementación real es <see cref="SunatClient"/>.</summary>
public interface ISunatClient
{
    Task<SendResult> SendBillAsync(FiscalEnvironment environment, string username, string password, string fileName, byte[] zip,
        CancellationToken ct = default);
}

/// <summary>
/// Cliente del servicio <c>billService</c> de SUNAT (SEE del contribuyente): <c>sendBill</c> con el ZIP en base64 y
/// seguridad WS-Security <c>UsernameToken</c> (usuario = RUC + usuario SOL). La respuesta trae el CDR comprimido; los
/// errores de recepción llegan como <c>soap:Fault</c> con el código de SUNAT.
/// </summary>
public sealed class SunatClient(HttpClient http) : ISunatClient
{
    public const string HttpClientName = "sunat";
    public const string BetaUrl = "https://e-beta.sunat.gob.pe/ol-ti-itcpfegem-beta/billService";
    public const string ProductionUrl = "https://e-factura.sunat.gob.pe/ol-ti-itcpfegem/billService";

    /// <summary>Códigos de SUNAT que indican una falla del servicio: se reintenta más tarde.</summary>
    private static readonly HashSet<string> TransientCodes = ["0100", "0109", "0110", "0200"];

    public async Task<SendResult> SendBillAsync(FiscalEnvironment environment, string username, string password, string fileName, byte[] zip,
        CancellationToken ct = default)
    {
        var envelope = Envelope(username, password, fileName + ".zip", Convert.ToBase64String(zip));
        using var request = new HttpRequestMessage(HttpMethod.Post, environment == FiscalEnvironment.Production ? ProductionUrl : BetaUrl)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };
        request.Headers.Add("SOAPAction", "urn:sendBill");
        string body;
        try
        {
            using var response = await http.SendAsync(request, ct);
            body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode && !body.Contains("Fault", StringComparison.Ordinal))
                return SendResult.Transient($"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return SendResult.Transient(ex.Message);
        }
        return ParseResponse(body);
    }

    public static string Envelope(string username, string password, string zipName, string base64) =>
        $"""
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:ser="http://service.sunat.gob.pe" xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd">
          <soapenv:Header>
            <wsse:Security>
              <wsse:UsernameToken>
                <wsse:Username>{SecurityElement.Escape(username)}</wsse:Username>
                <wsse:Password>{SecurityElement.Escape(password)}</wsse:Password>
              </wsse:UsernameToken>
            </wsse:Security>
          </soapenv:Header>
          <soapenv:Body>
            <ser:sendBill>
              <fileName>{SecurityElement.Escape(zipName)}</fileName>
              <contentFile>{base64}</contentFile>
            </ser:sendBill>
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    /// <summary>Interpreta la respuesta SOAP: el CDR de <c>applicationResponse</c> o el fault con su código.</summary>
    public static SendResult ParseResponse(string body)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException)
        {
            return SendResult.Transient("Respuesta no válida de SUNAT.");
        }

        var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is not null)
        {
            var faultCode = fault.Elements().FirstOrDefault(e => e.Name.LocalName == "faultcode")?.Value ?? "";
            var faultString = fault.Elements().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value ?? "";
            // SUNAT pone el código en faultcode ("soap-env:Client.2335") o, a veces, en faultstring.
            var code = Digits(faultCode) ?? Digits(faultString) ?? "";
            var description = string.IsNullOrWhiteSpace(faultString) ? faultCode : faultString.Trim();
            if (int.TryParse(code, out var number) && number is >= 2000 and < 4000)
                return new SendResult(SendOutcome.Rejected, code, description, [], null);
            return new SendResult(TransientCodes.Contains(code) ? SendOutcome.Transient : SendOutcome.Error, code, description, [], null);
        }

        var application = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "applicationResponse")?.Value;
        if (string.IsNullOrWhiteSpace(application))
            return SendResult.Transient("SUNAT no devolvió el CDR.");
        string cdr;
        try
        {
            cdr = ReadZip(Convert.FromBase64String(application.Trim()));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            return SendResult.Transient("CDR ilegible.");
        }
        return ParseCdr(cdr);
    }

    /// <summary>Lee el CDR: <c>cbc:ResponseCode</c>, <c>cbc:Description</c> y las observaciones en <c>cbc:Note</c>.</summary>
    public static SendResult ParseCdr(string cdr)
    {
        var doc = XDocument.Parse(cdr);
        var response = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Response");
        var code = response?.Elements().FirstOrDefault(e => e.Name.LocalName == "ResponseCode")?.Value.Trim();
        var description = response?.Elements().FirstOrDefault(e => e.Name.LocalName == "Description")?.Value.Trim();
        var notes = doc.Root?.Elements().Where(e => e.Name.LocalName == "Note").Select(e => e.Value.Trim()).Where(n => n.Length > 0).ToList() ?? [];
        return new SendResult(SendOutcome.Responded, code, description, notes, cdr);
    }

    public static byte[] Zip(string entryName, string content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return stream.ToArray();
    }

    /// <summary>Primer XML del ZIP (el CDR viene como <c>R-RUC-TIPO-SERIE-NUMERO.xml</c>).</summary>
    public static string ReadZip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("El ZIP no trae XML.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? Digits(string text)
    {
        var digits = new string(text.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (digits.Length == 0)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, @"\b\d{4}\b");
            return match.Success ? match.Value : null;
        }
        return digits;
    }
}
