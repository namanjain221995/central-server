using EndpointPlatform.Domain.WorkTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class WorkTimeSettingConfiguration : IEntityTypeConfiguration<WorkTimeSetting>
{
    public void Configure(EntityTypeBuilder<WorkTimeSetting> builder)
    {
        builder.ToTable("work_time_settings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.OrganizationId).IsRequired();
        builder.Property(s => s.Enabled).IsRequired();
        builder.Property(s => s.ShiftStartMinutes).IsRequired();
        builder.Property(s => s.ShiftEndMinutes).IsRequired();
        builder.Property(s => s.IdleThresholdMinutes).IsRequired();
        builder.Property(s => s.BreakMinutes).IsRequired();
        builder.Property(s => s.UtcOffsetMinutes).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        builder.HasOne<Domain.Identity.Organization>()
            .WithMany()
            .HasForeignKey(s => s.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // One row per organization.
        builder.HasIndex(s => s.OrganizationId)
            .IsUnique()
            .HasDatabaseName("ux_work_time_settings_organization_id");
    }
}
