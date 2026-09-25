using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class ChromeProfileConfiguration : IEntityTypeConfiguration<ChromeProfile>
{
    public void Configure(EntityTypeBuilder<ChromeProfile> builder)
    {
        builder.ToTable("chrome_profiles");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.DeviceId).IsRequired();
        builder.Property(p => p.ChromeInstallationId).IsRequired();

        // The limits are the wire contract's (InventoryChromeProfile.Max*),
        // mirrored by the entity's guards.
        builder.Property(p => p.UserSid).HasMaxLength(184).IsRequired();
        builder.Property(p => p.UserAccount).HasMaxLength(256);
        builder.Property(p => p.ProfileKey).HasMaxLength(64).IsRequired();
        builder.Property(p => p.ProfileName).HasMaxLength(256);
        builder.Property(p => p.ProfilePath).HasMaxLength(512).IsRequired();
        builder.Property(p => p.CollectedAt).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(p => p.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ChromeInstallation>()
            .WithMany()
            .HasForeignKey(p => p.ChromeInstallationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => p.DeviceId)
            .HasDatabaseName("ix_chrome_profiles_device_id");

        // Declared rather than left to the foreign-key convention, so the index the
        // installation-to-profile cascade relies on has a name a migration diff
        // can be checked against, like every other index in these configurations.
        builder.HasIndex(p => p.ChromeInstallationId)
            .HasDatabaseName("ix_chrome_profiles_chrome_installation_id");

        // A profile's identity on a machine is which Windows user owns it and which
        // directory it lives in. Ingestion dedupes on the same pair before it
        // writes; the constraint is what makes that hold under a concurrent upload.
        builder.HasIndex(p => new { p.DeviceId, p.UserSid, p.ProfileKey })
            .IsUnique()
            .HasDatabaseName("ux_chrome_profiles_device_user_profile");
    }
}
