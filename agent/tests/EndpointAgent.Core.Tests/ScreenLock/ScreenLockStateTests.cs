using System.Security.Cryptography;
using System.Text;
using EndpointAgent.Core.Configuration;
using EndpointAgent.Core.ScreenLock;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Core.Tests.ScreenLock;

/// <summary>
/// The screen lock's persistence and its unlock verifier: the file store round-trips
/// a lock and reads a torn or missing file as "not locked" (the recovery-safe
/// misreading), and the PBKDF2 verifier matches a correct code and only a correct
/// code.
/// </summary>
public sealed class ScreenLockStateTests
{
    private static readonly DateTimeOffset Engaged = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    internal static ScreenLockVerifier VerifierFor(string code, int iterations = 120_000, int hashBytes = 32)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), salt, iterations, HashAlgorithmName.SHA256, hashBytes);
        return new ScreenLockVerifier(
            ScreenLockVerifier.Pbkdf2Sha256, iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    private static ScreenLockState State(string code = "ElephantGingerStapler42") =>
        new(Guid.Parse("0199d000-0000-7000-8000-00000000abcd"),
            "This device is locked. Contact the service desk.",
            "it-help@example.invalid, ext. 4000",
            Engaged,
            VerifierFor(code),
            PriorPolicy: "{\"priorDisableTaskMgr\":null}");

    // ---------------------------------------------------------------- the store

    [Fact]
    public async Task The_store_round_trips_a_lock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = Store(dir);
            (await store.LoadAsync()).ShouldBeNull("a fresh device is not locked");

            var state = State();
            await store.SaveAsync(state);

            (await store.LoadAsync()).ShouldBe(state);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Clearing_the_store_removes_the_lock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = Store(dir);
            await store.SaveAsync(State());
            (await store.LoadAsync()).ShouldNotBeNull();

            await store.SaveAsync(null);

            (await store.LoadAsync()).ShouldBeNull("a cleared store is not locked");
            File.Exists(Path.Combine(dir, FileScreenLockStore.StateFileName)).ShouldBeFalse();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task A_torn_or_missing_file_reads_as_not_locked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = Store(dir);
            (await store.LoadAsync()).ShouldBeNull("missing file: not locked");

            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, FileScreenLockStore.StateFileName), "{ not json");

            (await store.LoadAsync()).ShouldBeNull("a damaged lock file frees a user, it never strands one");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // -------------------------------------------------------------- the verifier

    [Fact]
    public void The_verifier_matches_the_correct_code_and_nothing_else()
    {
        var verifier = VerifierFor("CorrectHorseBatteryStaple");

        verifier.Verify("CorrectHorseBatteryStaple").ShouldBeTrue();
        verifier.Verify("correcthorsebatterystaple").ShouldBeFalse("case matters");
        verifier.Verify("CorrectHorseBatteryStapl").ShouldBeFalse("a near miss is still a miss");
        verifier.Verify("").ShouldBeFalse();
        verifier.Verify(null).ShouldBeFalse();
    }

    [Fact]
    public void The_verifier_derives_the_stored_hash_length_so_the_server_chooses_it()
    {
        // A 64-byte hash and a 20-byte hash both verify, because the device derives
        // exactly as many bytes as the server stored.
        ScreenLockStateTests.VerifierFor("len64", hashBytes: 64).Verify("len64").ShouldBeTrue();
        ScreenLockStateTests.VerifierFor("len20", hashBytes: 20).Verify("len20").ShouldBeTrue();
    }

    [Fact]
    public void A_verifier_with_the_wrong_algorithm_or_junk_matches_nothing()
    {
        var good = VerifierFor("code");

        (good with { Algo = "bcrypt" }).Verify("code").ShouldBeFalse("only pbkdf2-sha256 is understood");
        (good with { Salt = "not base64 !!" }).Verify("code").ShouldBeFalse();
        (good with { Hash = "not base64 !!" }).Verify("code").ShouldBeFalse();
        (good with { Iterations = 0 }).Verify("code").ShouldBeFalse();
    }

    [Fact]
    public void Well_formed_accepts_a_real_verifier_and_refuses_the_implausible()
    {
        VerifierFor("code").IsWellFormed().ShouldBeTrue();

        VerifierFor("code").IsWellFormed().ShouldBeTrue();
        (VerifierFor("code") with { Algo = "pbkdf2" }).IsWellFormed().ShouldBeFalse("wrong algorithm");
        (VerifierFor("code") with { Iterations = 5 }).IsWellFormed().ShouldBeFalse("too few iterations");
        (VerifierFor("code") with { Iterations = 100_000_000 }).IsWellFormed().ShouldBeFalse("absurdly many iterations");
        (VerifierFor("code") with { Salt = Convert.ToBase64String(new byte[4]) }).IsWellFormed().ShouldBeFalse("salt too short");
        (VerifierFor("code") with { Hash = Convert.ToBase64String(new byte[8]) }).IsWellFormed().ShouldBeFalse("hash too short");
        (VerifierFor("code") with { Salt = "not base64" }).IsWellFormed().ShouldBeFalse();
    }

    private static FileScreenLockStore Store(string dir) =>
        new(Options.Create(new AgentOptions { StateDirectory = dir }), NullLogger<FileScreenLockStore>.Instance);

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
