namespace VortexApi.Domain;

public enum PublicLinkStatus
{
    /// <summary>Esperando la respuesta del cliente.</summary>
    Open = 0,
    Accepted = 1,
    Rejected = 2,

    /// <summary>El sistema lo retiró (p. ej. hay una versión nueva del documento).</summary>
    Revoked = 3,
}

/// <summary>
/// Enlace público firmado para que alguien de fuera de la organización (un cliente) vea un documento y lo acepte con su
/// firma o lo rechace, sin usuario en el hub. Lo crea un sistema de la suite (el CRM, para sus cotizaciones) con una copia
/// del documento tal como se envió; la respuesta llega al sistema como evento <c>hub.public_link.accepted|rejected</c>.
/// <para>
/// El token del enlace no se guarda: es el Id firmado con HMAC (<see cref="Security.PublicLinkTokens"/>), así cualquier
/// equipo que vuelva a pedir el enlace de un documento recibe la misma URL.
/// </para>
/// </summary>
public class PublicLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }

    /// <summary>Sistema dueño del documento (CRM). Solo él lo administra y recibe su respuesta.</summary>
    public required string Product { get; set; }

    /// <summary>Tipo de documento (<c>quote</c>).</summary>
    public required string DocumentType { get; set; }

    /// <summary>Id del documento en el sistema (uno por documento: volver a crearlo actualiza la copia).</summary>
    public Guid DocumentId { get; set; }

    public required string DocumentNumber { get; set; }

    /// <summary>Idioma de la página (<c>es</c>, <c>en</c>, <c>pt</c>).</summary>
    public string Language { get; set; } = "es";

    /// <summary>Copia del documento (<see cref="Contracts.PublicDocument"/> en JSON).</summary>
    public required string Document { get; set; }

    public PublicLinkStatus Status { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }

    public int ViewCount { get; set; }
    public DateTimeOffset? FirstViewedAt { get; set; }
    public DateTimeOffset? LastViewedAt { get; set; }

    public DateTimeOffset? RespondedAt { get; set; }
    public string? SignerName { get; set; }
    public string? SignerEmail { get; set; }

    /// <summary>Firma dibujada por el cliente (<c>data:image/png;base64,…</c>).</summary>
    public string? Signature { get; set; }
    public string? Comment { get; set; }
    public string? ResponderIp { get; set; }
    public string? ResponderUserAgent { get; set; }
}

public static class PublicLinkEvents
{
    public const string Accepted = "hub.public_link.accepted";
    public const string Rejected = "hub.public_link.rejected";
}
