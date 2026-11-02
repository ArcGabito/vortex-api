using Microsoft.EntityFrameworkCore;
using VortexApi.Domain;
using VortexApi.Security;

namespace VortexApi.Data;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    /// <summary>Crea el primer PlatformAdmin si aún no existe ninguno.</summary>
    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }

    /// <summary>Claves de aplicación iniciales por producto (solo se aplican si el producto no tiene clave).</summary>
    public Dictionary<string, string> ProductKeys { get; set; } = [];

    /// <summary>
    /// Organización de demostración con los 4 sistemas habilitados y usuarios listos para iniciar sesión.
    /// Solo para desarrollo: se crea una vez, si aún no existe una organización con ese nombre.
    /// </summary>
    public DemoSeedOptions? Demo { get; set; }
}

public sealed class DemoSeedOptions
{
    public string OrganizationName { get; set; } = "Vortex Demo";
    public string TaxId { get; set; } = "20000000001";

    /// <summary>Contraseña común de los usuarios demo.</summary>
    public string Password { get; set; } = "";

    /// <summary>Usuarios a crear. Si el rol es <c>OrgAdmin</c> pueden además administrar la organización.</summary>
    public List<DemoUser> Users { get; set; } = [];
}

public sealed class DemoUser
{
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.User;
}

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services, SeedOptions options, ILogger logger)
    {
        var db = services.GetRequiredService<VortexDbContext>();
        var clock = services.GetRequiredService<TimeProvider>();

        if (db.Database.IsSqlServer())
            await db.Database.MigrateAsync();
        else
            await db.Database.EnsureCreatedAsync();

        foreach (var (code, name) in ProductCodes.Defaults)
        {
            var product = await db.Products.SingleOrDefaultAsync(p => p.Code == code);
            if (product is null)
            {
                product = new Product { Code = code, Name = name };
                db.Products.Add(product);
            }
            if (product.ClientKeyHash is null && options.ProductKeys.TryGetValue(code, out var key) && key.Length > 0)
                product.ClientKeyHash = Hashing.Sha256(key);
            if (product.ClientKeyHash is null)
                logger.LogWarning("El producto {Code} no tiene clave de aplicación. Genérela con POST /api/admin/products/{Code}/rotate-key.", code, code);
        }

        if (!await db.Users.AnyAsync(u => u.Role == UserRole.PlatformAdmin))
        {
            if (string.IsNullOrWhiteSpace(options.AdminEmail) || string.IsNullOrWhiteSpace(options.AdminPassword))
            {
                logger.LogWarning("No existe ningún PlatformAdmin. Configure Seed:AdminEmail y Seed:AdminPassword.");
            }
            else
            {
                var hasher = services.GetRequiredService<IPasswordHasher>();
                db.Users.Add(new User
                {
                    Email = options.AdminEmail,
                    NormalizedEmail = options.AdminEmail.ToUpperInvariant(),
                    FullName = "Administrador Vortex",
                    PasswordHash = hasher.Hash(options.AdminPassword),
                    Role = UserRole.PlatformAdmin,
                    CreatedAt = clock.GetUtcNow(),
                });
                logger.LogInformation("PlatformAdmin inicial creado: {Email}", options.AdminEmail);
            }
        }

        await db.SaveChangesAsync();

        if (options.Demo is { } demo)
            await SeedDemoAsync(services, db, clock, demo, logger);
    }

    private static async Task SeedDemoAsync(IServiceProvider services, VortexDbContext db, TimeProvider clock, DemoSeedOptions demo, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(demo.Password) || await db.Organizations.AnyAsync(o => o.Name == demo.OrganizationName))
            return;

        var now = clock.GetUtcNow();
        var org = new Organization { Name = demo.OrganizationName, TaxId = demo.TaxId, CreatedAt = now };
        db.Organizations.Add(org);

        // Los 4 sistemas habilitados, sin límites, con acceso automático para todos los usuarios.
        foreach (var product in await db.Products.ToListAsync())
            db.OrganizationProducts.Add(new OrganizationProduct
            {
                OrganizationId = org.Id, ProductId = product.Id, AutoAssignUsers = true, CreatedAt = now,
            });

        var hasher = services.GetRequiredService<IPasswordHasher>();
        foreach (var u in demo.Users.Where(u => !string.IsNullOrWhiteSpace(u.Email)))
            db.Users.Add(new User
            {
                OrganizationId = org.Id,
                Email = u.Email,
                NormalizedEmail = u.Email.ToUpperInvariant(),
                FullName = u.FullName,
                PasswordHash = hasher.Hash(demo.Password),
                Role = u.Role,
                CreatedAt = now,
            });

        await db.SaveChangesAsync();
        logger.LogInformation("Organización demo creada: {Name} ({Id}) con {Count} usuarios.", org.Name, org.Id, demo.Users.Count);
    }
}
