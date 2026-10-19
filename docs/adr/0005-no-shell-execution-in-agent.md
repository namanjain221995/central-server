# ADR-0005: The agent never executes shell commands

Status: accepted (Phase 0)

## Context

The agent runs as LocalSystem. The most dangerous pattern privileged agents
adopt is composing command strings ("net user " + name + " ...") and handing
them to cmd/PowerShell: every string that touches that path becomes a
potential privileged injection, and auditing "what did we actually run"
becomes string archaeology.

## Decision

The agent performs Windows work exclusively through APIs — Win32/P-Invoke,
WMI/CIM with fixed query text, System.DirectoryServices.AccountManagement,
registry APIs — which take typed parameters and have no command line to
inject into.

The rule is enforced structurally, not by review: the agent assemblies do not
reference `System.Diagnostics.Process` or the PowerShell SDK **at all**
(`AgentSafetyTests` fails the build otherwise). WMI usage keeps query text
constant — no interpolation of runtime values into WQL.

When Phase 10 introduces approved-script execution, it will be a reviewed,
narrow call site behind the signed-script pipeline (hash + signature +
recorded approval), and the enforcement test will be tightened to allow
exactly that call site rather than removed.

## Consequences

- Local user/group management (Phase 4) must use account-management APIs even
  where a shell one-liner would be shorter. This is intended friction.
- No PowerShell SDK dependency keeps the agent's footprint and attack surface
  small.
- Anything genuinely impossible without a process launch is deferred to the
  Phase 10 pipeline rather than snuck in early.

## Amendment (Phase 9): process/service control

Phase 9 introduces service control (`ServiceController`) and process termination
(`Process.GetProcessById().Kill()` with an expected-image guard). These are NOT
the pattern ADR-0005 forbids: they take typed arguments and have no command line
to inject into. The enforcement test was therefore made *precise* rather than
loosened:

- `EndpointAgent.Core` still references no process API at all.
- A source scan asserts **`Process.Start` appears in no agent source file** -
  that is the actual shell/launch vector. `Kill`/`GetProcessById`/`GetProcesses`
  and `ServiceController` are permitted.
- The PowerShell-SDK ban is unchanged.

`Process.Start` (for approved-script execution) remains forbidden until the
signed-script pipeline exists, at which point the scan is tightened to allow
exactly that one reviewed call site.

## Amendment (Phase 11): MSI installation via the Windows Installer service

Phase 11 gives the agent a real software-install capability. This is the one
capability that appears to reopen the door ADR-0005 closes, so the boundary is
drawn deliberately:

- Installation is driven by **`MsiInstallProduct` (msi.dll)**, product detection
  by **`MsiQueryProductState`**, and there is **no process launch and no shell**.
  `Process.Start` remains absent from every agent source file (the
  `AgentSafetyTests` scan is unchanged and still passes). MSI is a data format
  consumed by a Windows service, not a command line the agent composes.
- The capability is **closed to signed MSI**. The package type enum has one
  member (`WindowsInstaller`); there is no `.exe`, no script, no arbitrary
  installer. Widening it is a reviewed code change, not configuration.
- **Two independent pins** gate every install, both verified on the agent:
  1. the content **SHA-256**, checked against the pin in the task payload before
     a byte reaches the installer; and
  2. the **Authenticode signer**, verified via `WinVerifyTrust` plus a signer
     -subject match, refusing an unsigned or untrusted file outright.
  A server tricked into serving the wrong bytes, or a network tamperer, cannot
  get anything installed: the hash check fails first, the signature check second.
- Installs are **idempotent by ProductCode** and reboots are **suppressed** (an
  install that wants one is reported, never performed). The privileged install
  runs as LocalSystem; the Admin API only ever stores content and queues intent,
  both audited (`software.deploy`, marked high-risk).

This does not reintroduce arbitrary execution: the agent cannot be made to run
anything other than a hash-pinned, signature-verified MSI that an administrator
holding `software.deploy` explicitly registered and deployed. The
`Process.Start`/PowerShell bans, and their enforcement test, are unchanged.

## Amendment (Phase 16, withdrawn): application removal

Phase 16 added an application-removal capability: a `RemoveApplication` task
that stopped an application and then uninstalled it, a Windows Installer product
through `MsiConfigureProductEx` and an MSIX/AppX package through the deployment
engine's `RemovePackageWithOptionsAsync`.

**That feature has been withdrawn and its code removed.** Software Management is
Force Stop only. The msi.dll bindings it alone used were deleted with it; the
installer keeps `MsiQueryProductState`, `MsiInstallProduct` and
`MsiSetInternalUI`, which are Phase 11's and unaffected.

Nothing in this ADR was relaxed to accommodate that feature, and nothing needed
tightening when it went: it never launched a process or composed a command line,
and EXE uninstallers were refused throughout precisely because running a
vendor-supplied `UninstallString` is the vector this ADR forbids. That reasoning
stands and is the reason any future removal feature must not take that route.

The `Process.Start`/PowerShell bans, and their enforcement test, are unchanged.

## Amendment: the session restart notice

A restart Windows has accepted is announced to signed-in users by a small session
notifier, `EndpointAgent.SessionNotice.exe`. It crosses the boundary between the
LocalSystem service and ordinary users' sessions, so how it does that is recorded
here.

- **Two things start the notifier, and nothing else.** Windows starts it at
  sign-in, in each user's session and as that user, from a quoted value under
  `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` written by the installer.
  And the service starts it for a user who is *already* signed in when the agent
  is installed, updated or restarted — once when the service starts, and again
  whenever it has a restart to announce, for any session with no notifier
  connected — through the call Windows documents for exactly this:
  `WTSEnumerateSessions` → `WTSQueryUserToken` → `CreateProcessAsUserW`. That is
  the one place in the agent that creates a process. It is not the capability
  this ADR excludes, which is composing *what to run* from data, and
  `AgentSafetyTests` pins it to that one file and to how it is used:
  - the image is a constant name (`EndpointAgent.SessionNotice.exe`) resolved
    inside the service's own directory (Program Files, administrator-only). It is
    passed as `lpApplicationName`, so Windows never parses a path out of a command
    line; the command line is that image quoted and nothing else; and there is no
    parameter anywhere — not on the launcher, not on the host — through which an
    argument could be supplied;
  - the token is the session user's own primary token, and if it is the elevated
    half of a UAC pair the limited half is used instead. The notifier runs with
    exactly what the user has at their desktop, never more — a process with the
    user's environment and elevated rights would be a way around UAC;
  - the environment is the user's own (`CreateEnvironmentBlock`), the working
    directory is the install folder, no handle is inherited across the boundary,
    and the process gets the user's default security descriptor. The service
    closes its handles at once and never waits on, signals or reads from it;
  - nothing a user controls reaches the call: the pipe carries no request (it is
    still one-way, below), and no file or registry value is read to decide what
    to start or where.
  The only thing anyone who controlled every input to that call could achieve is
  starting, as themselves, a program they can already start from their Start
  menu. Every other process-creating API — `Process.Start`, `CreateProcess*`,
  `ShellExecute*`, `WinExec` — remains banned everywhere by the scan.
- **One per session, and it ends with the service.** The notifier holds a
  per-session mutex; a second copy exits at once, so the Run key and the service
  racing at sign-in still leave one. It exits when the service it was reading
  from goes away, and when Windows or an installer's Restart Manager asks it to
  close (`WM_ENDSESSION`), and the service starts a fresh one when it is back.
  That is what lets an agent upgrade replace the runtime files a running
  notifier shares with the service: the installer stops the service before it
  touches a file, the service waits briefly for its notifiers to exit, and by
  the time files are replaced nothing of the agent's is running. A notifier that
  disappears is never a hazard: Windows' own shutdown warning stands.
- **One direction, data only.** The service hosts a named pipe and is its only
  writer. Interactive users get read access and nothing else — no write, no
  `CreateNewInstance`, no permission change — enforced by an explicit, protected
  DACL. The notifier never sends a byte. A notice is one line of strictly parsed
  JSON carrying a time and a grace period; any extra property, unknown version,
  out-of-range value or overlong line is refused. There is no text, command or path
  in it, so there is nothing to execute or display.
- **Trust is the server's session.** The notifier runs as an ordinary user and
  cannot open a SYSTEM process: measured on Windows 11 26200 from a non-elevated
  session, `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` and
  `ProcessIdToSessionId` are both denied for SYSTEM processes, so the server's
  account or image path cannot be checked from where the check runs.
  `GetNamedPipeServerSessionId` asks the pipe driver instead, and returns 0 for real
  service pipes (`lsass`, `services.exe`, `eventlog`, `InitShutdown`) and the
  caller's own session for a pipe the caller created. No interactive user can start
  a process in session 0, so the notifier trusts a server in session 0 and refuses
  every other without reading from it. A user who squats the pipe name is refused;
  the service also claims the name with `FirstPipeInstance`. Another service could
  squat it, but installing a service already requires administrator rights.
- **Fixed words.** Every word the user sees is a constant. There is no cancel
  control: cancellation belongs to an administrator in the console, before the
  restart is delivered.
- **A courtesy, not a dependency.** The notice is sent only after Windows accepts
  the restart, and a failure to send it changes neither the restart nor its
  reported result. Windows' own shutdown warning is unaffected.

The notifier is plain Win32 through P/Invoke, shares the service's self-contained
runtime (five files, about 240 KB), references no UI framework and no PowerShell,
takes no arguments and reads none, and sits inside the process-creation scan,
which a test pins.

## Amendment: the Company Portal

Employees install approved software themselves from `EndpointAgent.CompanyPortal.exe`,
a desktop app in their own session ([company-portal.md](../company-portal.md)).
For the first time, a process in a user's session sends something to the
LocalSystem service. What it can send, and what the service does with it, is
recorded here, because it is the kind of thing this ADR exists to bound.

- **Three questions, about this device, and nothing else.** The portal connects
  to a second named pipe, `EndpointPlatformAgent.CompanyPortal`, and sends one
  line of strictly parsed JSON: `list`, `icon` with a catalog app id, or
  `install` with a catalog app id. A line with any other property, an unknown
  version or operation, an id that is not a GUID, or more than 256 bytes is
  refused unread. There is no path, no package name, no argument, no text: a
  request names an entry the management server already holds, by its id, and
  the only text the portal ever shows is what the server sends back for that
  entry.
- **The service relays; the server decides.** `CompanyPortalRequestHandler` turns
  a request into the matching authenticated call on the device's own credential
  and returns the answer. It installs nothing, downloads nothing, runs nothing.
  An install the server agrees to arrives later as an ordinary `InstallPackage`
  task and passes every gate that executor already has — the content hash, the
  Authenticode signer, the Windows Installer API, no process launch. The portal
  therefore cannot cause anything on the machine that an administrator's
  deployment could not, and only for an entry an administrator published to
  this device's group.
- **The user is a name on a record, not an identity.** The service learns who
  asked from the client's session (`WTSQuerySessionInformation`), sends it to
  the server for the request history, and the server decides on the device
  credential and the device's group alone. A process that lied about its user
  would change what the history says and nothing else.
- **Bounded on both sides.** The service answers one request per connection,
  reads at most 257 bytes of it, waits at most five seconds, and admits at most
  120 lists, 600 icons and 10 installs a minute per session before answering
  `TooManyRequests` without asking the server; the server applies its own limit
  on top. The portal reads a bounded response and parses it as strictly as the
  notifier parses a notice: one payload, known error words, an icon no larger
  than the server's ceiling.
- **Access.** The pipe's protected DACL gives the service account full control
  and interactive users read and write — enough to send a line and read one —
  and nothing else: no new instance, no permission change, no ownership. The
  name is claimed with `FirstPipeInstance`, and the portal trusts only a server
  in session 0, for the reasons the notifier amendment gives.
- **Starting it is the user's business.** The portal is a Start-menu shortcut.
  The service never starts it: `CreateProcessAsUserW` stays pinned to the
  notifier, one constant image with no arguments, and the process-creation scan
  covers the portal's sources too. The portal itself opens no link, folder or
  process.
- **A UI framework, deliberately.** The notifier draws one window in raw Win32
  so as not to ship the desktop runtime. The portal is a real catalog — tiles,
  icons, scrolling, search — and is written with Windows Forms, which adds the
  desktop runtime to the self-contained publish folder and some fifteen to
  twenty megabytes to the installer. That cost was accepted by the owner. The
  notifier keeps its own rule and its test; the portal is a separate project
  with the same self-contained publish, so the two share one runtime copy.

What is unchanged: `Process.Start` and PowerShell are banned in every agent
source file, the notifier pipe is still one-way, and the only program the
service ever starts is still the notifier.

## Amendment: announcements

The session-notice amendment above fixed **every word the user sees** as a
compile-time constant, so nothing reaching the endpoint could put its own words
in a window that speaks for IT. Announcements (agent 1.18.0) consciously amend
that principle: a `ShowAnnouncement` task carries an administrator's title
(≤80 characters) and message (≤500), and the notifier displays them. Why that is
now acceptable, and what still holds:

- **The words originate with an authenticated administrator, nowhere else.** They
  arrive only through the server's task channel — authorization
  `announcement.send`, audited like every task, TTL-bounded — on the device's own
  credential. Nothing local can inject them: the pipe's DACL still gives
  interactive users read access and nothing else, the name is still claimed with
  `FirstPipeInstance`, and the notifier still refuses any server outside
  session 0 without reading a byte. The forgery the fixed-words rule was built
  against — a local process putting words in IT's mouth — is still impossible.
- **Bounded and strictly parsed, at both ends.** Protocol v3 gives every line a
  `kind`; an announcement line is exactly six properties (version, kind, the
  server's announcement id — which is what a user's dismissal keys on, so a
  re-delivered message stays closed — the moment, title 1–80 and message 1–500
  characters), no control characters beyond newline and tab, and at most
  2048 bytes — anything else is refused, exactly as v2 refused the unexpected.
  The executor validates the task payload against the same bounds before
  anything is encoded, so nothing the service agrees to send can be refused on
  arrival. Restart lines still carry no text at all.
- **Rendered as data, never interpreted.** The window chrome stays fixed words
  ("Announcement", "Message from your IT administrator"); the administrator's
  text is drawn with `DrawTextW` as plain text — never parsed, never a link,
  never a path or command, nothing to click, follow or execute.
- **The pipe stays one-way, and a restart keeps the screen.** The notifier still
  never sends a byte, and when a restart notice and an announcement are both
  live, the countdown is shown: a message can wait; a reboot cannot.

`AgentSafetyTests` is unchanged and still pins the rest: no process creation
outside the one host, no PowerShell, the notifier inside the scan.

## Amendment: the screen lock

The screen lock (agent 1.19.0) is the heaviest thing the agent does to a user: a
full-screen overlay in every session that blocks the keyboard and mouse, survives
a reboot, and comes off only two ways. It crosses the service/session boundary in
both directions, so how it does that is recorded here.

- **A second service-launched image, deliberately.** The overlay,
  `EndpointAgent.LockScreen.exe`, is started in each interactive session by the
  service, the same way and down the same reviewed code path as the notifier
  (`WTSEnumerateSessions` → `WTSQueryUserToken` → `CreateProcessAsUserW` in
  `WindowsSessionProcessHost`, as the session's user and never elevated). It is a
  *second* image rather than a mode of the notifier because it needs what the
  notifier must never have: the foreground, low-level keyboard and mouse hooks, and
  a UI framework (WinForms, for a logo, wrapped text and a passphrase box on every
  monitor). Keeping those in a separate project leaves the notifier's own rule and
  test intact. `CreateProcessAsUserW` is still pinned to that one host, still passes
  the image as `lpApplicationName` with a command line of the quoted image and
  nothing else, and there is still no parameter through which an argument could be
  supplied: the overlay reads **nothing from its command line** about what to show.
  `AgentSafetyTests` now pins **both** reviewed image names explicitly
  (`EndpointAgent.SessionNotice.exe` and `EndpointAgent.LockScreen.exe`); a third
  would be a deliberate change to that assertion.
- **The overlay asks the service; it is never told on the command line.** A
  dedicated duplex pipe, `EndpointPlatformAgent.ScreenLock`, carries exactly two
  bounded operations, one line of strictly parsed JSON each way (protocol in
  `ScreenLockPipeProtocol`): `show` (read-only: the plain-text message and contact
  line to display, and whether the device is still locked) and `submit` (a
  candidate unlock code, at most 256 plain-text characters). A line that is too
  long, is not exactly the expected properties for its operation, names an unknown
  version or operation, or carries a code that is not plain text is refused unread.
  There is no path, package, command or id anywhere in it, and nothing the overlay
  receives is executed: the message and contact line are drawn as plain text with
  mnemonics off, never parsed, never a link.
- **The verifier lives and dies in SYSTEM.** A lock carries a PBKDF2-SHA256
  verifier (algorithm, iterations, salt, hash) that the server derived from the IT
  unlock code. It is persisted only in the ACL-protected state directory and
  **never travels to a user session**. The overlay sends a *candidate* code over
  the pipe; the service does the PBKDF2 and a constant-time compare and answers a
  single bool. So a user who owns the overlay process learns nothing that would let
  them derive the code offline, and a wrong guess reveals nothing by timing.
- **Bounded, and wrong codes rate-limited.** The service answers one request per
  connection, reads at most a bounded line, waits at most five seconds, and admits
  at most ten `submit` codes a minute per session (and a looser bound on the
  read-only `show` poll) before answering `TooManyRequests` without touching the
  verifier -- so the pipe cannot be used to brute-force the code at machine speed.
  The pipe's protected DACL gives the service account full control and interactive
  users read and write and nothing else; the name is claimed with
  `FirstPipeInstance`; and the overlay trusts only a server in session 0, for the
  reasons the notifier amendment gives.
- **A reversible lockdown, undone exactly.** On engage the service disables Task
  Manager (`HKLM\...\Policies\System\DisableTaskMgr`) and sets the power, sleep and
  lid-button actions to "Do nothing", through the registry and the power-management
  API (`powrprof.dll`) -- typed API writes, **no `powercfg` process and no shell**.
  Every setting's prior value is captured *before* it is changed, and a setting is
  only ever changed when its prior value was captured, so there is never a change
  that cannot be undone. The captured values are persisted with the lock and handed
  back on release, which restores every one of them -- the `DisableTaskMgr` value is
  deleted again when it was absent before -- then clears the lock. Restore runs
  before the clear, so a crash in between re-locks on the next boot rather than
  leaving the machine half-locked-down with nothing to undo it.
- **Two ways out, and the recovery path is sacred.** A `ReleaseScreenLock` task
  from the server releases the device (`EngageScreenLock`/`ReleaseScreenLock`
  executors, permission-checked and audited like every task). And an IT operator at
  the device presses Ctrl+Alt+U and types the code, which the overlay submits for
  the service to verify. A corrupt or missing lock file reads as **not locked**, the
  recovery-safe misreading: a damaged file frees a user, it never strands one
  behind a verifier that can no longer be matched.
- **Honest limits of a user-mode lock.** The low-level hooks cannot intercept
  Ctrl+Alt+Del -- the Secure Attention Sequence is Winlogon's, on a desktop no user
  process can reach -- so the overlay cannot suppress the SAS menu; it is topmost
  and watchdog-relaunched, so it covers the desktop again when the user returns, but
  that is all. It cannot stop a hard power-off; the next boot re-locks. And a local
  administrator can end the overlay process (the watchdog restarts it, but the lock
  is not a defence against the machine's own administrator) or clear the state. The
  always-available second way out -- the server release -- depends on none of the
  on-device machinery, so a user mistyping, or a hook that a hostile local admin
  disabled, never strands a device.

`AgentSafetyTests` still pins the rest, and was tightened rather than loosened: the
process-creation scan is unchanged and now covers the overlay's sources too
(process *control* -- the watchdog's image-guarded `Kill`/`GetProcessById` -- is the
Phase 9 kind, not the launch vector); `CreateProcessAsUserW` stays pinned to the one
host; the two reviewed image names are both asserted; `EndpointAgent.Core`
references no process API; and PowerShell stays banned in every agent assembly.
