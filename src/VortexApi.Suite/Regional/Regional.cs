using System.Globalization;

namespace VortexApi.Suite.Regional;

/// <summary>Idiomas de la interfaz de la suite.</summary>
public static class Languages
{
    public const string Spanish = "es";
    public const string English = "en";
    public const string Portuguese = "pt";

    public static readonly string[] Supported = [Spanish, English, Portuguese];

    public static string NameOf(string language) => language switch
    {
        English => "English",
        Portuguese => "Português",
        _ => "Español",
    };

    /// <summary>Idioma por defecto según el país de la empresa: portugués en Brasil y Portugal, inglés en EE. UU. y el Reino Unido.</summary>
    public static string DefaultFor(string? countryCode) => (countryCode ?? "").ToUpperInvariant() switch
    {
        "BR" or "PT" or "AO" or "MZ" => Portuguese,
        "US" or "GB" or "IE" or "CA" or "AU" or "NZ" => English,
        _ => Spanish,
    };

    public static string Normalize(string? language) =>
        language is { Length: >= 2 } l && Supported.Contains(l[..2].ToLowerInvariant()) ? l[..2].ToLowerInvariant() : Spanish;
}

/// <summary>
/// Formatos regionales: la cultura sale del idioma del usuario y del país de la empresa (<c>es</c> + <c>PE</c> = es-PE,
/// <c>en</c> + <c>MX</c> = en-MX…). Si la combinación no existe, se usa la cultura principal del idioma (es-PE, en-US, pt-BR).
/// </summary>
public static class RegionalSettings
{
    private static readonly HashSet<string> Known =
        CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static CultureInfo CultureFor(string? language, string? countryCode)
    {
        var lang = Languages.Normalize(language);
        var country = (countryCode ?? "").Trim().ToUpperInvariant();
        foreach (var name in new[] { $"{lang}-{country}", Fallback(lang) })
        {
            if (Known.Contains(name))
                return CultureInfo.GetCultureInfo(name);
        }
        return CultureInfo.InvariantCulture;
    }

    private static string Fallback(string language) => language switch
    {
        Languages.English => "en-US",
        Languages.Portuguese => "pt-BR",
        _ => "es-PE",
    };

    /// <summary>Hora local de la empresa (zona IANA del hub, <c>company.timeZone</c>), en cualquier plataforma.</summary>
    public static DateTimeOffset ToCompanyTime(DateTimeOffset instant, string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
            return instant;
        try
        {
            return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return instant;
        }
    }

    /// <summary>La fecha de hoy para la empresa.</summary>
    public static DateOnly Today(string? timeZone, TimeProvider? clock = null) =>
        DateOnly.FromDateTime(ToCompanyTime((clock ?? TimeProvider.System).GetUtcNow(), timeZone).DateTime);
}
