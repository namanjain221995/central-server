using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class ChromeInstallationConfiguration : IEntityTypeConfiguration<ChromeInstallation>
{
    public void Configure(EntityTypeBuilder<ChromeInstallation> builder)
    {
        builder.ToTable("chrome_installations");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.DeviceId).IsRequired();

        // Stored as text so reordering the enum can never reinterpret history, the
        // same stance the BitLocker availability and the task types take.
        builder.Property(i => i.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        // The limits are the wire contract's (InventoryChromeInstallation.Max*),
        // mirrored by the entity's guards.
        builder.Property(i => i.Version).HasMaxLength(64);
        builder.Property(i => i.ExecutablePath).HasMaxLength(512);
        builder.Property(i => i.Architecture).HasMaxLength(16);
        builder.Property(i => i.Channel).HasMaxLength(16);
        builder.Property(i => i.InstallationScope).HasMaxLength(16);
        builder.Property(i => i.InstalledForUser).HasMaxLength(256);
        builder.Property(i => i.UpdaterVersion).HasMaxLength(64);
        builder.Property(i => i.CollectedAt).IsRequired();
        builder.Property(i => i.CreatedAt).IsRequired();
        builder.Property(i => i.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(i => i.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per device: the row is upserted, never duplicated, so that "has
        // this device ever reported Chrome" is a single existence check.
        builder.HasIndex(i => i.DeviceId)
            .IsUnique()
            .HasDatabaseName("ux_chrome_installations_device_id");
    }
}
