using EndpointPlatform.Domain.Announcements;
using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> builder)
    {
        builder.ToTable("announcements");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.OrganizationId).IsRequired();
        builder.Property(a => a.Title).HasMaxLength(Announcement.MaxTitleLength).IsRequired();
        builder.Property(a => a.Message).HasMaxLength(Announcement.MaxMessageLength).IsRequired();

        builder.Property(a => a.TargetType)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // Deliberately no foreign key: the announcement is history and must
        // outlive the group it once named. The name is resolved live when the
        // history is read and simply goes blank once the group is gone.
        builder.Property(a => a.DeviceGroupId);

        builder.Property(a => a.CreatedByUserId).IsRequired();
        builder.Property(a => a.CreatedByDisplay).HasMaxLength(Announcement.MaxDisplayLength).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt).IsRequired();

        // What the page asks: this organization's announcements, newest first.
        builder.HasIndex(a => new { a.OrganizationId, a.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_announcements_organization_id_created_at");
    }
}

internal sealed class AnnouncementDeviceConfiguration : IEntityTypeConfiguration<AnnouncementDevice>
{
    public void Configure(EntityTypeBuilder<AnnouncementDevice> builder)
    {
        builder.ToTable("announcement_devices");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.AnnouncementId).IsRequired();
        builder.Property(d => d.DeviceId).IsRequired();
        builder.Property(d => d.Hostname).HasMaxLength(AnnouncementDevice.MaxHostnameLength).IsRequired();

        // A plain value, not a foreign key: this history outlives the tasks it
        // points at, and a removed task must take the pointer's target, never
        // the row.
        builder.Property(d => d.TaskId);

        builder.Property(d => d.SkipReason).HasMaxLength(AnnouncementDevice.MaxSkipReasonLength);
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt).IsRequired();

        builder.HasOne<Announcement>()
            .WithMany()
            .HasForeignKey(d => d.AnnouncementId)
            .OnDelete(DeleteBehavior.Cascade);

        // Cascade, exactly like restart_schedule_devices: deleting a device
        // takes its per-device delivery rows with it. What the broadcast said,
        // who sent it and to how many machines survives on the announcement row
        // and in the audit log; the hostname copied onto each row only serves
        // the rows while they live.
        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(d => d.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // A device is targeted once per announcement, however it was selected.
        builder.HasIndex(d => new { d.AnnouncementId, d.DeviceId })
            .IsUnique()
            .HasDatabaseName("ux_announcement_devices_announcement_id_device_id");
    }
}
