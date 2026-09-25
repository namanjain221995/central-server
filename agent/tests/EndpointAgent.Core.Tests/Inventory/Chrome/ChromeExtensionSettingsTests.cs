using System.Text;
using EndpointAgent.Core.Inventory.Chrome;

namespace EndpointAgent.Core.Tests.Inventory.Chrome;

/// <summary>
/// Reading extension records out of a profile's preference files: the few
/// properties the inventory carries, and refusal of everything a broken or
/// tampered file could try. Fixtures follow the real on-disk shape of
/// <c>Secure Preferences</c>, with every name, key and hash invented.
/// </summary>
public sealed class ChromeExtensionSettingsTests
{
    // Ids are 32 letters a-p. These are shaped like Chrome's but belong to nothing.
    private const string TabTidy = "cafebabecafebabecafebabecafebabe";
    private const string Reader = "deadbeefdeadbeefdeadbeefdeadbeef";
    private const string Vault = "feedfacefeedfacefeedfacefeedface";
    private const string PrintPreview = "aabbccddeeffgghhiijjkkllmmnnoopp";
    private const string Plain = "abcdefghijklmnopabcdefghijklmnop";

    private static readonly DateTimeOffset TabTidyInstalled =
        new DateTimeOffset(2026, 9, 22, 14, 56, 15, TimeSpan.Zero).AddTicks(8_797_410);

    private static readonly DateTimeOffset TabTidyUpdated =
        new DateTimeOffset(2026, 9, 25, 14, 59, 17, TimeSpan.Zero).AddTicks(5_046_650);

    /// <summary>
    /// A Secure Preferences file as Chrome writes it, trimmed to four entries: a
    /// Web Store extension, a disabled one, a policy-installed one and a component.
    /// The MACs and the manifest key are the shape of the real thing, not values.
    /// </summary>
    private const string SecurePreferences = $$"""
        {
           "browser": {
              "show_home_button": false
           },
           "extensions": {
              "alerts": {
                 "initialized": true
              },
              "settings": {
                 "{{TabTidy}}": {
                    "active_permissions": {
                       "api": [ "storage", "tabs" ],
                       "explicit_host": [ "https://*.contoso.example/*" ],
                       "manifest_permissions": [  ],
                       "scriptable_host": [  ]
                    },
                    "commands": {  },
                    "content_settings": [  ],
                    "creation_flags": 9,
                    "disable_reasons": [  ],
                    "first_install_time": "13434562575879741",
                    "from_webstore": true,
                    "incognito_content_settings": [  ],
                    "incognito_preferences": {  },
                    "last_update_time": "13434821957504665",
                    "location": 1,
                    "manifest": {
                       "action": {
                          "default_icon": {
                             "16": "icons/16.png",
                             "32": "icons/32.png"
                          },
                          "default_popup": "popup.html",
                          "default_title": "Tab Tidy"
                       },
                       "background": {
                          "service_worker": "background.js"
                       },
                       "description": "Groups and closes tabs you have stopped using.",
                       "icons": {
                          "128": "icons/128.png",
                          "16": "icons/16.png",
                          "48": "icons/48.png"
                       },
                       "key": "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAinventedkeymaterialnotarealone",
                       "manifest_version": 3,
                       "name": "Contoso Tab Tidy",
                       "permissions": [ "storage", "tabs" ],
                       "update_url": "https://clients2.google.com/service/update2/crx",
                       "version": "2.0.14"
                    },
                    "path": "{{TabTidy}}\\2.0.14_0",
                    "preferences": {  },
                    "regular_only_preferences": {  },
                    "was_installed_by_default": false
                 },
                 "{{Reader}}": {
                    "active_permissions": {
                       "api": [ "activeTab", "scripting" ],
                       "explicit_host": [  ],
                       "manifest_permissions": [  ],
                       "scriptable_host": [  ]
                    },
                    "commands": {  },
                    "content_settings": [  ],
                    "creation_flags": 9,
                    "disable_reasons": [ 1 ],
                    "first_install_time": "13421234567890123",
                    "from_webstore": true,
                    "incognito_content_settings": [  ],
                    "incognito_preferences": {  },
                    "last_update_time": "13430000000000000",
                    "location": 1,
                    "manifest": {
                       "action": {
                          "default_title": "Reader"
                       },
                       "background": {
                          "service_worker": "sw.js"
                       },
                       "description": "Strips a page down to its text.",
                       "icons": {
                          "128": "128.png"
                       },
                       "key": "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAanotherinventedkeyvalue",
                       "manifest_version": 3,
                       "name": "Fabrikam Reader",
                       "permissions": [ "activeTab", "scripting" ],
                       "update_url": "https://clients2.google.com/service/update2/crx",
                       "version": "5.1.0"
                    },
                    "path": "{{Reader}}\\5.1.0_0",
                    "preferences": {  },
                    "regular_only_preferences": {  },
                    "was_installed_by_default": false
                 },
                 "{{Vault}}": {
                    "active_permissions": {
                       "api": [ "storage", "identity" ],
                       "explicit_host": [ "https://vault.northwind.example/*" ],
                       "manifest_permissions": [  ],
                       "scriptable_host": [  ]
                    },
                    "commands": {  },
                    "content_settings": [  ],
                    "creation_flags": 137,
                    "disable_reasons": [  ],
                    "first_install_time": "13430000000000000",
                    "from_webstore": true,
                    "incognito_content_settings": [  ],
                    "incognito_preferences": {  },
                    "last_update_time": "13434000000000000",
                    "location": 9,
                    "manifest": {
                       "background": {
                          "service_worker": "background.js"
                       },
                       "description": "Company password vault.",
                       "icons": {
                          "128": "icon128.png"
                       },
                       "key": "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAyetanotherinventedkey",
                       "manifest_version": 3,
                       "name": "Northwind Password Vault",
                       "permissions": [ "storage", "identity" ],
                       "update_url": "https://clients2.google.com/service/update2/crx",
                       "version": "11.3.2"
                    },
                    "path": "{{Vault}}\\11.3.2_0",
                    "preferences": {  },
                    "regular_only_preferences": {  },
                    "was_installed_by_default": false
                 },
                 "{{PrintPreview}}": {
                    "active_permissions": {
                       "api": [ "printing" ],
                       "explicit_host": [  ],
                       "manifest_permissions": [  ],
                       "scriptable_host": [  ]
                    },
                    "commands": {  },
                    "content_settings": [  ],
                    "creation_flags": 1,
                    "disable_reasons": [  ],
                    "first_install_time": "13434562575879741",
                    "from_webstore": false,
                    "incognito_content_settings": [  ],
                    "incognito_preferences": {  },
                    "last_update_time": "13434562575879741",
                    "location": 5,
                    "manifest": {
                       "background": {
                          "page": "print_preview.html"
                       },
                       "description": "Print preview.",
                       "key": "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAcomponentinventedkey",
                       "manifest_version": 2,
                       "name": "Chrome Print Preview",
                       "permissions": [ "printing" ],
                       "version": "154.0.7400.80"
                    },
                    "path": "C:\\Program Files\\Google\\Chrome\\Application\\154.0.7400.80\\resources\\print_preview",
                    "preferences": {  },
                    "regular_only_preferences": {  },
                    "was_installed_by_default": false
                 }
              },
              "ui": {
                 "developer_mode": false
              }
           },
           "protection": {
              "macs": {
                 "extensions": {
                    "settings": {
                       "{{TabTidy}}": "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
                       "{{Reader}}": "123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0",
                       "{{Vault}}": "23456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF01",
                       "{{PrintPreview}}": "3456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF012"
                    }
                 }
              },
              "super_mac": "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210"
           }
        }
        """;

    // ---- the real file ---------------------------------------------------------------

    [Fact]
    public void A_web_store_extension_is_read_completely()
    {
        var entry = Parse(SecurePreferences).Single(e => e.ExtensionId == TabTidy);

        entry.Name.ShouldBe("Contoso Tab Tidy");
        entry.Version.ShouldBe("2.0.14");
        entry.ManifestVersion.ShouldBe(3);
        entry.Enabled.ShouldBe(true);
        entry.Location.ShouldBe(1);
        entry.FromWebStore.ShouldBe(true);
        entry.UpdateUrl.ShouldBe("https://clients2.google.com/service/update2/crx");
        entry.InstalledAt.ShouldBe(TabTidyInstalled);
        entry.UpdatedAt.ShouldBe(TabTidyUpdated);
    }

    [Fact]
    public void Every_entry_is_read_in_file_order()
    {
        Parse(SecurePreferences).Select(e => e.ExtensionId).ShouldBe([TabTidy, Reader, Vault, PrintPreview]);
    }

    [Fact]
    public void A_disabled_extension_is_reported_disabled()
    {
        var entry = Parse(SecurePreferences).Single(e => e.ExtensionId == Reader);

        entry.Enabled.ShouldBe(false);
        entry.Name.ShouldBe("Fabrikam Reader");
        entry.Version.ShouldBe("5.1.0");
    }

    /// <summary>
    /// Policy installs carry Chrome's raw location number; naming it, and
    /// deriving "managed" from it, is the normalizer's job and pinned there.
    /// </summary>
    [Fact]
    public void A_policy_installed_extension_carries_its_raw_location()
    {
        var entry = Parse(SecurePreferences).Single(e => e.ExtensionId == Vault);

        entry.Location.ShouldBe(9);
        entry.Enabled.ShouldBe(true);
        entry.FromWebStore.ShouldBe(true);
    }

    [Fact]
    public void A_component_extension_has_no_update_url()
    {
        var entry = Parse(SecurePreferences).Single(e => e.ExtensionId == PrintPreview);

        entry.Location.ShouldBe(5);
        entry.Name.ShouldBe("Chrome Print Preview");
        entry.Version.ShouldBe("154.0.7400.80");
        entry.ManifestVersion.ShouldBe(2);
        entry.UpdateUrl.ShouldBeNull();
        entry.FromWebStore.ShouldBe(false);
        entry.Enabled.ShouldBe(true);
        entry.InstalledAt.ShouldBe(TabTidyInstalled);
    }

    // ---- enablement --------------------------------------------------------------------

    [Theory]
    [InlineData("\"state\": 1", true)]
    [InlineData("\"state\": 0", false)]
    [InlineData("\"state\": 3", null)]
    public void An_older_record_uses_state_when_there_are_no_disable_reasons(string state, bool? expected)
    {
        var entry = Parse(Settings($$"""
            "{{Plain}}": {
               "location": 1,
               {{state}},
               "manifest": { "name": "Old Timer", "version": "1.0", "manifest_version": 2 }
            }
            """)).ShouldHaveSingleItem();

        entry.Enabled.ShouldBe(expected);
    }

    /// <summary>
    /// Older Chrome kept the record of an externally installed extension the
    /// user removed, manifest and all, with state 2 so the external source would
    /// not put it back. Nothing is installed: it is a tombstone, not a disabled
    /// extension, and the entry beside it is unaffected.
    /// </summary>
    [Fact]
    public void A_tombstone_for_an_externally_uninstalled_extension_is_not_an_entry()
    {
        var entries = Parse(Settings($$"""
            "{{Reader}}": {
               "location": 3,
               "state": 2,
               "manifest": { "name": "Removed By User", "version": "1.0", "manifest_version": 2 }
            },
            "{{Plain}}": { "location": 1, "state": 1, "manifest": { "name": "Genuine", "version": "1.0" } }
            """));

        entries.ShouldHaveSingleItem().Name.ShouldBe("Genuine");
    }

    /// <summary>
    /// Before the list, "state" was what Chrome itself consulted and the bitmask
    /// only explained why, so a record from that era can be disabled with a mask
    /// of zero. State decides when both are numbers.
    /// </summary>
    [Theory]
    [InlineData("\"state\": 0, \"disable_reasons\": 0", false)]
    [InlineData("\"state\": 1, \"disable_reasons\": 0", true)]
    [InlineData("\"state\": 1, \"disable_reasons\": 4", true)]
    [InlineData("\"state\": 0, \"disable_reasons\": 4", false)]
    public void In_a_record_from_before_the_list_state_wins_over_the_bitmask(string enablement, bool expected)
    {
        var entry = Parse(Settings($$"""
            "{{Plain}}": { {{enablement}}, "location": 1 }
            """)).ShouldHaveSingleItem();

        entry.Enabled.ShouldBe(expected);
    }

    /// <summary>
    /// Current Chrome creates the disable-reasons list the first time a reason is
    /// recorded, so an extension that has never been disabled has no enablement
    /// key at all. Real profiles carry enabled, toolbar-visible extensions in
    /// exactly this shape; reading them as "unknown" hid their state.
    /// </summary>
    [Fact]
    public void A_record_with_no_enablement_key_at_all_is_enabled()
    {
        var entry = Parse(Settings($$"""
            "{{Plain}}": {
               "location": 1,
               "manifest": { "name": "Silent", "version": "1.0", "manifest_version": 3 }
            }
            """)).ShouldHaveSingleItem();

        entry.Enabled.ShouldBe(true);
    }

    /// <summary>
    /// Chrome kept the key when it turned the bitmask into a list, so a profile
    /// last written by an in-between Chrome carries a number under it.
    /// </summary>
    [Theory]
    [InlineData("0", true)]
    [InlineData("4", false)]
    [InlineData("65", false)]
    public void An_unmigrated_bitmask_under_disable_reasons_is_still_an_answer(string mask, bool expected)
    {
        var entry = Parse(Settings($$"""
            "{{Plain}}": { "disable_reasons": {{mask}}, "location": 1 }
            """)).ShouldHaveSingleItem();

        entry.Enabled.ShouldBe(expected);
    }

    [Fact]
    public void Disable_reasons_win_over_state_when_a_record_has_both()
    {
        var entry = Parse(Settings($$"""
            "{{Plain}}": { "disable_reasons": [ 1 ], "state": 1, "location": 1 }
            """)).ShouldHaveSingleItem();

        entry.Enabled.ShouldBe(false);
    }

    [Theory]
    [InlineData("\"disable_reasons\": \"none\"")]
    [InlineData("\"disable_reasons\": null")]
    [InlineData("\"state\": \"1\"")]
    [InlineData("\"state\": true")]
    public void An_enablement_value_of_the_wrong_type_is_not_an_answer(string value)
    {
        var entry = Parse(Settings($$"""
            "{{Plain}}": { {{value}}, "location": 1 }
            """)).ShouldHaveSingleItem();

        entry.Enabled.ShouldBeNull();
    }

    // ---- shape tolerance ---------------------------------------------------------------

    /// <summary>
    /// Chrome's settings map also holds records that are not installed extensions:
    /// empty leftovers, and declined or pending external installs, which carry
    /// neither a manifest nor a location. A real profile had four empty ones,
    /// which came out as four "(unnamed)" rows. They are not entries.
    /// </summary>
    [Theory]
    [InlineData("{ }")]
    [InlineData("""{ "ack_external": true }""")]
    [InlineData("""{ "was_installed_by_default": false, "first_install_time": "13434562575879741" }""")]
    public void A_record_with_neither_manifest_nor_location_is_bookkeeping_not_an_entry(string record)
    {
        var entries = Parse(Settings($$"""
            "{{Plain}}": {{record}},
            "ppppoooonnnnmmmmllllkkkkjjjjiiii": { "location": 5, "manifest": { "name": "Genuine", "version": "1.0" } }
            """));

        entries.ShouldHaveSingleItem().Name.ShouldBe("Genuine");
    }

    [Fact]
    public void An_entry_with_a_location_but_no_manifest_is_still_an_entry()
    {
        var entry = Parse(Settings($$"""
            "{{Plain}}": {
               "disable_reasons": [  ],
               "first_install_time": "13434562575879741",
               "from_webstore": true,
               "location": 1,
               "path": "{{Plain}}\\0.0.1_0"
            }
            """)).ShouldHaveSingleItem();

        entry.ExtensionId.ShouldBe(Plain);
        entry.Name.ShouldBeNull();
        entry.Version.ShouldBeNull();
        entry.ManifestVersion.ShouldBeNull();
        entry.UpdateUrl.ShouldBeNull();
        entry.Enabled.ShouldBe(true);
        entry.Location.ShouldBe(1);
        entry.InstalledAt.ShouldBe(TabTidyInstalled);
        entry.UpdatedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("abcdefghijklmnopabcdefghijklmno")]   // 31 characters
    [InlineData("abcdefghijklmnopabcdefghijklmnopa")] // 33 characters
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP")]  // upper case
    [InlineData("qbcdefghijklmnopabcdefghijklmnop")]  // q is past p
    [InlineData("abcdefghijklmnopabcdefghijklmn0p")]  // a digit
    [InlineData("")]
    public void A_key_that_is_not_an_extension_id_is_skipped(string key)
    {
        var entries = Parse(Settings($$"""
            "{{key}}": { "location": 1, "manifest": { "name": "Impostor", "version": "1.0" } },
            "{{Plain}}": { "location": 1, "manifest": { "name": "Genuine", "version": "1.0" } }
            """));

        entries.ShouldHaveSingleItem().Name.ShouldBe("Genuine");
    }

    [Theory]
    [InlineData("\"gone\"")]
    [InlineData("7")]
    [InlineData("[ ]")]
    [InlineData("null")]
    [InlineData("true")]
    public void An_entry_whose_value_is_not_an_object_is_skipped(string value)
    {
        var entries = Parse(Settings($$"""
            "{{TabTidy}}": {{value}},
            "{{Plain}}": { "location": 1, "manifest": { "name": "Genuine", "version": "1.0" } }
            """));

        entries.ShouldHaveSingleItem().ExtensionId.ShouldBe(Plain);
    }

    [Theory]
    [InlineData("\"3\"")]
    [InlineData("3.5")]
    [InlineData("99999999999")]
    [InlineData("null")]
    [InlineData("true")]
    public void A_manifest_version_that_is_not_a_whole_number_is_null(string manifestVersion)
    {
        var entry = Parse(Settings($$"""
            "{{Plain}}": { "location": 1, "manifest": { "name": "Odd", "version": "1.0", "manifest_version": {{manifestVersion}} } }
            """)).ShouldHaveSingleItem();

        entry.ManifestVersion.ShouldBeNull();
        entry.Name.ShouldBe("Odd");
    }

    /// <summary>
    /// Chrome quotes the microsecond count because JSON numbers cannot hold it
    /// exactly. A bare number is not Chrome's encoding and is not guessed at.
    /// </summary>
    [Fact]
    public void An_install_time_written_as_a_number_is_null()
    {
        var entry = Parse(Settings($$"""
            "{{Plain}}": {
               "first_install_time": 13434562575879741,
               "last_update_time": 13434821957504665,
               "location": 1
            }
            """)).ShouldHaveSingleItem();

        entry.InstalledAt.ShouldBeNull();
        entry.UpdatedAt.ShouldBeNull();
    }

    [Fact]
    public void A_field_of_the_wrong_type_is_unrecorded_not_wrong()
    {
        var entry = Parse(Settings($$"""
            "{{Plain}}": {
               "disable_reasons": [  ],
               "from_webstore": "true",
               "location": "1",
               "manifest": { "name": 5, "version": [ "1", "0" ], "update_url": { "href": "x" }, "manifest_version": 3 }
            }
            """)).ShouldHaveSingleItem();

        entry.FromWebStore.ShouldBeNull();
        entry.Location.ShouldBeNull();
        entry.Name.ShouldBeNull();
        entry.Version.ShouldBeNull();
        entry.UpdateUrl.ShouldBeNull();
        entry.ManifestVersion.ShouldBe(3);
        entry.Enabled.ShouldBe(true);
    }

    [Fact]
    public void A_blank_name_is_unrecorded()
    {
        var entry = Parse(Settings($$"""
            "{{Plain}}": { "location": 1, "manifest": { "name": "   ", "version": "  1.0  " } }
            """)).ShouldHaveSingleItem();

        entry.Name.ShouldBeNull();
        entry.Version.ShouldBe("1.0");
    }

    [Fact]
    public void The_same_id_seen_twice_in_one_file_is_one_entry_and_the_first_wins()
    {
        var entries = Parse(Settings($$"""
            "{{Plain}}": { "location": 1, "manifest": { "name": "First", "version": "1.0" } },
            "{{Plain}}": { "location": 5, "manifest": { "name": "Second", "version": "2.0" } }
            """));

        entries.ShouldHaveSingleItem().Name.ShouldBe("First");
    }

    [Fact]
    public void A_trailing_comma_or_a_comment_does_not_lose_the_list()
    {
        var entries = Parse(Settings($$"""
            // hand-edited
            "{{Plain}}": { "location": 1, "manifest": { "name": "Genuine", "version": "1.0", }, },
            """));

        entries.ShouldHaveSingleItem().Name.ShouldBe("Genuine");
    }

    [Fact]
    public void More_entries_than_the_cap_are_cut_at_the_cap_in_file_order()
    {
        var entries = new StringBuilder();
        for (var n = 0; n <= ChromeExtensionSettings.MaxEntries; n++)
        {
            entries.Append(n == 0 ? "" : ",\n").Append('"').Append(IdFor(n)).Append("\": { \"location\": 1 }");
        }

        var result = Parse(Settings(entries.ToString()));

        result.Count.ShouldBe(ChromeExtensionSettings.MaxEntries);
        result[0].ExtensionId.ShouldBe(IdFor(0));
        result[^1].ExtensionId.ShouldBe(IdFor(ChromeExtensionSettings.MaxEntries - 1));
    }

    // ---- refusal -----------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{ \"extensions\": { \"settings\": { ")]
    [InlineData("{ \"extensions\": { \"settings\": { \"abcdefghijklmnopabcdefghijklmnop\": { \"location\": 1 } } } } trailing")]
    [InlineData("\uFEFF{ \"extensions\": { \"settings\": { \"abcdefghijklmnopabcdefghijklmnop\": 1 } } } }")]
    public void Malformed_json_is_an_empty_list_not_an_exception(string json)
    {
        Should.NotThrow(() => Parse(json)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("[ ]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("{ \"extensions\": { } }")]
    [InlineData("{ \"extensions\": [ ] }")]
    [InlineData("{ \"extensions\": { \"settings\": [ ] } }")]
    [InlineData("{ \"extensions\": { \"settings\": null } }")]
    [InlineData("{ \"extensions\": { \"settings\": { } } }")]
    public void A_file_without_extension_settings_is_an_empty_list(string json)
    {
        Parse(json).ShouldBeEmpty();
    }

    [Fact]
    public void A_deeply_nested_file_is_refused_not_recursed_into()
    {
        var json = new string('[', 100) + new string(']', 100);

        Should.NotThrow(() => Parse(json)).ShouldBeEmpty();
    }

    /// <summary>
    /// The same file once at the limit and once one byte over: what changes the
    /// outcome is the size, not the content.
    /// </summary>
    [Fact]
    public void A_file_over_the_size_limit_is_not_read()
    {
        var json = Encoding.UTF8.GetBytes(Settings($$"""
            "{{Plain}}": { "location": 1 }
            """));

        Padded(json, ChromeExtensionSettings.MaxBytes).ShouldHaveSingleItem().ExtensionId.ShouldBe(Plain);
        Padded(json, ChromeExtensionSettings.MaxBytes + 1).ShouldBeEmpty();
    }

    /// <summary>
    /// A stream's length is a claim, not a promise: the file is the user's, is
    /// opened with shared write access, and can grow between the length check
    /// and the read. The reader stops at the cap whatever the stream said.
    /// </summary>
    [Fact]
    public void A_stream_that_understates_its_length_is_still_not_read_past_the_cap()
    {
        var json = Encoding.UTF8.GetBytes(Settings($$""" "{{Plain}}": { "location": 1 } """));

        // The document followed by whitespace out past the cap: still one valid
        // document, so only the bound can be what refuses it.
        var bytes = new byte[ChromeExtensionSettings.MaxBytes + 4096];
        Array.Fill(bytes, (byte)' ');
        json.CopyTo(bytes, 0);
        using var stream = new UnderstatingStream(bytes, claimedLength: json.Length);

        Should.NotThrow(() => ChromeExtensionSettings.Parse(stream)).ShouldBeEmpty();
        stream.Position.ShouldBeLessThanOrEqualTo(ChromeExtensionSettings.MaxBytes + 1);
    }

    /// <summary>A stream whose <c>Length</c> says one thing while its bytes say another.</summary>
    private sealed class UnderstatingStream(byte[] bytes, long claimedLength) : MemoryStream(bytes)
    {
        public override long Length => claimedLength;
    }

    [Fact]
    public void A_utf8_byte_order_mark_is_tolerated()
    {
        // Chrome writes none, but a file that has been through an editor may carry one.
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(Settings($$""" "{{Plain}}": { "location": 1 } """)))
            .ToArray();
        using var stream = new MemoryStream(bytes);

        ChromeExtensionSettings.Parse(stream).ShouldHaveSingleItem().ExtensionId.ShouldBe(Plain);
    }

    // ---- helpers -----------------------------------------------------------------------

    private static IReadOnlyList<ChromeExtensionEntry> Parse(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return ChromeExtensionSettings.Parse(stream);
    }

    /// <summary>The file around one or more settings entries, with the parts Chrome puts beside them.</summary>
    private static string Settings(string entries) => $$"""
        {
           "extensions": {
              "settings": {
                 {{entries}}
              }
           },
           "protection": {
              "super_mac": "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210"
           }
        }
        """;

    /// <summary>The JSON followed by whitespace out to exactly <paramref name="length"/> bytes: still one valid document.</summary>
    private static IReadOnlyList<ChromeExtensionEntry> Padded(byte[] json, long length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)' ');
        json.CopyTo(bytes, 0);

        using var stream = new MemoryStream(bytes);
        return ChromeExtensionSettings.Parse(stream);
    }

    /// <summary>A distinct, well-formed id for each n: n in base 16 with the digits a-p, padded to 32.</summary>
    private static string IdFor(int n)
    {
        var chars = new char[32];
        Array.Fill(chars, 'a');
        for (var i = 31; n > 0; i--, n >>= 4)
        {
            chars[i] = (char)('a' + (n & 0xF));
        }

        return new string(chars);
    }
}
