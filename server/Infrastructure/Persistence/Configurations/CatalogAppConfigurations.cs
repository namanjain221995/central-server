using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Software;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class CatalogAppConfiguration : IEntityTypeConfiguration<CatalogApp>
{
    /// <summary>Named so the service can tell this violation from any other.</summary>
    public const string UniquePackageIndex = "ix_catalog_apps_organization_id_package_id";

    public void Configure(EntityTypeBuilder<CatalogApp> builder)
    {
        builder.ToTable("catalog_apps");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.OrganizationId).IsRequired();
        builder.Property(a => a.PackageId).IsRequired();
        builder.Property(a => a.DisplayName).HasMaxLength(128).IsRequired();
        builder.Property(a => a.Description).HasMaxLength(1000);
        builder.Property(a => a.Category).HasMaxLength(64);
        builder.Property(a => a.Icon);
        builder.Property(a => a.Visibility).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(a => a.IsPublished).IsRequired();
        builder.Property(a => a.CreatedByUserId).IsRequired();
        builder.Property(a => a.CreatedByDisplay).HasMaxLength(256).IsRequired();
        builder.Property(a => a.PublishedByUserId).IsRequired();
        builder.Property(a => a.PublishedByDisplay).HasMaxLength(256).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt).IsRequired();

        // Restrict, not cascade: a package that is offered in the portal cannot
        // be deleted out from under it. Packages are withdrawn, never deleted,
        // so in practice this only ever refuses a mistake.
        builder.HasOne<SoftwarePackage>().WithMany()
            .HasForeignKey(a => a.PackageId).OnDelete(DeleteBehavior.Restrict);

        // One portal entry per package: two tiles installing the same bytes would
        // only confuse the person choosing between them.
        builder.HasIndex(a => new { a.OrganizationId, a.PackageId })
            .IsUnique()
            .HasDatabaseName(UniquePackageIndex);
    }
}

internal sealed class CatalogAppGroupConfiguration : IEntityTypeConfiguration<CatalogAppGroup>
{
    public void Configure(EntityTypeBuilder<CatalogAppGroup> builder)
    {
        builder.ToTable("catalog_app_groups");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.CatalogAppId).IsRequired();
        builder.Property(g => g.DeviceGroupId).IsRequired();

        // Deleting a group or an app removes the link. A removed group can only
        // ever narrow who sees an app, never widen it.
        builder.HasOne<CatalogApp>().WithMany()
            .HasForeignKey(g => g.CatalogAppId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DeviceGroup>().WithMany()
            .HasForeignKey(g => g.DeviceGroupId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(g => new { g.CatalogAppId, g.DeviceGroupId })
            .IsUnique()
            .HasDatabaseName("ix_catalog_app_groups_app_id_group_id");
    }
}

internal sealed class CatalogInstallRequestConfiguration : IEntityTypeConfiguration<CatalogInstallRequest>
{
    public void Configure(EntityTypeBuilder<CatalogInstallRequest> builder)
    {
        builder.ToTable("catalog_install_requests");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.OrganizationId).IsRequired();
        builder.Property(r => r.DeviceId).IsRequired();
        builder.Property(r => r.CatalogAppId).IsRequired();
        builder.Property(r => r.AppName).HasMaxLength(128).IsRequired();
        builder.Property(r => r.RequestedBy).HasMaxLength(256);
        builder.Property(r => r.Outcome).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        // History goes with the device, and with nothing else: no FK to the app
        // or the task, both of which are removed on their own schedules.
        builder.HasOne<Domain.Devices.Device>().WithMany()
            .HasForeignKey(r => r.DeviceId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.OrganizationId, r.CreatedAt })
            .HasDatabaseName("ix_catalog_install_requests_org_created");
    }
}
