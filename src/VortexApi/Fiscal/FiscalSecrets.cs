using System.Security.Cryptography;
using System.Text;
using VortexApi.Security;

namespace VortexApi.Fiscal;

/// <summary>
/// Cifra los secretos fiscales (certificado, contraseñas) con AES-GCM. La llave sale de <c>Fiscal:SecretKey</c> o, si no se
/// configura, de la llave privada del hub (rotarla obliga a volver a cargar los certificados). Formato: nonce (12) + tag
/// (16) + texto cifrado.
/// </summary>
public sealed class FiscalSecrets
{
    private readonly byte[] _key;

    public FiscalSecrets(IConfiguration config, SigningKeyProvider signing)
    {
        var secret = config["Fiscal:SecretKey"];
        _key = string.IsNullOrWhiteSpace(secret)
            ? SHA256.HashData([.. "vortex-fiscal|"u8, .. signing.PrivateKey.Rsa.ExportPkcs8PrivateKey()])
            : SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public byte[] Protect(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] data)
    {
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return plain;
    }

    public byte[] Protect(string plain) => Protect(Encoding.UTF8.GetBytes(plain));

    public string UnprotectString(byte[] data) => Encoding.UTF8.GetString(Unprotect(data));
}
