using Net.Codecrete.QrCodeGenerator;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// Renders the enrolment URI as an SVG QR code.
/// </summary>
/// <remarks>
/// <para>
/// <b>SVG, and rendered on the server.</b> The dashboard carries exactly three
/// runtime dependencies and that restraint is worth keeping; an inline SVG needs
/// no client library, no canvas and no image endpoint, and scales cleanly on any
/// display. The markup is inlined into the enrolment response rather than served
/// from its own URL, so the secret never becomes a fetchable resource with its
/// own cache entry and access-control question.
/// </para>
/// <para>
/// <b>Medium error correction.</b> The default for authenticator QR codes: it
/// tolerates a smudged or partly obscured code without inflating the module
/// count, which matters because an otpauth URI carrying a 32-character secret is
/// already a fairly dense code.
/// </para>
/// </remarks>
public static class TotpQrCode
{
    /// <summary>
    /// Renders <paramref name="uri"/> as a standalone SVG document.
    /// </summary>
    /// <param name="uri">The otpauth:// URI from <see cref="Totp.BuildUri"/>.</param>
    /// <remarks>
    /// The border is four modules, which is the quiet zone the QR specification
    /// requires. Scanners fail on codes rendered flush to their container, and it
    /// is a common and confusing way for enrolment to "not work".
    /// </remarks>
    public static string ToSvg(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        var qr = QrCode.EncodeText(uri, QrCode.Ecc.Medium);

        // Explicit colours rather than relying on inherited CSS: the dashboard has
        // a dark mode, and a QR code that inverts with the theme does not scan.
        return qr.ToSvgString(border: 4, foreground: "#000000", background: "#ffffff");
    }
}
