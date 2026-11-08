using System.Security.Cryptography;

namespace VortexApi.Client;

/// <summary>
/// Verificador del PIN de caja de un miembro del sistema (<c>GET api/access/pins</c>). Permite validar el PIN sin
/// conexión para cambiar de operador o autorizar una acción. Guárdelo en almacenamiento seguro del equipo, no en la base
/// local: un PIN es corto y un verificador expuesto se puede adivinar probando.
/// </summary>
public sealed record VortexPinVerifier(Guid UserId, string FullName, string? Email, string? Role, string PinHash);

public static class VortexPin
{
    /// <summary>Si <paramref name="pin"/> corresponde al verificador <c>pin1$iteraciones$sal$hash</c> (PBKDF2-SHA256).</summary>
    public static bool Verify(string? pin, string? pinHash)
    {
        if (string.IsNullOrEmpty(pin) || pinHash?.Split('$') is not ["pin1", var iterText, var saltText, var hashText]
            || !int.TryParse(iterText, out var iterations) || iterations is < 1000 or > 5_000_000)
            return false;
        try
        {
            var salt = Convert.FromBase64String(saltText);
            var expected = Convert.FromBase64String(hashText);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
