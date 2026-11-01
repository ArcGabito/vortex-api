using System.Net;
using System.Net.Sockets;

namespace VortexApi.Services.Webhooks;

/// <summary>Protección contra SSRF: impide que un webhook apunte a la red interna o a servicios de metadatos de la nube.</summary>
public static class NetworkGuard
{
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.IPv6None))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                   (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 (ULA)

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => true,
            100 => b[1] is >= 64 and <= 127,        // 100.64.0.0/10 (CGNAT)
            169 => b[1] == 254,                     // link-local y metadatos de la nube (169.254.169.254)
            172 => b[1] is >= 16 and <= 31,
            192 => b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2),
            198 => b[1] is 18 or 19,
            >= 224 => true,                         // multicast y reservadas
            _ => false,
        };
    }

    /// <summary>Validación al registrar el webhook. La verificación definitiva ocurre al conectar (ver <see cref="CreateHandler"/>).</summary>
    public static string? ValidateUrl(string url, WebhookOptions options)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "La URL no es válida.";
        if (uri.Scheme != Uri.UriSchemeHttps && !(options.AllowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
            return "La URL debe usar https.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "La URL no puede incluir usuario ni contraseña.";
        if (options.AllowPrivateNetworks)
            return null;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return "La URL apunta a la red interna.";
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IsPrivate(ip))
            return "La URL apunta a una red privada.";
        return null;
    }

    /// <summary>
    /// Handler HTTP para webhooks: sin redirecciones y resolviendo el DNS al conectar, de modo que un dominio
    /// que luego apunte a una IP interna (DNS rebinding) tampoco pase.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(WebhookOptions options) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || (!options.AllowPrivateNetworks && addresses.Any(IsPrivate)))
                throw new HttpRequestException($"Destino no permitido: {context.DnsEndPoint.Host} resuelve a una red privada.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
