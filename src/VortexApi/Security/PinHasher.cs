using System.Security.Cryptography;
using VortexApi.Infrastructure;

namespace VortexApi.Security;

/// <summary>
/// PIN de caja de un usuario (cambio rápido de operador y autorización del supervisor en el POS). Se guarda como
/// <c>pin1$iteraciones$sal$hash</c> (PBKDF2-SHA256): los equipos del sistema reciben ese verificador para validar el PIN
/// sin conexión con <c>VortexPin.Verify</c> del SDK. El PIN no reemplaza a la contraseña: no inicia sesión en el hub.
/// </summary>
public static class PinHasher
{
    public const int Iterations = 200_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"pin1${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>PIN de 4 a 8 dígitos, que no sea una secuencia ni un mismo dígito repetido.</summary>
    public static void EnsurePolicy(string? pin)
    {
        if (pin is null || pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit))
            throw new ApiException(StatusCodes.Status400BadRequest, ErrorCodes.Validation, "El PIN debe tener de 4 a 8 dígitos.");
        var steps = pin.Zip(pin.Skip(1), (a, b) => b - a).Distinct().ToList();
        if (steps is [0] or [1] or [-1])
            throw new ApiException(StatusCodes.Status400BadRequest, ErrorCodes.Validation,
                "El PIN es muy fácil de adivinar: no uses un mismo dígito ni una secuencia.");
    }
}
