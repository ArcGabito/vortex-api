using System.Security.Cryptography;
using System.Text;

namespace VortexApi.Security;

public static class Hashing
{
    /// <summary>SHA-256 en hex; para secretos de alta entropía (tokens, claves) e identificadores de equipo.</summary>
    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool FixedTimeEqualsHex(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    /// <summary>Secreto aleatorio en base64url.</summary>
    public static string RandomToken(int bytes = 48) =>
        Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
