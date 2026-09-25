using System.Text;
using System.Text.Json;
using EndpointAgent.Core.Inventory.Chrome;
using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Tests.Inventory.Chrome;

/// <summary>
/// Reading the profile list out of Chrome's <c>Local State</c>: the few fields
/// discovery needs, and refusal or tolerance of everything else the file holds.
/// </summary>
/// <remarks>
/// Fixtures follow the real file's structure -- the other top-level sections,
/// the full key set of an info cache entry, the account fields -- with invented
/// values. The file is Chrome's, not ours, so a malformed one is not an edge
/// case but the point: it yields null or a skipped entry, never an exception.
/// </remarks>
public sealed class ChromeLocalStateTests
{
    /// <summary>
    /// A Local State with two profiles, trimmed to the sections that matter and
    /// the noise around them. The account fields carry invented values so the
    /// test that they are never carried has something to look for.
    /// </summary>
    private const string TwoProfiles = """
        {
          "background_mode": { "enabled": false },
          "browser": {
            "enabled_labs_experiments": [],
            "last_redirect_origin": ""
          },
          "hardware_acceleration_mode_previous": true,
          "os_crypt": {
            "app_bound_encrypted_key": "QVBQQg==",
            "encrypted_key": "RFBBUEk="
          },
          "profile": {
            "info_cache": {
              "Default": {
                "active_time": 1700000000.25,
                "avatar_icon": "chrome://theme/IDR_PROFILE_AVATAR_26",
                "background_apps": false,
                "default_avatar_fill_color": -14273992,
                "default_avatar_stroke_color": -1,
                "first_account_name_hash": 3423214641,
                "force_signin_profile_locked": false,
                "gaia_given_name": "Casey",
                "gaia_id": "100000000000000000001",
                "gaia_name": "Casey Example",
                "gaia_picture_file_name": "Google Profile Picture.png",
                "hosted_domain": "NO_HOSTED_DOMAIN",
                "is_consented_primary_account": true,
                "is_ephemeral": false,
                "is_managed": 0,
                "is_using_default_avatar": false,
                "is_using_default_name": false,
                "managed_user_id": "",
                "metrics_bucket_index": 1,
                "name": "Casey",
                "profile_color_seed": -11361325,
                "profile_highlight_color": -11361325,
                "signin.with_credential_provider": false,
                "use_gaia_picture": true,
                "user_accepted_account_management": false,
                "user_name": "casey.example@example.com"
              },
              "Profile 9": {
                "active_time": 1720000000,
                "avatar_icon": "chrome://theme/IDR_PROFILE_AVATAR_44",
                "background_apps": false,
                "gaia_id": "100000000000000000002",
                "gaia_name": "Casey Example",
                "hosted_domain": "example.com",
                "is_consented_primary_account": true,
                "is_ephemeral": false,
                "is_managed": 1,
                "is_using_default_avatar": true,
                "is_using_default_name": false,
                "managed_user_id": "",
                "metrics_bucket_index": 2,
                "name": "Work",
                "profile_color_seed": -14273992,
                "user_accepted_account_management": true,
                "user_name": "casey@example.com"
              }
            },
            "last_active_profiles": [ "Profile 9" ],
            "last_used": "Profile 9",
            "metrics": { "next_bucket_index": 3 },
            "picker_shown": true,
            "profiles_created": 2,
            "profiles_order": [ "Default", "Profile 9" ]
          },
          "session_id_generator_last_value": "12345",
          "shutdown": { "num_processes": 8, "num_processes_slow": 0, "type": 1 },
          "variations_compressed_seed": "H4sIAAAAAAAA",
          "variations_seed_signature": "MEUCIQDinvented"
        }
        """;

    // ---- well-formed --------------------------------------------------------------------

    [Fact]
    public void Reads_every_profile_in_file_order()
    {
        var state = Parse(TwoProfiles).ShouldNotBeNull();

        state.Profiles.Count.ShouldBe(2);

        var first = state.Profiles[0];
        first.ProfileKey.ShouldBe("Default");
        first.Name.ShouldBe("Casey");
        first.IsManaged.ShouldBe(false);
        first.LastActiveAt.ShouldBe(new DateTimeOffset(2023, 11, 14, 22, 13, 20, TimeSpan.Zero));

        var second = state.Profiles[1];
        second.ProfileKey.ShouldBe("Profile 9");
        second.Name.ShouldBe("Work");
        second.IsManaged.ShouldBe(true);
        second.LastActiveAt.ShouldBe(new DateTimeOffset(2024, 7, 3, 9, 46, 40, TimeSpan.Zero));
    }

    [Fact]
    public void Reads_the_last_used_profile()
    {
        Parse(TwoProfiles).ShouldNotBeNull().LastUsed.ShouldBe("Profile 9");
    }

    /// <summary>
    /// The entry names the Google account the profile is signed in to. Nothing
    /// of it may reach a record: the record has no field for it, and this pins
    /// that no field is ever added that carries it under another name.
    /// </summary>
    [Fact]
    public void Nothing_about_the_google_account_is_carried()
    {
        var state = Parse(TwoProfiles).ShouldNotBeNull();

        var carried = JsonSerializer.Serialize(state);

        carried.ShouldNotContain("example.com");
        carried.ShouldNotContain("Casey Example");
        carried.ShouldNotContain("100000000000000000001");
        carried.ShouldNotContain("100000000000000000002");
    }

    /// <summary>
    /// The number is Chrome's <c>signin::Tribool</c>: -1 is "not yet determined",
    /// which must never come out as managed, and anything else outside 0/1 is
    /// unrecorded rather than a guess.
    /// </summary>
    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("-1", null)]
    [InlineData("2", null)]
    [InlineData("1.0", null)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("\"1\"", null)]
    [InlineData("null", null)]
    [InlineData("[]", null)]
    public void The_managed_flag_is_read_as_a_number_or_a_bool(string json, bool? expected)
    {
        var state = Parse(Document($$"""
            "Default": { "name": "Solo", "is_managed": {{json}} }
            """)).ShouldNotBeNull();

        state.Profiles.ShouldHaveSingleItem().IsManaged.ShouldBe(expected);
    }

    [Theory]
    [InlineData("\"name\": \"Solo\",", "Solo")]
    [InlineData("\"name\": \"  Spaced  \",", "Spaced")]
    [InlineData("\"name\": \"\",", null)]
    [InlineData("\"name\": \"   \",", null)]
    [InlineData("\"name\": 5,", null)]
    [InlineData("\"name\": null,", null)]
    [InlineData("", null)]
    public void The_name_is_read_only_when_it_is_a_string_with_something_in_it(string nameProperty, string? expected)
    {
        var state = Parse(Document($$"""
            "Default": { {{nameProperty}} "is_managed": 0 }
            """)).ShouldNotBeNull();

        state.Profiles.ShouldHaveSingleItem().Name.ShouldBe(expected);
    }

    /// <summary>Chrome writes fractional seconds; whole seconds are all activity needs.</summary>
    [Fact]
    public void The_active_time_is_read_to_the_second()
    {
        var state = Parse(Document("""
            "Default": { "name": "Solo", "active_time": 1700000000.999 }
            """)).ShouldNotBeNull();

        state.Profiles.ShouldHaveSingleItem().LastActiveAt
            .ShouldBe(new DateTimeOffset(2023, 11, 14, 22, 13, 20, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("\"active_time\": \"1700000000\",")]
    [InlineData("\"active_time\": null,")]
    [InlineData("\"active_time\": true,")]
    [InlineData("\"active_time\": {},")]
    [InlineData("\"active_time\": 0,")]
    [InlineData("\"active_time\": -1,")]
    [InlineData("\"active_time\": 1e300,")]
    [InlineData("")]
    public void An_active_time_that_is_missing_or_not_a_plausible_number_is_unrecorded(string activeTimeProperty)
    {
        var state = Parse(Document($$"""
            "Default": { {{activeTimeProperty}} "name": "Solo" }
            """)).ShouldNotBeNull();

        state.Profiles.ShouldHaveSingleItem().LastActiveAt.ShouldBeNull();
    }

    [Fact]
    public void Fields_the_entry_does_not_record_are_null_not_defaulted()
    {
        var state = Parse(Document("""
            "Default": { "avatar_icon": "chrome://theme/IDR_PROFILE_AVATAR_26", "background_apps": false }
            """)).ShouldNotBeNull();

        var profile = state.Profiles.ShouldHaveSingleItem();
        profile.ProfileKey.ShouldBe("Default");
        profile.Name.ShouldBeNull();
        profile.IsManaged.ShouldBeNull();
        profile.LastActiveAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("\"last_used\": \"Profile 9\",", "Profile 9")]
    [InlineData("\"last_used\": \"\",", null)]
    [InlineData("\"last_used\": 9,", null)]
    [InlineData("\"last_used\": null,", null)]
    [InlineData("", null)]
    public void The_last_used_profile_is_read_only_when_it_is_a_string(string lastUsedProperty, string? expected)
    {
        var state = Parse(Document("""
            "Default": { "name": "Solo" }
            """, lastUsedProperty)).ShouldNotBeNull();

        state.LastUsed.ShouldBe(expected);
    }

    [Fact]
    public void An_empty_info_cache_is_a_local_state_with_no_profiles()
    {
        var state = Parse("""{ "profile": { "info_cache": {} } }""").ShouldNotBeNull();

        state.Profiles.ShouldBeEmpty();
        state.LastUsed.ShouldBeNull();
    }

    [Fact]
    public void Trailing_commas_and_comments_are_tolerated()
    {
        var state = Parse("""
            {
              // Chrome writes neither, but a hand-edited file may carry both.
              "profile": {
                "info_cache": {
                  "Default": { "name": "Solo", "is_managed": 0, },
                },
                "last_used": "Default",
              },
            }
            """).ShouldNotBeNull();

        var profile = state.Profiles.ShouldHaveSingleItem();
        profile.ProfileKey.ShouldBe("Default");
        profile.Name.ShouldBe("Solo");
        profile.IsManaged.ShouldBe(false);
        state.LastUsed.ShouldBe("Default");
    }

    // ---- entries that are skipped -------------------------------------------------------

    [Fact]
    public void An_entry_that_is_not_an_object_is_skipped()
    {
        var state = Parse(Document("""
            "Default": "not an object",
            "Profile 1": 7,
            "Profile 2": null,
            "Profile 3": [ { "name": "in a list" } ],
            "Profile 4": { "name": "Kept" }
            """)).ShouldNotBeNull();

        var profile = state.Profiles.ShouldHaveSingleItem();
        profile.ProfileKey.ShouldBe("Profile 4");
        profile.Name.ShouldBe("Kept");
    }

    /// <summary>
    /// The key is joined onto the User Data directory to find the profile. A key
    /// that could point anywhere else is not a profile, and the check runs on the
    /// decoded key, so an escaped separator is caught as well as a literal one.
    /// </summary>
    /// <remarks>
    /// Each row is the key as it sits in the JSON text, so <c>\\</c> and <c>\/</c>
    /// are JSON escapes that decode to one backslash and one slash. A lone
    /// backslash before an ordinary character is not an escape at all but a
    /// malformed document, which the reader refuses whole; that belongs to
    /// <see cref="A_document_that_is_not_valid_json_is_refused"/>, not here.
    /// </remarks>
    [Theory]
    [InlineData(@"..\\Secrets")]
    [InlineData("Profile/9")]
    [InlineData(@"Profile\/9")]
    [InlineData(@"Profile\\9")]
    [InlineData("C:")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("Profile..9")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Profile\u0000")]
    public void A_key_that_is_not_a_plain_directory_name_is_skipped(string key)
    {
        var state = Parse(Document($$"""
            "{{key}}": { "name": "Odd" },
            "Default": { "name": "Kept" }
            """)).ShouldNotBeNull();

        state.Profiles.ShouldHaveSingleItem().ProfileKey.ShouldBe("Default");
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Profile 9")]
    [InlineData("Person 1.old")]
    [InlineData("Guest Profile")]
    public void A_plain_directory_name_is_a_key(string key)
    {
        var state = Parse(Document($$"""
            "{{key}}": { "name": "Kept" }
            """)).ShouldNotBeNull();

        state.Profiles.ShouldHaveSingleItem().ProfileKey.ShouldBe(key);
    }

    /// <summary>An over-long key is skipped, not clamped: a clamped key names no directory.</summary>
    [Fact]
    public void A_key_at_the_length_limit_is_kept_and_one_over_it_is_skipped()
    {
        var atLimit = new string('p', InventoryChromeProfile.MaxProfileKey);
        var overLimit = atLimit + "p";

        var state = Parse(Document($$"""
            "{{overLimit}}": { "name": "Too long" },
            "{{atLimit}}": { "name": "Fits" }
            """)).ShouldNotBeNull();

        state.Profiles.ShouldHaveSingleItem().ProfileKey.ShouldBe(atLimit);
    }

    /// <summary>
    /// Chrome never writes a duplicate key, but a key names a directory and on
    /// Windows two spellings of one name are one directory, so a second entry
    /// for it is the same profile seen twice. The first wins.
    /// </summary>
    [Fact]
    public void The_same_key_seen_twice_is_one_row()
    {
        var state = Parse(Document("""
            "Default": { "name": "First" },
            "Default": { "name": "Second" },
            "default": { "name": "Third" }
            """)).ShouldNotBeNull();

        var profile = state.Profiles.ShouldHaveSingleItem();
        profile.ProfileKey.ShouldBe("Default");
        profile.Name.ShouldBe("First");
    }

    [Fact]
    public void Profiles_are_capped_at_what_a_report_carries()
    {
        var entries = string.Join(",", Enumerable.Range(0, InventoryChrome.MaxProfiles + 8).Select(i => $$"""
            "Profile {{i}}": { "name": "Person {{i}}" }
            """));

        var state = Parse(Document(entries)).ShouldNotBeNull();

        state.Profiles.Count.ShouldBe(InventoryChrome.MaxProfiles);
        state.Profiles[0].ProfileKey.ShouldBe("Profile 0");
        state.Profiles[^1].ProfileKey.ShouldBe($"Profile {InventoryChrome.MaxProfiles - 1}");
    }

    // ---- documents that are refused -----------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("""{ "profile": { "info_cache": { "Default": { "name": "x" } } }""")]
    [InlineData("""{ "profile": { "info_cache": { "Default": { "name": "x" } } } } trailing""")]
    [InlineData("""{ "profile": { "info_cache": { "Default": { "name": "x" } } } }{}""")]
    // A backslash before an ordinary character is not a JSON escape: the whole
    // file is refused, so a key with one never reaches the directory-name check.
    [InlineData("""{ "profile": { "info_cache": { "Profile\9": { "name": "x" } } } }""")]
    public void A_document_that_is_not_valid_json_is_refused(string json)
    {
        Should.NotThrow(() => Parse(json)).ShouldBeNull();
    }

    [Fact]
    public void Bytes_that_are_not_utf8_are_refused()
    {
        using var stream = new MemoryStream(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D });

        Should.NotThrow(() => ChromeLocalState.Parse(stream)).ShouldBeNull();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("""{ "browser": {}, "profile": "Default" }""")]
    [InlineData("""{ "browser": {}, "profile": [] }""")]
    [InlineData("""{ "profile": {} }""")]
    [InlineData("""{ "profile": { "last_used": "Default" } }""")]
    [InlineData("""{ "profile": { "info_cache": null } }""")]
    [InlineData("""{ "profile": { "info_cache": [ { "name": "x" } ] } }""")]
    public void A_document_without_a_profile_cache_object_is_not_a_local_state(string json)
    {
        Should.NotThrow(() => Parse(json)).ShouldBeNull();
    }

    /// <summary>The reader is bounded in depth; a file that nests past it is refused whole, not recursed into.</summary>
    [Fact]
    public void A_document_nested_deeper_than_the_bound_is_refused()
    {
        var deep = new string('[', 100) + new string(']', 100);

        var json = $$"""{ "browser": {{deep}}, "profile": { "info_cache": { "Default": { "name": "x" } } } }""";

        Should.NotThrow(() => Parse(json)).ShouldBeNull();
    }

    /// <summary>
    /// The length is checked before a byte is read, which is what the position
    /// pins: a stream over the limit is refused untouched, one at the limit is
    /// read as usual.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void A_stream_is_read_only_when_it_is_within_the_size_limit(int bytesOverLimit, bool accepted)
    {
        var frameLength = Padded(string.Empty).Length;
        var json = Padded(new string('x', (int)ChromeLocalState.MaxBytes - frameLength + bytesOverLimit));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        stream.Length.ShouldBe(ChromeLocalState.MaxBytes + bytesOverLimit);

        var state = ChromeLocalState.Parse(stream);

        if (accepted)
        {
            state.ShouldNotBeNull().Profiles.ShouldHaveSingleItem().ProfileKey.ShouldBe("Default");
        }
        else
        {
            state.ShouldBeNull();
            stream.Position.ShouldBe(0);
        }

        static string Padded(string padding) =>
            $$"""{ "pad": "{{padding}}", "profile": { "info_cache": { "Default": { "name": "x" } } } }""";
    }

    /// <summary>
    /// A stream's length is a claim, not a promise: the file belongs to the user
    /// whose profile it is in, is opened with shared write access, and can grow
    /// between the length check and the read. The reader stops at the cap
    /// whatever the stream said, so a file grown to gigabytes in that window
    /// cannot become a gigabyte allocation.
    /// </summary>
    [Fact]
    public void A_stream_that_understates_its_length_is_still_not_read_past_the_cap()
    {
        var json = Encoding.UTF8.GetBytes("""{ "profile": { "info_cache": { "Default": { "name": "x" } } } }""");
        Parse(Encoding.UTF8.GetString(json)).ShouldNotBeNull("the document itself is a valid Local State");

        // The document followed by whitespace out past the cap: still one valid
        // document, so only the bound can be what refuses it.
        var bytes = new byte[ChromeLocalState.MaxBytes + 4096];
        Array.Fill(bytes, (byte)' ');
        json.CopyTo(bytes, 0);
        using var stream = new UnderstatingStream(bytes, claimedLength: json.Length);

        Should.NotThrow(() => ChromeLocalState.Parse(stream)).ShouldBeNull();
        stream.Position.ShouldBeLessThanOrEqualTo(ChromeLocalState.MaxBytes + 1);
    }

    /// <summary>A stream whose <c>Length</c> says one thing while its bytes say another.</summary>
    private sealed class UnderstatingStream(byte[] bytes, long claimedLength) : MemoryStream(bytes)
    {
        public override long Length => claimedLength;
    }

    // ---- helpers ------------------------------------------------------------------------

    private static ChromeLocalStateInfo? Parse(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return ChromeLocalState.Parse(stream);
    }

    /// <summary>A whole document around one set of info cache entries, with the noise a real file carries.</summary>
    private static string Document(string infoCacheEntries, string profileExtra = "") => $$"""
        {
          "browser": { "enabled_labs_experiments": [], "last_redirect_origin": "" },
          "os_crypt": { "encrypted_key": "RFBBUEk=" },
          "profile": {
            "info_cache": {
              {{infoCacheEntries}}
            },
            {{profileExtra}}
            "profiles_order": []
          },
          "variations_seed_signature": "MEUCIQDinvented"
        }
        """;
}
