using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// The remote-support configure executor: the strict gate in front of the device
/// (a bad payload or an expired task changes nothing, and never redeems a secret),
/// the one-time secret redemption, and the discipline that the password never
/// travels back in the result.
/// </summary>
public sealed class ConfigureRemoteSupportExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeControl : IRemoteSupportControl
    {
        public List<string> Passwords { get; } = [];
        public RemoteSupportConfigureResult Result { get; set; } = new(true, "Remote support access configured.");

        public Task<RemoteSupportConfigureResult> SetUnattendedPasswordAsync(string password, CancellationToken ct)
        {
            Passwords.Add(password);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeSecrets : ISecretRedeemer
    {
        public string? Secret { get; set; } = "RustDeskPass123!";
        public List<string> RedeemedRefs { get; } = [];

        public Task<string?> RedeemAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            RedeemedRefs.Add(secretReference);
            return Task.FromResult(Secret);
        }
    }

    private static ConfigureRemoteSupportExecutor New(FakeControl control, FakeSecrets secrets) =>
        new(control, secrets, NullLogger<ConfigureRemoteSupportExecutor>.Instance, new FixedClock(Now));

    private static AgentTask MakeTask(string? payload, DateTimeOffset? expiresAt = null) =>
        new(Guid.CreateVersion7(), "ConfigureRemoteSupport", payload, expiresAt);

    private static string Payload(string secretRef = "ref-abc") =>
        JsonSerializer.Serialize(new { passwordSecretRef = secretRef });

    // ------------------------------------------------------------- happy path

    [Fact]
    public async Task A_valid_payload_redeems_the_secret_and_configures_remote_support()
    {
        var control = new FakeControl();
        var secrets = new FakeSecrets { Secret = "RustDeskPass123!" };

        var result = await New(control, secrets).ExecuteAsync(MakeTask(Payload("ref-abc")));

        result.Succeeded.ShouldBeTrue();
        secrets.RedeemedRefs.ShouldHaveSingleItem().ShouldBe("ref-abc", "the exact reference from the payload is redeemed");
        control.Passwords.ShouldHaveSingleItem().ShouldBe("RustDeskPass123!", "the redeemed password is handed to the control");

        // The ID is reported on heartbeat, so there is no result document, and the
        // password must never travel back to the server in the result.
        result.ResultJson.ShouldBeNull("the RustDesk ID is reported via heartbeat, not here");
        (result.Message ?? "").ShouldNotContain("RustDeskPass123!");
    }

    [Fact]
    public async Task The_control_outcome_message_is_relayed_when_configuration_does_not_succeed()
    {
        // e.g. RustDesk not installed on the device; the executor relays the honest reason.
        var control = new FakeControl { Result = new RemoteSupportConfigureResult(false, "RustDesk is not installed") };
        var secrets = new FakeSecrets();

        var result = await New(control, secrets).ExecuteAsync(MakeTask(Payload()));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("not installed");
    }

    // --------------------------------------------------------- bad payloads

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{}")]                             // no passwordSecretRef
    [InlineData("{\"passwordSecretRef\":\"\"}")]   // blank reference
    [InlineData("{\"passwordSecretRef\":123}")]    // wrong type
    public async Task A_missing_or_malformed_payload_changes_nothing_and_redeems_nothing(string? payload)
    {
        var control = new FakeControl();
        var secrets = new FakeSecrets();

        var result = await New(control, secrets).ExecuteAsync(MakeTask(payload));

        result.Succeeded.ShouldBeFalse();
        secrets.RedeemedRefs.ShouldBeEmpty("a bad payload must never redeem a secret");
        control.Passwords.ShouldBeEmpty("a bad payload must never touch the device");
    }

    // --------------------------------------------------------- expired task

    [Fact]
    public async Task An_expired_task_configures_nothing_and_redeems_nothing()
    {
        var control = new FakeControl();
        var secrets = new FakeSecrets();

        var result = await New(control, secrets).ExecuteAsync(
            MakeTask(Payload(), expiresAt: Now.AddSeconds(-1)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("expired");
        secrets.RedeemedRefs.ShouldBeEmpty("an expired task must not redeem a secret");
        control.Passwords.ShouldBeEmpty("an expired task must not touch the device");
    }

    [Fact]
    public async Task A_task_whose_deadline_is_still_ahead_is_executed()
    {
        var control = new FakeControl();
        var secrets = new FakeSecrets();

        var result = await New(control, secrets).ExecuteAsync(
            MakeTask(Payload(), expiresAt: Now.AddMinutes(5)));

        result.Succeeded.ShouldBeTrue();
        control.Passwords.ShouldHaveSingleItem();
    }

    // ------------------------------------------------------ secret redemption

    [Fact]
    public async Task An_unredeemable_secret_fails_the_task_without_touching_the_device()
    {
        var control = new FakeControl();
        var secrets = new FakeSecrets { Secret = null }; // expired, replayed, or another device's

        var result = await New(control, secrets).ExecuteAsync(MakeTask(Payload("stale-ref")));

        result.Succeeded.ShouldBeFalse();
        secrets.RedeemedRefs.ShouldHaveSingleItem().ShouldBe("stale-ref", "it still tries to redeem what the payload named");
        control.Passwords.ShouldBeEmpty("a task that cannot get its secret must change nothing");
    }

    // ------------------------------------------------------- executor name

    [Fact]
    public void The_task_type_matches_the_server_enum_name()
    {
        // A mismatch here means the dispatcher silently reports "unsupported task type"
        // for a task the server happily queued.
        new ConfigureRemoteSupportExecutor(
                new FakeControl(), new FakeSecrets(), NullLogger<ConfigureRemoteSupportExecutor>.Instance)
            .TaskType.ShouldBe("ConfigureRemoteSupport");
    }
}
