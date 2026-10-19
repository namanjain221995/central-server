using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.ScreenLocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class ScreenLockConfiguration : IEntityTypeConfiguration<ScreenLock>
{
    public void Configure(EntityTypeBuilder<ScreenLock> builder)
    {
        builder.ToTable("screen_locks");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.OrganizationId).IsRequired();
        builder.Property(l => l.Message).HasMaxLength(ScreenLock.MaxMessageLength).IsRequired();
        builder.Property(l => l.ContactLine).HasMaxLength(ScreenLock.MaxContactLineLength);

        builder.Property(l => l.TargetType)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // Deliberately no foreign key: the lock is history and must outlive the
        // group it once named. The name is resolved live when the history is read
        // and simply goes blank once the group is gone -- exactly as announcements do.
        builder.Property(l => l.DeviceGroupId);

        builder.Property(l => l.CreatedByUserId).IsRequired();
        builder.Property(l => l.CreatedByDisplay).HasMaxLength(ScreenLock.MaxDisplayLength).IsRequired();
        builder.Property(l => l.CreatedAt).IsRequired();
        builder.Property(l => l.UpdatedAt).IsRequired();

        // What the page asks: this organization's locks, newest first.
        builder.HasIndex(l => new { l.OrganizationId, l.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_screen_locks_organization_id_created_at");
    }
}

internal sealed class ScreenLockDeviceConfiguration : IEntityTypeConfiguration<ScreenLockDevice>
{
    public void Configure(EntityTypeBuilder<ScreenLockDevice> builder)
    {
        builder.ToTable("screen_lock_devices");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.ScreenLockId).IsRequired();
        builder.Property(d => d.DeviceId).IsRequired();
        builder.Property(d => d.Hostname).HasMaxLength(ScreenLockDevice.MaxHostnameLength).IsRequired();

        // Plain values, not foreign keys: this history outlives the tasks it points
        // at, and a removed task must take the pointer's target, never the row.
        builder.Property(d => d.EngageTaskId);
        builder.Property(d => d.ReleaseTaskId);

        builder.Property(d => d.SkipReason).HasMaxLength(ScreenLockDevice.MaxSkipReasonLength);
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt).IsRequired();

        builder.HasOne<ScreenLock>()
            .WithMany()
            .HasForeignKey(d => d.ScreenLockId)
            .OnDelete(DeleteBehavior.Cascade);

        // Cascade, exactly like announcement_devices: deleting a device takes its
        // per-device lock rows with it. What the lock said, who engaged it and to
        // how many machines survives on the lock row and in the audit log.
        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(d => d.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // A device is targeted once per lock, however it was selected.
        builder.HasIndex(d => new { d.ScreenLockId, d.DeviceId })
            .IsUnique()
            .HasDatabaseName("ux_screen_lock_devices_screen_lock_id_device_id");

        // The list and the device-level "locked" derivation both ask "which lock
        // rows does this device have".
        builder.HasIndex(d => d.DeviceId)
            .HasDatabaseName("ix_screen_lock_devices_device_id");
    }
}
