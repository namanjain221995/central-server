using EndpointPlatform.Domain.Chat;
using EndpointPlatform.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSession>
{
    public void Configure(EntityTypeBuilder<ChatSession> builder)
    {
        builder.ToTable("chat_sessions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.OrganizationId).IsRequired();
        builder.Property(s => s.DeviceId).IsRequired();
        builder.Property(s => s.StartedByUserId).IsRequired();
        builder.Property(s => s.StartedByDisplay)
            .HasMaxLength(ChatSession.MaxStartedByDisplayLength).IsRequired();

        // Stored as text so reordering the enum can never reinterpret history.
        builder.Property(s => s.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(s => s.StartedAt).IsRequired();
        builder.Property(s => s.EndedAt);
        builder.Property(s => s.EndedReason)
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired();
        builder.Property(s => s.LastActivityAt).IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(s => s.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // At most one active session per device, enforced by the database rather
        // than by a read-then-write check that two concurrent starts would both
        // pass -- exactly the pattern ux_device_tasks_active_restart_per_device
        // uses for an in-flight restart. Terminal (Ended) rows are excluded, so a
        // device may start a new chat the moment the previous one closes. Status is
        // stored as a string (HasConversion above), so the filter compares names.
        builder.HasIndex(s => s.DeviceId)
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName("ux_chat_sessions_active_per_device");

        // The console's active-chat list and each session's belongs-to-org check.
        builder.HasIndex(s => new { s.OrganizationId, s.Status })
            .HasDatabaseName("ix_chat_sessions_organization_id_status");
    }
}

internal sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("chat_messages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.ChatSessionId).IsRequired();
        builder.Property(m => m.Seq).IsRequired();

        builder.Property(m => m.Sender)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(m => m.SenderDisplay)
            .HasMaxLength(ChatMessage.MaxSenderDisplayLength).IsRequired();
        builder.Property(m => m.Body).HasMaxLength(ChatMessage.MaxBodyLength).IsRequired();
        builder.Property(m => m.SentAt).IsRequired();

        builder.HasOne<ChatSession>()
            .WithMany()
            .HasForeignKey(m => m.ChatSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Both sides page the conversation as "everything in this session after
        // seq N", so the read is keyed on (session, seq) in order.
        builder.HasIndex(m => new { m.ChatSessionId, m.Seq })
            .HasDatabaseName("ix_chat_messages_chat_session_id_seq");
    }
}
