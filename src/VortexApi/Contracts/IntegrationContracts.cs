using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using VortexApi.Domain;

namespace VortexApi.Contracts;

/// <summary>
/// Evento a publicar. Envíe siempre <see cref="Id"/> (GUID generado por el sistema): si el envío se reintenta,
/// el hub devuelve el evento ya registrado en lugar de duplicarlo.
/// </summary>
public sealed record PublishEventRequest(
    [Required, MaxLength(100)] string Type,
    [Required] JsonElement Data,
    Guid? Id = null,
    DateTimeOffset? OccurredAt = null,
    [MaxLength(50)] string? SubjectType = null,
    [MaxLength(100)] string? SubjectId = null,
    [Range(1, 1000)] int SchemaVersion = 1);

public sealed record PublishBatchRequest([Required, MinLength(1), MaxLength(100)] List<PublishEventRequest> Events);

public sealed record PublishResult(Guid Id, long? Sequence, bool Created, string? ErrorCode, string? Error);

public sealed record EventDto(
    Guid Id, long Sequence, string Type, string Source, int SchemaVersion, string? SubjectType, string? SubjectId,
    DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, Guid? UserId, JsonElement Data);

public sealed record EventFeedResponse(IReadOnlyList<EventDto> Events, long NextCursor, bool HasMore);

// Webhooks

public sealed record WebhookRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(500)] string Url,
    [Required, MinLength(1), MaxLength(50)] List<string> EventTypes,
    [MaxLength(16)] string? ReceiverProduct = null,
    bool IsActive = true);

public sealed record WebhookDto(
    Guid Id, string Name, string Url, IReadOnlyList<string> EventTypes, string? ReceiverProduct, bool IsActive,
    DateTimeOffset CreatedAt, int PendingDeliveries, int FailedDeliveries);

/// <summary>El secreto se muestra solo aquí; guárdelo en el backend receptor para verificar la firma.</summary>
public sealed record WebhookSecretResponse(WebhookDto Webhook, string Secret);

public sealed record WebhookDeliveryDto(
    long Id, Guid EventId, string EventType, long EventSequence, DeliveryStatus Status, int Attempts,
    DateTimeOffset NextAttemptAt, DateTimeOffset? LastAttemptAt, int? LastStatusCode, string? LastError,
    DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt);

/// <summary>Cuerpo que recibe el webhook.</summary>
public sealed record WebhookPayload(
    Guid Id, long Sequence, string Type, string Source, Guid OrganizationId, int SchemaVersion, string? SubjectType,
    string? SubjectId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, JsonElement Data);

// Aplicaciones de integración

public sealed record CreateIntegrationClientRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(40)] string Slug,
    [Required, MinLength(1), MaxLength(20)] List<string> Scopes,
    DateTimeOffset? ExpiresAt = null);

public sealed record UpdateIntegrationClientRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MinLength(1), MaxLength(20)] List<string> Scopes,
    bool IsActive = true,
    DateTimeOffset? ExpiresAt = null);

public sealed record IntegrationClientDto(
    Guid Id, string Name, string Slug, string Source, string ClientId, string SecretHint, IReadOnlyList<string> Scopes,
    bool IsActive, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>Credenciales de la aplicación. El <see cref="ClientSecret"/> se muestra solo aquí.</summary>
public sealed record IntegrationCredentialsResponse(IntegrationClientDto Client, string ClientSecret, string TokenEndpoint);

public sealed record ScopeInfo(string Scope, string Description);
