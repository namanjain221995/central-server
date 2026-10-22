using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Tasks;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// The Chrome extension policy executor: the whole-state semantics (an empty rule
/// list clears, never merges), the result contract the server parses, the strict
/// gate in front of the registry (a bad payload or an unknown mode writes
/// nothing), the newest-policy-wins guard, and honest failure when the writer
/// cannot reach the registry. All against a fake writer, so no registry is
/// touched here.
/// </summary>
public sealed class ApplyChromeExtensionPolicyExecutorTests
{
    private const string IdA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IdB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string T1 = "2026-10-22T09:00:00Z";
    private const string T2 = "2026-10-22T10:00:00Z";

    private sealed class FakeWriter : IChromeExtensionPolicyWriter
    {
        public bool ApplyResult { get; set; } = true;
        public bool ClearResult { get; set; } = true;

        public List<string> Applied { get; } = [];
        public int ClearCalls { get; private set; }

        public bool Apply(string settingsJson)
        {
            Applied.Add(settingsJson);
            return ApplyResult;
        }

        public bool Clear()
        {
            ClearCalls++;
            return ClearResult;
        }
    }

    private static ApplyChromeExtensionPolicyExecutor Executor(FakeWriter writer) =>
        new(writer, NullLogger<ApplyChromeExtensionPolicyExecutor>.Instance);

    private static AgentTask PolicyTask(string? payload, DateTimeOffset? expiresAt = null) =>
        new(Guid.CreateVersion7(), "ApplyChromeExtensionPolicy", payload, expiresAt);

    private static string Payload(string issuedAt, params (string Id, string Mode)[] rules) =>
        JsonSerializer.Serialize(new
        {
            rules = rules.Select(r => new { extensionId = r.Id, mode = r.Mode }).ToArray(),
            issuedAt,
        });

    // =====================================================================  APPLY

    [Fact]
    public async Task A_valid_policy_is_written_and_reported_in_the_contract_shape()
    {
        var writer = new FakeWriter();

        var result = await Executor(writer).ExecuteAsync(
            PolicyTask(Payload(T1, (IdA, "ForceInstalled"), (IdB, "Blocked"))));

        result.Succeeded.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("Chrome extension policy applied: 2 rule(s).");

        var resultRoot = JsonDocument.Parse(result.ResultJson!).RootElement;
        resultRoot.GetProperty("applied").GetInt32().ShouldBe(2);
        resultRoot.GetProperty("cleared").GetBoolean().ShouldBeFalse();

        // The document that reached the writer is the builder's: Chrome's shape,
        // keyed by id.
        var written = JsonDocument.Parse(writer.Applied.ShouldHaveSingleItem()).RootElement;
        written.GetProperty(IdA).GetProperty("installation_mode").GetString().ShouldBe("force_installed");
        written.GetProperty(IdA).GetProperty("update_url").GetString()
            .ShouldBe("https://clients2.google.com/service/update2/crx");
        written.GetProperty(IdB).GetProperty("installation_mode").GetString().ShouldBe("blocked");

        writer.ClearCalls.ShouldBe(0, "a policy with rules replaces; it never clears");
    }

    [Fact]
    public async Task Unknown_extra_properties_are_tolerated_anywhere_in_the_payload()
    {
        // A newer server adding fields must not strand older agents.
        var payload = $$"""
            {
              "rules": [{ "extensionId": "{{IdA}}", "mode": "Allowed", "note": "new field" }],
              "issuedAt": "{{T1}}",
              "schemaVersion": 2
            }
            """;

        var writer = new FakeWriter();
        var result = await Executor(writer).ExecuteAsync(PolicyTask(payload));

        result.Succeeded.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("Chrome extension policy applied: 1 rule(s).");
        writer.Applied.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Mode_names_match_case_insensitively_but_only_the_four_names()
    {
        var writer = new FakeWriter();

        var result = await Executor(writer).ExecuteAsync(PolicyTask(Payload(T1, (IdA, "blocked"))));

        result.Succeeded.ShouldBeTrue(result.Message);
        JsonDocument.Parse(writer.Applied.ShouldHaveSingleItem()).RootElement
            .GetProperty(IdA).GetProperty("installation_mode").GetString().ShouldBe("blocked");
    }

    [Fact]
    public async Task A_task_that_sat_queued_past_its_deadline_still_applies()
    {
        // Whole state is idempotent and does not go stale with time -- only with a
        // newer policy. Unlike a restart, a late apply is the right thing to do.
        var writer = new FakeWriter();

        var result = await Executor(writer).ExecuteAsync(PolicyTask(
            Payload(T1, (IdA, "Blocked")),
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1)));

        result.Succeeded.ShouldBeTrue(result.Message);
        writer.Applied.ShouldHaveSingleItem();
    }

    // =====================================================================  CLEAR

    [Fact]
    public async Task An_empty_rule_list_clears_the_managed_policy()
    {
        var writer = new FakeWriter();

        var result = await Executor(writer).ExecuteAsync(PolicyTask(Payload(T1)));

        result.Succeeded.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("Chrome extension policy cleared.");

        var resultRoot = JsonDocument.Parse(result.ResultJson!).RootElement;
        resultRoot.GetProperty("applied").GetInt32().ShouldBe(0);
        resultRoot.GetProperty("cleared").GetBoolean().ShouldBeTrue();

        writer.ClearCalls.ShouldBe(1);
        writer.Applied.ShouldBeEmpty("an empty policy must never write a document");
    }

    // ==================================================================  REFUSALS

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]                                                            // not an object
    [InlineData("{}")]                                                            // nothing at all
    [InlineData("{\"rules\":[]}")]                                                // no issuedAt
    [InlineData("{\"rules\":[],\"issuedAt\":\"not a time\"}")]                    // unparseable issuedAt
    [InlineData("{\"rules\":[],\"issuedAt\":17}")]                                // issuedAt wrong type
    [InlineData("{\"issuedAt\":\"" + T1 + "\"}")]                                 // no rules
    [InlineData("{\"rules\":{},\"issuedAt\":\"" + T1 + "\"}")]                    // rules not an array
    [InlineData("{\"rules\":[\"x\"],\"issuedAt\":\"" + T1 + "\"}")]               // rule not an object
    [InlineData("{\"rules\":[{\"mode\":\"Blocked\"}],\"issuedAt\":\"" + T1 + "\"}")]              // no id
    [InlineData("{\"rules\":[{\"extensionId\":123,\"mode\":\"Blocked\"}],\"issuedAt\":\"" + T1 + "\"}")]   // id wrong type
    [InlineData("{\"rules\":[{\"extensionId\":\"too-short\",\"mode\":\"Blocked\"}],\"issuedAt\":\"" + T1 + "\"}")] // invalid id
    [InlineData("{\"rules\":[{\"extensionId\":\"" + IdA + "\"}],\"issuedAt\":\"" + T1 + "\"}")]   // no mode
    [InlineData("{\"rules\":[{\"extensionId\":\"" + IdA + "\",\"mode\":1}],\"issuedAt\":\"" + T1 + "\"}")] // mode as a number
    public async Task A_missing_or_malformed_payload_is_refused_and_writes_nothing(string? payload)
    {
        var writer = new FakeWriter();

        var result = await Executor(writer).ExecuteAsync(PolicyTask(payload));

        result.Succeeded.ShouldBeFalse(result.Message);
        result.ResultJson.ShouldBeNull();
        writer.Applied.ShouldBeEmpty("a bad payload must never reach the registry");
        writer.ClearCalls.ShouldBe(0, "a bad payload is not evidence that a policy was revoked");
    }

    [Theory]
    [InlineData("Disabled")]          // plausible but not in the contract
    [InlineData("force_installed")]   // Chrome's name, not the wire's
    [InlineData("ForceInstall")]      // near miss
    [InlineData("Restricted")]
    public async Task An_unknown_mode_is_refused_never_guessed(string mode)
    {
        var writer = new FakeWriter();

        var result = await Executor(writer).ExecuteAsync(PolicyTask(Payload(T1, (IdA, mode))));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("unknown mode");
        writer.Applied.ShouldBeEmpty();
        writer.ClearCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_duplicated_extension_id_is_refused()
    {
        var writer = new FakeWriter();

        var result = await Executor(writer).ExecuteAsync(
            PolicyTask(Payload(T1, (IdA, "Blocked"), (IdA, "Allowed"))));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("more than once");
        writer.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_policy_naming_absurdly_many_extensions_is_refused()
    {
        var rules = Enumerable.Range(0, ApplyChromeExtensionPolicyExecutor.MaxRules + 1)
            .Select(n => (NthId(n), "Blocked"))
            .ToArray();

        var writer = new FakeWriter();
        var result = await Executor(writer).ExecuteAsync(PolicyTask(Payload(T1, rules)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain($"{ApplyChromeExtensionPolicyExecutor.MaxRules}");
        writer.Applied.ShouldBeEmpty();
    }

    // ============================================================  HONEST FAILURE

    [Fact]
    public async Task A_writer_that_cannot_write_fails_the_task()
    {
        var writer = new FakeWriter { ApplyResult = false };

        var result = await Executor(writer).ExecuteAsync(PolicyTask(Payload(T1, (IdA, "Blocked"))));

        result.Succeeded.ShouldBeFalse("a policy that is not in place must not be reported applied");
        (result.Message ?? "").ShouldContain("could not be written");
        result.ResultJson.ShouldBeNull();
    }

    [Fact]
    public async Task A_writer_that_cannot_clear_fails_the_task()
    {
        var writer = new FakeWriter { ClearResult = false };

        var result = await Executor(writer).ExecuteAsync(PolicyTask(Payload(T1)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("could not be cleared");
    }

    // ==========================================================  NEWEST POLICY WINS

    [Fact]
    public async Task A_policy_older_than_the_newest_applied_is_ignored_as_a_success()
    {
        var writer = new FakeWriter();
        var executor = Executor(writer);

        (await executor.ExecuteAsync(PolicyTask(Payload(T2, (IdA, "Blocked"))))).Succeeded.ShouldBeTrue();

        // Queued before the policy above but delivered after it: applying it would
        // roll the device back to a policy the server has since replaced.
        var stale = await executor.ExecuteAsync(PolicyTask(Payload(T1, (IdA, "Allowed"), (IdB, "Allowed"))));

        stale.Succeeded.ShouldBeTrue("the state the server wants is already in force");
        (stale.Message ?? "").ShouldContain("newer policy");

        var resultRoot = JsonDocument.Parse(stale.ResultJson!).RootElement;
        resultRoot.GetProperty("applied").GetInt32().ShouldBe(0, "no rule of the stale task took hold");
        resultRoot.GetProperty("cleared").GetBoolean().ShouldBeFalse();

        writer.Applied.Count.ShouldBe(1, "the stale policy must never reach the registry");
    }

    [Fact]
    public async Task A_stale_clear_cannot_undo_a_newer_policy()
    {
        var writer = new FakeWriter();
        var executor = Executor(writer);

        (await executor.ExecuteAsync(PolicyTask(Payload(T2, (IdA, "Blocked"))))).Succeeded.ShouldBeTrue();

        var stale = await executor.ExecuteAsync(PolicyTask(Payload(T1)));

        stale.Succeeded.ShouldBeTrue();
        writer.ClearCalls.ShouldBe(0, "a clear issued before the policy in force must not remove it");
    }

    [Fact]
    public async Task A_newer_policy_replaces_an_older_one()
    {
        var writer = new FakeWriter();
        var executor = Executor(writer);

        (await executor.ExecuteAsync(PolicyTask(Payload(T1, (IdA, "Blocked"))))).Succeeded.ShouldBeTrue();
        (await executor.ExecuteAsync(PolicyTask(Payload(T2, (IdB, "Removed"))))).Succeeded.ShouldBeTrue();

        writer.Applied.Count.ShouldBe(2);

        // Whole state: the newest document carries only its own rules.
        var latest = JsonDocument.Parse(writer.Applied[^1]).RootElement;
        latest.TryGetProperty(IdA, out _).ShouldBeFalse("an extension absent from the new policy is unmanaged");
        latest.GetProperty(IdB).GetProperty("installation_mode").GetString().ShouldBe("removed");
    }

    [Fact]
    public async Task Replaying_the_policy_in_force_applies_again_idempotently()
    {
        var writer = new FakeWriter();
        var executor = Executor(writer);
        var payload = Payload(T1, (IdA, "Blocked"));

        (await executor.ExecuteAsync(PolicyTask(payload))).Succeeded.ShouldBeTrue();
        var replay = await executor.ExecuteAsync(PolicyTask(payload));

        replay.Succeeded.ShouldBeTrue();
        replay.Message.ShouldBe("Chrome extension policy applied: 1 rule(s).");
        writer.Applied.Count.ShouldBe(2, "re-applying identical whole state is harmless and keeps the result honest");
        writer.Applied[0].ShouldBe(writer.Applied[1], "the builder is deterministic");
    }

    [Fact]
    public async Task A_failed_write_does_not_count_as_the_newest_applied_policy()
    {
        var writer = new FakeWriter { ApplyResult = false };
        var executor = Executor(writer);

        // The newer policy failed to write, so nothing is in force from it...
        (await executor.ExecuteAsync(PolicyTask(Payload(T2, (IdA, "Blocked"))))).Succeeded.ShouldBeFalse();

        // ...and an older task must still be allowed to apply rather than being
        // "superseded" by a policy that never took hold.
        writer.ApplyResult = true;
        var older = await executor.ExecuteAsync(PolicyTask(Payload(T1, (IdB, "Allowed"))));

        older.Succeeded.ShouldBeTrue(older.Message);
        writer.Applied.Count.ShouldBe(2);
    }

    // =====================================================================  WIRING

    [Fact]
    public void The_task_type_matches_the_server_enum_name()
    {
        Executor(new FakeWriter()).TaskType.ShouldBe("ApplyChromeExtensionPolicy");
    }

    /// <summary>The nth distinct valid extension id: base-16 in the a-p alphabet, padded to 32.</summary>
    private static string NthId(int n)
    {
        var chars = new char[32];
        Array.Fill(chars, 'a');
        for (var i = 31; n > 0; i--, n /= 16)
        {
            chars[i] = (char)('a' + (n % 16));
        }

        return new string(chars);
    }
}
