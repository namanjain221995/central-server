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

- **The service launches nothing.** The notifier is started by Windows at sign-in,
  in each user's session and as that user, from a quoted value under
  `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` written by the installer.
  The service never calls `CreateProcessAsUser` or anything like it; a SYSTEM
  process that starts processes in users' sessions is the capability this ADR
  exists to exclude. The Run key and the install folder are administrator-only.
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
and sits inside the `Process.Start` scan, which a test pins.
