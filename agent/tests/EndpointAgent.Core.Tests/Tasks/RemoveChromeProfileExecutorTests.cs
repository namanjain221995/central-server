using System.Text.Json;
using EndpointAgent.Core.ChromeProfiles;
using EndpointAgent.Core.Tasks;
using EndpointAgent.Core.Tests.ChromeProfiles;
using EndpointAgent.Core.Tests.Usb;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using FakeRemover = EndpointAgent.Core.Tests.ChromeProfiles.FakeChromeProfileRemover;

namespace EndpointAgent.Core.Tests.Tasks;

/// <summary>
/// The Chrome profile removal task's whole decision table, against a fake
/// remover so no file system or process table is touched: the payload gates, the
/// Default-profile and stale-payload refusals, the account check against a stale
/// console view, the Local State rewrite (entry out, <c>last_used</c> fixed,
/// everything else preserved), the edit-before-delete order with the partial
/// outcome, the one retry a sharing violation gets, and the idempotent re-run
/// after a partial or completed removal. Chrome running for the user is the one
/// blocker that no longer refuses: a request that passed every other gate is
/// parked with the deferred-removal worker and the task completes as scheduled
/// -- and a request any OTHER gate faults must never be parked, which is pinned
/// here too. The worker's own passes are pinned in
/// <c>ChromeProfileRemovalWorkerTests</c>.
/// </summary>
public sealed class RemoveChromeProfileExecutorTests
{
    private const string Sid = "S-1-5-21-1000000000-2000000000-3000000000-1001";
    private const string UserData = @"C:\Users\casey\AppData\Local\Google\Chrome\User Data";
    private const string ProfileNinePath = UserData + @"\Profile 9";

    private static readonly DateTimeOffset Start = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A Local State with two profiles and the noise a real file carries, so the
    /// tests can pin that the rewrite touches the one entry and nothing else.
    /// </summary>
    private const string LocalStateDocument = """
        {
          "browser": { "enabled_labs_experiments": [] },
          "os_crypt": { "encrypted_key": "RFBBUEk=" },
          "profile": {
            "info_cache": {
              "Default": {
                "name": "Casey",
                "is_consented_primary_account": true,
                "user_name": "casey.example@example.com"
              },
              "Profile 9": {
                "name": "Work",
                "is_consented_primary_account": true,
                "user_name": "casey@example.com"
              }
            },
            "last_active_profiles": [ "Profile 9" ],
            "last_used": "Profile 9",
            "profiles_order": [ "Default", "Profile 9" ]
          },
          "variations_seed_signature": "MEUCIQDinvented"
        }
        """;

    // ===============================================================  THE WIRING

    private static FakeRemover Remover(string? localState = LocalStateDocument)
    {
        var remover = new FakeRemover { LocalState = localState };
        remover.UserDataBySid[Sid] = UserData;
        remover.Directories.Add("Profile 9");
        return remover;
    }

    private static RemoveChromeProfileExecutor Executor(
        FakeRemover remover, MemoryPendingChromeRemovalStore? store = null, TimeProvider? clock = null)
    {
        var engine = new ChromeProfileRemovalEngine(remover, NullLogger<ChromeProfileRemovalEngine>.Instance);
        var worker = new ChromeProfileRemovalWorker(
            store ?? new MemoryPendingChromeRemovalStore(), engine,
            NullLogger<ChromeProfileRemovalWorker>.Instance, clock);
        return new RemoveChromeProfileExecutor(engine, worker, NullLogger<RemoveChromeProfileExecutor>.Instance, clock);
    }

    private static AgentTask RemovalTask(string? payload) =>
        new(Guid.CreateVersion7(), "RemoveChromeProfile", payload);

    private static string Payload(
        string? sid = Sid,
        string? key = "Profile 9",
        string? email = "casey@example.com",
        string? path = ProfileNinePath)
    {
        var parts = new List<string>();
        if (sid is not null)
        {
            parts.Add($"\"userSid\": {JsonSerializer.Serialize(sid)}");
        }

        if (key is not null)
        {
            parts.Add($"\"profileKey\": {JsonSerializer.Serialize(key)}");
        }

        parts.Add($"\"expectedEmail\": {JsonSerializer.Serialize(email)}");

        if (path is not null)
        {
            parts.Add($"\"profilePath\": {JsonSerializer.Serialize(path)}");
        }

        return "{" + string.Join(",", parts) + "}";
    }

    private static IOException SharingViolation() =>
        new("The process cannot access the file because it is being used by another process.",
            unchecked((int)0x80070020));

    private static void NothingTouched(FakeRemover remover)
    {
        remover.Written.ShouldBeEmpty("a refusal must rewrite nothing");
        remover.Deleted.ShouldBeEmpty("a refusal must delete nothing");
        remover.DeleteCalls.ShouldBe(0);
    }

    // ==================================================================  REFUSALS

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]                                                             // not an object
    [InlineData("{}")]                                                             // nothing at all
    [InlineData("{\"profileKey\":\"Profile 9\",\"profilePath\":\"C:\\\\x\"}")]     // no userSid
    [InlineData("{\"userSid\":42,\"profileKey\":\"Profile 9\",\"profilePath\":\"C:\\\\x\"}")]      // sid wrong type
    [InlineData("{\"userSid\":\"" + Sid + "\",\"profilePath\":\"C:\\\\x\"}")]      // no profileKey
    [InlineData("{\"userSid\":\"" + Sid + "\",\"profileKey\":9,\"profilePath\":\"C:\\\\x\"}")]     // key wrong type
    [InlineData("{\"userSid\":\"" + Sid + "\",\"profileKey\":\"Profile 9\"}")]     // no profilePath
    [InlineData("{\"userSid\":\"" + Sid + "\",\"profileKey\":\"Profile 9\",\"profilePath\":\"\"}")] // blank path
    [InlineData("{\"userSid\":\"" + Sid + "\",\"profileKey\":\"Profile 9\",\"profilePath\":\"C:\\\\x\",\"expectedEmail\":42}")] // email wrong type
    public async Task A_missing_or_malformed_payload_is_refused_and_touches_nothing(string? payload)
    {
        var remover = Remover();

        var result = await Executor(remover).ExecuteAsync(RemovalTask(payload));

        result.Succeeded.ShouldBeFalse(result.Message);
        result.ResultJson.ShouldBeNull();
        NothingTouched(remover);
    }

    /// <summary>
    /// The same rule the Local State parser applies to a key: anything that is not
    /// a plain directory name under User Data is not a profile key, whatever the
    /// server sent.
    /// </summary>
    [Theory]
    [InlineData(@"..\Secrets")]
    [InlineData("Profile/9")]
    [InlineData(@"Profile\9")]
    [InlineData("C:")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("Profile..9")]
    public async Task A_profile_key_that_is_not_a_plain_directory_name_is_refused(string key)
    {
        var remover = Remover();

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload(key: key)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("profileKey");
        NothingTouched(remover);
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public async Task Removing_the_default_profile_is_refused_by_policy(string key)
    {
        var remover = Remover();

        var result = await Executor(remover).ExecuteAsync(
            RemovalTask(Payload(key: key, path: UserData + "\\" + key)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("refused by policy");
        NothingTouched(remover);
    }

    [Fact]
    public async Task An_unresolvable_user_is_refused()
    {
        var remover = Remover();
        remover.UserDataBySid.Clear();

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("could not be resolved");
        NothingTouched(remover);
    }

    /// <summary>
    /// The stale/foreign-payload defence: the path this device computes for the
    /// key is the only directory the task may mean, so a payload carrying any
    /// other path was built against another device or an out-of-date list.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\someoneelse\AppData\Local\Google\Chrome\User Data\Profile 9")]
    [InlineData(UserData + @"\Profile 8")]
    [InlineData(@"D:\Profile 9")]
    public async Task A_profile_path_that_is_not_this_devices_path_for_the_key_is_refused(string path)
    {
        var remover = Remover();

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload(path: path)));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("does not match this device's path");
        NothingTouched(remover);
    }

    /// <summary>NTFS does not care about case, a trailing separator, or which slash was written; neither may this check.</summary>
    [Theory]
    [InlineData(@"c:\users\CASEY\appdata\local\google\chrome\user data\profile 9")]
    [InlineData(ProfileNinePath + @"\")]
    [InlineData("C:/Users/casey/AppData/Local/Google/Chrome/User Data/Profile 9")]
    public async Task The_path_comparison_matches_the_file_systems_idea_of_sameness(string path)
    {
        var remover = Remover();

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload(path: path)));

        result.Succeeded.ShouldBeTrue(result.Message);
        remover.Deleted.ShouldBe(["Profile 9"]);
    }

    [Fact]
    public async Task Chrome_running_for_another_user_does_not_block_the_removal()
    {
        var remover = Remover();
        remover.ChromeRunningFor.Add("S-1-5-21-1000000000-2000000000-3000000000-1002");

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeTrue(result.Message);
        remover.Deleted.ShouldBe(["Profile 9"]);
    }

    [Fact]
    public async Task An_unreadable_local_state_is_refused()
    {
        var remover = Remover(localState: null);

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("could not be read");
        NothingTouched(remover);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public async Task A_local_state_that_is_not_a_json_object_is_refused(string localState)
    {
        var remover = Remover(localState: localState);

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("did not parse");
        NothingTouched(remover);
    }

    /// <summary>
    /// The stale-inventory defence: the console chose the profile off a snapshot,
    /// and if the profile's account has changed since, this may be the wrong
    /// person's profile. Refused with the exact message the console shows.
    /// </summary>
    [Theory]
    [InlineData("someoneelse@example.com")]   // a different account entirely
    [InlineData("casey@example.net")]         // nearly, but not, the account
    public async Task An_account_that_no_longer_matches_the_consoles_view_is_refused(string expectedEmail)
    {
        var remover = Remover();

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload(email: expectedEmail)));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldBe(
            "The profile's signed-in account no longer matches the console's view; "
            + "refresh Chrome inventory and retry.");
        NothingTouched(remover);
    }

    [Fact]
    public async Task An_expected_email_against_a_profile_with_no_account_is_refused()
    {
        // The console believed someone was signed in; the entry records no one.
        // That is the same staleness, refused the same way.
        var remover = Remover(localState: """
            { "profile": { "info_cache": { "Profile 9": { "name": "Work" } }, "last_used": "Default" } }
            """);

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("no longer matches the console's view");
        NothingTouched(remover);
    }

    // ==========================================  CHROME RUNNING: DEFER, NOT FAIL

    /// <summary>
    /// The feature: on a busy laptop Chrome is open all day, so the one blocker
    /// that clears itself parks the request instead of failing the task. The
    /// message and the result document are a contract with the console, pinned
    /// verbatim.
    /// </summary>
    [Fact]
    public async Task Chrome_running_for_that_user_schedules_the_removal_and_the_task_succeeds()
    {
        var remover = Remover();
        remover.ChromeRunningFor.Add(Sid);
        var store = new MemoryPendingChromeRemovalStore();
        var clock = new TestClock(Start);
        var task = RemovalTask(Payload());

        var result = await Executor(remover, store, clock).ExecuteAsync(task);

        result.Succeeded.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Scheduled: Chrome is open for this user; the profile will be removed the moment "
            + "Chrome closes or at the next startup. The console reflects it after the next "
            + "Chrome inventory refresh.");

        var resultRoot = JsonDocument.Parse(result.ResultJson!).RootElement;
        resultRoot.GetProperty("profileKey").GetString().ShouldBe("Profile 9");
        resultRoot.GetProperty("scheduled").GetBoolean().ShouldBeTrue();

        var pending = store.Saved.ShouldHaveSingleItem();
        pending.TaskId.ShouldBe(task.TaskId, "the deferred completion's logs read back to this click");
        pending.UserSid.ShouldBe(Sid);
        pending.ProfileKey.ShouldBe("Profile 9");
        pending.ExpectedEmail.ShouldBe("casey@example.com");
        pending.ProfilePath.ShouldBe(ProfileNinePath);
        pending.RequestedAt.ShouldBe(Start);

        NothingTouched(remover);
    }

    /// <summary>One entry per profile per user: clicking again moves the request's clock, it does not queue a second removal.</summary>
    [Fact]
    public async Task Scheduling_the_same_profile_again_requeues_it_with_the_new_request_time()
    {
        var remover = Remover();
        remover.ChromeRunningFor.Add(Sid);
        var store = new MemoryPendingChromeRemovalStore();
        var clock = new TestClock(Start);
        var executor = Executor(remover, store, clock);

        await executor.ExecuteAsync(RemovalTask(Payload()));
        clock.Advance(TimeSpan.FromHours(3));
        var second = RemovalTask(Payload());
        var result = await executor.ExecuteAsync(second);

        result.Succeeded.ShouldBeTrue(result.Message);
        var pending = store.Saved.ShouldHaveSingleItem("same user + same profile is one intent, not two");
        pending.TaskId.ShouldBe(second.TaskId);
        pending.RequestedAt.ShouldBe(Start.AddHours(3), "the operator's latest click restarts the seven-day window");
    }

    [Fact]
    public async Task Different_profiles_of_the_same_user_are_scheduled_separately()
    {
        var remover = Remover();
        remover.ChromeRunningFor.Add(Sid);
        remover.Directories.Add("Profile 2");
        var store = new MemoryPendingChromeRemovalStore();
        var executor = Executor(remover, store, new TestClock(Start));

        await executor.ExecuteAsync(RemovalTask(Payload()));
        await executor.ExecuteAsync(RemovalTask(Payload(
            key: "Profile 2", email: "casey.example@example.com", path: UserData + @"\Profile 2")));

        store.Saved.Count.ShouldBe(2);
    }

    /// <summary>
    /// "Scheduled" is reserved for a request that passed every other gate: a
    /// stale or wrong request fails NOW, exactly as before, whether or not
    /// Chrome happens to be running -- it must never sit parked, waiting to do
    /// the wrong thing at a quiet moment.
    /// </summary>
    [Fact]
    public async Task A_stale_account_is_refused_not_scheduled_even_while_chrome_runs()
    {
        var remover = Remover();
        remover.ChromeRunningFor.Add(Sid);
        var store = new MemoryPendingChromeRemovalStore();

        var result = await Executor(remover, store).ExecuteAsync(
            RemovalTask(Payload(email: "someoneelse@example.com")));

        result.Succeeded.ShouldBeFalse();
        result.Message.ShouldBe(
            "The profile's signed-in account no longer matches the console's view; "
            + "refresh Chrome inventory and retry.");
        store.Saved.ShouldBeEmpty();
        NothingTouched(remover);
    }

    [Fact]
    public async Task A_wrong_path_is_refused_not_scheduled_even_while_chrome_runs()
    {
        var remover = Remover();
        remover.ChromeRunningFor.Add(Sid);
        var store = new MemoryPendingChromeRemovalStore();

        var result = await Executor(remover, store).ExecuteAsync(
            RemovalTask(Payload(path: @"D:\Profile 9")));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("does not match this device's path");
        store.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_default_profile_is_refused_not_scheduled_even_while_chrome_runs()
    {
        var remover = Remover();
        remover.ChromeRunningFor.Add(Sid);
        var store = new MemoryPendingChromeRemovalStore();

        var result = await Executor(remover, store).ExecuteAsync(
            RemovalTask(Payload(key: "Default", path: UserData + "\\Default")));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("refused by policy");
        store.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unresolvable_user_is_refused_not_scheduled_even_while_chrome_runs()
    {
        var remover = Remover();
        remover.UserDataBySid.Clear();
        remover.ChromeRunningFor.Add(Sid);
        var store = new MemoryPendingChromeRemovalStore();

        var result = await Executor(remover, store).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("could not be resolved");
        store.Saved.ShouldBeEmpty();
    }

    /// <summary>
    /// The account gate needs Local State, so an unreadable file is a refusal
    /// even while Chrome runs: without reading the entry nothing can prove the
    /// request still describes the profile the console meant.
    /// </summary>
    [Fact]
    public async Task An_unreadable_local_state_is_refused_not_scheduled_even_while_chrome_runs()
    {
        var remover = Remover(localState: null);
        remover.ChromeRunningFor.Add(Sid);
        var store = new MemoryPendingChromeRemovalStore();

        var result = await Executor(remover, store).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("could not be read");
        store.Saved.ShouldBeEmpty();
    }

    // ===============================================================  HAPPY PATHS

    [Fact]
    public async Task A_removal_edits_local_state_first_and_then_deletes_the_directory()
    {
        var remover = Remover();

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Removed Chrome profile 'Profile 9' (casey@example.com): Chrome's profile list no "
            + "longer carries it and its directory was deleted. No other profile was touched.");

        // The rewrite: the one entry is gone, the neighbour is intact, last_used
        // points back at Default, and the rest of the document survived.
        var written = JsonDocument.Parse(remover.Written.ShouldHaveSingleItem()).RootElement;
        var profile = written.GetProperty("profile");
        var cache = profile.GetProperty("info_cache");
        cache.TryGetProperty("Profile 9", out _).ShouldBeFalse("the removed entry must be gone");
        cache.GetProperty("Default").GetProperty("user_name").GetString().ShouldBe("casey.example@example.com");
        profile.GetProperty("last_used").GetString().ShouldBe("Default");
        written.GetProperty("os_crypt").GetProperty("encrypted_key").GetString().ShouldBe("RFBBUEk=");
        written.GetProperty("variations_seed_signature").GetString().ShouldBe("MEUCIQDinvented");

        remover.Deleted.ShouldBe(["Profile 9"]);

        var resultRoot = JsonDocument.Parse(result.ResultJson!).RootElement;
        resultRoot.GetProperty("profileKey").GetString().ShouldBe("Profile 9");
        resultRoot.GetProperty("localStateUpdated").GetBoolean().ShouldBeTrue();
        resultRoot.GetProperty("directoryDeleted").GetBoolean().ShouldBeTrue();
        resultRoot.GetProperty("remainingPath").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_account_check_is_case_insensitive()
    {
        var remover = Remover();

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload(email: "CASEY@Example.COM")));

        result.Succeeded.ShouldBeTrue(result.Message);
        remover.Deleted.ShouldBe(["Profile 9"]);
    }

    [Fact]
    public async Task A_null_expected_email_skips_the_account_check()
    {
        // The console saw no account for the profile; there is nothing to pin the
        // check against, and the contract says null means exactly that.
        var remover = Remover();

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload(email: null)));

        result.Succeeded.ShouldBeTrue(result.Message);
        (result.Message ?? "").ShouldContain("(casey@example.com)", customMessage: "the on-device account still names the profile in the result");
        remover.Deleted.ShouldBe(["Profile 9"]);
    }

    [Fact]
    public async Task A_profile_with_no_account_is_reported_as_having_none()
    {
        var remover = Remover(localState: """
            { "profile": { "info_cache": { "Profile 9": { "name": "Work" } }, "last_used": "Default" } }
            """);

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload(email: null)));

        result.Succeeded.ShouldBeTrue(result.Message);
        (result.Message ?? "").ShouldContain("'Profile 9' ((no account))");
    }

    [Fact]
    public async Task A_last_used_naming_another_profile_is_left_alone()
    {
        var remover = Remover(localState: """
            {
              "profile": {
                "info_cache": {
                  "Default": { "name": "Casey" },
                  "Profile 9": { "name": "Work", "user_name": "casey@example.com" }
                },
                "last_used": "Default"
              }
            }
            """);

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeTrue(result.Message);
        JsonDocument.Parse(remover.Written.ShouldHaveSingleItem()).RootElement
            .GetProperty("profile").GetProperty("last_used").GetString().ShouldBe("Default");
    }

    /// <summary>
    /// The key names a directory and NTFS knows one directory under two spellings,
    /// so an entry whose spelling differs from the payload's is still the profile
    /// -- the parser reads it that way, and the removal must not leave it behind
    /// to resurrect the directory in Chrome's UI.
    /// </summary>
    [Fact]
    public async Task An_entry_spelled_differently_from_the_payload_key_is_still_removed()
    {
        var remover = Remover(localState: """
            {
              "profile": {
                "info_cache": {
                  "Default": { "name": "Casey" },
                  "profile 9": { "name": "Work", "user_name": "casey@example.com" }
                },
                "last_used": "profile 9"
              }
            }
            """);

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeTrue(result.Message);

        var profile = JsonDocument.Parse(remover.Written.ShouldHaveSingleItem()).RootElement.GetProperty("profile");
        profile.GetProperty("info_cache").TryGetProperty("profile 9", out _).ShouldBeFalse();
        profile.GetProperty("last_used").GetString().ShouldBe("Default");
        remover.Deleted.ShouldBe(["Profile 9"]);
    }

    [Fact]
    public async Task Unknown_extra_payload_properties_are_tolerated()
    {
        // A newer server adding fields must not strand older agents.
        var payload = Payload()[..^1] + ",\"reason\":\"dormant\",\"schemaVersion\":2}";
        var remover = Remover();

        var result = await Executor(remover).ExecuteAsync(RemovalTask(payload));

        result.Succeeded.ShouldBeTrue(result.Message);
        remover.Deleted.ShouldBe(["Profile 9"]);
    }

    // ===================================  PARTIALS, RETRIES AND RE-RUNS

    [Fact]
    public async Task A_directory_delete_failure_after_the_edit_is_reported_partial_with_the_remaining_path()
    {
        var remover = Remover();
        remover.DeleteFailures.Enqueue(new UnauthorizedAccessException("Access to the path is denied."));

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeFalse("the directory is still there; the task is not done");
        (result.Message ?? "").ShouldContain("removed from Chrome's profile list");
        (result.Message ?? "").ShouldContain($"Remaining path: {ProfileNinePath}");

        remover.Written.ShouldHaveSingleItem("the Local State edit stands; a retry only has the directory left");

        var resultRoot = JsonDocument.Parse(result.ResultJson!).RootElement;
        resultRoot.GetProperty("localStateUpdated").GetBoolean().ShouldBeTrue();
        resultRoot.GetProperty("directoryDeleted").GetBoolean().ShouldBeFalse();
        resultRoot.GetProperty("remainingPath").GetString().ShouldBe(ProfileNinePath);
    }

    /// <summary>
    /// The state a partial run leaves behind: the entry already gone, the
    /// directory still there. A re-run must finish the job, not refuse it.
    /// </summary>
    [Fact]
    public async Task A_rerun_after_a_partial_removal_deletes_the_leftover_directory()
    {
        var remover = Remover(localState: """
            { "profile": { "info_cache": { "Default": { "name": "Casey" } }, "last_used": "Default" } }
            """);

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeTrue(result.Message);
        (result.Message ?? "").ShouldContain("leftover directory");
        remover.Written.ShouldBeEmpty("the profile list is already clean; there is nothing to rewrite");
        remover.Deleted.ShouldBe(["Profile 9"]);
    }

    [Fact]
    public async Task A_rerun_after_a_partial_removal_still_fixes_a_last_used_naming_the_profile()
    {
        var remover = Remover(localState: """
            { "profile": { "info_cache": { "Default": { "name": "Casey" } }, "last_used": "Profile 9" } }
            """);

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeTrue(result.Message);
        JsonDocument.Parse(remover.Written.ShouldHaveSingleItem()).RootElement
            .GetProperty("profile").GetProperty("last_used").GetString().ShouldBe("Default");
        remover.Deleted.ShouldBe(["Profile 9"]);
    }

    [Fact]
    public async Task A_task_redelivered_after_a_completed_removal_reports_success_with_nothing_to_do()
    {
        var remover = Remover(localState: """
            { "profile": { "info_cache": { "Default": { "name": "Casey" } }, "last_used": "Default" } }
            """);
        remover.Directories.Clear();

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeTrue(result.Message);
        (result.Message ?? "").ShouldContain("already removed");
        remover.Written.ShouldBeEmpty();
        remover.Deleted.ShouldBeEmpty();

        var resultRoot = JsonDocument.Parse(result.ResultJson!).RootElement;
        resultRoot.GetProperty("localStateUpdated").GetBoolean().ShouldBeFalse();
        resultRoot.GetProperty("directoryDeleted").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_sharing_violation_on_the_local_state_write_gets_exactly_one_retry()
    {
        var remover = Remover();
        remover.WriteFailures.Enqueue(SharingViolation());

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeTrue(result.Message);
        remover.WriteCalls.ShouldBe(2, "first attempt hit the sharing violation; the retry succeeded");
        remover.Deleted.ShouldBe(["Profile 9"]);
    }

    [Fact]
    public async Task A_persistent_sharing_violation_on_the_write_fails_the_task_and_deletes_nothing()
    {
        var remover = Remover();
        remover.WriteFailures.Enqueue(SharingViolation());
        remover.WriteFailures.Enqueue(SharingViolation());

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeFalse();
        (result.Message ?? "").ShouldContain("could not be rewritten");
        remover.WriteCalls.ShouldBe(2, "one retry, not a loop");
        remover.Deleted.ShouldBeEmpty("the directory is only ever touched after the list is clean");
    }

    [Fact]
    public async Task A_write_failure_that_is_not_a_sharing_violation_is_not_retried()
    {
        var remover = Remover();
        remover.WriteFailures.Enqueue(new UnauthorizedAccessException("Access to the path is denied."));

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeFalse();
        remover.WriteCalls.ShouldBe(1, "only a transient hold earns a second try");
        remover.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sharing_violation_on_the_directory_delete_gets_exactly_one_retry()
    {
        var remover = Remover();
        remover.DeleteFailures.Enqueue(SharingViolation());

        var result = await Executor(remover).ExecuteAsync(RemovalTask(Payload()));

        result.Succeeded.ShouldBeTrue(result.Message);
        remover.DeleteCalls.ShouldBe(2);
        remover.Deleted.ShouldBe(["Profile 9"]);
    }

    // =====================================================================  WIRING

    [Fact]
    public void The_task_type_matches_the_server_enum_name()
    {
        Executor(Remover()).TaskType.ShouldBe("RemoveChromeProfile");
    }
}
