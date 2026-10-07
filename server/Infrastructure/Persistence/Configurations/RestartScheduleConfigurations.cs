using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Restarts;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class RestartScheduleConfiguration : IEntityTypeConfiguration<RestartSchedule>
{
    public void Configure(EntityTypeBuilder<RestartSchedule> builder)
    {
        builder.ToTable("restart_schedules");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.OrganizationId).IsRequired();
        builder.Property(s => s.DeviceGroupId).IsRequired();
        builder.Property(s => s.RequestedDelaySeconds).IsRequired();
        builder.Property(s => s.WarningSeconds).IsRequired();
        builder.Property(s => s.RestartAt).IsRequired();
        builder.Property(s => s.DispatchAt).IsRequired();

        builder.Property(s => s.Status)
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired();

        builder.Property(s => s.CreatedByUserId).IsRequired();
        builder.Property(s => s.CreatedByDisplay).HasMaxLength(RestartSchedule.MaxDisplayLength).IsRequired();
        builder.Property(s => s.CancelledByDisplay).HasMaxLength(RestartSchedule.MaxDisplayLength);
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        // The sweeper and an administrator's cancel can meet on the same row;
        // the row version decides, and the loser re-reads.
        builder.Property<uint>("xmin").HasColumnType("xid").IsRowVersion();

        // A schedule outlives its group only as history: deleting the group
        // deletes the schedules that named it, as it deletes its scopes.
        builder.HasOne<DeviceGroup>()
            .WithMany()
            .HasForeignKey(s => s.DeviceGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // What the sweeper asks every tick.
        builder.HasIndex(s => new { s.Status, s.DispatchAt })
            .HasDatabaseName("ix_restart_schedules_status_dispatch_at");

        // What the page asks: this group's schedules, newest first.
        builder.HasIndex(s => new { s.DeviceGroupId, s.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_restart_schedules_device_group_id_created_at");

        // One pending schedule per group. The service checks first; this makes
        // two simultaneous requests resolve to one schedule rather than two.
        builder.HasIndex(s => s.DeviceGroupId)
            .IsUnique()
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ux_restart_schedules_pending_per_group");
    }
}

internal sealed class RestartScheduleExclusionConfiguration : IEntityTypeConfiguration<RestartScheduleExclusion>
{
    public void Configure(EntityTypeBuilder<RestartScheduleExclusion> builder)
    {
        builder.ToTable("restart_schedule_exclusions");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.ScheduleId).IsRequired();
        builder.Property(e => e.DeviceId).IsRequired();
        builder.Property(e => e.Hostname).HasMaxLength(255).IsRequired();
        builder.Property(e => e.ExcludedByUserId).IsRequired();
        builder.Property(e => e.ExcludedByDisplay).HasMaxLength(RestartSchedule.MaxDisplayLength).IsRequired();
        builder.Property(e => e.ExcludedAt).IsRequired();

        builder.HasOne<RestartSchedule>()
            .WithMany()
            .HasForeignKey(e => e.ScheduleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Excluding a device twice is one exclusion.
        builder.HasIndex(e => new { e.ScheduleId, e.DeviceId })
            .IsUnique()
            .HasDatabaseName("ux_restart_schedule_exclusions_schedule_id_device_id");
    }
}

internal sealed class RestartScheduleDeviceConfiguration : IEntityTypeConfiguration<RestartScheduleDevice>
{
    public void Configure(EntityTypeBuilder<RestartScheduleDevice> builder)
    {
        builder.ToTable("restart_schedule_devices");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.ScheduleId).IsRequired();
        builder.Property(d => d.DeviceId).IsRequired();
        builder.Property(d => d.Hostname).HasMaxLength(255).IsRequired();

        builder.Property(d => d.DispatchOutcome)
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired();

        builder.Property(d => d.CancelOutcome)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(d => d.CancelRequestedByDisplay).HasMaxLength(RestartSchedule.MaxDisplayLength);

        builder.HasOne<RestartSchedule>()
            .WithMany()
            .HasForeignKey(d => d.ScheduleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(d => d.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The tasks are history in their own right; a row keeps pointing at
        // them, and loses the pointer rather than itself if one is ever removed.
        builder.HasOne<DeviceTask>()
            .WithMany()
            .HasForeignKey(d => d.RestartTaskId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<DeviceTask>()
            .WithMany()
            .HasForeignKey(d => d.CancelTaskId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(d => new { d.ScheduleId, d.DeviceId })
            .IsUnique()
            .HasDatabaseName("ux_restart_schedule_devices_schedule_id_device_id");
    }
}
