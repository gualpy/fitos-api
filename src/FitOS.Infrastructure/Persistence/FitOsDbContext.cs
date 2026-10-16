using FitOS.Domain.Customers;
using FitOS.Domain.Memberships;
using FitOS.Domain.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FitOS.Infrastructure.Persistence;

/// <summary>
/// Entry point for queries. The schema is owned by SQL migrations (FitOS.Migrator), not by EF migrations,
/// because row-level security, triggers, exclusion constraints and composite tenant FKs live in SQL.
/// Every query is filtered by PostgreSQL row-level security; entities are mapped to the existing columns.
/// </summary>
public sealed class FitOsDbContext(DbContextOptions<FitOsDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<Membership> Memberships => Set<Membership>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");

        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("customers");
            b.HasKey(x => x.Id);
            Id(b.Property(x => x.Id), "id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            // Set by the BEFORE INSERT trigger (per-tenant correlative): never sent by EF.
            b.Property(x => x.ClientCode).HasColumnName("client_code").ValueGeneratedOnAdd();
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.IdentificationType).HasColumnName("identification_type");
            b.Property(x => x.Identification).HasColumnName("identification");
            b.Property(x => x.Email).HasColumnName("email").HasColumnType("citext");
            b.Property(x => x.Phone).HasColumnName("phone");
            b.Property(x => x.Gender).HasColumnName("gender").HasColumnType("character(1)");
            b.Property(x => x.BirthDate).HasColumnName("birth_date");
            b.Property(x => x.RfidCard).HasColumnName("rfid_card");
            b.Property(x => x.Status).HasColumnName("status").HasConversion(LowerEnum<CustomerStatus>());
            b.Property(x => x.JoinedAt).HasColumnName("joined_at");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<Plan>(b =>
        {
            b.ToTable("plans");
            b.HasKey(x => x.Id);
            Id(b.Property(x => x.Id), "id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.Description).HasColumnName("description");
            b.Property(x => x.Price).HasColumnName("price");
            b.Property(x => x.IvaRate).HasColumnName("iva_rate");
            b.Property(x => x.DurationMonths).HasColumnName("duration_months");
            b.Property(x => x.IsPromotion).HasColumnName("is_promotion");
            b.Property(x => x.IsActive).HasColumnName("is_active");
        });

        modelBuilder.Entity<Membership>(b =>
        {
            b.ToTable("memberships");
            b.HasKey(x => x.Id);
            Id(b.Property(x => x.Id), "id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.CustomerId).HasColumnName("customer_id");
            b.Property(x => x.PlanId).HasColumnName("plan_id");
            b.Property(x => x.StartsOn).HasColumnName("starts_on");
            b.Property(x => x.EndsOn).HasColumnName("ends_on");
            b.Property(x => x.Status).HasColumnName("status").HasConversion(LowerEnum<MembershipStatus>());
            b.Property(x => x.SaleId).HasColumnName("sale_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
        });
    }

    private static void Id(PropertyBuilder<Guid> property, string column) =>
        property.HasColumnName(column).ValueGeneratedNever();

    private static ValueConverter<TEnum, string> LowerEnum<TEnum>()
        where TEnum : struct, Enum =>
        new(value => value.ToString().ToLowerInvariant(), text => Enum.Parse<TEnum>(text, true));
}
