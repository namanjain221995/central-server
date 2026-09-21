using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class PlatformUserConfiguration : IEntityTypeConfiguration<PlatformUser>
{
    public void Configure(EntityTypeBuilder<PlatformUser> builder)
    {
        builder.ToTable("platform_users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.OrganizationId).IsRequired();

        builder.Property(u => u.Email)
            .HasMaxLength(254)
            .IsRequired();

        builder.Property(u => u.NormalizedEmail)
            .HasMaxLength(254)
            .IsRequired();

        builder.Property(u => u.DisplayName)
            .HasMaxLength(200)
            .IsRequired();

        // Encoded hash only. Length accommodates Argon2id and PBKDF2 encodings.
        builder.Property(u => u.PasswordHash)
            .HasMaxLength(512);

        builder.Property(u => u.SecurityStamp)
            .HasMaxLength(64)
            .IsRequired();

        // Stored as text: an enum reordering must never reinterpret existing rows.
        builder.Property(u => u.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(u => u.FailedSignInCount).IsRequired();

        // Nullable, and null means "the counter is clear". Not defaulted to a
        // timestamp: a default would make every existing row look as though it had
        // just failed, and the decay window would then keep counters alive that
        // should have been forgotten.
        builder.Property(u => u.LastFailedSignInAt);
        builder.Property(u => u.IsSystemAccount).IsRequired();
        builder.Property(u => u.HasAllDeviceScope).IsRequired();

        // Defaults to false, so no existing administrator is forced into a change
        // screen by the deployment that adds the column.
        builder.Property(u => u.MustChangePassword).IsRequired();

        // Multi-factor state. All three nullable, and all three null together
        // means "has not enrolled": a secret without a confirmation timestamp is
        // an interrupted enrolment and must not count as a second factor.
        // The secret is ciphertext under a key the Agent API is forbidden to hold.
        builder.Property(u => u.TotpSealedSecret).HasMaxLength(512);
        builder.Property(u => u.TotpConfirmedAt);
        builder.Property(u => u.TotpLastCounter);
        builder.Property(u => u.CreatedAt).IsRequired();
        builder.Property(u => u.UpdatedAt).IsRequired();

        builder.HasOne(u => u.Organization)
            .WithMany()
            .HasForeignKey(u => u.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Email uniqueness is per organization, not global.
        builder.HasIndex(u => new { u.OrganizationId, u.NormalizedEmail })
            .IsUnique()
            .HasDatabaseName("ix_platform_users_organization_id_normalized_email");

        builder.HasIndex(u => u.Status)
            .HasDatabaseName("ix_platform_users_status");

        builder.HasMany(u => u.Roles)
            .WithOne(r => r.PlatformUser)
            .HasForeignKey(r => r.PlatformUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(u => u.Roles)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasField("_roles");
    }
}
