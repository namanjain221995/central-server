using System.Text.Json;
using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Inventory.Chrome;

/// <summary>
/// Reads the profile list out of Chrome's <c>Local State</c> file.
/// </summary>
/// <remarks>
/// <para>
/// <c>Local State</c> is the JSON file Chrome keeps beside the profile
/// directories under <c>User Data</c>. Almost all of it is browser state this
/// agent has no interest in; the one part it needs is <c>profile.info_cache</c>,
/// an object keyed by profile directory name whose values describe each profile.
/// The cache, not the directory listing, is the source of truth: <c>User Data</c>
/// also holds directories that are not profiles ("System Profile", "Guest
/// Profile"), and Chrome leaves them out of the cache.
/// </para>
/// <para>
/// Each cache entry also records the Google account the profile is signed in to.
/// The account's id (<c>gaia_id</c>) and its picture are never carried, and
/// because the reader looks up the few fields it needs by name rather than
/// walking the entry, they are never even visited. Nothing here enumerates an
/// entry's properties, and that is deliberate. What is read: the person's name
/// (<c>gaia_given_name</c>, <c>gaia_name</c>) and the account's domain
/// (<c>hosted_domain</c>), because the profile label Chrome shows is made from
/// them -- see <see cref="ReadDisplayName"/> -- and the account's e-mail address
/// (<c>user_name</c>), reported in its own field because it is what an
/// administrator needs to trace a profile to a person.
/// </para>
/// <para>
/// Pure and platform-neutral: it reads a stream and returns records, so every
/// rule can be pinned with fixtures instead of a machine. A file that is not
/// valid JSON yields null rather than an exception -- to the collector that is
/// a file it could not read this snapshot, not a failure of the inventory -- and
/// a malformed entry is skipped rather than failing the file, because one profile
/// Chrome wrote oddly must not hide the rest.
/// </para>
/// </remarks>
public static class ChromeLocalState
{
    /// <summary>
    /// A <c>Local State</c> file is tens of kilobytes; one larger than this is
    /// not worth reading. The caller checks the file length before opening it
    /// as a cheap early refusal; the reader enforces the bound regardless,
    /// reading no stream past it whatever length it reports (see
    /// <see cref="BoundedRead"/> for why a length is a claim, not a promise).
    /// </summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    // A real file nests about six levels deep; the bound stops a crafted one
    // from making the reader recurse. Chrome writes neither trailing commas nor
    // comments, but tolerating them costs nothing and never changes what a
    // well-formed file means, whereas refusing a whole profile list over a stray
    // comma in a hand-edited file helps no one.
    private static readonly JsonDocumentOptions Options = new()
    {
        MaxDepth = 64,
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Reads the profiles from a <c>Local State</c> stream. Returns null when the
    /// stream is longer than <see cref="MaxBytes"/>, is not valid JSON, or is not
    /// an object with a <c>profile.info_cache</c> object; otherwise the profiles
    /// in file order.
    /// </summary>
    /// <remarks>
    /// Only a malformed document is turned into null here. An I/O fault while
    /// reading the stream is the caller's to isolate: it knows which file it
    /// opened and how to report it. A seekable stream over the limit is refused
    /// before a byte of it is read, and no stream is read past the limit
    /// whatever length it reports.
    /// </remarks>
    public static ChromeLocalStateInfo? Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (BoundedRead.ReadAtMost(stream, MaxBytes) is not { } json)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, Options);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChromeLocalStateInfo? Read(JsonElement root)
    {
        if (!TryGetObject(root, "profile", out var profile) || !TryGetObject(profile, "info_cache", out var cache))
        {
            return null;
        }

        var profiles = new List<ChromeProfileInfo>();

        // The key names a directory, and on Windows two spellings of one name are
        // one directory, so a second spelling is the same profile seen twice.
        // Chrome never writes a duplicate; the choice only matters for a file
        // something else edited, and the first entry wins so the result stays in
        // file order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in cache.EnumerateObject())
        {
            if (profiles.Count >= InventoryChrome.MaxProfiles)
            {
                // No single file can contribute more than a whole report carries;
                // reading further only spends memory on rows the normalizer drops.
                break;
            }

            if (entry.Value.ValueKind != JsonValueKind.Object || !IsProfileKey(entry.Name) || !seen.Add(entry.Name))
            {
                continue;
            }

            profiles.Add(new ChromeProfileInfo(
                entry.Name,
                ReadDisplayName(entry.Value),
                ReadFlag(entry.Value, "is_managed"),
                ReadUnixSeconds(entry.Value, "active_time"),
                ReadString(entry.Value, "user_name")));
        }

        return new ChromeLocalStateInfo(profiles, ReadString(profile, "last_used"));
    }

    /// <summary>
    /// Whether a key is a plain directory name. The collector joins the key onto
    /// <c>User Data</c> to find the profile, so a key that could name anything
    /// else -- a separator, a drive, a parent reference, a control character --
    /// is not a profile, whatever Chrome would have made of it. The collector
    /// only reads, but it must never be steered to read outside <c>User Data</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An over-long key is skipped rather than clamped: a clamped key names no
    /// directory, so there would be nothing to report against it.
    /// </para>
    /// <para>
    /// This rule has a twin in the Windows collector (<c>IsPlainProfileKey</c>),
    /// on purpose: this one protects the identity of a record and is pinned
    /// with fixtures on any OS; that one protects the path join and adds what
    /// only the file system's own rules can say -- the characters Windows
    /// refuses and the spellings Win32 quietly rewrites. Defence in depth on a
    /// user-writable file; loosening either must not loosen the other.
    /// </para>
    /// </remarks>
    private static bool IsProfileKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > InventoryChromeProfile.MaxProfileKey)
        {
            return false;
        }

        if (key == "." || key.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in key)
        {
            if (c is '\\' or '/' or ':' || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        // TryGetProperty throws on anything that is not an object, so the kind
        // is checked first: a document whose root is an array or a string is a
        // wrong shape, not an exception.
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// The label Chrome shows for a profile, composed the way Chrome's own
    /// profile menu composes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>name</c> is the profile's local name, and for a profile signed in to a
    /// Google Workspace account Chrome sets it to the account's <b>domain</b>
    /// ("example.com"), not to anything about the person. Reporting that field
    /// alone therefore labelled every managed profile on a PC with the same
    /// company domain -- exactly what a console must not do -- while Chrome's
    /// menu shows "Casey (example.com)", built from the account's given name.
    /// </para>
    /// <para>
    /// So: a local name that is not the domain is the person's own choice and is
    /// used as it is. A local name that is the domain (or no name at all) yields
    /// the given name, then the full name, with the domain in parentheses when
    /// Chrome had labelled the profile with it. A profile with no name at all is
    /// reported without one. Only names are read here; the e-mail address is
    /// reported in its own field, and the account id beside it never is.
    /// </para>
    /// </remarks>
    private static string? ReadDisplayName(JsonElement entry)
    {
        var localName = ReadString(entry, "name");
        var domain = ReadString(entry, "hosted_domain");

        // Chrome's marker for a consumer account; not a domain anyone chose.
        if (string.Equals(domain, "NO_HOSTED_DOMAIN", StringComparison.OrdinalIgnoreCase))
        {
            domain = null;
        }

        var labelledWithDomain = localName is not null
            && domain is not null
            && string.Equals(localName, domain, StringComparison.OrdinalIgnoreCase);

        if (localName is not null && !labelledWithDomain)
        {
            return localName;
        }

        var person = ReadString(entry, "gaia_given_name") ?? ReadString(entry, "gaia_name");
        if (person is null)
        {
            return localName;
        }

        return labelledWithDomain ? $"{person} ({domain})" : person;
    }

    /// <summary>A string field; null when absent, not a string, or blank, because a blank name is no name.</summary>
    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// A flag Chrome has written both ways: current builds write an integer,
    /// older ones a bool.
    /// </summary>
    /// <remarks>
    /// The integer is Chrome's <c>signin::Tribool</c> (-1 unknown, 0 false,
    /// 1 true), which is why "any non-zero number is true" would be wrong: a
    /// profile whose management status Chrome has not yet determined is written
    /// as -1, and reporting it as managed would show an enterprise-managed
    /// profile that Chrome itself knows nothing about. So -1 is unrecorded, and
    /// so is anything else outside the encoding: nullable means "unrecorded",
    /// never a guess.
    /// </remarks>
    private static bool? ReadFlag(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number switch
            {
                0 => false,
                1 => true,
                _ => null,
            };
        }

        return null;
    }

    /// <summary>Floating-point seconds since the Unix epoch, as Chrome writes profile activity.</summary>
    private static DateTimeOffset? ReadUnixSeconds(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var seconds))
        {
            return null;
        }

        return ChromeTime.FromUnixSeconds(seconds);
    }
}
