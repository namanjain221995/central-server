using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.WorkTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceActivitySampleConfiguration : IEntityTypeConfiguration<DeviceActivitySample>
{
    public void Configure(EntityTypeBuilder<DeviceActivitySample> builder)
    {
        builder.ToTable("device_activity_samples");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.DeviceId).IsRequired();
        builder.Property(s => s.UserName).HasMaxLength(256).IsRequired();
        builder.Property(s => s.SampledAt).IsRequired();
        builder.Property(s => s.IdleSeconds).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(s => s.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The report scans a device's samples over a time window.
        builder.HasIndex(s => new { s.DeviceId, s.SampledAt })
            .HasDatabaseName("ix_device_activity_samples_device_sampled");

        // One sample per minute per device+user: a re-uploaded batch cannot create
        // duplicate minutes, so idle/active counts stay honest.
        builder.HasIndex(s => new { s.DeviceId, s.UserName, s.SampledAt })
            .IsUnique()
            .HasDatabaseName("ux_device_activity_samples_device_user_minute");
    }
}
