using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace VortexApi.Security;

/// <summary>
/// Llave RSA con la que se firman access tokens y license tokens (RS256). La parte pública se publica en
/// <c>/.well-known/jwks.json</c> para que los sistemas Vortex validen tokens sin compartir secretos.
/// </summary>
public sealed class SigningKeyProvider
{
    public RsaSecurityKey PrivateKey { get; }
    public RsaSecurityKey PublicKey { get; }
    public SigningCredentials Credentials { get; }

    public SigningKeyProvider(IOptions<JwtOptions> options, ILogger<SigningKeyProvider> logger)
    {
        var rsa = Load(options.Value, logger);
        var kid = ComputeKeyId(rsa);

        PrivateKey = new RsaSecurityKey(rsa) { KeyId = kid };
        PublicKey = new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false)) { KeyId = kid };
        Credentials = new SigningCredentials(PrivateKey, SecurityAlgorithms.RsaSha256);
    }

    public JsonWebKey PublicJwk
    {
        get
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(PublicKey);
            jwk.Use = "sig";
            jwk.Alg = SecurityAlgorithms.RsaSha256;
            return jwk;
        }
    }

    private static RSA Load(JwtOptions options, ILogger logger)
    {
        var rsa = RSA.Create();

        if (!string.IsNullOrWhiteSpace(options.SigningKeyPem))
        {
            rsa.ImportFromPem(options.SigningKeyPem);
            return rsa;
        }

        if (string.IsNullOrWhiteSpace(options.SigningKeyPath))
            throw new InvalidOperationException("Configure Jwt:SigningKeyPem o Jwt:SigningKeyPath.");

        if (File.Exists(options.SigningKeyPath))
        {
            rsa.ImportFromPem(File.ReadAllText(options.SigningKeyPath));
            return rsa;
        }

        if (!options.GenerateKeyIfMissing)
            throw new InvalidOperationException($"No existe la llave de firma '{options.SigningKeyPath}'.");

        rsa.KeySize = 3072;
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.SigningKeyPath))!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(options.SigningKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(options.SigningKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        logger.LogWarning("Se generó una nueva llave de firma en {Path}. No use llaves generadas en producción.",
            options.SigningKeyPath);
        return rsa;
    }

    private static string ComputeKeyId(RSA rsa)
    {
        var hash = SHA256.HashData(rsa.ExportSubjectPublicKeyInfo());
        return Base64UrlEncoder.Encode(hash[..16]);
    }
}
