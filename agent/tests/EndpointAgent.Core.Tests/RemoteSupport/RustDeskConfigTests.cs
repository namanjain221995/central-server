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
}
