using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace VortexApi.Suite.Text;

/// <summary>
/// Textos de la interfaz en varios idiomas. Cada app incrusta un JSON plano por idioma (<c>es.json</c>, <c>en.json</c>,
/// <c>pt.json</c>: <c>{ "nav.home": "Panel" }</c>) y los textos comunes de la suite vienen en esta librería. Si una clave
/// falta en un idioma se usa el español, y si falta en todos, la clave misma (así se nota en pantalla).
/// </summary>
public sealed class TextCatalog
{
    private readonly Dictionary<string, Dictionary<string, string>> _byLanguage = new(StringComparer.OrdinalIgnoreCase);

    public TextCatalog()
    {
        foreach (var language in Regional.Languages.Supported)
            Load(typeof(TextCatalog).Assembly, $"VortexApi.Suite.Text.{language}.json", language);
    }

    /// <summary>Agrega los textos de una app desde recursos incrustados <c>{prefijo}.{idioma}.json</c>; pisan a los comunes.</summary>
    public TextCatalog AddResources(Assembly assembly, string resourcePrefix)
    {
        foreach (var language in Regional.Languages.Supported)
            Load(assembly, $"{resourcePrefix}.{language}.json", language);
        return this;
    }

    public TextCatalog Add(string language, IReadOnlyDictionary<string, string> texts)
    {
        var target = Table(language);
        foreach (var (key, value) in texts)
            target[key] = value;
        return this;
    }

    /// <summary>El texto de la clave en el idioma pedido, con <c>{0}</c>, <c>{1}</c>… reemplazados.</summary>
    public string Get(string language, string key, params object?[] args)
    {
        var text = Table(Regional.Languages.Normalize(language)).GetValueOrDefault(key)
            ?? Table(Regional.Languages.Spanish).GetValueOrDefault(key)
            ?? key;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    /// <summary>Claves que existen en español y faltan en otro idioma (para las pruebas de cada app).</summary>
    public IReadOnlyList<string> Missing(string language) =>
        Table(Regional.Languages.Spanish).Keys.Where(k => !Table(language).ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private Dictionary<string, string> Table(string language)
    {
        if (!_byLanguage.TryGetValue(language, out var table))
            _byLanguage[language] = table = new Dictionary<string, string>(StringComparer.Ordinal);
        return table;
    }

    private void Load(Assembly assembly, string resource, string language)
    {
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
            return;
        var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        Add(language, texts);
    }
}
