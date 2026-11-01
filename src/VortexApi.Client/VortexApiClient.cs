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

/// <summary>Cliente HTTP del hub Vortex para los sistemas de la suite.</summary>
public sealed class VortexApiClient(HttpClient http, VortexApiClientOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

    /// <summary>JWKS con las llaves públicas; guárdelo junto al offline token para validar sin conexión.</summary>
    public Task<string> GetSigningKeysAsync(CancellationToken ct = default) =>
        http.GetStringAsync(".well-known/jwks.json", ct);

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
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
