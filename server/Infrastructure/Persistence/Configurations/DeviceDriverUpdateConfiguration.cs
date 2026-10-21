using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceDriverUpdateConfiguration : IEntityTypeConfiguration<DeviceDriverUpdate>
{
    public void Configure(EntityTypeBuilder<DeviceDriverUpdate> builder)
    {
        builder.ToTable("device_driver_updates");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.DeviceId).IsRequired();
        builder.Property(u => u.UpdateId).HasMaxLength(64).IsRequired();
        builder.Property(u => u.Revision).IsRequired();
        builder.Property(u => u.Title).HasMaxLength(512).IsRequired();
        builder.Property(u => u.DriverClass).HasMaxLength(128);
        builder.Property(u => u.DriverProvider).HasMaxLength(256);
        builder.Property(u => u.IsPreview).IsRequired();
        builder.Property(u => u.CollectedAt).IsRequired();
        builder.Property(u => u.CreatedAt).IsRequired();
        builder.Property(u => u.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(u => u.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The per-device list is read by device id, and the fleet overview groups
        // by it; the one index both questions share.
        builder.HasIndex(u => u.DeviceId)
            .HasDatabaseName("ix_device_driver_updates_device_id");

        // One pending row per Windows Update update per device. The scan replaces
        // the device's rows wholesale, so this is defence against an agent reporting
        // the same update twice, not a constraint callers race on.
        builder.HasIndex(u => new { u.DeviceId, u.UpdateId })
            .IsUnique()
            .HasDatabaseName("ux_device_driver_updates_device_update");
    }
}
