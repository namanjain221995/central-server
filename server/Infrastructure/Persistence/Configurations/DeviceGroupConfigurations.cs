using EndpointPlatform.Domain.Groups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class DeviceGroupConfiguration : IEntityTypeConfiguration<DeviceGroup>
{
    /// <summary>
    /// Case-insensitive name uniqueness per organization. Created by raw SQL in
    /// the <c>ExclusiveDeviceGroups</c> migration because it indexes an
    /// expression, <c>lower(name)</c>, which EF cannot model -- so it is named
    /// here for the service that translates its violation, not declared below.
    /// </summary>
    public const string UniqueNameIndex = "ux_device_groups_organization_id_lower_name";

    public void Configure(EntityTypeBuilder<DeviceGroup> builder)
    {
        builder.ToTable("device_groups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.OrganizationId).IsRequired();
        builder.Property(g => g.Name).HasMaxLength(DeviceGroup.MaxNameLength).IsRequired();
        builder.Property(g => g.Description).HasMaxLength(DeviceGroup.MaxDescriptionLength).IsRequired();
        builder.Property(g => g.Type).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(g => g.IsBuiltIn).IsRequired().HasDefaultValue(false);
        builder.Property(g => g.CreatedAt).IsRequired();
        builder.Property(g => g.UpdatedAt).IsRequired();

        builder.HasOne<Domain.Identity.Organization>().WithMany()
            .HasForeignKey(g => g.OrganizationId).OnDelete(DeleteBehavior.Restrict);

        // Exactly one "All Devices" per organization. Every device that is in no
        // other group lives here, so two would make "where does a removed device
        // go" ambiguous, and none would leave it nowhere to go.
        builder.HasIndex(g => g.OrganizationId)
            .IsUnique()
            .HasFilter("is_built_in")
            .HasDatabaseName("ux_device_groups_one_built_in_per_organization");

        // Name uniqueness is UniqueNameIndex, on lower(name) -- see above. The
        // earlier case-sensitive (organization_id, name) index let "MKT" and
        // "mkt" coexist, and would have let "all devices" sit beside the built-in
        // "All Devices".
    }
}
