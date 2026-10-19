using EndpointPlatform.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationSecuritySettingConfiguration
    : IEntityTypeConfiguration<OrganizationSecuritySetting>
{
    public void Configure(EntityTypeBuilder<OrganizationSecuritySetting> builder)
    {
        builder.ToTable("organization_security_settings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.OrganizationId).IsRequired();
        builder.Property(s => s.ScreenUnlockAlgo).HasMaxLength(OrganizationSecuritySetting.MaxAlgoLength).IsRequired();
        builder.Property(s => s.ScreenUnlockIterations).IsRequired();
        builder.Property(s => s.ScreenUnlockSalt)
            .HasMaxLength(OrganizationSecuritySetting.MaxVerifierPartLength).IsRequired();
        builder.Property(s => s.ScreenUnlockHash)
            .HasMaxLength(OrganizationSecuritySetting.MaxVerifierPartLength).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        builder.HasOne<Domain.Identity.Organization>()
            .WithMany()
            .HasForeignKey(s => s.OrganizationId)
            // The organization must outlive its settings; deleting one is a managed
            // operation that clears dependents first, as everywhere else.
            .OnDelete(DeleteBehavior.Restrict);

        // One row per organization.
        builder.HasIndex(s => s.OrganizationId)
            .IsUnique()
            .HasDatabaseName("ux_organization_security_settings_organization_id");
    }
}
