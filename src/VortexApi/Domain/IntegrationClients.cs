using System.Text.RegularExpressions;

namespace VortexApi.Domain;

/// <summary>
/// Aplicación externa autorizada por una organización para integrarse con la suite (tienda online, facturación
/// electrónica, pasarela de pago, BI…). Se autentica con OAuth2 <c>client_credentials</c> y solo puede hacer lo que
/// permiten sus <see cref="Scopes"/>.
/// </summary>
public class IntegrationClient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>Identificador corto (<c>tienda-woo</c>); sus eventos y cambios llevan <c>source = "EXT:tienda-woo"</c>.</summary>
    public required string Slug { get; set; }

    /// <summary><c>client_id</c> público de OAuth2.</summary>
    public required string ClientId { get; set; }

    /// <summary>SHA-256 del <c>client_secret</c> (secreto aleatorio de alta entropía; nunca se guarda en claro).</summary>
    public required string SecretHash { get; set; }

    /// <summary>Últimos caracteres del secreto, para reconocerlo en la consola.</summary>
    public required string SecretHint { get; set; }

    public List<string> Scopes { get; set; } = [];
    public bool IsActive { get; set; } = true;

    /// <summary>Opcional: la aplicación deja de poder pedir tokens después de esta fecha.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Cambia al rotar el secreto o desactivar la aplicación: invalida los tokens ya emitidos.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public string Source => IntegrationScopes.SourcePrefix + Slug;
}

public static partial class IntegrationScopes
{
    public const string SourcePrefix = "EXT:";

    public const string MasterRead = "master.read";
    public const string MasterWrite = "master.write";
    public const string EventsRead = "events.read";

    /// <summary>Publicar cualquier evento no reservado. Para restringir: <c>events.publish:order.*</c>.</summary>
    public const string EventsPublish = "events.publish";
    public const string WebhooksManage = "webhooks.manage";

    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [MasterRead] = "Leer datos maestros (clientes, artículos, sedes, empresa) y su feed de cambios.",
        [MasterWrite] = "Crear, modificar y borrar clientes, artículos y sedes.",
        [EventsRead] = "Leer el feed de eventos de integración.",
        [EventsPublish] = "Publicar eventos. Restringible a ciertos tipos: events.publish:order.*",
        [WebhooksManage] = "Registrar y administrar sus propios webhooks.",
    };

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex SlugRegex();

    public static bool IsValidSlug(string slug) => SlugRegex().IsMatch(slug);

    public static bool IsValid(string scope)
    {
        if (Descriptions.ContainsKey(scope))
            return true;
        // events.publish:<patrón>
        return scope.StartsWith(EventsPublish + ":", StringComparison.Ordinal) &&
               EventTypePattern.IsValidPattern(scope[(EventsPublish.Length + 1)..]) &&
               scope[(EventsPublish.Length + 1)..] != "*";
    }

    /// <summary>¿Alguno de los scopes permite publicar este tipo de evento?</summary>
    public static bool CanPublish(IEnumerable<string> scopes, string eventType) => scopes.Any(s =>
        s == EventsPublish ||
        (s.StartsWith(EventsPublish + ":", StringComparison.Ordinal) && EventTypePattern.Matches(s[(EventsPublish.Length + 1)..], eventType)));

    public static bool CanPublishAny(IEnumerable<string> scopes) =>
        scopes.Any(s => s == EventsPublish || s.StartsWith(EventsPublish + ":", StringComparison.Ordinal));
}
