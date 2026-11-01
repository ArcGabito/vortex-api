using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VortexApi.Domain;

namespace VortexApi.Data;

public class VortexDbContext(DbContextOptions<VortexDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<LicenseSeat> LicenseSeats => Set<LicenseSeat>();
    public DbSet<DeviceActivation> DeviceActivations => Set<DeviceActivation>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite (usado en pruebas) no sabe comparar/ordenar DateTimeOffset; se guarda como entero.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            builder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.ClientKeyHash).HasMaxLength(64);
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<Organization>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.TaxId).HasMaxLength(50);
            e.Property(x => x.ContactEmail).HasMaxLength(256);
        });

        b.Entity<User>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.NormalizedEmail).HasMaxLength(256);
            e.Property(x => x.FullName).HasMaxLength(200);
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.Property(x => x.SecurityStamp).HasMaxLength(64);
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.HasOne(x => x.Organization).WithMany(o => o.Users)
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<License>(e =>
        {
            e.Property(x => x.LicenseKey).HasMaxLength(64);
            e.Property(x => x.Plan).HasMaxLength(50);
            e.Property(x => x.StatusReason).HasMaxLength(500);
            e.HasIndex(x => x.LicenseKey).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.ProductId });
            e.HasOne(x => x.Organization).WithMany(o => o.Licenses)
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Product).WithMany()
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LicenseSeat>(e =>
        {
            e.HasIndex(x => new { x.LicenseId, x.UserId }).IsUnique();
            e.HasOne(x => x.License).WithMany(l => l.Seats)
                .HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(u => u.Seats)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DeviceActivation>(e =>
        {
            e.Property(x => x.DeviceHash).HasMaxLength(64);
            e.Property(x => x.DeviceName).HasMaxLength(200);
            e.Property(x => x.Platform).HasMaxLength(50);
            e.Property(x => x.LastIp).HasMaxLength(64);
            e.HasIndex(x => new { x.SeatId, x.DeviceHash });
            e.HasOne(x => x.Seat).WithMany(s => s.Devices)
                .HasForeignKey(x => x.SeatId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.DeviceActivation).WithMany()
                .HasForeignKey(x => x.DeviceActivationId).OnDelete(DeleteBehavior.SetNull);
        });

        // Los Id Guid se asignan en el constructor; así EF trata como nuevas (Added) las entidades
        // agregadas por navegación (p. ej. license.Seats.Add) en lugar de intentar un UPDATE.
        foreach (var entity in b.Model.GetEntityTypes())
        {
            var key = entity.FindPrimaryKey()?.Properties.SingleOrDefault();
            if (key?.ClrType == typeof(Guid))
                key.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }

        b.Entity<AuditLog>(e =>
        {
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.ProductCode).HasMaxLength(16);
            e.Property(x => x.TargetId).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.OrganizationId);
        });
    }
}
