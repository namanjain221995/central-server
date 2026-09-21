using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class MfaRecoveryCodeConfiguration : IEntityTypeConfiguration<MfaRecoveryCode>
{
    public void Configure(EntityTypeBuilder<MfaRecoveryCode> builder)
    {
        builder.ToTable("mfa_recovery_codes");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.UserId).IsRequired();

        builder.Property(c => c.CodeHash)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();

        // Cascade: codes are meaningless without the account, and leaving orphans
        // would let a deleted administrator's codes outlive them.
        builder.HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Redemption looks a code up by its hash within one account.
        builder.HasIndex(c => new { c.UserId, c.CodeHash })
            .IsUnique()
            .HasDatabaseName("ix_mfa_recovery_codes_user_hash");
    }
}

internal sealed class AdminMfaChallengeConfiguration : IEntityTypeConfiguration<AdminMfaChallenge>
{
    public void Configure(EntityTypeBuilder<AdminMfaChallenge> builder)
    {
        builder.ToTable("admin_mfa_challenges");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.UserId).IsRequired();

        builder.Property(c => c.TokenHash)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(c => c.SecurityStamp)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(c => c.IssuedAt).IsRequired();
        builder.Property(c => c.ExpiresAt).IsRequired();
        builder.Property(c => c.AttemptCount).IsRequired();
        builder.Property(c => c.SourceIp).HasMaxLength(64);
        builder.Property(c => c.UserAgent).HasMaxLength(512);
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();

        builder.HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // The verify step resolves a challenge by token hash, exactly as the
        // session handler resolves a session.
        builder.HasIndex(c => c.TokenHash)
            .IsUnique()
            .HasDatabaseName("ix_admin_mfa_challenges_token_hash");

        // Expired challenges are swept by expiry.
        builder.HasIndex(c => c.ExpiresAt)
            .HasDatabaseName("ix_admin_mfa_challenges_expires_at");
    }
}
