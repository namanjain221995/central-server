using EndpointPlatform.Domain.Peripherals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class UsbDeviceConfiguration : IEntityTypeConfiguration<UsbDevice>
{
    public void Configure(EntityTypeBuilder<UsbDevice> builder)
    {
        builder.ToTable("usb_devices");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.InstanceId).HasMaxLength(512).IsRequired();
        builder.Property(d => d.VendorId).HasMaxLength(8);
        builder.Property(d => d.ProductId).HasMaxLength(8);
        builder.Property(d => d.SerialNumber).HasMaxLength(128);
        builder.Property(d => d.Manufacturer).HasMaxLength(256);
        builder.Property(d => d.Product).HasMaxLength(256);
        builder.Property(d => d.HardwareIds).HasMaxLength(1024);
        builder.Property(d => d.EnforcementError).HasMaxLength(512);

        // Enums as text, like every other enum in the schema: reordering a
        // member can then never silently reinterpret stored history, and a
        // policy column that reads "Restricted" is legible in a psql session
        // during an incident.
        builder.Property(d => d.DeviceClass).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(d => d.Policy).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.EnforcedPolicy).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.EnforcementStatus).HasConversion<string>().HasMaxLength(16);

        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt).IsRequired();

        // The identity of a USB device on a machine. Re-plugging the same stick
        // must update the existing row rather than accumulate a new one per
        // insertion, and a grant is scoped to exactly this pair.
        builder.HasIndex(d => new { d.DeviceId, d.InstanceId })
            .IsUnique()
            .HasDatabaseName("ix_usb_devices_device_instance");

        // Drives the fleet-wide "what has live access right now" view.
        builder.HasIndex(d => new { d.OrganizationId, d.Policy })
            .HasDatabaseName("ix_usb_devices_organization_policy");

        builder.HasOne<Domain.Devices.Device>()
            .WithMany()
            .HasForeignKey(d => d.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UsbAccessRequestConfiguration : IEntityTypeConfiguration<UsbAccessRequest>
{
    public void Configure(EntityTypeBuilder<UsbAccessRequest> builder)
    {
        builder.ToTable("usb_access_requests");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.InstanceId).HasMaxLength(512).IsRequired();
        builder.Property(r => r.Justification).HasMaxLength(1000).IsRequired();
        builder.Property(r => r.DecidedByDisplay).HasMaxLength(256);
        builder.Property(r => r.DecisionNote).HasMaxLength(1000);

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.Source).HasConversion<string>().HasMaxLength(16).IsRequired();

        // Text, and required. Rows written before read/write grants existed are
        // backfilled to ReadOnly by the migration, which is the truth for them:
        // read-only was the only level the platform could issue at the time.
        builder.Property(r => r.GrantedPolicy).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Property(r => r.RequestedAt).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        // Two hot paths: the per-device history, and the sweeper looking for
        // approved grants whose deadline has passed.
        builder.HasIndex(r => new { r.DeviceId, r.Status })
            .HasDatabaseName("ix_usb_access_requests_device_status");

        builder.HasIndex(r => new { r.Status, r.ExpiresAt })
            .HasDatabaseName("ix_usb_access_requests_status_expires");

        // The request row outlives the USB device row on purpose — "who granted
        // access to what, when" must stay answerable after inventory pruning —
        // so there is no FK to usb_devices, only to the endpoint.
        builder.HasOne<Domain.Devices.Device>()
            .WithMany()
            .HasForeignKey(r => r.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
