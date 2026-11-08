namespace VortexApi.Domain;

/// <summary>Sistema de la suite Vortex (ERP, CRM, WMS, POS).</summary>
public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Hash SHA-256 de la clave de aplicación que envía el sistema en <c>X-Vortex-Product-Key</c>.</summary>
    public string? ClientKeyHash { get; set; }
}

/// <summary>Empresa que usa uno o varios sistemas de la suite.</summary>
public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? TaxId { get; set; }
    public string? ContactEmail { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Último número de secuencia asignado en el feed de cambios de datos maestros.</summary>
    public long ChangeSequence { get; set; }

    /// <summary>Último número de secuencia asignado en el flujo de eventos de integración.</summary>
    public long EventSequence { get; set; }

    public List<User> Users { get; set; } = [];
    public List<OrganizationProduct> Products { get; set; } = [];
}

/// <summary>Idiomas de la interfaz de la suite (los mismos de <c>VortexApi.Suite.Regional.Languages</c>).</summary>
public static class UserLanguages
{
    public static readonly string[] Supported = ["es", "en", "pt"];

    /// <summary>Normaliza (<c>pt-BR</c> → <c>pt</c>); vacío = null; un idioma no soportado es un error de validación.</summary>
    public static string? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var code = value.Trim()[..Math.Min(2, value.Trim().Length)].ToLowerInvariant();
        return Supported.Contains(code)
            ? code
            : throw Infrastructure.ApiException.BadRequest(Infrastructure.ErrorCodes.Validation,
                $"Idioma no soportado: {value}. Usa {string.Join(", ", Supported)}.");
    }
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Null solo para usuarios <see cref="UserRole.PlatformAdmin"/>.</summary>
    public Guid? OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string FullName { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public bool IsActive { get; set; } = true;

    /// <summary>Idioma preferido (<c>es</c>, <c>en</c>, <c>pt</c>); null = el del país de la empresa. Viaja en la sesión.</summary>
    public string? Language { get; set; }

    /// <summary>PIN de caja (<see cref="Security.PinHasher"/>): cambio rápido de operador y autorizaciones en el POS. Null = sin PIN.</summary>
    public string? PinHash { get; set; }

    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>Cambia al modificar contraseña, rol o estado; invalida todos los tokens emitidos.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public List<ProductMember> Memberships { get; set; } = [];
}

/// <summary>
/// Sistema de la suite habilitado para una organización. Una organización puede usar uno, varios o los cuatro,
/// y cambiar de sistema (p. ej. de CRM a ERP) es habilitar uno y deshabilitar otro: usuarios y datos se conservan.
/// </summary>
public class OrganizationProduct
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public bool IsEnabled { get; set; } = true;
    public string? DisabledReason { get; set; }

    /// <summary>Si es true, todo usuario activo de la organización obtiene acceso en su primer inicio de sesión.</summary>
    public bool AutoAssignUsers { get; set; }

    /// <summary>Máximo de usuarios con acceso. Null = sin límite.</summary>
    public int? MaxUsers { get; set; }

    /// <summary>Máximo de equipos por usuario. Null = sin límite.</summary>
    public int? MaxDevicesPerUser { get; set; }

    /// <summary>Días que el sistema puede operar sin conexión con el último offline token.</summary>
    public int OfflineGraceDays { get; set; } = 7;

    /// <summary>Módulos habilitados (p. ej. <c>facturacion</c>, <c>nomina</c>). Los define cada sistema.</summary>
    public List<string> Features { get; set; } = [];

    /// <summary>Opcional, para accesos temporales (pruebas, hosting). Null = sin vencimiento.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<ProductMember> Members { get; set; } = [];
}

/// <summary>Usuario con acceso a un sistema de la suite.</summary>
public class ProductMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationProductId { get; set; }
    public OrganizationProduct OrganizationProduct { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Rol dentro del sistema (p. ej. <c>cajero</c> en POS). Lo interpreta cada sistema.</summary>
    public string? Role { get; set; }

    /// <summary>
    /// Sedes en las que trabaja el usuario dentro de este sistema (p. ej. el operario del almacén central). Vacío: todas.
    /// Lo aplica cada sistema (el WMS solo muestra y opera esas sedes).
    /// </summary>
    public List<Guid> SiteIds { get; set; } = [];

    public DateTimeOffset AddedAt { get; set; }

    public List<Device> Devices { get; set; } = [];
}

/// <summary>Equipo desde el que un miembro usa el sistema.</summary>
public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MemberId { get; set; }
    public ProductMember Member { get; set; } = null!;

    /// <summary>Hash SHA-256 del identificador de equipo enviado por el cliente.</summary>
    public required string DeviceHash { get; set; }
    public string? DeviceName { get; set; }
    public string? Platform { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string? LastIp { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string TokenHash { get; set; }

    /// <summary>Todos los tokens rotados de una misma sesión comparten familia; reutilizar uno viejo revoca la familia.</summary>
    public Guid FamilyId { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Null en sesiones de administración.</summary>
    public Guid? DeviceId { get; set; }
    public Device? Device { get; set; }
    public Guid? ProductId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string Action { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? OrganizationId { get; set; }
    public string? ProductCode { get; set; }
    public string? TargetId { get; set; }
    public bool Success { get; set; }
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
}
