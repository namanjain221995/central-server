using EndpointAgent.Core.RemoteSupport;

namespace EndpointAgent.Core.Tests.RemoteSupport;

/// <summary>
/// The pure RustDesk2.toml ID parser: it reads the ID in each shape RustDesk writes,
/// ignores every other key (notably <c>enc_id</c>), and reads "no usable ID" as null.
/// </summary>
public sealed class RustDeskConfigTests
{
    [Theory]
    [InlineData("id = '123456789'", "123456789")]
    [InlineData("id = \"123456789\"", "123456789")]
    [InlineData("id = 123456789", "123456789")]
    [InlineData("id='987654321'", "987654321")]
    [InlineData("  id = '555000111'  ", "555000111")]
    [InlineData("id = 'abc123XYZ'", "abc123XYZ")]
    public void It_reads_the_id_in_each_shape(string line, string expected)
    {
        RustDeskConfig.ParseDeviceId(line).ShouldBe(expected);
    }

    [Fact]
    public void It_reads_the_id_from_a_full_config_and_ignores_the_other_keys()
    {
        var toml = string.Join('\n',
            "rendezvous_server = 'rs-sg.rustdesk.com'",
            "nat_type = 1",
            "enc_id = 'SHOULD-NOT-MATCH'",
            "id = '246813579'",
            "[options]",
            "key = 'value'");

        RustDeskConfig.ParseDeviceId(toml).ShouldBe("246813579");
    }

    [Fact]
    public void It_never_matches_enc_id()
    {
        // enc_id is a different, encrypted field; matching it would report the wrong value.
        RustDeskConfig.ParseDeviceId("enc_id = 'ENCRYPTED'").ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rendezvous_server = 'x'\nnat_type = 1")]
    [InlineData("id = ''")]       // present but not yet registered
    [InlineData("id = \"\"")]
    public void It_returns_null_when_there_is_no_usable_id(string? text)
    {
        RustDeskConfig.ParseDeviceId(text).ShouldBeNull();
    }

    // --- enc_id decryption (RustDesk 1.4.7+) --------------------------------

    // Synthetic, not a real device: a made-up MachineGuid and a made-up ID. The
    // XSalsa20-Poly1305 framing is pinned by round-tripping through RustDesk's own
    // scheme (RustDeskEncId.Encrypt), so these never carry a real machine's
    // identifiers. Compatibility with an actual RustDesk 1.5.0 enc_id was confirmed
    // separately against a live device and in the format research; here we guard the
    // implementation against regression.
    private const string Guid1 = "00000000-0000-4000-8000-000000000001";
    private const string Guid2 = "abcdef01-2345-6789-abcd-ef0123456789";

    [Theory]
    [InlineData("1580161508", Guid1)]   // an assigned/updated ID (RustDesk's [1e9,2e9) range)
    [InlineData("123456789", Guid2)]    // a nine-digit ID
    [InlineData("2000000000", Guid1)]
    public void It_round_trips_a_format_v1_enc_id(string id, string guid)
    {
        var toml = string.Join('\n',
            "rendezvous_server = 'rs-sg.rustdesk.com'",
            $"enc_id = '00{RustDeskEncId.Encrypt(id, guid)}'",
            "[options]");

        RustDeskConfig.ParseDeviceId(toml, guid).ShouldBe(id);
    }

    [Fact]
    public void It_reads_a_legacy_zero_nonce_enc_id()
    {
        // Values written by RustDesk <= 1.4.6 have no format byte and a zero nonce.
        var toml = $"enc_id = '00{RustDeskEncId.Encrypt("555000111", Guid1, legacy: true)}'";
        RustDeskConfig.ParseDeviceId(toml, Guid1).ShouldBe("555000111");
    }

    [Fact]
    public void A_plaintext_id_is_still_preferred_when_present()
    {
        var toml = string.Join('\n', "id = '246813579'", $"enc_id = '00{RustDeskEncId.Encrypt("999", Guid1)}'");
        RustDeskConfig.ParseDeviceId(toml, Guid1).ShouldBe("246813579");
    }

    [Fact]
    public void An_enc_id_needs_the_machine_guid()
    {
        var toml = $"enc_id = '00{RustDeskEncId.Encrypt("123456789", Guid1)}'";
        RustDeskConfig.ParseDeviceId(toml, machineGuid: null).ShouldBeNull();
        RustDeskConfig.ParseDeviceId(toml, machineGuid: "").ShouldBeNull();
    }

    [Fact]
    public void A_wrong_machine_guid_yields_null_not_a_wrong_id()
    {
        // The Poly1305 tag fails under any other key, so a mismatched machine can
        // never produce a plausible-but-wrong ID.
        var toml = $"enc_id = '00{RustDeskEncId.Encrypt("123456789", Guid1)}'";
        RustDeskConfig.ParseDeviceId(toml, Guid2).ShouldBeNull();
    }

    [Fact]
    public void The_machine_guid_overload_still_reads_a_plaintext_id()
    {
        RustDeskConfig.ParseDeviceId("id = '123456789'", Guid1).ShouldBe("123456789");
    }
}
