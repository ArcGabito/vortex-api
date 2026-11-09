using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace VortexApi.Security;

/// <summary>
/// Token de un enlace público: el Id del enlace y 16 bytes de HMAC-SHA256 sobre él, en Base64Url (43 caracteres). No se
/// guarda en la base: se recalcula para verificarlo, y el mismo enlace siempre da la misma URL.
/// <para>
/// La llave sale de <c>PublicLinks:Secret</c> o, si no se configura, de la llave privada con que el hub firma los tokens
/// (rotarla invalida los enlaces que estén abiertos).
/// </para>
/// </summary>
public sealed class PublicLinkTokens
{
    private const int MacBytes = 16;
    private readonly byte[] _key;

    public PublicLinkTokens(IConfiguration config, SigningKeyProvider signing)
    {
        var secret = config["PublicLinks:Secret"];
        _key = string.IsNullOrWhiteSpace(secret)
            ? SHA256.HashData([.. "vortex-public-links|"u8, .. signing.PrivateKey.Rsa.ExportPkcs8PrivateKey()])
            : SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public string Create(Guid id)
    {
        Span<byte> token = stackalloc byte[16 + MacBytes];
        id.TryWriteBytes(token);
        Mac(token[..16], token[16..]);
        return Base64Url.EncodeToString(token);
    }

    /// <summary>El Id del enlace si el token es válido; null si está mal formado o la firma no coincide.</summary>
    public Guid? Verify(string? token)
    {
        if (token is not { Length: 43 })
            return null;
        Span<byte> bytes = stackalloc byte[16 + MacBytes];
        if (!Base64Url.TryDecodeFromChars(token, bytes, out var written) || written != bytes.Length)
            return null;
        Span<byte> expected = stackalloc byte[MacBytes];
        Mac(bytes[..16], expected);
        return CryptographicOperations.FixedTimeEquals(expected, bytes[16..]) ? new Guid(bytes[..16]) : null;
    }

    private void Mac(ReadOnlySpan<byte> id, Span<byte> destination)
    {
        Span<byte> full = stackalloc byte[32];
        HMACSHA256.HashData(_key, id, full);
        full[..MacBytes].CopyTo(destination);
    }
}
