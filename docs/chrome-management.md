# Chrome management

Read-only visibility of Google Chrome across the fleet: which devices have it
and at what version and channel, which Chrome profiles exist for which Windows
accounts, and which extensions each profile carries and where they came from.
This is phases 2 (agent discovery) and 3 (server ingestion, read-only API,
inventory refresh sweep). **Nothing in these phases changes an endpoint.**
Enforcement, updates and packages are later phases and are listed at the end as
not yet built.

## What the agent reads — and what it never reads

Everything is read from the registry and from files Chrome owns, by the agent
running as LocalSystem, with the collector fault-isolated like every other
inventory section (`WindowsChromeCollector`):

| Fact | Source |
|---|---|
| Installed version, install location | The Chrome uninstall entry under HKLM, WOW6432Node view first — Google Update is a 32-bit product and registers there even though Chrome is 64-bit — then the native view |
| Channel, architecture, updater version, last update check | The Google Update `Clients` / `ClientState` keys for Chrome's application id. Architecture is the updater's own record, never inferred from the registry view |
| Executable path | `chrome.exe` under the recorded install location, when it exists and is local |
| Profiles | Each Windows user's `User Data\Local State` (`profile.info_cache`), reached through the machine's profile list so signed-out users are included — the service's own profile belongs to SYSTEM and holds no one's Chrome |
| Extensions | Each profile's `Secure Preferences` and `Preferences` (`extensions.settings`), as Chrome records them: name, version, manifest version, enabled state, install type, Web Store origin, update URL, install and update times |

A per-user Chrome counts only when no machine-wide one was found: the report
carries one installation, and the machine-wide one is the one every account runs.

What is **never** done, structurally rather than by policy:

- **No account e-mail address or id.** Chrome keeps `user_name` (the e-mail),
  `gaia_id` and a picture beside each profile; the parser does not expose them
  and the collector has no path that could. What *is* read is what Chrome's own
  profile menu shows: the person's name (`gaia_given_name`, `gaia_name`) and
  the account's domain (`hosted_domain`). For a profile signed in to a
  Workspace account, Chrome stores the **domain** in the profile's `name` field,
  so reporting that field alone labels every managed profile on a PC with the
  same company domain; the label is therefore composed as Chrome composes it —
  the person's own local name if they set one, else "Given name (domain)". A
  profile is *identified* by Windows SID and by its directory name (`Default`,
  `Profile 1`, …), because labels are renameable.
- **No `manifest.json`.** Names and versions come from Chrome's own localised
  record; the on-disk manifest carries an unlocalised `__MSG_*__` placeholder
  and lives in a directory the extension controls.
- **No write under a profile, ever.** Chrome authenticates its preference files
  with a machine-bound MAC and treats an outside edit as corruption, resetting
  the profile. Files are opened read-only with shared access.
- **No process launched** (ADR-0005). Registry keys are opened for reading.
- Reads are bounded in bytes whatever length a file claims, and every directory
  between the HKLM-trusted profile root and a file read must be a real
  directory, not a junction — a reparse point needs no privilege to plant.

## The data model

Three tables in `endpoint_platform`, all cascading from `devices`:

| Table | Identity | Notes |
|---|---|---|
| `chrome_installations` | one row per device (`ux_chrome_installations_device_id`) | Upserted on every upload: status, version, executable path, architecture, channel, installation scope, installed-for user, updater version, last update check, `collected_at` |
| `chrome_profiles` | (`device_id`, `user_sid`, `profile_key`) | Cascades from the installation; SID plus directory name, for the reason above |
| `chrome_extensions` | (`chrome_profile_id`, `extension_id`) | Cascades from the profile. `ix_chrome_extensions_extension_id` answers "which devices have extension X" |

Status and install type are stored as their names, not numbers, so a renumbering
on either side cannot silently change what a row means. The enum names equal the
wire contract's value sets exactly, and a test pins them equal.

Ingestion rules, applied with the rest of the inventory snapshot:

- **One installation per device, always updated.** Every upload with a Chrome
  section refreshes the installation row, whatever the status.
- **Profiles and extensions are replaced wholesale** on `Available` and
  `NotInstalled`: the device's existing rows are removed and the reported ones
  inserted. Uploads carry a full snapshot, so there is nothing to diff.
- **`Error` keeps the last known set.** The agent's enumeration was incomplete
  and the section carries whatever it managed to read; a partial snapshot must
  not replace a complete one. This is the BitLocker rule applied to Chrome.
- **`NotInstalled` may carry leftover profiles.** Chrome's uninstaller leaves
  `User Data` behind by default, and what it recorded there is still fact — but
  the console never reads a non-empty profile list as "Chrome is present".
- Caps: 64 profiles per device, 256 extensions per profile, counted on what is
  kept. Duplicates are dropped first-wins before they are counted (profiles on
  SID + key, case-insensitively; extensions on id), so a repeated entry never
  costs a real one its place.
- The Agent API refuses the whole upload (400) when the Chrome section carries a
  status outside the contract's set, an installation entry without a version, an
  extension id that is not 32 letters in `a`–`p`, an install type outside the
  contract's set, or a manifest version outside 1–99. Ingestion restates those
  rules as a second fence (skip the extension / store `Unknown` / store the
  manifest version as unrecorded) that no route reaches.
- **Component extensions are not counted.** `Component` and `ExternalComponent`
  are Chrome's own built-ins, not something anyone installed, so every
  "extension count" the console shows excludes them. They are still stored and
  still listed, marked as components.
- **Nothing is fabricated.** Whether an update is available for an installed
  Chrome is not computable until the update reference exists (a later phase), so
  it is reported as unknown — `null`, never `0` — and every per-device update
  status reads `Unknown`.
- Chrome uploads are not audited per event. Inventory is re-reported every
  cycle; auditing each upload would bury real events.

## Status rules

| `Status` | Meaning | Installation | Profiles and extensions |
|---|---|---|---|
| `Available` | An installation was found | Recorded | Replaced with what was sent |
| `NotInstalled` | No installation was found | Version and paths null | Replaced with what was sent — possibly leftover, possibly empty |
| `Error` | Enumeration failed part-way | Recorded as far as it was read | **Kept as last known** |

`Available` may arrive with no installation entry: the agent found Chrome but
could not read its version, and the contract requires one. The row is stored as
`Available` with no version, and the device counts as *reporting* Chrome but not
as *having* it — the entity's `IsInstalled` rule (Available **and** a version)
is the one the overview count restates in SQL. An installation entry that is
present but names no version is malformed, and the upload is refused.

An unrecognised status is refused by the Agent API before it reaches ingestion;
if one somehow reached the ingestion path it would be treated as `Error`, which
is the reading that changes the least.

## Groups, and what "All Devices" means here

The per-group Chrome view resolves membership with exactly the rule the Groups
page uses — active devices whose `device_group_id` is the group, in the caller's
organization — and nothing else. There is no Chrome-specific grouping or
membership table, and none will be added.

So this feature inherits the platform's group semantics unchanged
([device-groups.md](device-groups.md)): every device is in exactly one group,
and the built-in **"All Devices"** group holds only the devices that are in **no
custom group**. A device moved into a custom group leaves the All Devices Chrome
view and appears in its group's. The fleet overview, by contrast, counts every
active device in the administrator's scope regardless of group.

Scope follows the rest of the Admin API. A group the caller cannot act on, a
device outside their scope, or a profile that does not belong to the named
device is answered **404, never 403**, so the Chrome routes reveal nothing about
what exists. Fleet counts are narrowed to the caller's device and group scope;
an administrator with all-device scope sees the whole organization.

## Permissions

| Permission | Risk | Held by |
|---|---|---|
| `chrome.view` — view Chrome installations, profiles and extensions | normal | IT Administrator, Helpdesk, Auditor |
| `chrome.manage` — manage Chrome extensions and updates on devices | **high** | IT Administrator |

Super Administrator holds every permission, as always. Helpdesk holds view so a
"why is this extension missing" call can be answered from the console; Auditor
holds view because what is installed is evidence. `chrome.manage` exists in the
catalogue so that role grants and access levels are settled before the first
mutation ships; **no route in this phase checks it**, because no route in this
phase changes anything.

## Read routes

All under the Admin API, all requiring `chrome.view`, all returning 404 for
anything out of scope:

| Route | Returns |
|---|---|
| `GET /admin/v1/chrome/overview` | Fleet counts in the caller's scope, over active devices: groups, devices, online devices, devices reporting a Chrome section, devices with Chrome (`Available` with a version), profiles, extensions (non-component), and devices with updates available — `null` until the update reference exists |
| `GET /admin/v1/chrome/groups/{groupId}/devices?q=` | The group's active members by the rule above, ordered by hostname, with Chrome status, version, channel, profile and extension counts, online state and collection time. `q` matches hostname or display name, case-insensitively. Bounded to 5,000 rows — a ceiling no real group reaches, there so a pathological one cannot make the read unbounded |
| `GET /admin/v1/devices/{deviceId}/chrome` | The device's installation (or none) and its profiles, ordered by account then profile key, each with its extension and managed-extension counts, plus whether an inventory refresh is pending |
| `GET /admin/v1/devices/{deviceId}/chrome/profiles/{profileId}/extensions` | The profile's extensions, components last, then by name |

Online is decided the way the Groups page decides it: last heartbeat within
`OfflineAfterSeconds` (180 by default), on the server's clock. Times are the
server's: `collectedAt` is when the server ingested the snapshot, not when the
agent took it — a laptop that collects offline and uploads on reconnecting shows
the upload time. Chrome's own times (a profile's last use, an extension's
install and update) are Chrome's, as the agent read them.

## The daily inventory refresh sweep

Inventory is collected on request, not continuously, so without a nudge a quiet
machine's Chrome data — like the rest of its inventory — would simply age. The
sweep is that nudge, and it refreshes the **whole** inventory snapshot, not only
the Chrome section; it is an inventory mechanism that Chrome relies on.

- **`Inventory:RefreshAfterHours`**, default **24**, validated at startup to
  0–720. **`0` disables** the sweep entirely.
- `InventoryRefreshSweeper` runs on the Admin host beside the task-expiry
  sweeper: once at startup and then every 15 minutes, in batches of 500, draining
  while a batch comes back full, and never crashing the host on a failed tick.
- Each pass selects active devices whose snapshot is older than the cutoff and
  that have no refresh request newer than their last snapshot, oldest first, and
  marks each as needing a refresh. The next heartbeat then carries
  `InventoryRequested` — the existing pull-based handshake in
  [agent-protocol.md](agent-protocol.md); nothing on the agent changes.
- A device that has never uploaded is already pending and is skipped. A device
  an administrator already asked to refresh is skipped for the same reason.
- One audit entry per organization per batch of 500 (a pass may drain several
  batches), `device.refresh_inventory.sweep` — beside the manual
  `device.refresh_inventory` — by the system actor `inventory refresh sweeper`,
  recording how many devices were asked and the age threshold. Nothing is
  audited when there was nothing to ask.

An offline device is asked like any other and answers when it returns; the
request stays pending until it does.

## The console page

**Chrome Management** sits under Configuration in the sidebar (`/chrome`,
behind `chrome.view`). It is the Groups page's shape with Chrome facts in it:

- Six summary tiles from the overview route. A tile shows an em-dash, not a
  zero, before the overview has loaded and wherever the server sent `null`
  ("Devices Outdated", until the release reference exists).
- The group panel is the existing groups list (`GET /admin/v1/groups`), All
  Devices first. Selecting a group loads that group's devices from the Chrome
  group route, re-read every 30 seconds like the Groups page — so a device
  moved on the Groups page leaves one table and appears in the other on the
  next read, and never sits in both. The page never decides membership.
- The device table: Chrome version and channel with the report status
  (Installed / Not installed / Incomplete report / Not reported), profile and
  extension counts (components excluded), update status ("Unknown" today),
  online state, and **Manage**, which opens the device's workspace below the
  table without leaving the page. Search filters the loaded list by hostname or
  display name.
- The workspace has four tabs: **Overview** (the installation facts),
  **Profiles** (each profile with its extensions beside it), **Extensions** (a
  profile picker over the same table) and **Updates** (version, channel, the
  update status and Google Update's own last-check time). Its one action is
  **Refresh inventory**, the same request the device page makes, shown only to
  holders of `device.refresh_inventory`.

Profiles are shown for inspection. Chrome's enterprise policy applies to every
profile on a PC, so whatever enforcement arrives later will be machine-wide;
the profile list answers "what does each profile have", not "which profile to
change". Nothing on the page installs, removes or updates anything, because
nothing on the server does yet.

The page's rules — tile values, status labels and tones, extension ordering,
search — live in `dashboard/src/pages/chromeView.ts` and are unit-tested; the
client functions are pinned to their routes by `chromeApiPaths.test.ts`.

## Later phases — not yet built

None of the following exists in the codebase. They are listed so that the
`chrome.manage` permission, the `Unknown` update status and the `null` update
count above are read as placeholders for these, not as bugs.

- **Enforcement through Chrome policy.** Extension allow-lists, block-lists and
  forced installs written as Chrome enterprise policy under
  `HKLM\SOFTWARE\Policies\Google\Chrome`, which Chrome reads and applies itself —
  the platform would set a registry value, never touch a profile.
- **Chrome updates from an uploaded Chrome MSI**, through the existing package
  pipeline (content-addressed store, hash and Authenticode signer pin, Windows
  Installer service). The update reference that makes "update available"
  computable arrives with it.
- **Extension packages**: an approved set of extensions deployable to a device or
  group, resolved and audited server-side like a software deployment.

Until then the console offers no Chrome control of any kind: a button that only
flipped a database value would be worse than its absence.
