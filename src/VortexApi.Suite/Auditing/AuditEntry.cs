namespace VortexApi.Suite.Auditing;

/// <summary>
/// Una acción sensible que queda registrada: quién (usuario del hub), dónde (equipo), cuándo, qué hizo y sobre qué
/// documento. Cada sistema la guarda en su base local en la misma transacción que la acción; no se edita ni se borra.
/// </summary>
/// <param name="Action">Código estable de la acción (<see cref="AuditActions"/> o propio del sistema).</param>
/// <param name="Entity">Tipo de documento o dato (<c>sale</c>, <c>invoice</c>, <c>quote</c>…).</param>
/// <param name="Reference">Número visible del documento (<c>B001-000123</c>), para buscar sin abrir el Id.</param>
/// <param name="Detail">Motivo o cambio en lenguaje natural (<c>Precio 18.50 → 15.00</c>).</param>
public sealed record AuditEntry(
    Guid Id, DateTimeOffset At, Guid? UserId, string? UserName, string? Device, string Action, string Entity, string? EntityId,
    string? Reference, string? Detail);

/// <summary>Acciones comunes a los sistemas de la suite. Cada sistema puede agregar las suyas con el formato <c>entidad.accion</c>.</summary>
public static class AuditActions
{
    public const string Voided = "document.voided";
    public const string Cancelled = "document.cancelled";
    public const string Returned = "document.returned";
    public const string Deleted = "record.deleted";
    public const string PriceOverride = "price.override";
    public const string Discount = "discount.applied";
    public const string CashOpened = "cash.opened";
    public const string CashClosed = "cash.closed";
    public const string Issued = "document.issued";
    public const string PaymentRegistered = "payment.registered";
    public const string StageChanged = "stage.changed";
    public const string SettingsChanged = "settings.changed";
    public const string StockAdjusted = "stock.adjusted";
}

/// <summary>
/// Esquema SQLite de la bitácora, igual en los cuatro sistemas: cada uno lo agrega como migración de su base local.
/// </summary>
public static class AuditSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS audit_log (
            id TEXT PRIMARY KEY, at TEXT NOT NULL, user_id TEXT, user_name TEXT, device TEXT, action TEXT NOT NULL,
            entity TEXT NOT NULL, entity_id TEXT, reference TEXT, detail TEXT);
        CREATE INDEX IF NOT EXISTS ix_audit_log_at ON audit_log (at);
        CREATE INDEX IF NOT EXISTS ix_audit_log_entity ON audit_log (entity, entity_id);
        """;
}
