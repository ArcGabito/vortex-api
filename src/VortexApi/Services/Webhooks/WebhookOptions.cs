namespace VortexApi.Services.Webhooks;

public sealed class WebhookOptions
{
    public const string Section = "Webhooks";

    /// <summary>Ejecutar el despachador en segundo plano en esta instancia.</summary>
    public bool DispatcherEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 50;
    public int MaxAttempts { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Permitir destinos en redes privadas (10.x, 192.168.x, localhost…). Desactivado por defecto para evitar SSRF;
    /// actívelo solo en instalaciones on-premise donde los backends están en la red interna.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>Permitir URLs http:// (solo desarrollo).</summary>
    public bool AllowInsecureHttp { get; set; }
}
