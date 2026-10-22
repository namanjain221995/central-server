using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class ChromeExtensionRuleConfiguration : IEntityTypeConfiguration<ChromeExtensionRule>
{
    public void Configure(EntityTypeBuilder<ChromeExtensionRule> builder)
    {
        builder.ToTable("chrome_extension_rules");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.DeviceId).IsRequired();

        // The id is exactly 32 characters, but a fixed-width column would pad
        // rather than refuse, so the entity holds the exact rule and the column
        // only the ceiling -- the same split chrome_extensions draws.
        builder.Property(r => r.ExtensionId).HasMaxLength(32).IsRequired();

        // Stored as text so reordering the enum can never reinterpret history --
        // and a rule misread would not be a mislabel, it would be a different
        // instruction to every Chrome on the machine.
        builder.Property(r => r.Mode)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(r => r.DisplayName).HasMaxLength(ChromeExtensionRule.MaxDisplayNameLength);
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(r => r.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One rule per extension per device -- the identity the upsert matches on
        // and the agent enforces by. The service dedupes before it writes; the
        // constraint is what makes that hold under concurrent edits. Its leading
        // column also serves the per-device reads (the rule list, and the
        // whole-state payload every mutation rebuilds).
        builder.HasIndex(r => new { r.DeviceId, r.ExtensionId })
            .IsUnique()
            .HasDatabaseName("ux_chrome_extension_rules_device_extension");
    }
}
