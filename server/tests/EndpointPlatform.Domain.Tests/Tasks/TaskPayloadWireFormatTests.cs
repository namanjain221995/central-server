using System.Text.Json;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Tasks;

/// <summary>
/// Pins the payload wire format to what the deployed agents parse.
/// </summary>
/// <remarks>
/// The server can be redeployed in a minute; the agents on two hundred
/// endpoints cannot. Their executors read payload fields with
/// <c>JsonDocument</c> — camelCase names, and enum values as strings — so the
/// serialization here is a compatibility contract with binaries already in the
/// field, not a style choice. This suite exists because the ServiceAction enum
/// shipped serialised as a number, every deployed agent rejected it as a
/// malformed payload, and nothing failed until a live task hit a real machine.
/// </remarks>
public sealed class TaskPayloadWireFormatTests
{
    /// <summary>The exact options DeviceTaskService serialises payloads with.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>Reads a property the way the agent executors do.</summary>
    private static string AgentReadString(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(property).GetString() ?? "";
    }

    [Theory]
    [InlineData(TaskPayloads.ServiceAction.Start, "Start")]
    [InlineData(TaskPayloads.ServiceAction.Stop, "Stop")]
    [InlineData(TaskPayloads.ServiceAction.Restart, "Restart")]
    public void Service_action_travels_as_the_string_the_agent_switches_on(
        TaskPayloads.ServiceAction action, string expected)
    {
        var json = Serialize(new TaskPayloads.ControlService("Spooler", action));

        // GetString() throws on a JSON number — this call IS the agent's parse.
        AgentReadString(json, "action").ShouldBe(expected);
        AgentReadString(json, "serviceName").ShouldBe("Spooler");
    }

    [Fact]
    public void Terminate_process_payload_matches_the_agent_field_names()
    {
        var json = Serialize(new TaskPayloads.TerminateProcess(4242, "notepad.exe"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("processId").GetInt32().ShouldBe(4242);
        doc.RootElement.GetProperty("expectedImageName").GetString().ShouldBe("notepad.exe");
    }

    [Fact]
    public void Update_agent_payload_matches_the_agent_field_names()
    {
        var releaseId = Guid.CreateVersion7();
        var json = Serialize(new TaskPayloads.UpdateAgent(releaseId, "1.1.0", new string('a', 64)));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("releaseId").GetGuid().ShouldBe(releaseId);
        doc.RootElement.GetProperty("version").GetString().ShouldBe("1.1.0");
        doc.RootElement.GetProperty("sha256").GetString().ShouldBe(new string('a', 64));
    }

    /// <summary>
    /// The USB policy payload as <c>ApplyUsbPolicyExecutor</c> actually reads it.
    /// </summary>
    /// <remarks>
    /// The executor drops any grant whose <c>policy</c> is not the string
    /// <c>ReadOnly</c>. If this enum ever serialised as a number the agent would
    /// silently discard every grant — restricting instead of granting, which is
    /// the safe direction but would make the whole feature appear broken with no
    /// error anywhere. Pinned here rather than discovered on a real endpoint.
    /// </remarks>
    [Fact]
    public void Usb_policy_payload_matches_what_the_agent_executor_parses()
    {
        var expiry = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        var issued = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

        var json = Serialize(new TaskPayloads.ApplyUsbPolicy(
            [new TaskPayloads.UsbGrant(
                @"USB\VID_0781&PID_5581\ABC123", TaskPayloads.UsbGrantPolicy.ReadOnly, expiry)],
            issued));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("issuedAt").GetDateTimeOffset().ShouldBe(issued);

        var grant = doc.RootElement.GetProperty("grants")[0];
        grant.GetProperty("instanceId").GetString().ShouldBe(@"USB\VID_0781&PID_5581\ABC123");
        grant.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(expiry);

        // GetString() throws on a JSON number — this call IS the agent's parse.
        grant.GetProperty("policy").GetString().ShouldBe("ReadOnly");
    }

    /// <summary>
    /// An empty grant list must serialise as an empty array, not as null.
    /// </summary>
    /// <remarks>
    /// "Restrict everything" is a real and important policy — it is what a
    /// revocation produces. The executor requires <c>grants</c> to be an array
    /// and rejects the payload otherwise, and a rejected payload leaves the
    /// previous policy in force, so a null here would mean a revocation that
    /// never took effect.
    /// </remarks>
    [Fact]
    public void Revoking_everything_serialises_as_an_empty_array()
    {
        var json = Serialize(new TaskPayloads.ApplyUsbPolicy([], DateTimeOffset.UnixEpoch));

        using var doc = JsonDocument.Parse(json);
        var grants = doc.RootElement.GetProperty("grants");

        grants.ValueKind.ShouldBe(JsonValueKind.Array);
        grants.GetArrayLength().ShouldBe(0);
    }


    [Fact]
    public void Restart_payload_matches_the_agent_field_names()
    {
        var json = Serialize(new TaskPayloads.RestartOrShutdown(30, "maintenance"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("graceSeconds").GetInt32().ShouldBe(30);
        doc.RootElement.GetProperty("message").GetString().ShouldBe("maintenance");
    }

    /// <summary>
    /// The Chrome extension policy as the agent's executor reads it: camelCase
    /// field names, and each rule's mode as one of the four exact strings.
    /// </summary>
    /// <remarks>
    /// The server builds the mode string from
    /// <c>Domain.Chrome.ChromeExtensionRuleMode.ToString()</c>, so this test also
    /// pins those member names: renaming one would change the wire value under
    /// every deployed agent, exactly the ServiceAction failure this suite exists
    /// to prevent.
    /// </remarks>
    [Theory]
    [InlineData(EndpointPlatform.Domain.Chrome.ChromeExtensionRuleMode.ForceInstalled, "ForceInstalled")]
    [InlineData(EndpointPlatform.Domain.Chrome.ChromeExtensionRuleMode.Blocked, "Blocked")]
    [InlineData(EndpointPlatform.Domain.Chrome.ChromeExtensionRuleMode.Allowed, "Allowed")]
    [InlineData(EndpointPlatform.Domain.Chrome.ChromeExtensionRuleMode.Removed, "Removed")]
    public void Chrome_extension_policy_mode_travels_as_the_string_the_agent_matches_on(
        EndpointPlatform.Domain.Chrome.ChromeExtensionRuleMode mode, string expected)
    {
        const string extensionId = "abcdefghijklmnopabcdefghijklmnop";
        var issued = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        var json = Serialize(new TaskPayloads.ApplyChromeExtensionPolicy(
            [new TaskPayloads.ChromeExtensionRulePayload(extensionId, mode.ToString())],
            issued));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("issuedAt").GetDateTimeOffset().ShouldBe(issued);

        var rule = doc.RootElement.GetProperty("rules")[0];
        rule.GetProperty("extensionId").GetString().ShouldBe(extensionId);

        // GetString() throws on a JSON number — this call IS the agent's parse.
        rule.GetProperty("mode").GetString().ShouldBe(expected);
    }

    /// <summary>
    /// Lifting the last rule must serialise as an empty array, not as null.
    /// </summary>
    /// <remarks>
    /// The empty policy is the instruction that clears the device -- it is what
    /// removing the final rule produces. The executor requires <c>rules</c> to be
    /// an array and rejects the payload otherwise, and a rejected payload leaves
    /// the previous policy in force, so a null here would mean a lift that never
    /// took effect.
    /// </remarks>
    [Fact]
    public void Clearing_the_chrome_extension_policy_serialises_as_an_empty_array()
    {
        var json = Serialize(new TaskPayloads.ApplyChromeExtensionPolicy([], DateTimeOffset.UnixEpoch));

        using var doc = JsonDocument.Parse(json);
        var rules = doc.RootElement.GetProperty("rules");

        rules.ValueKind.ShouldBe(JsonValueKind.Array);
        rules.GetArrayLength().ShouldBe(0);
    }
}
