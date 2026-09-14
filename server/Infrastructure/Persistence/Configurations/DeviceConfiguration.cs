using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("devices");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.OrganizationId).IsRequired();

        builder.Property(d => d.Hostname)
            .HasMaxLength(253)
            .IsRequired();

        // Nullable on purpose: "no label" is a real state, distinct from an empty
        // one, and it is what makes the hostname fallback unambiguous.
        builder.Property(d => d.DisplayName)
            .HasMaxLength(128);

        builder.Property(d => d.MachineIdentifier)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(d => d.AgentVersion)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(d => d.OperatingSystem)
            .HasMaxLength(256);

        builder.Property(d => d.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(d => d.EnrolledWithTokenId).IsRequired();
        builder.Property(d => d.EnrolledAt).IsRequired();
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt).IsRequired();

        builder.HasOne<Domain.Identity.Organization>()
            .WithMany()
            .HasForeignKey(d => d.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Exactly one group per device, enforced here rather than by application
        // code: a required foreign key means a device cannot exist in no group,
        // and being a single column it cannot be in two.
        //
        // Restrict, deliberately. Deleting a group moves its devices to "All
        // Devices" first, in the same transaction. Cascade would delete devices;
        // SetNull cannot express "the built-in group" and would violate NOT NULL.
        // Restrict turns any device that slipped into the group between the move
        // and the delete into a failed delete -- which the service reports and the
        // caller retries -- instead of an orphaned or deleted device.
        builder.Property(d => d.DeviceGroupId).IsRequired();
        builder.HasOne<Domain.Groups.DeviceGroup>()
            .WithMany()
            .HasForeignKey(d => d.DeviceGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        // Group pages, group actions and scope resolution all ask "which devices
        // are in this group".
        builder.HasIndex(d => d.DeviceGroupId)
            .HasDatabaseName("ix_devices_device_group_id");

        builder.HasOne<Domain.Enrollment.EnrollmentToken>()
            .WithMany()
            .HasForeignKey(d => d.EnrolledWithTokenId)
            // The token must outlive the devices it admitted - audit lineage.
            .OnDelete(DeleteBehavior.Restrict);

        // One ACTIVE device row per physical machine per organization. This is what
        // turns re-running the installer into re-enrollment instead of a duplicate.
        //
        // Filtered to Active on purpose. Retiring a device closes that record; the
        // machine may then enrol again and get a new row with its own id and its own
        // history, which is only possible if a retired row does not hold the machine
        // identifier hostage. Two active rows for one machine remain impossible,
        // which is the invariant that actually matters.
        //
        // The enrolment lookup in AgentEnrollmentService is scoped identically, so
        // the query and the constraint cannot disagree about what "already enrolled"
        // means.
        builder.HasIndex(d => new { d.OrganizationId, d.MachineIdentifier })
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName("ix_devices_organization_id_machine_identifier");

        // Device list is sorted by recency; online/offline derives from last_seen.
        builder.HasIndex(d => new { d.OrganizationId, d.LastSeenAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_devices_organization_id_last_seen_at");

        builder.HasIndex(d => new { d.OrganizationId, d.Hostname })
            .HasDatabaseName("ix_devices_organization_id_hostname");
    }
}
