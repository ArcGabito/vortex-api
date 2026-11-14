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

/// <summary>Respuesta de <c>sendSummary</c>: el ticket para consultar, o el error de recepción.</summary>
public sealed record SummaryResult(SendOutcome Outcome, string? Ticket, string? Code, string? Description);

/// <summary>
/// Estado de un ticket (<c>getStatus</c>): 0 procesado con su CDR, 98 en proceso, 99 procesado con errores (CDR con el
/// código del rechazo). <see cref="Cdr"/> trae el CDR interpretado.
/// </summary>
public sealed record TicketStatus(SendOutcome Outcome, string? StatusCode, SendResult? Cdr, string? Description)
{
    public bool InProcess => StatusCode == "98";
}

/// <summary>Envía a SUNAT. La implementación real es <see cref="SunatClient"/>.</summary>
public interface ISunatClient
{
    Task<SendResult> SendBillAsync(FiscalEnvironment environment, string username, string password, string fileName, byte[] zip,
        CancellationToken ct = default);

    /// <summary>Comunicación de baja o resumen diario: devuelve el ticket.</summary>
    Task<SummaryResult> SendSummaryAsync(FiscalEnvironment environment, string username, string password, string fileName, byte[] zip,
        CancellationToken ct = default);

    Task<TicketStatus> GetStatusAsync(FiscalEnvironment environment, string username, string password, string ticket, CancellationToken ct = default);

    /// <summary>
    /// Consulta el CDR de un comprobante ya enviado (<c>billConsultService.getStatusCdr</c>). Solo existe en producción; en
    /// beta devuelve <see cref="SendOutcome.Error"/>.
    /// </summary>
    Task<SendResult> GetStatusCdrAsync(FiscalEnvironment environment, string username, string password, string ruc, string typeCode, string series,
        long number, CancellationToken ct = default);
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

    public const string ConsultUrl = "https://e-factura.sunat.gob.pe/ol-it-wsconscpegem/billConsultService";

    public async Task<SummaryResult> SendSummaryAsync(FiscalEnvironment environment, string username, string password, string fileName, byte[] zip,
        CancellationToken ct = default)
    {
        var body = $"""
            <ser:sendSummary>
              <fileName>{SecurityElement.Escape(fileName + ".zip")}</fileName>
              <contentFile>{Convert.ToBase64String(zip)}</contentFile>
            </ser:sendSummary>
            """;
        var (ok, response) = await PostAsync(environment == FiscalEnvironment.Production ? ProductionUrl : BetaUrl, "urn:sendSummary",
            Wrap(username, password, body), ct);
        return ok ? ParseSummary(response) : new SummaryResult(SendOutcome.Transient, null, null, response);
    }

    public async Task<TicketStatus> GetStatusAsync(FiscalEnvironment environment, string username, string password, string ticket, CancellationToken ct = default)
    {
        var body = $"<ser:getStatus><ticket>{SecurityElement.Escape(ticket)}</ticket></ser:getStatus>";
        var (ok, response) = await PostAsync(environment == FiscalEnvironment.Production ? ProductionUrl : BetaUrl, "urn:getStatus",
            Wrap(username, password, body), ct);
        return ok ? ParseTicket(response) : new TicketStatus(SendOutcome.Transient, null, null, response);
    }

    public async Task<SendResult> GetStatusCdrAsync(FiscalEnvironment environment, string username, string password, string ruc, string typeCode,
        string series, long number, CancellationToken ct = default)
    {
        if (environment != FiscalEnvironment.Production)
            return new SendResult(SendOutcome.Error, null, "SUNAT solo ofrece la consulta del CDR en producción.", [], null);
        var body = $"""
            <ser:getStatusCdr>
              <rucComprobante>{SecurityElement.Escape(ruc)}</rucComprobante>
              <tipoComprobante>{SecurityElement.Escape(typeCode)}</tipoComprobante>
              <serieComprobante>{SecurityElement.Escape(series)}</serieComprobante>
              <numeroComprobante>{number}</numeroComprobante>
            </ser:getStatusCdr>
            """;
        var (ok, response) = await PostAsync(ConsultUrl, "urn:getStatusCdr", Wrap(username, password, body), ct);
        return ok ? ParseStatusCdr(response) : SendResult.Transient(response);
    }

    /// <summary>POST SOAP. Devuelve (true, cuerpo) o (false, descripción del error de red) para reintentar.</summary>
    private async Task<(bool Ok, string Body)> PostAsync(string url, string action, string envelope, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") };
        request.Headers.Add("SOAPAction", action);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return !response.IsSuccessStatusCode && !body.Contains("Fault", StringComparison.Ordinal)
                ? (false, $"HTTP {(int)response.StatusCode}")
                : (true, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Sobre SOAP con WS-Security y el cuerpo de la operación.</summary>
    public static string Wrap(string username, string password, string operation) =>
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
            {operation}
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    /// <summary>Respuesta de <c>sendSummary</c>: el <c>ticket</c> o el fault con su código.</summary>
    public static SummaryResult ParseSummary(string body)
    {
        if (Parse(body) is not { } doc)
            return new SummaryResult(SendOutcome.Transient, null, null, "Respuesta no válida de SUNAT.");
        if (Fault(doc) is { } fault)
            return new SummaryResult(fault.Outcome, null, fault.Code, fault.Description);
        var ticket = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ticket")?.Value.Trim();
        return string.IsNullOrEmpty(ticket)
            ? new SummaryResult(SendOutcome.Transient, null, null, "SUNAT no devolvió el ticket.")
            : new SummaryResult(SendOutcome.Responded, ticket, null, null);
    }

    /// <summary>Respuesta de <c>getStatus</c>: <c>statusCode</c> (0, 98, 99) y el CDR en <c>content</c>.</summary>
    public static TicketStatus ParseTicket(string body)
    {
        if (Parse(body) is not { } doc)
            return new TicketStatus(SendOutcome.Transient, null, null, "Respuesta no válida de SUNAT.");
        if (Fault(doc) is { } fault)
            return new TicketStatus(fault.Outcome, fault.Code, null, fault.Description);
        var status = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "statusCode")?.Value.Trim();
        var content = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "content")?.Value.Trim();
        SendResult? cdr = null;
        if (!string.IsNullOrEmpty(content))
        {
            try
            {
                cdr = ParseCdr(ReadZip(Convert.FromBase64String(content)));
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or System.Xml.XmlException)
            {
                return new TicketStatus(SendOutcome.Transient, status, null, "CDR ilegible.");
            }
        }
        return new TicketStatus(SendOutcome.Responded, status, cdr, cdr?.Description);
    }

    /// <summary>Respuesta de <c>getStatusCdr</c>: el CDR si existe (<c>statusCode</c> 0004) o el motivo.</summary>
    public static SendResult ParseStatusCdr(string body)
    {
        if (Parse(body) is not { } doc)
            return SendResult.Transient("Respuesta no válida de SUNAT.");
        if (Fault(doc) is { } fault)
            return new SendResult(fault.Outcome, fault.Code, fault.Description, [], null);
        var code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "statusCode")?.Value.Trim();
        var message = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "statusMessage")?.Value.Trim();
        var content = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "content")?.Value.Trim();
        if (string.IsNullOrEmpty(content))
            return new SendResult(SendOutcome.Error, code, message ?? "SUNAT no tiene la constancia de este comprobante.", [], null);
        try
        {
            return ParseCdr(ReadZip(Convert.FromBase64String(content)));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or System.Xml.XmlException)
        {
            return SendResult.Transient("CDR ilegible.");
        }
    }

    private static XDocument? Parse(string body)
    {
        try
        {
            return XDocument.Parse(body);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static (SendOutcome Outcome, string Code, string Description)? Fault(XDocument doc)
    {
        var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is null)
            return null;
        var faultCode = fault.Elements().FirstOrDefault(e => e.Name.LocalName == "faultcode")?.Value ?? "";
        var faultString = fault.Elements().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value ?? "";
        var code = Digits(faultCode) ?? Digits(faultString) ?? "";
        var description = string.IsNullOrWhiteSpace(faultString) ? faultCode : faultString.Trim();
        var outcome = int.TryParse(code, out var number) && number is >= 2000 and < 4000 ? SendOutcome.Rejected
            : TransientCodes.Contains(code) ? SendOutcome.Transient : SendOutcome.Error;
        return (outcome, code, description);
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
