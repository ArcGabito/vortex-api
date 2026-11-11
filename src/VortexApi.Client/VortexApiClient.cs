using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VortexApi.Client;

public sealed class VortexApiClientOptions
{
    /// <summary>Código del sistema: ERP, CRM, WMS o POS.</summary>
    public required string ProductCode { get; init; }

    /// <summary>Clave de aplicación del sistema (generada en /api/admin/products/{code}/rotate-key).</summary>
    public required string ProductKey { get; init; }
}

/// <summary>
/// Cliente HTTP del hub Vortex. Es de bajo nivel: cada llamada recibe el access token. Para que la sesión se
/// renueve sola use <see cref="Sessions.VortexSessionManager"/>, y para sincronizar <see cref="Sync.VortexSync"/>.
/// </summary>
public sealed class VortexApiClient(HttpClient http, VortexApiClientOptions options)
{
    private static readonly JsonSerializerOptions Json = VortexJson.Options;

    public string ProductCode => options.ProductCode;

    // ---------- Sesión ----------

    public Task<VortexSession> LoginAsync(string email, string password, string deviceId, string? deviceName = null,
        string? platform = null, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = JsonContent.Create(new { email, password, deviceId, deviceName, platform }, options: Json),
        };
        request.Headers.Add("X-Vortex-Product", options.ProductCode);
        request.Headers.Add("X-Vortex-Product-Key", options.ProductKey);
        return SendAsync<VortexSession>(request, ct)!;
    }

    public Task<VortexSession> RefreshAsync(string refreshToken, string deviceId, CancellationToken ct = default) =>
        SendAsync<VortexSession>(new HttpRequestMessage(HttpMethod.Post, "api/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken, deviceId }, options: Json),
        }, ct)!;

    /// <summary>Confirma con el hub que el acceso sigue vigente y obtiene un offline token renovado.</summary>
    public Task<VortexHeartbeat> HeartbeatAsync(string accessToken, CancellationToken ct = default) =>
        SendAsync<VortexHeartbeat>(Authorized(HttpMethod.Post, "api/access/heartbeat", accessToken), ct)!;

    /// <summary>Sistemas de la suite a los que el usuario puede entrar (para un selector de sistemas).</summary>
    public Task<IReadOnlyList<VortexAvailableProduct>> GetAvailableProductsAsync(string accessToken, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<VortexAvailableProduct>>(Authorized(HttpMethod.Get, "api/access/products", accessToken), ct)!;

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default) =>
        await SendAsync<object>(new HttpRequestMessage(HttpMethod.Post, "api/auth/logout")
        {
            Content = JsonContent.Create(new { refreshToken }, options: Json),
        }, ct);

/// <summary>
    /// Guarda el idioma preferido del usuario en el hub (<c>es</c>, <c>en</c>, <c>pt</c>; null vuelve al del país), para
    /// que lo usen todos los sistemas y equipos. No cierra la sesión.
    /// </summary>
    public Task<VortexUser> SetLanguageAsync(string accessToken, string? language, CancellationToken ct = default)
    {
        var request = Authorized(HttpMethod.Put, "api/auth/me/preferences", accessToken);
        request.Content = JsonContent.Create(new { language }, options: Json);
        return SendAsync<VortexUser>(request, ct)!;
    }

    /// <summary>Fija el PIN de caja del usuario (4 a 8 dígitos) o lo quita con <c>null</c>. Pide la contraseña actual.</summary>
    public Task SetPinAsync(string accessToken, string currentPassword, string? pin, CancellationToken ct = default)
    {
        var request = Authorized(HttpMethod.Put, "api/auth/me/pin", accessToken);
        request.Content = JsonContent.Create(new { currentPassword, pin }, options: Json);
        return SendAsync<object>(request, ct);
    }

    /// <summary>Verificadores de PIN de los miembros del sistema en la organización (ver <see cref="VortexPin"/>).</summary>
    public Task<IReadOnlyList<VortexPinVerifier>> GetPinVerifiersAsync(string accessToken, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<VortexPinVerifier>>(Authorized(HttpMethod.Get, "api/access/pins", accessToken), ct)!;

    /// <summary>JWKS con las llaves públicas; guárdelo junto al offline token para validar sin conexión.</summary>
    public Task<string> GetSigningKeysAsync(CancellationToken ct = default) =>
        http.GetStringAsync(".well-known/jwks.json", ct);

    // ---------- Datos maestros ----------

    public Task<VortexChangePage> GetMasterChangesAsync(string accessToken, long since, IEnumerable<string>? types = null,
        int limit = 200, CancellationToken ct = default) =>
        SendAsync<VortexChangePage>(Authorized(HttpMethod.Get,
            $"api/master/changes?since={since}&limit={limit}{TypesQuery(types)}", accessToken), ct)!;

    public Task<JsonElement> GetMasterAsync(string accessToken, string resource, Guid id, CancellationToken ct = default) =>
        SendAsync<JsonElement>(Authorized(HttpMethod.Get, $"api/master/{resource}/{id}", accessToken), ct);

    /// <summary>
    /// Crea o actualiza un dato maestro con un Id generado por la app. Con <paramref name="expectedVersion"/> el hub
    /// responde 412 (<c>version_conflict</c>) si otro sistema lo modificó antes.
    /// </summary>
    public Task<JsonElement> PutMasterAsync(string accessToken, string resource, Guid id, object body, int? expectedVersion = null,
        CancellationToken ct = default)
    {
        var request = Authorized(HttpMethod.Put, $"api/master/{resource}/{id}", accessToken);
        request.Content = body is JsonElement raw
            ? new StringContent(raw.GetRawText(), System.Text.Encoding.UTF8, "application/json")
            : JsonContent.Create(body, options: Json);
        if (expectedVersion is { } v)
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return SendAsync<JsonElement>(request, ct);
    }

    public async Task DeleteMasterAsync(string accessToken, string resource, Guid id, int? expectedVersion = null,
        CancellationToken ct = default)
    {
        var request = Authorized(HttpMethod.Delete, $"api/master/{resource}/{id}", accessToken);
        if (expectedVersion is { } v)
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        await SendAsync<object>(request, ct);
    }

    /// <summary>
    /// Tipo de cambio vigente en <paramref name="date"/> (el de ese día o el último anterior); sin fecha, el más reciente.
    /// Devuelve null si no hay ninguno. Sin conexión, use la copia local que llega por <c>OnExchangeRates</c>.
    /// </summary>
    public async Task<VortexExchangeRate?> GetExchangeRateAsync(string accessToken, string from, string to, DateOnly? date = null,
        CancellationToken ct = default)
    {
        var query = $"api/master/exchange-rates/latest?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}" +
                    (date is { } d ? $"&date={d:yyyy-MM-dd}" : "");
        try
        {
            return await SendAsync<VortexExchangeRate>(Authorized(HttpMethod.Get, query, accessToken), ct);
        }
        catch (VortexApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    // ---------- Eventos ----------

    public Task<VortexEventPage> GetEventsAsync(string accessToken, long since, IEnumerable<string>? types = null,
        string? excludeSource = null, int limit = 200, CancellationToken ct = default) =>
        SendAsync<VortexEventPage>(Authorized(HttpMethod.Get,
            $"api/events?since={since}&limit={limit}{TypesQuery(types)}" +
            (excludeSource is null ? "" : $"&excludeSource={Uri.EscapeDataString(excludeSource)}"), accessToken), ct)!;

    /// <summary>Publica hasta 100 eventos en orden; devuelve el resultado de cada uno.</summary>
    public Task<IReadOnlyList<VortexPublishResult>> PublishEventsAsync(string accessToken, IReadOnlyList<VortexEventToPublish> events,
        CancellationToken ct = default)
    {
        var request = Authorized(HttpMethod.Post, "api/events/batch", accessToken);
        request.Content = JsonContent.Create(new { events }, options: Json);
        return SendAsync<IReadOnlyList<VortexPublishResult>>(request, ct)!;
    }

    // ---------- Enlaces públicos ----------

    /// <summary>
    /// Crea el enlace público del documento (o actualiza su copia y vencimiento si ya existe; la URL no cambia). Si el
    /// cliente ya respondió, devuelve el enlace tal como quedó.
    /// </summary>
    public Task<VortexPublicLink> CreatePublicLinkAsync(string accessToken, VortexPublicLinkRequest link, CancellationToken ct = default)
    {
        var request = Authorized(HttpMethod.Post, "api/public-links", accessToken);
        request.Content = JsonContent.Create(link, options: Json);
        return SendAsync<VortexPublicLink>(request, ct)!;
    }

    /// <summary>El enlace con su estado y, si el cliente respondió, su firma. Null si no existe.</summary>
    public async Task<VortexPublicLink?> GetPublicLinkAsync(string accessToken, Guid id, CancellationToken ct = default)
    {
        try
        {
            return await SendAsync<VortexPublicLink>(Authorized(HttpMethod.Get, $"api/public-links/{id}", accessToken), ct);
        }
        catch (VortexApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<VortexPublicLink>> GetPublicLinksAsync(string accessToken, string? documentType = null, Guid? documentId = null,
        CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<VortexPublicLink>>(Authorized(HttpMethod.Get,
            $"api/public-links?documentType={Uri.EscapeDataString(documentType ?? "")}" + (documentId is { } d ? $"&documentId={d}" : ""),
            accessToken), ct)!;

    /// <summary>Retira el enlace: deja de aceptar respuestas (error <c>link_already_responded</c> si el cliente ya respondió).</summary>
    public async Task RevokePublicLinkAsync(string accessToken, Guid id, CancellationToken ct = default) =>
        await SendAsync<object>(Authorized(HttpMethod.Delete, $"api/public-links/{id}", accessToken), ct);

    // ---------- Facturación electrónica ----------

    /// <summary>
    /// Pide un comprobante electrónico al hub (módulo <c>facturacion_electronica</c>). Vuelve en cola (<see cref="VortexFiscalStatus.Pending"/>);
    /// el resultado llega como <see cref="VortexHubEvents.FiscalAccepted"/> o <see cref="VortexHubEvents.FiscalRejected"/>. Con el
    /// mismo <c>SourceId</c> devuelve el que ya existe, así se puede reintentar desde la cola sin conexión.
    /// </summary>
    public Task<VortexFiscalDocument> IssueFiscalDocumentAsync(string accessToken, VortexFiscalRequest document, CancellationToken ct = default)
    {
        var request = Authorized(HttpMethod.Post, "api/fiscal/documents", accessToken);
        request.Content = JsonContent.Create(document, options: Json);
        return SendAsync<VortexFiscalDocument>(request, ct)!;
    }

    /// <summary>El comprobante con su estado. Null si no existe.</summary>
    public async Task<VortexFiscalDocument?> GetFiscalDocumentAsync(string accessToken, Guid id, CancellationToken ct = default)
    {
        try
        {
            return await SendAsync<VortexFiscalDocument>(Authorized(HttpMethod.Get, $"api/fiscal/documents/{id}", accessToken), ct);
        }
        catch (VortexApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>Comprobantes del sistema: por documento de origen, por estado o los cambiados desde una fecha.</summary>
    public Task<IReadOnlyList<VortexFiscalDocument>> GetFiscalDocumentsAsync(string accessToken, Guid? sourceId = null,
        VortexFiscalStatus? status = null, DateTimeOffset? updatedSince = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (sourceId is { } source)
            query.Add($"sourceId={source}");
        if (status is { } s)
            query.Add($"status={s}");
        if (updatedSince is { } since)
            query.Add($"updatedSince={Uri.EscapeDataString(since.ToString("O"))}");
        return SendAsync<IReadOnlyList<VortexFiscalDocument>>(Authorized(HttpMethod.Get,
            "api/fiscal/documents" + (query.Count > 0 ? "?" + string.Join('&', query) : ""), accessToken), ct)!;
    }

    /// <summary>Vuelve a enviar un comprobante que quedó en <see cref="VortexFiscalStatus.Failed"/>.</summary>
    public Task<VortexFiscalDocument> RetryFiscalDocumentAsync(string accessToken, Guid id, CancellationToken ct = default) =>
        SendAsync<VortexFiscalDocument>(Authorized(HttpMethod.Post, $"api/fiscal/documents/{id}/retry", accessToken), ct)!;

    // ---------- Internos ----------

    private static string TypesQuery(IEnumerable<string>? types)
    {
        var list = types?.ToList();
        return list is { Count: > 0 } ? $"&types={Uri.EscapeDataString(string.Join(',', list))}" : "";
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var _ = request;
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return response.Content.Headers.ContentLength == 0 || response.StatusCode == System.Net.HttpStatusCode.NoContent
                ? default
                : await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }

        string code = "http_error", message = $"HTTP {(int)response.StatusCode}";
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (problem.TryGetProperty("code", out var c)) code = c.GetString() ?? code;
            if (problem.TryGetProperty("title", out var t)) message = t.GetString() ?? message;
        }
        catch (JsonException)
        {
        }
        throw new VortexApiException((int)response.StatusCode, code, message);
    }
}
