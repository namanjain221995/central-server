using System.Text.Json;
using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Inventory.Chrome;

/// <summary>
/// Reads the extension records out of a profile's <c>Secure Preferences</c> or
/// <c>Preferences</c> file.
/// </summary>
/// <remarks>
/// <para>
/// Both files are JSON of the same shape: <c>extensions.settings</c> is an
/// object keyed by extension id, and each value is Chrome's own record of the
/// extension -- where it came from, whether it is disabled, and a copy of the
/// manifest with the display name already localised. That copy is why the
/// extension's own <c>manifest.json</c> is never opened: on disk the name is a
/// placeholder (<c>__MSG_extName__</c>) that only Chrome's locale files resolve,
/// and the extension directory is code the extension controls rather than a
/// record Chrome keeps.
/// </para>
/// <para>
/// Pure and platform-neutral: a stream in, records out. Only the handful of
/// properties the inventory carries are read. The record also holds the
/// extension's public key, its granted permissions and its install path; none
/// of those are looked at, so none can leak into a report by accident.
/// </para>
/// <para>
/// Nothing here throws into the inventory path. A file that is not JSON, not an
/// object, or has no settings yields an empty list; an entry that is not shaped
/// like one is skipped and the rest are still read. Chrome rewrites these files
/// on its own schedule and authenticates them with a MAC, so a half-written or
/// oddly shaped file is a thing that happens, not an error worth surfacing.
/// </para>
/// </remarks>
public static class ChromeExtensionSettings
{
    /// <summary>
    /// A profile with hundreds of extensions writes a few megabytes; a file
    /// larger than this is not worth reading into memory. The reader enforces
    /// the bound itself, reading no stream past it whatever length it reports
    /// (see <see cref="BoundedRead"/> for why a length is a claim, not a promise).
    /// </summary>
    public const long MaxBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Older Chrome's <c>state</c> for an externally installed extension the
    /// user removed (<c>EXTERNAL_EXTENSION_UNINSTALLED</c>). The record was kept
    /// only so the extension would not be installed again.
    /// </summary>
    private const int StateExternalExtensionUninstalled = 2;

    /// <summary>
    /// The most entries read from one file. A memory bound well above the
    /// <see cref="InventoryChromeProfile.MaxExtensions"/> a report carries, so
    /// that which entries survive is decided by the normalizer, not by where an
    /// entry happens to sit in the file.
    /// </summary>
    public const int MaxEntries = 4 * InventoryChromeProfile.MaxExtensions;

    // Chrome writes plain JSON, but a hand-edited or partly rewritten file may
    // carry a trailing comma or a comment; neither is a reason to lose the whole
    // extension list. The depth cap is far above the six levels a record uses,
    // and exists only so a hostile file cannot make the reader recurse.
    private static readonly JsonDocumentOptions Options = new()
    {
        MaxDepth = 64,
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Reads every extension record in the file, in file order. Empty when the
    /// stream is not a preferences file with an <c>extensions.settings</c> object.
    /// </summary>
    /// <remarks>
    /// A seekable stream that admits to being longer than <see cref="MaxBytes"/>
    /// is refused without being read, and no stream is read past that bound
    /// whatever length it reports: the file belongs to the user whose profile
    /// it is in and can grow between the collector's length check and the read.
    /// </remarks>
    public static IReadOnlyList<ChromeExtensionEntry> Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (BoundedRead.ReadAtMost(stream, MaxBytes) is not { } json)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json, Options);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<ChromeExtensionEntry> Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("extensions", out var extensions)
            || extensions.ValueKind != JsonValueKind.Object
            || !extensions.TryGetProperty("settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var entries = new List<ChromeExtensionEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in settings.EnumerateObject())
        {
            if (entries.Count >= MaxEntries)
            {
                break;
            }

            // The key is the identity. Chrome only ever writes ids here, so
            // anything else is a file that has been tampered with or corrupted,
            // and the safe reading of a key that is not an id is "not an entry".
            if (!InventoryChromeExtension.IsValidExtensionId(property.Name)
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // JSON permits a repeated key and Chrome never writes one; the first
            // occurrence wins so a repeated key cannot become two rows.
            if (!seen.Add(property.Name))
            {
                continue;
            }

            // A tombstone, not an extension: older Chrome kept the record of an
            // externally installed extension the user removed, with its manifest
            // and location intact, purely so the external source would not put
            // it back. Nothing is installed, so reporting it -- even as disabled
            // -- would be a false row. Current Chrome migrates these out of the
            // settings altogether, so only a profile not opened since carries one.
            if (Integer(property.Value, "state") == StateExternalExtensionUninstalled)
            {
                continue;
            }

            // Bookkeeping, not an extension. Chrome writes the manifest and the
            // location into a record when it installs; a record with neither is
            // something else the settings map also holds -- an empty leftover, a
            // declined or still-pending external install -- and chrome://extensions
            // lists none of them. The first real profile carried four such empty
            // records, which came out as four "(unnamed)" rows until this check.
            if (!HasObject(property.Value, "manifest") && Integer(property.Value, "location") is null)
            {
                continue;
            }

            entries.Add(ReadEntry(property.Name, property.Value));
        }

        return entries;
    }

    private static ChromeExtensionEntry ReadEntry(string extensionId, JsonElement record)
    {
        string? name = null;
        string? version = null;
        string? updateUrl = null;
        int? manifestVersion = null;

        // A record with a location but no manifest (an install Chrome has
        // registered but not yet unpacked) is still an entry: the id and location
        // say it exists. Name and version are simply unrecorded.
        if (record.TryGetProperty("manifest", out var manifest) && manifest.ValueKind == JsonValueKind.Object)
        {
            name = Text(manifest, "name");
            version = Text(manifest, "version");
            updateUrl = Text(manifest, "update_url");
            manifestVersion = Integer(manifest, "manifest_version");
        }

        return new ChromeExtensionEntry(
            extensionId,
            name,
            version,
            manifestVersion,
            Enabled(record),
            Integer(record, "location"),
            Flag(record, "from_webstore"),
            updateUrl,
            // Both times are strings holding int64 microseconds since 1601: the
            // JSON number type cannot carry the value exactly, so Chrome quotes it.
            // A number here is not Chrome's encoding and is not trusted.
            ChromeTime.FromWindowsMicroseconds(Text(record, "first_install_time")),
            ChromeTime.FromWindowsMicroseconds(Text(record, "last_update_time")));
    }

    /// <summary>
    /// Whether Chrome has the extension enabled, from whichever encoding the
    /// record uses. Enabled when the record carries no enablement key at all;
    /// null when it carries one the reader cannot make sense of.
    /// </summary>
    private static bool? Enabled(JsonElement record)
    {
        var hasReasons = record.TryGetProperty("disable_reasons", out var reasons);
        var hasState = record.TryGetProperty("state", out _);

        // Current Chrome: a list of reason codes, empty when enabled. The list is
        // the authority whenever it is present, whatever an older "state" left
        // beside it says, because it is what this Chrome consults.
        if (hasReasons && reasons.ValueKind == JsonValueKind.Array)
        {
            return reasons.GetArrayLength() == 0;
        }

        // Before the list, "state" (1 enabled, 0 disabled) was what Chrome itself
        // read to decide, and "disable_reasons" was a bitmask that only explained
        // why. So in a record from that era state wins over the mask: a record
        // could be disabled with a mask of zero, and reading the mask first would
        // call it enabled. A state outside the encoding is not an answer and
        // decides nothing.
        switch (Integer(record, "state"))
        {
            case 0:
                return false;
            case 1:
                return true;
        }

        // A bitmask with no state beside it. Chrome kept the same key when it
        // changed the value from a bitmask to a list, so a record not yet
        // rewritten by a newer Chrome can still carry the number, where zero
        // means no reason to be disabled.
        if (hasReasons && reasons.ValueKind == JsonValueKind.Number && reasons.TryGetInt64(out var mask))
        {
            return mask == 0;
        }

        // No enablement key at all is how current Chrome writes an extension that
        // has never been disabled: the list is created the first time a reason is
        // recorded. Real profiles carry enabled, toolbar-visible extensions with
        // neither key, and reporting them as unknown hid their state. A key that
        // is present but unreadable is different: that is an answer the reader
        // cannot trust, so it stays unrecorded.
        return hasReasons || hasState ? null : true;
    }

    /// <summary>Whether the property is present and is a JSON object.</summary>
    private static bool HasObject(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object;

    /// <summary>A string property, trimmed; null when absent, not a string, or blank.</summary>
    private static string? Text(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text
            && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    /// <summary>A number property that fits an int; null otherwise, including for a quoted number.</summary>
    private static int? Integer(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>A boolean property; null when absent or anything but a JSON true/false.</summary>
    private static bool? Flag(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
