using VortexApi.Infrastructure;

namespace VortexApi.Services;

public static class PasswordPolicy
{
    public const int MinLength = 10;

    public static void Ensure(string password, string email)
    {
        var categories = new[]
        {
            password.Any(char.IsUpper), password.Any(char.IsLower), password.Any(char.IsDigit),
            password.Any(c => !char.IsLetterOrDigit(c)),
        }.Count(x => x);

        if (password.Length < MinLength || categories < 3 ||
            password.Contains(email.Split('@')[0], StringComparison.OrdinalIgnoreCase))
        {
            throw ApiException.BadRequest(ErrorCodes.WeakPassword,
                $"La contraseña debe tener al menos {MinLength} caracteres, combinar 3 de: mayúsculas, minúsculas, " +
                "números y símbolos, y no contener el correo.");
        }
    }
}
