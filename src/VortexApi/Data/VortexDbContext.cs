using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VortexApi.Domain;

namespace VortexApi.Data;

public class VortexDbContext(DbContextOptions<VortexDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<OrganizationProduct> OrganizationProducts => Set<OrganizationProduct>();
    public DbSet<ProductMember> ProductMembers => Set<ProductMember>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Party> Parties => Set<Party>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<CompanyProfile> CompanyProfiles => Set<CompanyProfile>();
    public DbSet<ChangeEntry> ChangeEntries => Set<ChangeEntry>();

    public DbSet<IntegrationEvent> IntegrationEvents => Set<IntegrationEvent>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<IntegrationClient> IntegrationClients => Set<IntegrationClient>();

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
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.PinHash).HasMaxLength(200);
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.Property(x => x.SecurityStamp).HasMaxLength(64);
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.HasOne(x => x.Organization).WithMany(o => o.Users)
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OrganizationProduct>(e =>
        {
            e.Property(x => x.DisabledReason).HasMaxLength(500);
            e.HasIndex(x => new { x.OrganizationId, x.ProductId }).IsUnique();
            e.HasOne(x => x.Organization).WithMany(o => o.Products)
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Product).WithMany()
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProductMember>(e =>
        {
            e.Property(x => x.Role).HasMaxLength(50);
            e.HasIndex(x => new { x.OrganizationProductId, x.UserId }).IsUnique();
            e.HasOne(x => x.OrganizationProduct).WithMany(p => p.Members)
                .HasForeignKey(x => x.OrganizationProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(u => u.Memberships)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Device>(e =>
        {
            e.Property(x => x.DeviceHash).HasMaxLength(64);
            e.Property(x => x.DeviceName).HasMaxLength(200);
            e.Property(x => x.Platform).HasMaxLength(50);
            e.Property(x => x.LastIp).HasMaxLength(64);
            e.HasIndex(x => new { x.MemberId, x.DeviceHash });
            e.HasOne(x => x.Member).WithMany(m => m.Devices)
                .HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Device).WithMany()
                .HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);
        });

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

        ConfigureMasterData(b);
        ConfigureIntegration(b);

        b.Entity<IntegrationClient>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Slug).HasMaxLength(40);
            e.Property(x => x.ClientId).HasMaxLength(64);
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.Property(x => x.SecretHint).HasMaxLength(8);
            e.Property(x => x.SecurityStamp).HasMaxLength(64);
            e.Ignore(x => x.Source);
            e.HasIndex(x => x.ClientId).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique();
            e.HasOne(x => x.Organization).WithMany()
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        // Los Id Guid se asignan en el constructor; así EF trata como nuevas (Added) las entidades
        // agregadas por navegación (p. ej. product.Members.Add) en lugar de intentar un UPDATE.
        foreach (var entity in b.Model.GetEntityTypes().Where(t => !t.IsOwned()))
        {
            var key = entity.FindPrimaryKey()?.Properties.SingleOrDefault();
            if (key?.ClrType == typeof(Guid))
                key.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }
    }

    private static void ConfigureMasterData(ModelBuilder b)
    {
        // Unicidad solo entre registros no borrados (borrado lógico).
        const string NotDeleted = "[DeletedAt] IS NULL";

        b.Entity<Party>(e =>
        {
            MasterEntity(e);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.TradeName).HasMaxLength(200);
            e.Property(x => x.DocumentType).HasMaxLength(20);
            e.Property(x => x.DocumentNumber).HasMaxLength(20);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Phone).HasMaxLength(50);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.CreditLimit).HasPrecision(18, 2);
            e.Property(x => x.DiscountPercent).HasPrecision(5, 2);
            e.Property(x => x.OwnerName).HasMaxLength(200);
            Address(e);
            e.HasIndex(x => new { x.OrganizationId, x.DocumentType, x.DocumentNumber }).IsUnique()
                .HasFilter($"{NotDeleted} AND [DocumentNumber] IS NOT NULL");
            e.HasIndex(x => new { x.OrganizationId, x.Name });
        });

        b.Entity<Item>(e =>
        {
            MasterEntity(e);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Unit).HasMaxLength(20);
            e.Property(x => x.Barcode).HasMaxLength(64);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.Price).HasPrecision(18, 4);
            e.Property(x => x.CurrencyCode).HasMaxLength(3);
            e.Property(x => x.TaxCode).HasMaxLength(20);
            e.HasIndex(x => new { x.OrganizationId, x.Sku }).IsUnique().HasFilter(NotDeleted);
            e.HasIndex(x => new { x.OrganizationId, x.Barcode });
            e.Property(x => x.UnitCode).HasMaxLength(10);
            e.Property(x => x.WeightKg).HasPrecision(18, 4);
            e.OwnsMany(x => x.Presentations, p =>
            {
                p.ToJson();
                p.Property(x => x.Factor).HasPrecision(18, 6);
            });
        });

        b.Entity<Site>(e =>
        {
            MasterEntity(e);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(50);
            Address(e);
            e.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasFilter(NotDeleted);
        });

        b.Entity<ExchangeRate>(e =>
        {
            MasterEntity(e);
            e.Property(x => x.FromCurrency).HasMaxLength(3);
            e.Property(x => x.ToCurrency).HasMaxLength(3);
            e.Property(x => x.Rate).HasPrecision(18, 8);
            e.Property(x => x.BuyRate).HasPrecision(18, 8);
            e.Property(x => x.SellRate).HasPrecision(18, 8);
            e.Property(x => x.Source).HasMaxLength(20);
            e.HasIndex(x => new { x.OrganizationId, x.FromCurrency, x.ToCurrency, x.Date }).IsUnique().HasFilter(NotDeleted);
        });

        b.Entity<CompanyProfile>(e =>
        {
            MasterEntity(e);
            e.Property(x => x.LegalName).HasMaxLength(200);
            e.Property(x => x.TradeName).HasMaxLength(200);
            e.Property(x => x.TaxId).HasMaxLength(20);
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.Property(x => x.CurrencyCode).HasMaxLength(3);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.DefaultTaxRate).HasPrecision(5, 2);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Phone).HasMaxLength(50);
            e.Property(x => x.AddressLine).HasMaxLength(300);
            e.Property(x => x.District).HasMaxLength(100);
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.Region).HasMaxLength(100);
            e.Property(x => x.PostalCode).HasMaxLength(20);
            e.HasIndex(x => x.OrganizationId).IsUnique();
        });

        b.Entity<ChangeEntry>(e =>
        {
            e.Property(x => x.EntityType).HasMaxLength(32);
            e.Property(x => x.Source).HasMaxLength(64);
            e.HasIndex(x => new { x.OrganizationId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.EntityType, x.EntityId });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureIntegration(ModelBuilder b)
    {
        b.Entity<IntegrationEvent>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Category).HasMaxLength(50);
            e.Property(x => x.Source).HasMaxLength(64);
            e.Property(x => x.SubjectType).HasMaxLength(50);
            e.Property(x => x.SubjectId).HasMaxLength(100);
            e.HasIndex(x => new { x.OrganizationId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Category, x.Sequence });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WebhookSubscription>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Url).HasMaxLength(500);
            e.Property(x => x.ReceiverProduct).HasMaxLength(64);
            e.Property(x => x.Secret).HasMaxLength(100);
            e.HasIndex(x => x.OrganizationId);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.SubscriptionId, x.Id });
            e.HasOne(x => x.Subscription).WithMany()
                .HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Event).WithMany()
                .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void MasterEntity<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e)
        where T : MasterEntity
    {
        e.Property(x => x.Version).IsConcurrencyToken();
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.OrganizationId, x.UpdatedAt });
    }

    private static void Address<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e)
        where T : class
    {
        e.Property<string?>("AddressLine").HasMaxLength(300);
        e.Property<string?>("District").HasMaxLength(100);
        e.Property<string?>("City").HasMaxLength(100);
        e.Property<string?>("Region").HasMaxLength(100);
        e.Property<string?>("PostalCode").HasMaxLength(20);
        e.Property<string?>("CountryCode").HasMaxLength(2);
    }
}
