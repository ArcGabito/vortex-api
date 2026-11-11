namespace VortexApi.Fiscal;

public sealed class FiscalOptions
{
    public const string Section = "Fiscal";

    /// <summary>Ejecutar en esta instancia el procesador que firma y envía los comprobantes pendientes.</summary>
    public bool ProcessorEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 20;

    /// <summary>Intentos ante errores pasajeros (sin conexión, servicio caído) antes de dejarlo en <see cref="FiscalStatus.Failed"/>.</summary>
    public int MaxAttempts { get; set; } = 12;
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Llave para cifrar certificados y claves (si falta, se deriva de la llave del hub).</summary>
    public string? SecretKey { get; set; }
}
