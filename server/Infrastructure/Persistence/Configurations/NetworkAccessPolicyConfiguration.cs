using System.Text.Json;
using EndpointPlatform.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EndpointPlatform.Infrastructure.Persistence.Configurations;

internal sealed class NetworkAccessPolicyConfiguration : IEntityTypeConfiguration<NetworkAccessPolicy>
{
    public void Configure(EntityTypeBuilder<NetworkAccessPolicy> builder)
    {
        builder.ToTable("network_access_policies");

        // The singleton's fixed id is the key; the application only ever addresses
        // NetworkAccessPolicy.SingletonId, so the primary key itself is what makes
        // a second row impossible even under a racing double-insert.
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Enabled).IsRequired();

        // One jsonb document rather than a child table: at most 64 entries, always
        // read as a whole when the policy loads and replaced as a whole when an
        // administrator saves - a row per entry would buy queries nobody runs.
        var converter = new ValueConverter<IReadOnlyList<string>, string>(
            list => JsonSerializer.Serialize(list, (JsonSerializerOptions?)null),
            json => JsonSerializer.Deserialize<List<string>>(json, (JsonSerializerOptions?)null)
                    ?? new List<string>());

        // Compared by contents, not by list reference, so change tracking sees a
        // replaced list as an update and an identical reload as unchanged.
        var comparer = new ValueComparer<IReadOnlyList<string>>(
            (left, right) => (left ?? new List<string>()).SequenceEqual(
                right ?? new List<string>(), StringComparer.Ordinal),
            list => list.Aggregate(
                0, (hash, entry) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(entry))),
            list => list.ToList());

        builder.Property(p => p.AllowedCidrs)
            .HasConversion(converter, comparer)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(p => p.UpdatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedByUserId);

        // 320 is the maximum length of an e-mail address; matches the domain guard.
        builder.Property(p => p.UpdatedByEmail).HasMaxLength(320);
    }
}
