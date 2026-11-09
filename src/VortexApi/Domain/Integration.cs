using System.Text.RegularExpressions;

namespace VortexApi.Domain;

/// <summary>
/// Evento de integración publicado por un sistema (p. ej. una venta en el POS) para que otros reaccionen
/// (el ERP la contabiliza, el WMS descuenta stock). Si un sistema no está instalado, nadie lo consume.
/// </summary>
public class IntegrationEvent
{
    /// <summary>Lo genera el sistema que publica: reintentar con el mismo Id no duplica el evento.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }

    /// <summary>Correlativa por organización, asignada en orden de commit.</summary>
    public long Sequence { get; set; }

    /// <summary>Tipo con formato <c>categoria.accion</c> (p. ej. <c>sale.completed</c>).</summary>
    public required string Type { get; set; }

    /// <summary>Primer segmento del tipo (<c>sale</c>); permite filtrar por <c>sale.*</c>.</summary>
    public required string Category { get; set; }

    /// <summary>Sistema que lo publicó (ERP, CRM, WMS, POS) o <c>HUB</c>.</summary>
    public required string Source { get; set; }

    public int SchemaVersion { get; set; } = 1;
    public string? SubjectType { get; set; }
    public string? SubjectId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? UserId { get; set; }
    public Guid? DeviceId { get; set; }

    /// <summary>Contenido del evento (objeto JSON), definido por quien lo publica.</summary>
    public required string Data { get; set; }
}

/// <summary>Suscripción de un backend a eventos de la organización, entregados por HTTP POST firmado.</summary>
public class WebhookSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }

    /// <summary>Patrones: tipo exacto, prefijo (<c>sale.*</c>, <c>master.item.*</c>) o <c>*</c>.</summary>
    public List<string> EventTypes { get; set; } = [];

    /// <summary>Sistema dueño del webhook; no recibe los eventos que él mismo publica.</summary>
    public string? ReceiverProduct { get; set; }

    /// <summary>Secreto compartido para la firma HMAC-SHA256 (se muestra una sola vez al crearlo o rotarlo).</summary>
    public required string Secret { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Aplicación de integración que registró el webhook (null si lo creó el OrgAdmin).</summary>
    public Guid? OwnerClientId { get; set; }

    public bool Accepts(IntegrationEvent e) =>
        IsActive && e.Source != ReceiverProduct && EventTypes.Any(p => EventTypePattern.Matches(p, e.Type));
}

public enum DeliveryStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
}

public class WebhookDelivery
{
    public long Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public WebhookSubscription Subscription { get; set; } = null!;
    public Guid EventId { get; set; }
    public IntegrationEvent Event { get; set; } = null!;

    public DeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Lease: la réplica que la toma la bloquea hasta esta hora para que otra no la envíe a la vez.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

public static partial class EventTypePattern
{
    /// <summary>Prefijos reservados para eventos que emite el propio hub.</summary>
    public static readonly string[] ReservedCategories = ["master", "hub"];

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex TypeRegex();

    [GeneratedRegex(@"^(\*|[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*\.\*|[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+)$")]
    private static partial Regex PatternRegex();

    public static bool IsValidType(string type) => type.Length <= 100 && TypeRegex().IsMatch(type);

    public static bool IsValidPattern(string pattern) => pattern.Length <= 100 && PatternRegex().IsMatch(pattern);

    public static string CategoryOf(string type) => type[..type.IndexOf('.')];

    public static bool Matches(string pattern, string type) =>
        pattern == "*" ||
        (pattern.EndsWith(".*") ? type.StartsWith(pattern[..^1], StringComparison.Ordinal) : pattern == type);
}

/// <summary>Eventos conocidos de la suite. Se puede publicar cualquier tipo con formato válido; este catálogo es la referencia.</summary>
public static class EventCatalog
{
    public sealed record Entry(string Type, string Description, string[] Producers, string[] Consumers);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("sale.completed", "Venta registrada en caja.", ["POS"], ["ERP", "WMS", "CRM"]),
        new("sale.voided", "Venta anulada.", ["POS"], ["ERP", "WMS"]),
        new("sale.returned", "Devolución de una venta.", ["POS"], ["ERP", "WMS"]),
        new("quote.accepted", "Cotización aceptada por el cliente.", ["CRM"], ["ERP"]),
        new("deal.won", "Oportunidad ganada.", ["CRM"], ["ERP"]),
        new("order.created", "Pedido de venta creado.", ["ERP", "CRM", "WMS"], ["WMS", "ERP"]),
        new("order.approved", "Pedido que superaba el límite de crédito del cliente, aprobado: ya se puede preparar y facturar.", ["ERP"], ["ERP", "WMS"]),
        new("order.picking_started", "Empezó la preparación del pedido en el almacén.", ["WMS"], ["WMS", "ERP", "CRM"]),
        new("order.cancelled", "Pedido cancelado antes de despacharse: libera la reserva de stock.", ["WMS", "ERP", "CRM"], ["WMS", "ERP", "CRM"]),
        new("invoice.issued", "Comprobante emitido.", ["ERP"], ["CRM", "POS"]),
        new("payment.received", "Cobro registrado.", ["ERP", "POS"], ["CRM", "ERP"]),
        new("cash.deposited", "Efectivo de una caja depositado en el banco (lo concilia la tesorería del ERP).", ["POS"], ["ERP"]),
        new("credit_note.issued", "Nota de crédito vinculada a un comprobante (anulación, devolución, descuento, corrección): rebaja su saldo.", ["ERP"], ["ERP"]),
        new("debit_note.issued", "Nota de débito vinculada a un comprobante (intereses, aumento de valor, penalidad): suma a su saldo.", ["ERP"], ["ERP"]),
        new("invoice.voided", "Comprobante anulado por error, sin cobros ni notas: el pedido o la venta de caja quedan libres.", ["ERP"], ["ERP"]),
        new("purchase_order.created", "Orden de compra a proveedor.", ["ERP"], ["WMS"]),
        new("purchase_order.cancelled", "Orden de compra cancelada (ya no se espera la mercadería).", ["ERP"], ["WMS", "ERP"]),
        new("goods.received", "Mercadería recibida en almacén.", ["WMS"], ["ERP"]),
        new("receipt.cancelled", "Recepción esperada anulada en el almacén (la orden de compra ya no se espera).", ["WMS"], ["WMS", "ERP"]),
        new("stock.adjusted", "Ajuste de inventario.", ["WMS"], ["ERP", "POS"]),
        new("stock.transferred", "Transferencia entre sedes o almacenes.", ["WMS"], ["ERP", "POS"]),
        new("shipment.dispatched", "Despacho enviado al cliente.", ["WMS"], ["ERP", "CRM"]),
        new("crm.record.upserted", "Replicación interna del CRM entre los equipos de una organización (contactos, oportunidades, cotizaciones, tareas).", ["CRM"], ["CRM"]),
        new("crm.record.deleted", "Registro propio del CRM eliminado en otro equipo de la organización.", ["CRM"], ["CRM"]),
        new("wms.record.upserted", "Replicación interna del WMS entre los equipos de una organización (ubicación y mínimo por artículo y sede, estado de lote, vida útil, mapa de ubicaciones).", ["WMS"], ["WMS"]),
        new("stock.relocated", "Traslado interno entre ubicaciones de una sede (no cambia el stock de la sede).", ["WMS"], ["WMS"]),
        new("erp.record.upserted", "Replicación interna del ERP entre los equipos de una organización (solicitudes de compra, facturas y pagos a proveedores, ajustes de compras).", ["ERP"], ["ERP"]),
        new("pos.record.upserted", "Replicación interna del POS entre las cajas de una organización (configuración de cada tienda: ticket, cierre ciego, cajón).", ["POS"], ["POS"]),
        new("hub.public_link.accepted", "El cliente aceptó con su firma un documento desde su enlace público (p. ej. una cotización): lo consume el sistema dueño del documento.", ["HUB"], ["CRM"]),
        new("hub.public_link.rejected", "El cliente rechazó un documento desde su enlace público.", ["HUB"], ["CRM"]),
        new("master.party.created|updated|deleted", "Cambio en clientes/proveedores (datos maestros).", ["HUB"], ["ERP", "CRM", "POS"]),
        new("master.item.created|updated|deleted", "Cambio en artículos (datos maestros).", ["HUB"], ["ERP", "WMS", "POS"]),
        new("master.site.created|updated|deleted", "Cambio en sedes/almacenes (datos maestros).", ["HUB"], ["ERP", "WMS", "POS"]),
        new("master.company.created|updated", "Cambio en los datos de la empresa.", ["HUB"], ["ERP", "CRM", "WMS", "POS"]),
        new("master.exchange_rate.created|updated|deleted", "Tipo de cambio del día (datos maestros).", ["HUB"], ["ERP", "CRM", "POS"]),
        new("master.price_list.created|updated|deleted", "Lista de precios (datos maestros).", ["HUB"], ["ERP", "CRM", "POS"]),
    ];
}
