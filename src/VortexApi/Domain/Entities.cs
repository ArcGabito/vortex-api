namespace VortexApi.Domain;

/// <summary>Sistema de la suite Vortex (ERP, CRM, WMS, POS) que consume la API.</summary>
public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Hash SHA-256 de la clave de aplicación que envía el cliente en <c>X-Vortex-Product-Key</c>.</summary>
    public string? ClientKeyHash { get; set; }
}

/// <summary>Cliente (empresa) que compra licencias.</summary>
public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? TaxId { get; set; }
    public string? ContactEmail { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public List<User> Users { get; set; } = [];
    public List<License> Licenses { get; set; } = [];
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

    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>Cambia al modificar contraseña, rol o estado; invalida todos los tokens emitidos.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public List<LicenseSeat> Seats { get; set; } = [];
}

public class License
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string LicenseKey { get; set; }

    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public required string Plan { get; set; }
    public int MaxSeats { get; set; }
    public int MaxDevicesPerUser { get; set; } = 2;

    /// <summary>Días que la app puede operar sin conexión con el último license token.</summary>
    public int OfflineGraceDays { get; set; } = 3;

    public List<string> Features { get; set; } = [];

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public LicenseStatus Status { get; set; } = LicenseStatus.Active;
    public string? StatusReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<LicenseSeat> Seats { get; set; } = [];
}

/// <summary>Asignación de un puesto de la licencia a un usuario.</summary>
public class LicenseSeat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset AssignedAt { get; set; }

    public List<DeviceActivation> Devices { get; set; } = [];
}

/// <summary>Equipo en el que un usuario activó su puesto.</summary>
public class DeviceActivation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SeatId { get; set; }
    public LicenseSeat Seat { get; set; } = null!;

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
    public Guid? DeviceActivationId { get; set; }
    public DeviceActivation? DeviceActivation { get; set; }
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
