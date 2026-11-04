namespace VortexApi.Suite.Security;

/// <summary>
/// Roles y permisos de un sistema de la suite. El OrgAdmin asigna a cada miembro un rol del sistema en el hub
/// (<c>session.Access.Role</c>); cada sistema declara qué puede hacer cada rol. Como el rol viaja en la sesión, los
/// permisos funcionan sin conexión.
/// </summary>
/// <param name="Roles">Permisos de cada rol del sistema (nombres en minúsculas, como se asignan en el hub).</param>
/// <param name="DefaultRole">Rol de quien entra sin rol asignado.</param>
public sealed class PermissionMatrix(IReadOnlyDictionary<string, IReadOnlySet<string>> Roles, string DefaultRole)
{
    /// <summary>Rol con todos los permisos del sistema.</summary>
    public const string Administrator = "administrador";

    /// <summary>Roles de administración del hub que tienen todos los permisos en cualquier sistema.</summary>
    private static readonly string[] HubAdmins = ["OrgAdmin", "PlatformAdmin"];

    public IReadOnlyCollection<string> RoleNames => Roles.Keys.ToList();

    public string DefaultRole { get; } = DefaultRole;

    /// <summary>El rol efectivo: el del sistema si existe en la matriz; si no, el rol por defecto.</summary>
    public string EffectiveRole(string? productRole, string? hubRole) =>
        HubAdmins.Contains(hubRole) ? Administrator
        : productRole is { } r && Roles.ContainsKey(r.Trim().ToLowerInvariant()) ? r.Trim().ToLowerInvariant()
        : DefaultRole;

    public bool Can(string? productRole, string? hubRole, string permission)
    {
        var role = EffectiveRole(productRole, hubRole);
        return role == Administrator || (Roles.TryGetValue(role, out var permissions) && permissions.Contains(permission));
    }

    /// <summary>Construye la matriz: <c>("cajero", ["sale.create", …])</c>. El administrador no se declara: puede todo.</summary>
    public static PermissionMatrix Create(string defaultRole, params (string Role, string[] Permissions)[] roles)
    {
        var map = roles.ToDictionary(r => r.Role, r => (IReadOnlySet<string>)r.Permissions.ToHashSet(StringComparer.Ordinal));
        map.TryAdd(Administrator, new HashSet<string>());
        return new PermissionMatrix(map, defaultRole);
    }
}

/// <summary>Quién está operando, tal como lo necesitan los servicios de Core para validar permisos.</summary>
public interface IUserPermissions
{
    /// <summary>Si el usuario actual puede hacer <paramref name="permission"/> en este sistema.</summary>
    bool Can(string permission);
}

/// <summary>Se lanza cuando el usuario no tiene permiso; el mensaje es para mostrarlo tal cual.</summary>
public sealed class PermissionDeniedException(string message) : Exception(message);
