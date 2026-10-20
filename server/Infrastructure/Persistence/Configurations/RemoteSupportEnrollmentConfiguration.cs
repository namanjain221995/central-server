using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.RemoteSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class RemoteSupportEnrollmentConfiguration
    : IEntityTypeConfiguration<RemoteSupportEnrollment>
{
    public void Configure(EntityTypeBuilder<RemoteSupportEnrollment> builder)
    {
        builder.ToTable("remote_support_enrollments");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrganizationId).IsRequired();
        builder.Property(e => e.DeviceId).IsRequired();

        // The AES-GCM envelope, base64. Never indexed and never selected into a
        // projection that leaves the service layer.
        builder.Property(e => e.SealedPassword).HasMaxLength(4096).IsRequired();

        builder.Property(e => e.KeyVersion).IsRequired();
        builder.Property(e => e.ConfiguredByUserId).IsRequired();
        builder.Property(e => e.ConfiguredByDisplay).HasMaxLength(320).IsRequired();
        builder.Property(e => e.ConfiguredAt).IsRequired();
        builder.Property(e => e.RevealedCount).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One live enrolment per device.
        builder.HasIndex(e => e.DeviceId)
            .IsUnique()
            .HasDatabaseName("ux_remote_support_enrollments_device_id");
    }
}
