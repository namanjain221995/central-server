using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceSecurityUpdateInstallRecordConfiguration
    : IEntityTypeConfiguration<DeviceSecurityUpdateInstallRecord>
{
    public void Configure(EntityTypeBuilder<DeviceSecurityUpdateInstallRecord> builder)
    {
        builder.ToTable("device_security_update_install_records");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.DeviceId).IsRequired();
        builder.Property(r => r.UpdateId).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Title).HasMaxLength(512).IsRequired();

        // Text, like the task type and status: a row read by a person in psql, or
        // by a later build with a reordered enum, still says what it means.
        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(r => r.Attempts).IsRequired();
        builder.Property(r => r.FirstAttemptAt).IsRequired();
        builder.Property(r => r.LastAttemptAt).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(r => r.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One memory per update per device, and the index every read uses: the
        // per-device list, the overview's per-device counts, the reconcile after a
        // scan, and the heartbeat's "any reboot-pending installs here?" probe all
        // lead with the device id.
        builder.HasIndex(r => new { r.DeviceId, r.UpdateId })
            .IsUnique()
            .HasDatabaseName("ux_device_security_update_install_records_device_update");

        // The heartbeat's level-triggered probe runs on EVERY heartbeat and asks two
        // questions of this table: "does this device hold an install waiting for a
        // restart?" and "the latest install still unverified?" -- an EXISTS and a
        // MAX on (device, status). Its own index keeps each a single index probe
        // whatever else the device has remembered.
        builder.HasIndex(r => new { r.DeviceId, r.Status })
            .HasDatabaseName("ix_device_security_update_install_records_device_status");
    }
}

internal sealed class DeviceSecurityUpdateSuppressionConfiguration
    : IEntityTypeConfiguration<DeviceSecurityUpdateSuppression>
{
    public void Configure(EntityTypeBuilder<DeviceSecurityUpdateSuppression> builder)
    {
        builder.ToTable("device_security_update_suppressions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.DeviceId).IsRequired();
        builder.Property(s => s.UpdateId).HasMaxLength(64).IsRequired();
        builder.Property(s => s.Title).HasMaxLength(512).IsRequired();

        // Required, unlike the driver suppression's: hiding a security patch must
        // say why, and the database holds that line too.
        builder.Property(s => s.Reason).HasMaxLength(DeviceSecurityUpdateSuppression.MaxReasonLength).IsRequired();
        builder.Property(s => s.CreatedByUserId).IsRequired();
        builder.Property(s => s.CreatedByDisplay).HasMaxLength(320).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(s => s.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Hiding is idempotent: a second hide of the same update is a no-op, and the
        // database is what makes that true when two operators click at once.
        builder.HasIndex(s => new { s.DeviceId, s.UpdateId })
            .IsUnique()
            .HasDatabaseName("ux_device_security_update_suppressions_device_update");
    }
}
