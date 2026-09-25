using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class ChromeExtensionConfiguration : IEntityTypeConfiguration<ChromeExtension>
{
    public void Configure(EntityTypeBuilder<ChromeExtension> builder)
    {
        builder.ToTable("chrome_extensions");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.DeviceId).IsRequired();
        builder.Property(e => e.ChromeProfileId).IsRequired();

        // The limits are the wire contract's (InventoryChromeExtension.Max*),
        // mirrored by the entity's guards. The id is exactly 32 characters, but a
        // fixed-width column would pad rather than refuse, so the entity holds the
        // exact rule and the column only the ceiling.
        builder.Property(e => e.ExtensionId).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(256);
        builder.Property(e => e.Version).HasMaxLength(64);

        // Stored as text so reordering the enum can never reinterpret history.
        builder.Property(e => e.InstallType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.IsManaged).IsRequired();
        builder.Property(e => e.UpdateUrl).HasMaxLength(512);
        builder.Property(e => e.CollectedAt).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ChromeProfile>()
            .WithMany()
            .HasForeignKey(e => e.ChromeProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Device-scoped reads (the whole Chrome section of one device) and the
        // wholesale replace on ingestion both select by device.
        builder.HasIndex(e => e.DeviceId)
            .HasDatabaseName("ix_chrome_extensions_device_id");

        // Backs the fleet-wide question this feature exists to answer: which
        // devices have extension X.
        builder.HasIndex(e => e.ExtensionId)
            .HasDatabaseName("ix_chrome_extensions_extension_id");

        // Chrome records one entry per extension per profile. Ingestion dedupes on
        // the same pair before it writes; the constraint is what makes that hold
        // under a concurrent upload.
        builder.HasIndex(e => new { e.ChromeProfileId, e.ExtensionId })
            .IsUnique()
            .HasDatabaseName("ux_chrome_extensions_profile_extension");
    }
}
