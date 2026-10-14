using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace EndpointAgent.CompanyPortal;

/// <summary>
/// Icons the portal has already fetched, keyed by app id and the catalog's
/// <c>updatedAt</c>, so an app whose tile changed on the server is fetched again
/// and one that did not is not.
/// </summary>
/// <remarks>
/// <para>
/// An icon is the one thing from the wire that is not text. It is decoded here,
/// once, into a bitmap the tiles draw; decoding is displaying, not executing,
/// and it is done by the same image decoder every picture on the desktop goes
/// through. Still, it is treated as data of unknown quality: the bytes must
/// begin with the PNG signature and an IHDR chunk, must fit the protocol's
/// ceiling (the parser already enforced that), and must declare -- and then
/// decode to -- a picture no larger than a tile could use. The declared size is
/// checked before the decoder is called at all, because a few kilobytes of PNG
/// can describe an enormous image and the decoder would allocate it in full.
/// </para>
/// <para>
/// A failed decode is remembered too, as "no icon", so the portal does not ask
/// for the same unusable icon on every refresh.
/// </para>
/// </remarks>
internal sealed class IconCache : IDisposable
{
    /// <summary>Wider than any tile draws; a legitimate icon is 48 to 256 pixels.</summary>
    private const int MaxDecodedPixels = 512;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The first chunk of every PNG is IHDR, and its first eight bytes are the
    /// width and height. Offsets: 8-byte signature, 4-byte chunk length, 4-byte
    /// chunk type, then width at 16 and height at 20, both big-endian.
    /// </summary>
    private static readonly byte[] IhdrType = [(byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    private const int IhdrTypeOffset = 12;
    private const int IhdrWidthOffset = 16;
    private const int IhdrHeightOffset = 20;
    private const int IhdrHeaderEnd = 24;

    private readonly Dictionary<Guid, (DateTimeOffset UpdatedAt, Image? Image)> _entries = [];

    /// <summary>True when the icon for this app and catalog version was already fetched, with or without success.</summary>
    public bool TryGet(Guid id, DateTimeOffset updatedAt, out Image? image)
    {
        if (_entries.TryGetValue(id, out var entry) && entry.UpdatedAt == updatedAt)
        {
            image = entry.Image;
            return true;
        }

        image = null;
        return false;
    }

    /// <summary>Records the icon, or the absence of one, replacing whatever an older catalog version had.</summary>
    public void Put(Guid id, DateTimeOffset updatedAt, Image? image)
    {
        if (_entries.TryGetValue(id, out var old))
        {
            old.Image?.Dispose();
        }

        _entries[id] = (updatedAt, image);
    }

    /// <summary>Turns the service's PNG bytes into a bitmap, or null when they are not a picture this portal will draw.</summary>
    public static Image? Decode(byte[] png)
    {
        if (!HasDrawableHeader(png))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png, writable: false);
            using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);

            // Belt and braces: the header was checked before decoding, but the
            // decoder has the last word on what the picture actually is.
            if (decoded.Width <= 0 || decoded.Height <= 0 || decoded.Width > MaxDecodedPixels || decoded.Height > MaxDecodedPixels)
            {
                return null;
            }

            // A copy, so the bitmap outlives the stream it was decoded from.
            return new Bitmap(decoded);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException)
        {
            // GDI+ reports a malformed image as any of these; none is a reason to stop drawing tiles.
            return null;
        }
    }

    /// <summary>
    /// The size check that must come first. The decoder validates the whole image
    /// before it can be asked anything, and a few kilobytes of PNG whose header
    /// claims 30000 by 30000 would have it allocate gigabytes on the window
    /// thread. The declared size is read straight from the bytes instead, and a
    /// picture too large for a tile never reaches GDI+.
    /// </summary>
    internal static bool HasDrawableHeader(ReadOnlySpan<byte> png)
    {
        if (png.Length < IhdrHeaderEnd
            || !png[..PngSignature.Length].SequenceEqual(PngSignature)
            || !png.Slice(IhdrTypeOffset, IhdrType.Length).SequenceEqual(IhdrType))
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(png[IhdrWidthOffset..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(png[IhdrHeightOffset..]);
        return width is > 0 and <= MaxDecodedPixels && height is > 0 and <= MaxDecodedPixels;
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Image?.Dispose();
        }

        _entries.Clear();
    }
}

/// <summary>
/// The tile an app gets when the catalog has no icon for it: a rounded square
/// in a colour chosen by the name, with the name's first character on it.
/// </summary>
internal static class PlaceholderIcon
{
    /// <summary>Muted, readable under white text, and fixed: nothing from the wire picks a colour except by hashing into this list.</summary>
    private static readonly Color[] Palette =
    [
        Color.FromArgb(0x2B, 0x6C, 0xB0),
        Color.FromArgb(0x2F, 0x85, 0x5A),
        Color.FromArgb(0xB7, 0x6E, 0x2A),
        Color.FromArgb(0x80, 0x4F, 0xB3),
        Color.FromArgb(0xC5, 0x3B, 0x3B),
        Color.FromArgb(0x1F, 0x8A, 0x8A),
        Color.FromArgb(0x6B, 0x5B, 0x95),
        Color.FromArgb(0x4A, 0x6F, 0x8A),
    ];

    public static Bitmap Create(string displayName, int sizePixels, Font font)
    {
        var bitmap = new Bitmap(sizePixels, sizePixels);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var radius = Math.Max(2, sizePixels / 5);
        using var path = RoundedSquare(sizePixels, radius);
        using var fill = new SolidBrush(Palette[ColourIndex(displayName)]);
        graphics.FillPath(fill, path);

        using var letterFont = new Font(font.FontFamily, sizePixels * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        graphics.DrawString(FirstGlyph(displayName), letterFont, Brushes.White, new RectangleF(0, 0, sizePixels, sizePixels), format);

        return bitmap;
    }

    /// <summary>The first visible character of the name, whole even when it takes two UTF-16 units, upper-cased.</summary>
    private static string FirstGlyph(string displayName)
    {
        var trimmed = displayName.Trim();
        if (trimmed.Length == 0)
        {
            return "?";
        }

        return StringInfo.GetNextTextElement(trimmed).ToUpperInvariant();
    }

    private static int ColourIndex(string displayName)
    {
        // Stable across runs, which string.GetHashCode is not: the same app keeps the same colour.
        var sum = 0;
        foreach (var c in displayName)
        {
            sum = unchecked(sum * 31 + c);
        }

        return (sum & 0x7FFFFFFF) % Palette.Length;
    }

    private static GraphicsPath RoundedSquare(int size, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(size - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(size - diameter, size - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, size - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
