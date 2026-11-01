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
    }
}
