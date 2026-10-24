using System.Net;
using EndpointPlatform.Domain.Security;

namespace EndpointPlatform.Domain.Tests.Security;

/// <summary>
/// The rules of the admin-console network allow-list: what an entry may look like,
/// how a bare address is widened to a single-address network, that the list is
/// deduplicated and bounded, and that the policy cannot be enabled empty - every
/// rule the PUT endpoint and the enforcement middleware rely on.
/// </summary>
public sealed class NetworkAccessPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private static NetworkAccessPolicy Create(bool enabled = true, params string[] cidrs) =>
        NetworkAccessPolicy.Create(enabled, cidrs, Now, Admin, "admin@test.local");

    // ---------------------------------------------------------- normalization

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7/32")]
    [InlineData("  203.0.113.7  ", "203.0.113.7/32")]
    [InlineData("2001:db8::1", "2001:db8::1/128")]
    [InlineData("2001:DB8::1", "2001:db8::1/128")]
    [InlineData("203.0.113.0/24", "203.0.113.0/24")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    public void Entries_normalize_to_canonical_cidr_text(string entry, string expected)
    {
        NetworkAccessPolicy.TryNormalizeCidr(entry, out var normalized).ShouldBeTrue();
        normalized.ShouldBe(expected);
    }

    [Fact]
    public void A_bare_ipv4_mapped_ipv6_address_normalizes_to_its_ipv4_form()
    {
        // The form a dual-stack socket reports for a plain IPv4 caller. Stored
        // unmapped so it matches the unmapped address the middleware compares.
        NetworkAccessPolicy.TryNormalizeCidr("::ffff:203.0.113.7", out var normalized).ShouldBeTrue();
        normalized.ShouldBe("203.0.113.7/32");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-network")]
    [InlineData("300.1.2.3")]
    [InlineData("203.0.113.7/33")]
    [InlineData("203.0.113.7/-1")]
    [InlineData("203.0.113.7/one")]
    // Host bits set after the prefix: refused, not silently masked - the stored
    // policy must mean exactly what the administrator typed.
    [InlineData("203.0.113.7/24")]
    [InlineData("2001:db8::1/64")]
    public void Garbage_entries_are_refused(string entry)
    {
        NetworkAccessPolicy.TryNormalizeCidr(entry, out _).ShouldBeFalse();

        var policy = Create(enabled: true, "203.0.113.0/24");
        Should.Throw<ArgumentException>(() =>
            policy.Update(true, [entry], Now, Admin, "admin@test.local"));
    }

    [Fact]
    public void An_overlong_entry_is_refused_before_parsing()
    {
        var entry = new string('1', NetworkAccessPolicy.MaxEntryLength + 1);
        NetworkAccessPolicy.TryNormalizeCidr(entry, out _).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- dedupe

    [Fact]
    public void Duplicates_are_removed_case_insensitively_after_normalization()
    {
        var policy = Create(
            enabled: true,
            "203.0.113.7",       // normalizes to 203.0.113.7/32 ...
            "203.0.113.7/32",    // ... so this is the same entry
            "2001:DB8::/32",
            "2001:db8::/32");

        policy.AllowedCidrs.ShouldBe(new[] { "203.0.113.7/32", "2001:db8::/32" });
    }

    // ---------------------------------------------------------------- limits

    [Fact]
    public void More_than_the_maximum_number_of_entries_is_refused()
    {
        var entries = Enumerable.Range(0, NetworkAccessPolicy.MaxEntries + 1)
            .Select(i => $"10.{i / 256}.{i % 256}.0/24")
            .ToArray();

        var exception = Should.Throw<ArgumentException>(() => Create(enabled: true, entries));
        exception.Message.ShouldContain($"{NetworkAccessPolicy.MaxEntries}");

        // Exactly the maximum is fine.
        Create(enabled: true, entries[..NetworkAccessPolicy.MaxEntries])
            .AllowedCidrs.Count.ShouldBe(NetworkAccessPolicy.MaxEntries);
    }

    [Fact]
    public void Enabling_with_an_empty_list_is_refused()
    {
        Should.Throw<ArgumentException>(() => Create(enabled: true));

        var policy = Create(enabled: false);
        Should.Throw<ArgumentException>(() => policy.Update(true, [], Now, Admin, "admin@test.local"));
    }

    [Fact]
    public void Disabling_with_an_empty_list_is_allowed()
    {
        var policy = Create(enabled: false);

        policy.Enabled.ShouldBeFalse();
        policy.AllowedCidrs.ShouldBeEmpty();
    }

    // ------------------------------------------------------------- the update

    [Fact]
    public void Update_records_what_changed_and_who_changed_it()
    {
        var policy = Create(enabled: false);
        var later = Now.AddMinutes(5);
        var otherAdmin = Guid.CreateVersion7();

        policy.Update(true, ["203.0.113.0/24", "10.0.0.1"], later, otherAdmin, "  owner@test.local  ");

        policy.Enabled.ShouldBeTrue();
        policy.AllowedCidrs.ShouldBe(new[] { "203.0.113.0/24", "10.0.0.1/32" });
        policy.UpdatedAtUtc.ShouldBe(later);
        policy.UpdatedByUserId.ShouldBe(otherAdmin);
        policy.UpdatedByEmail.ShouldBe("owner@test.local");
    }

    [Fact]
    public void A_failed_update_changes_nothing()
    {
        var policy = Create(enabled: true, "203.0.113.0/24");

        Should.Throw<ArgumentException>(() =>
            policy.Update(true, ["garbage"], Now.AddMinutes(1), Admin, "admin@test.local"));

        policy.Enabled.ShouldBeTrue();
        policy.AllowedCidrs.ShouldBe(new[] { "203.0.113.0/24" });
        policy.UpdatedAtUtc.ShouldBe(Now);
    }

    [Fact]
    public void The_singleton_id_is_fixed_and_every_instance_carries_it()
    {
        // The id is the address every reader and writer agrees on; a drifting
        // literal would silently fork the policy into unread rows.
        NetworkAccessPolicy.SingletonId.ShouldBe(Guid.Parse("b5b1f1aa-8c6a-41a9-9f70-3e26b2d9c001"));
        Create(enabled: false).Id.ShouldBe(NetworkAccessPolicy.SingletonId);
    }

    // ------------------------------------------------------ address normalizer

    [Fact]
    public void Ipv4_mapped_ipv6_addresses_unmap_and_everything_else_passes_through()
    {
        NetworkAccessPolicy.NormalizeAddress(IPAddress.Parse("::ffff:203.0.113.7"))
            .ShouldBe(IPAddress.Parse("203.0.113.7"));

        NetworkAccessPolicy.NormalizeAddress(IPAddress.Parse("203.0.113.7"))
            .ShouldBe(IPAddress.Parse("203.0.113.7"));

        NetworkAccessPolicy.NormalizeAddress(IPAddress.Parse("2001:db8::1"))
            .ShouldBe(IPAddress.Parse("2001:db8::1"));
    }

    [Fact]
    public void ValidateAndNormalize_reports_a_problem_instead_of_throwing()
    {
        NetworkAccessPolicy.ValidateAndNormalize(true, ["nonsense"], out _)
            .ShouldNotBeNull()
            .ShouldContain("nonsense");

        NetworkAccessPolicy.ValidateAndNormalize(true, null, out _)
            .ShouldNotBeNull();

        NetworkAccessPolicy.ValidateAndNormalize(false, null, out var normalized).ShouldBeNull();
        normalized.ShouldBeEmpty();
    }
}
