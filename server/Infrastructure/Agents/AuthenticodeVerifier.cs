using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Agents;

/// <summary>Why an artifact may not be published, or null when it may.</summary>
/// <remarks>
/// One reason, the first that failed, in the order the checks run. The operator
/// needs to know what to fix; the reasons are not independent of each other, and a
/// list would invite fixing the last one first.
/// </remarks>
public enum AuthenticodeFailure
{
    /// <summary>The bytes are not an OLE2 compound file, so not an MSI at all.</summary>
    NotAnMsi,

    /// <summary>The MSI carries no Authenticode signature stream.</summary>
    Unsigned,

    /// <summary>The signature stream exists but is not a valid Authenticode PKCS#7 blob.</summary>
    MalformedSignature,

    /// <summary>The PKCS#7 signature does not verify against its own content.</summary>
    InvalidSignature,

    /// <summary>The signing certificate is not valid for code signing.</summary>
    NotACodeSigningCertificate,

    /// <summary>The signing certificate does not chain to a trusted root.</summary>
    UntrustedChain,

    /// <summary>No expected publisher is configured, so nothing could be trusted.</summary>
    NoPublisherConfigured,

    /// <summary>The signer is valid but is not the configured publisher.</summary>
    UnexpectedSigner,
}

/// <summary>The outcome of verifying one artifact.</summary>
/// <param name="SignerSubject">The verified signer's subject, when the signature was valid.</param>
public sealed record AuthenticodeVerification(
    AuthenticodeFailure? Failure,
    string? SignerSubject,
    string? Detail)
{
    public bool IsTrusted => Failure is null;

    public static AuthenticodeVerification Trusted(string signerSubject) => new(null, signerSubject, null);

    public static AuthenticodeVerification Failed(AuthenticodeFailure failure, string? detail = null, string? signer = null) =>
        new(failure, signer, detail);

    /// <summary>A message safe to return to an administrator. Names the check, never the bytes.</summary>
    public string Describe() => Failure switch
    {
        null => "The artifact is signed by the expected publisher.",
        AuthenticodeFailure.NotAnMsi => "The artifact is not a Windows Installer package.",
        AuthenticodeFailure.Unsigned => "The artifact is not Authenticode-signed.",
        AuthenticodeFailure.MalformedSignature => "The artifact's signature could not be read.",
        AuthenticodeFailure.InvalidSignature => "The artifact's signature is not valid.",
        AuthenticodeFailure.NotACodeSigningCertificate => "The signing certificate is not a code-signing certificate.",
        AuthenticodeFailure.UntrustedChain => "The signing certificate does not chain to a trusted root.",
        AuthenticodeFailure.NoPublisherConfigured => "No expected publisher is configured for agent releases, so no release can be published.",
        AuthenticodeFailure.UnexpectedSigner => "The artifact is signed, but not by the expected publisher.",
        _ => "The artifact could not be verified.",
    };
}

/// <summary>Verifies the Authenticode signature on an MSI.</summary>
public interface IAuthenticodeVerifier
{
    AuthenticodeVerification Verify(ReadOnlyMemory<byte> msi);
}

/// <summary>
/// Adjusts the certificate chain policy used to validate a signer.
/// </summary>
/// <remarks>
/// Exists so tests can trust an in-memory certificate authority. There is no
/// production implementation that widens trust, and the type is internal to the
/// infrastructure assembly on purpose: configuring extra trust anchors is not a
/// feature, it is the hole this whole verifier exists to close.
/// </remarks>
public interface IAuthenticodeChainPolicy
{
    void Apply(X509ChainPolicy policy);
}

/// <summary>System trust only.</summary>
public sealed class SystemTrustChainPolicy : IAuthenticodeChainPolicy
{
    public void Apply(X509ChainPolicy policy)
    {
        policy.TrustMode = X509ChainTrustMode.System;
    }
}

/// <summary>
/// Verifies an MSI's Authenticode signature without Windows.
/// </summary>
/// <remarks>
/// <para>
/// The Admin API runs on Linux, where <c>WinVerifyTrust</c> does not exist. What
/// this verifier establishes is: the MSI carries a signature; it is a well-formed
/// Authenticode PKCS#7 <c>SignedData</c>; the signature verifies against its own
/// content; the signer holds a certificate valid for code signing that chains to a
/// trusted root; and that certificate belongs to the configured publisher.
/// </para>
/// <para>
/// <b>What it deliberately does not claim.</b> Authenticode binds the signature
/// to the file through an <c>SpcIndirectDataContent</c> hash computed under
/// Windows Installer's own rules for which streams are covered. Reproducing that
/// hashing off-Windows is where independent implementations get subtly wrong, and
/// a wrong "verified" is worse than none. That final byte-to-signature binding is
/// enforced where it matters and where the real implementation is available: on
/// the endpoint, by <c>WinVerifyTrust</c> in the agent's update executor, before
/// anything installs. This gate stops an unsigned, mis-signed, or wrongly-signed
/// build from ever being published; the agent stops a tampered one from ever
/// installing. Both are required and neither substitutes for the other.
/// </para>
/// <para>
/// Revocation is not checked here. The server may have no route to a CRL or OCSP
/// responder, and refusing every publish over that would make the gate a
/// liability rather than a control. Nor is it checked on the endpoint: the
/// agent's <c>WinVerifyTrust</c> call runs with revocation checks deliberately
/// disabled (<c>WTD_REVOKE_NONE</c> + <c>WTD_REVOCATION_CHECK_NONE</c>,
/// agent/EndpointAgent.Windows/WinTrust.cs) so installs never depend on
/// revocation-network reachability. A revoked publisher certificate is handled
/// by rotating <c>ExpectedSignerSubject</c> and revoking the affected releases,
/// not by a CRL lookup.
/// </para>
/// </remarks>
public sealed class AuthenticodeVerifier(
    IOptions<AgentReleaseOptions> options,
    IAuthenticodeChainPolicy chainPolicy,
    ILogger<AuthenticodeVerifier> logger) : IAuthenticodeVerifier
{
    /// <summary>
    /// The stream Windows Installer stores its signature in:
    /// <c>\u0005DigitalSignature</c>, with the U+0005 control character first.
    /// </summary>
    /// <remarks>
    /// The prefix is part of the literal stream name: compound files reserve a
    /// leading U+0005 for metadata streams, and signtool writes the signature
    /// under exactly this name -- a stream named plain <c>DigitalSignature</c>,
    /// which no real Windows Installer package carries, is not a signature and
    /// is deliberately not read. The prefix used to be embedded here as a RAW
    /// control byte, which every editor and diff rendered as plain
    /// <c>"DigitalSignature"</c> -- invisible, one well-meaning cleanup away
    /// from being stripped (silently breaking Public mode against every signed
    /// MSI), and already misread once in review as the wrong name. It is an
    /// explicit escape now, and a regression test pins the literal value so it
    /// cannot quietly change in either direction.
    /// </remarks>
    public const string SignatureStreamName = "\u0005DigitalSignature";

    /// <summary>Authenticode's content type: SpcIndirectDataContent.</summary>
    private static readonly Oid SpcIndirectData = new("1.3.6.1.4.1.311.2.1.4");

    /// <summary>Extended key usage: id-kp-codeSigning.</summary>
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";

    private readonly AgentReleaseOptions _options = options.Value;
    private readonly IAuthenticodeChainPolicy _chainPolicy = chainPolicy;
    private readonly ILogger<AuthenticodeVerifier> _logger = logger;

    public AuthenticodeVerification Verify(ReadOnlyMemory<byte> msi)
    {
        if (!CompoundFile.IsCompoundFile(msi.Span))
        {
            return AuthenticodeVerification.Failed(AuthenticodeFailure.NotAnMsi);
        }

        var blob = CompoundFile.TryReadStream(msi, SignatureStreamName);
        if (blob is null || blob.Length == 0)
        {
            return AuthenticodeVerification.Failed(AuthenticodeFailure.Unsigned);
        }

        SignedCms cms;
        try
        {
            cms = new SignedCms();
            cms.Decode(blob);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning("Agent release signature stream could not be decoded: {Reason}", ex.Message);
            return AuthenticodeVerification.Failed(AuthenticodeFailure.MalformedSignature);
        }

        if (cms.ContentInfo.ContentType.Value != SpcIndirectData.Value || cms.SignerInfos.Count == 0)
        {
            return AuthenticodeVerification.Failed(AuthenticodeFailure.MalformedSignature,
                "The signature stream is not an Authenticode SignedData.");
        }

        // The signature math, over the signed content. Chain trust is checked
        // separately below so the reason for a refusal is precise.
        try
        {
            cms.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning("Agent release signature failed to verify: {Reason}", ex.Message);
            return AuthenticodeVerification.Failed(AuthenticodeFailure.InvalidSignature);
        }

        var signer = cms.SignerInfos[0].Certificate;
        if (signer is null)
        {
            return AuthenticodeVerification.Failed(AuthenticodeFailure.InvalidSignature,
                "The signer's certificate is not present in the signature.");
        }

        if (!HasCodeSigningEku(signer))
        {
            return AuthenticodeVerification.Failed(
                AuthenticodeFailure.NotACodeSigningCertificate, signer: signer.Subject);
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigningEku));

        // Timestamped signatures stay valid past certificate expiry, so the chain
        // is evaluated at signing time when a countersignature says when that was.
        var signingTime = SigningTime(cms.SignerInfos[0]);
        if (signingTime is { } at)
        {
            chain.ChainPolicy.VerificationTime = at;
        }

        // Intermediates travel inside the PKCS#7; hand them to the builder.
        chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
        _chainPolicy.Apply(chain.ChainPolicy);

        if (!chain.Build(signer))
        {
            var statuses = string.Join("; ", chain.ChainStatus.Select(s => s.Status));
            _logger.LogWarning("Agent release signer {Subject} does not chain to a trusted root: {Status}",
                signer.Subject, statuses);
            return AuthenticodeVerification.Failed(
                AuthenticodeFailure.UntrustedChain, statuses, signer.Subject);
        }

        if (!_options.IsSignerConfigured)
        {
            return AuthenticodeVerification.Failed(
                AuthenticodeFailure.NoPublisherConfigured, signer: signer.Subject);
        }

        // Same rule as the agent's pin, so both ends agree on "expected publisher".
        if (!signer.Subject.Contains(_options.ExpectedSignerSubject!, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Agent release is signed by {Subject}, not the expected publisher.", signer.Subject);
            return AuthenticodeVerification.Failed(
                AuthenticodeFailure.UnexpectedSigner, signer: signer.Subject);
        }

        return AuthenticodeVerification.Trusted(signer.Subject);
    }

    private static bool HasCodeSigningEku(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509EnhancedKeyUsageExtension eku)
            {
                foreach (var oid in eku.EnhancedKeyUsages)
                {
                    if (oid.Value == CodeSigningEku)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        // No EKU extension at all: a general-purpose certificate, not a code-signing one.
        return false;
    }

    /// <summary>The signing time from a signed attribute, when present.</summary>
    private static DateTime? SigningTime(SignerInfo signer)
    {
        foreach (var attribute in signer.SignedAttributes)
        {
            if (attribute.Oid.Value == "1.2.840.113549.1.9.5" && attribute.Values.Count > 0
                && attribute.Values[0] is Pkcs9SigningTime signingTime)
            {
                return signingTime.SigningTime;
            }
        }

        return null;
    }
}
