using System.Security.Cryptography;
using System.Text;
using EndpointPlatform.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Tests.Agents;

/// <summary>
/// The server-side publish gate on an MSI's Authenticode signature.
/// </summary>
/// <remarks>
/// <para>
/// Six checks, each with a test that fails it in isolation and one that passes
/// all of them. The ordering matters and is asserted: a build that fails for two
/// reasons reports the first, because that is the one to fix first.
/// </para>
/// <para>
/// Every signature here comes from a certificate authority generated for the test
/// and trusted only through <see cref="TestArtifacts.TrustingChainPolicy"/>. Under
/// the production <see cref="SystemTrustChainPolicy"/> the same artifacts fail
/// with <see cref="AuthenticodeFailure.UntrustedChain"/>, and that too is asserted
/// -- it is the property that makes the test authority safe to have at all.
/// </para>
/// </remarks>
public sealed class AuthenticodeVerifierTests
{
    private const string Publisher = "CN=Techsara Test Signing";

    private static AuthenticodeVerifier Verifier(
        IAuthenticodeChainPolicy policy, string? expectedSigner = Publisher) =>
        new(Options.Create(new AgentReleaseOptions { ExpectedSignerSubject = expectedSigner }),
            policy, NullLogger<AuthenticodeVerifier>.Instance);

    // ---- the pass ----------------------------------------------------------

    [Fact]
    public void A_correctly_signed_msi_by_the_expected_publisher_is_trusted()
    {
        using var authority = TestArtifacts.CreateAuthority();
        var msi = TestArtifacts.SignedMsi(authority);

        var result = Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root)).Verify(msi);

        result.IsTrusted.ShouldBeTrue(result.Describe());
        result.SignerSubject.ShouldNotBeNull();
        result.SignerSubject.ShouldContain(Publisher);
    }

    /// <summary>The signature stream is small enough to live in the mini stream.</summary>
    [Fact]
    public void A_signature_stored_in_the_mini_stream_is_found()
    {
        using var authority = TestArtifacts.CreateAuthority();
        var msi = TestArtifacts.SignedMsi(authority, miniCutoff: 4096);

        Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root)).Verify(msi)
            .IsTrusted.ShouldBeTrue();
    }

    /// <summary>
    /// The publisher pin is a case-insensitive substring, exactly as the agent
    /// applies it, so a subject with extra RDNs still matches its CN.
    /// </summary>
    [Fact]
    public void The_publisher_pin_matches_as_a_case_insensitive_substring()
    {
        using var authority = TestArtifacts.CreateAuthority(
            leafSubject: "CN=Techsara Test Signing, O=Techsara Solutions, C=IN");
        var msi = TestArtifacts.SignedMsi(authority);

        Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root), "cn=techsara test signing")
            .Verify(msi).IsTrusted.ShouldBeTrue();
    }

    // ---- the stream name itself --------------------------------------------

    /// <summary>
    /// Pins the signature stream's name, control prefix included.
    /// </summary>
    /// <remarks>
    /// Windows Installer stores the Authenticode blob under
    /// <c>\u0005DigitalSignature</c> -- U+0005 first, as compound files name
    /// their metadata streams. The constant used to carry that prefix as a raw,
    /// invisible control byte that every editor rendered as plain
    /// <c>"DigitalSignature"</c>; it was misread exactly that way in review, and
    /// one well-meaning cleanup would have stripped it -- after which Public
    /// mode would refuse every genuinely signed MSI as unsigned, while every
    /// test stayed green because the test artifact writer shares the constant.
    /// Assertions are spelled out character by character so this test cannot be
    /// fooled by the same invisible-byte trick, and "simplifying" the constant
    /// back to the visible name fails here first.
    /// </remarks>
    [Fact]
    public void The_signature_stream_name_is_the_msi_literal_with_the_control_prefix()
    {
        AuthenticodeVerifier.SignatureStreamName.ShouldBe("\u0005DigitalSignature");

        AuthenticodeVerifier.SignatureStreamName.Length.ShouldBe("DigitalSignature".Length + 1);
        AuthenticodeVerifier.SignatureStreamName[0].ShouldBe('\u0005');
        AuthenticodeVerifier.SignatureStreamName[1..].ShouldBe("DigitalSignature");
    }

    /// <summary>
    /// A stream named plain <c>DigitalSignature</c> -- no U+0005 prefix -- is not
    /// where Windows Installer keeps a signature, so even a valid Authenticode
    /// blob stored there is treated as no signature at all.
    /// </summary>
    [Fact]
    public void A_valid_blob_in_a_stream_named_plain_DigitalSignature_is_not_signed()
    {
        using var authority = TestArtifacts.CreateAuthority();
        var blob = TestArtifacts.SignatureBlob(authority);
        var msi = TestArtifacts.CompoundFile("DigitalSignature", blob);

        var result = Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root)).Verify(msi);

        result.IsTrusted.ShouldBeFalse();
        result.Failure.ShouldBe(AuthenticodeFailure.Unsigned);
    }

    // ---- each check, failed in isolation ----------------------------------

    [Fact]
    public void Bytes_that_are_not_a_compound_file_are_not_an_msi()
    {
        using var authority = TestArtifacts.CreateAuthority();

        Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root))
            .Verify(Encoding.ASCII.GetBytes("MZ this is an exe, not an msi"))
            .Failure.ShouldBe(AuthenticodeFailure.NotAnMsi);
    }

    [Fact]
    public void An_unsigned_msi_is_refused_as_unsigned()
    {
        using var authority = TestArtifacts.CreateAuthority();

        var result = Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root))
            .Verify(TestArtifacts.UnsignedMsi());

        result.Failure.ShouldBe(AuthenticodeFailure.Unsigned);
        result.SignerSubject.ShouldBeNull();
    }

    [Fact]
    public void A_signature_stream_that_is_not_pkcs7_is_malformed()
    {
        using var authority = TestArtifacts.CreateAuthority();
        var msi = TestArtifacts.CompoundFile(
            AuthenticodeVerifier.SignatureStreamName, Encoding.ASCII.GetBytes("garbage in the signature slot"));

        Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root)).Verify(msi)
            .Failure.ShouldBe(AuthenticodeFailure.MalformedSignature);
    }

    /// <summary>Flip one byte inside the signature value: the math no longer holds.</summary>
    [Fact]
    public void A_tampered_signature_is_invalid()
    {
        using var authority = TestArtifacts.CreateAuthority();
        var blob = TestArtifacts.SignatureBlob(authority);

        // The encrypted digest is at the tail of the SignedData; corrupt it there.
        blob[^10] ^= 0x5A;
        var msi = TestArtifacts.CompoundFile(AuthenticodeVerifier.SignatureStreamName, blob);

        var result = Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root)).Verify(msi);

        result.IsTrusted.ShouldBeFalse();
        result.Failure.ShouldBeOneOf(AuthenticodeFailure.InvalidSignature, AuthenticodeFailure.MalformedSignature);
    }

    [Fact]
    public void A_certificate_without_the_code_signing_eku_is_refused()
    {
        using var authority = TestArtifacts.CreateAuthority(leafHasCodeSigningEku: false);
        var msi = TestArtifacts.SignedMsi(authority);

        Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root)).Verify(msi)
            .Failure.ShouldBe(AuthenticodeFailure.NotACodeSigningCertificate);
    }

    /// <summary>
    /// The one that keeps the test authority honest. Under system trust -- what
    /// production runs -- a certificate from an authority nobody installed chains
    /// to nothing, and the build is refused.
    /// </summary>
    [Fact]
    public void Under_system_trust_the_test_authority_is_an_untrusted_chain()
    {
        using var authority = TestArtifacts.CreateAuthority();
        var msi = TestArtifacts.SignedMsi(authority);

        var result = Verifier(new SystemTrustChainPolicy()).Verify(msi);

        result.IsTrusted.ShouldBeFalse();
        result.Failure.ShouldBe(AuthenticodeFailure.UntrustedChain);
    }

    [Fact]
    public void A_signature_from_a_different_authority_is_an_untrusted_chain()
    {
        using var trusted = TestArtifacts.CreateAuthority();
        using var other = TestArtifacts.CreateAuthority(rootSubject: "CN=Somebody Else Root");
        var msi = TestArtifacts.SignedMsi(other);

        Verifier(new TestArtifacts.TrustingChainPolicy(trusted.Root)).Verify(msi)
            .Failure.ShouldBe(AuthenticodeFailure.UntrustedChain);
    }

    /// <summary>Fail closed: no configured publisher means nothing is publishable.</summary>
    [Fact]
    public void With_no_publisher_configured_even_a_trusted_signature_is_refused()
    {
        using var authority = TestArtifacts.CreateAuthority();
        var msi = TestArtifacts.SignedMsi(authority);

        foreach (var unset in new[] { null, "", "   " })
        {
            var result = Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root), unset).Verify(msi);

            result.IsTrusted.ShouldBeFalse();
            result.Failure.ShouldBe(AuthenticodeFailure.NoPublisherConfigured);
            result.SignerSubject.ShouldNotBeNull("the signer is still reported, so the operator can see who did sign it");
        }
    }

    [Fact]
    public void A_valid_signature_by_the_wrong_publisher_is_refused()
    {
        using var authority = TestArtifacts.CreateAuthority(leafSubject: "CN=Not Techsara Ltd");
        var msi = TestArtifacts.SignedMsi(authority);

        var result = Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root)).Verify(msi);

        result.Failure.ShouldBe(AuthenticodeFailure.UnexpectedSigner);
        result.SignerSubject.ShouldNotBeNull("the actual signer is reported so the operator sees who did sign it");
        result.SignerSubject!.ShouldContain("Not Techsara");
    }

    // ---- ordering ----------------------------------------------------------

    /// <summary>
    /// Two things wrong -- wrong publisher AND no EKU -- reports the EKU, because
    /// the checks run in the order a fix would need to happen in.
    /// </summary>
    [Fact]
    public void The_first_failing_check_is_the_one_reported()
    {
        using var authority = TestArtifacts.CreateAuthority(
            leafSubject: "CN=Not Techsara Ltd", leafHasCodeSigningEku: false);
        var msi = TestArtifacts.SignedMsi(authority);

        Verifier(new TestArtifacts.TrustingChainPolicy(authority.Root)).Verify(msi)
            .Failure.ShouldBe(AuthenticodeFailure.NotACodeSigningCertificate);
    }

    // ---- messages ----------------------------------------------------------

    /// <summary>Every failure has a sentence an administrator can act on.</summary>
    [Fact]
    public void Every_failure_describes_itself_without_leaking_bytes()
    {
        foreach (AuthenticodeFailure failure in Enum.GetValues<AuthenticodeFailure>())
        {
            var text = AuthenticodeVerification.Failed(failure).Describe();

            text.Length.ShouldBeGreaterThan(20);
            text.ShouldNotContain("0x");
            text.ShouldNotContain("Exception");
        }
    }
}

/// <summary>The compound-file reader on its own, independent of any signature.</summary>
public sealed class CompoundFileTests
{
    private static byte[] Read(byte[] file, string name) =>
        typeof(AuthenticodeVerifier).Assembly
            .GetType("EndpointPlatform.Infrastructure.Agents.CompoundFile")!
            .GetMethod("TryReadStream")!
            .Invoke(null, [(ReadOnlyMemory<byte>)file, name]) as byte[] ?? [];

    [Fact]
    public void Reads_a_stream_stored_in_regular_sectors()
    {
        var payload = RandomNumberGenerator.GetBytes(5000);
        var file = TestArtifacts.CompoundFile("Payload", payload, miniCutoff: 0);

        Read(file, "Payload").ShouldBe(payload);
    }

    [Fact]
    public void Reads_a_stream_stored_in_the_mini_stream()
    {
        var payload = RandomNumberGenerator.GetBytes(700);
        var file = TestArtifacts.CompoundFile("Payload", payload, miniCutoff: 4096);

        Read(file, "Payload").ShouldBe(payload);
    }

    [Fact]
    public void A_stream_spanning_several_sectors_comes_back_intact()
    {
        var payload = RandomNumberGenerator.GetBytes(Sector * 7 + 13);
        var file = TestArtifacts.CompoundFile("Payload", payload, miniCutoff: 0);

        Read(file, "Payload").ShouldBe(payload);
    }

    [Fact]
    public void A_missing_stream_is_null_not_an_exception()
    {
        var file = TestArtifacts.CompoundFile("Other", RandomNumberGenerator.GetBytes(100), 0);

        Read(file, "Payload").ShouldBeEmpty();
    }

    [Fact]
    public void Non_compound_bytes_are_rejected()
    {
        Read(Encoding.ASCII.GetBytes("nope"), "Payload").ShouldBeEmpty();
        Read([], "Payload").ShouldBeEmpty();
    }

    /// <summary>
    /// A file whose directory points past the end of the data must not read out of
    /// range. The reader bounds every sector against the file length.
    /// </summary>
    [Fact]
    public void A_truncated_file_is_refused_rather_than_read_out_of_range()
    {
        var file = TestArtifacts.CompoundFile("Payload", RandomNumberGenerator.GetBytes(3000), 0);
        var truncated = file[..(file.Length - 1024)];

        Read(truncated, "Payload").ShouldBeEmpty();
    }

    /// <summary>
    /// A file whose final sector is short is not a truncated file. Windows
    /// Installer writes packages whose length is not a multiple of the sector
    /// size -- the built agent MSI ends 3,085 bytes into its last 4 KB sector --
    /// and the stream that ends there is whole. The reader used to refuse any
    /// sector it could not read in full, which made that stream unreadable and
    /// the package's string pool "missing".
    /// </summary>
    [Fact]
    public void A_stream_ending_in_a_short_final_sector_is_read_whole()
    {
        var payload = RandomNumberGenerator.GetBytes(Sector * 2 + 100);
        var file = TestArtifacts.CompoundFile("Payload", payload, 0);

        // The writer pads the last data sector with zeros; drop that padding so
        // the file ends exactly where the payload does, as msi.dll would write it.
        var unpadded = file[..(file.Length - (Sector - 100))];

        Read(unpadded, "Payload").ShouldBe(payload);
    }

    /// <summary>Tolerating a short sector must not tolerate a missing one.</summary>
    [Fact]
    public void A_stream_that_claims_more_than_the_file_holds_is_still_refused()
    {
        var payload = RandomNumberGenerator.GetBytes(Sector * 2 + 100);
        var file = TestArtifacts.CompoundFile("Payload", payload, 0);

        // One byte short of the payload's end: the stream cannot be completed.
        var cut = file[..(file.Length - (Sector - 100) - 1)];

        Read(cut, "Payload").ShouldBeEmpty();
    }

    /// <summary>A FAT that loops on itself terminates at the cap, not never.</summary>
    [Fact]
    public void A_cyclic_fat_chain_terminates()
    {
        var file = TestArtifacts.CompoundFile("Payload", RandomNumberGenerator.GetBytes(3000), 0);

        // First data sector is 2 (after FAT and directory); make it point to itself.
        var fatOffset = Sector + 2 * 4;
        file[fatOffset] = 2; file[fatOffset + 1] = 0; file[fatOffset + 2] = 0; file[fatOffset + 3] = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Read(file, "Payload");
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The real 1.4.1 build from this repository, when present on disk: an unsigned
    /// MSI, which the reader must identify as a compound file with no signature.
    /// </summary>
    [Fact]
    public void The_real_unsigned_141_msi_has_no_signature_stream()
    {
        var path = FindRepoFile(Path.Combine(
            "build", "installer", "1.4.1", "EndpointPlatformAgent-1.4.1-x64-UNSIGNED", "EndpointPlatformAgent-1.4.1-x64.msi"));
        if (path is null)
        {
            return; // artifact not on this machine; nothing to assert
        }

        var msi = File.ReadAllBytes(path);

        // Through the real verifier: "Unsigned" proves it was recognised as a
        // compound file (otherwise NotAnMsi) and that no signature stream exists.
        using var authority = TestArtifacts.CreateAuthority();
        var verifier = new AuthenticodeVerifier(
            Options.Create(new AgentReleaseOptions { ExpectedSignerSubject = "CN=Techsara Test Signing" }),
            new TestArtifacts.TrustingChainPolicy(authority.Root),
            NullLogger<AuthenticodeVerifier>.Instance);

        verifier.Verify(msi).Failure.ShouldBe(AuthenticodeFailure.Unsigned);
        Read(msi, AuthenticodeVerifier.SignatureStreamName).ShouldBeEmpty();
    }

    private const int Sector = 512;

    private static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
