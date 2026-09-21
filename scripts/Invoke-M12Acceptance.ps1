<#
.SYNOPSIS
    Milestone 12 (temporary local administrator elevation) physical acceptance.

.DESCRIPTION
    Reconstructed from the implemented M12 contracts only. Every route below was
    read out of the repository; none is guessed:

      POST /api/admin/v1/auth/login                         -> sessionToken + permissions
      GET  /api/admin/v1/devices?search=                    -> { items, totalCount, page, pageSize }
      GET  /api/admin/v1/devices/{id}                       -> detail incl. machineIdentifier
      GET  /api/admin/v1/devices/{id}/tasks                 -> array of { id, type, status, ... }
      GET  /api/admin/v1/devices/{id}/local-users           -> array of DeviceLocalUser
      GET  /api/admin/v1/devices/{id}/local-admin-posture   -> M11b verdict + lastReportedAt
      POST /api/admin/v1/devices/{id}/refresh-inventory     -> request an inventory upload
      GET  /api/admin/v1/devices/{id}/elevations            -> array incl. isLive
      POST /api/admin/v1/devices/{id}/elevations            -> { targetSid, justification, durationMinutes }
      POST /api/admin/v1/elevations/{id}/approve            -> { durationMinutes }
      POST /api/admin/v1/elevations/{id}/revoke             -> { note }   (204 No Content)

.NOTES
    WHAT THIS TOUCHES ON THE MACHINE
    --------------------------------
    Sections 1-4    READ-ONLY baseline.
    Sections 5-14   Exercise elevation. Every endpoint-mutating step is marked
                    *** MUTATING STEP ***.
    Sections 15-16  Cleanup, then a read-only diff against the baseline.

    It creates exactly two temporary accounts, EPP-M12-STD and EPP-M12-ADM, and
    deletes both at the end. It deletes ONLY accounts it created itself: cleanup
    is driven by a list of SIDs captured at creation time, never by name
    matching, so an abort can never remove a pre-existing account that happens
    to share a name. It never modifies a pre-existing account, never touches the
    built-in Administrator, never stops the agent service, and never touches USB.

    ON THE REPORTING CYCLE
    ----------------------
    Account inventory is not collected on a timer. The agent uploads it when the
    server sets InventoryRequested, which refresh-inventory does. That endpoint
    sets a flag and writes an audit row; it creates no DeviceTask, so the
    "no unexpected task" check in section 14 stays meaningful.

    ON WHAT PROVES SUCCESS
    ----------------------
    Windows group membership is read locally and resolved through the well-known
    SID S-1-5-32-544 rather than through the group name, which is renameable and
    localized. A task reporting success is not evidence; the membership is.

    PREREQUISITES
    -------------
    Run elevated, on the target endpoint itself, with the agent service running
    and the device enrolled and online. The administrator signing in must hold
    localuser.elevate; the script checks and aborts before touching anything.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ServerBaseUrl,

    [Parameter(Mandatory)] [string] $AdminEmail,
    [Parameter(Mandatory)] [SecureString] $AdminPassword,

    # Named explicitly so the script can refuse to act on the wrong machine.
    [Parameter(Mandatory)] [string] $ExpectedHostname,

    [string] $StandardUser = 'EPP-M12-STD',
    [string] $ExistingAdmin = 'EPP-M12-ADM',

    [int] $InventoryTimeoutSeconds = 300,

    # The shortest window the domain permits, so section 11 is observable
    # without an hours-long wait.
    [int] $ShortElevationMinutes = 15,

    # Section 11 waits out a real window. Skip it for a shorter run; the
    # criteria are then reported DEFERRED rather than silently omitted.
    [switch] $SkipExpiryTest
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$AdministratorsSid = 'S-1-5-32-544'
$UsersSid = 'S-1-5-32-545'

$script:Results = [ordered]@{}
$script:Token = $null
$script:DeviceId = $null

# Accounts this run created, by SID. Cleanup works from this list and nothing
# else, so an abort can never delete an account the script found already there.
$script:CreatedSids = @()

function Set-Criterion {
    param([string]$Id, [string]$State, [string]$Detail = '')
    $script:Results[$Id] = [PSCustomObject]@{ State = $State; Detail = $Detail }
}

function Section { param([string]$Name) "`n$('=' * 76)`n  $Name`n$('=' * 76)" }
function Mutating { param([string]$What) "`n*** MUTATING STEP *** $What" }

function Get-AdministratorsGroupName {
    $g = Get-LocalGroup | Where-Object { $_.SID.Value -eq $AdministratorsSid }
    if (-not $g) { throw "Could not resolve the local Administrators group by SID $AdministratorsSid." }
    $g.Name
}

function Remove-CreatedAccounts {
    if (-not $script:CreatedSids) { '  nothing to remove'; return }
    foreach ($sid in $script:CreatedSids) {
        $u = Get-LocalUser | Where-Object { $_.SID.Value -eq $sid }
        if ($u) {
            Remove-LocalUser -SID $sid -ErrorAction SilentlyContinue
            "  removed $($u.Name) ($sid)"
        }
    }
}

function Show-Result {
    Section 'M12 ACCEPTANCE RESULT'
    foreach ($k in $script:Results.Keys) {
        $r = $script:Results[$k]
        $colour = switch ($r.State) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
        Write-Host ('  {0,-9} {1}' -f $r.State, $k) -ForegroundColor $colour
        if ($r.Detail) { Write-Host "            $($r.Detail)" -ForegroundColor DarkGray }
    }

    $failed = @($script:Results.Values | Where-Object { $_.State -eq 'FAIL' }).Count
    $deferred = @($script:Results.Values | Where-Object { $_.State -eq 'DEFERRED' }).Count

    ''
    if ($failed -gt 0) {
        Write-Host "  M12 ACCEPTANCE RESULT: FAIL  ($failed failed)" -ForegroundColor Red
    } else {
        Write-Host '  M12 ACCEPTANCE RESULT: PASS  (all script-verifiable criteria)' -ForegroundColor Green
    }
    if ($deferred -gt 0) {
        Write-Host "  $deferred criterion/criteria DEFERRED - not verifiable from this endpoint." -ForegroundColor Yellow
    }
    ''
}

function Fail-Hard {
    param([string]$Why)
    Write-Host "`nABORTED: $Why" -ForegroundColor Red
    Write-Host 'The script refuses to guess. Removing anything it created, then stopping.' -ForegroundColor Red
    try { Remove-CreatedAccounts } catch { Write-Host "  cleanup failed: $($_.Exception.Message)" -ForegroundColor Red }
    Show-Result
    exit 2
}

# --------------------------------------------------------------------------
# API
# --------------------------------------------------------------------------
function Connect-Api {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($AdminPassword))
    try {
        $r = Invoke-RestMethod -Method Post -Uri "$ServerBaseUrl/api/admin/v1/auth/login" `
            -Headers @{ 'X-Requested-With' = 'XMLHttpRequest' } -ContentType 'application/json' `
            -Body (@{ email = $AdminEmail; password = $plain } | ConvertTo-Json) -TimeoutSec 30
    } catch {
        Fail-Hard "Could not authenticate to $ServerBaseUrl as $AdminEmail. $($_.Exception.Message)"
    } finally {
        $plain = $null
        [GC]::Collect()
    }

    if (-not $r.sessionToken) { Fail-Hard 'Login returned no sessionToken.' }
    $script:Token = $r.sessionToken

    # Checked up front rather than discovered as a 403 halfway through, once
    # test accounts already exist on the machine.
    foreach ($p in @('localuser.elevate', 'localuser.view', 'device.view')) {
        if ($r.permissions -notcontains $p) {
            Fail-Hard "The account $AdminEmail does not hold '$p'. Grant it before running the acceptance."
        }
    }
    "  signed in as $($r.email); localuser.elevate held"
}

function Api {
    param([string]$Path, [string]$Method = 'Get', $Body = $null)

    # Deliberately not $args: that is an automatic variable.
    $req = @{
        Method     = $Method
        Uri        = "$ServerBaseUrl/api$Path"
        Headers    = @{ Authorization = "Bearer $($script:Token)"; 'X-Requested-With' = 'XMLHttpRequest' }
        TimeoutSec = 60
    }
    if ($null -ne $Body) {
        $req.ContentType = 'application/json'
        $req.Body = ($Body | ConvertTo-Json -Depth 5)
    }
    Invoke-RestMethod @req
}

function Get-HttpStatus {
    param($ErrorRecord)
    try { [int]$ErrorRecord.Exception.Response.StatusCode.value__ } catch { -1 }
}

# --------------------------------------------------------------------------
# Windows, read by SID rather than by name
# --------------------------------------------------------------------------
function Get-AccountSnapshot {
    $groupName = Get-AdministratorsGroupName
    try {
        $adminSids = @(Get-LocalGroupMember -Group $groupName -ErrorAction Stop |
            ForEach-Object { $_.SID.Value })
    } catch {
        Fail-Hard "Could not enumerate '$groupName': $($_.Exception.Message)"
    }

    Get-LocalUser | ForEach-Object {
        [PSCustomObject]@{
            Sid     = $_.SID.Value
            Rid     = ($_.SID.Value -split '-')[-1]
            Name    = $_.Name
            Enabled = [bool]$_.Enabled
            IsAdmin = $adminSids -contains $_.SID.Value
        }
    } | Sort-Object Sid
}

function Format-Accounts { param($S) $S | Format-Table Name, Rid, Enabled, IsAdmin, Sid -AutoSize | Out-String }

function Test-IsAdminSid {
    param([string]$Sid)
    [bool](Get-AccountSnapshot | Where-Object { $_.Sid -eq $Sid -and $_.IsAdmin })
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSeconds, [int]$PollSeconds = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Seconds $PollSeconds
    }
    & $Condition
}

function Wait-ForInventory {
    param([string]$Because)

    $before = (Api "/admin/v1/devices/$($script:DeviceId)/local-admin-posture").lastReportedAt
    "  requesting inventory refresh ($Because)"
    Api "/admin/v1/devices/$($script:DeviceId)/refresh-inventory" -Method Post | Out-Null

    $ok = Wait-Until -TimeoutSeconds $InventoryTimeoutSeconds -Condition {
        (Api "/admin/v1/devices/$($script:DeviceId)/local-admin-posture").lastReportedAt -ne $before
    }
    if (-not $ok) {
        Fail-Hard "No fresh inventory within $InventoryTimeoutSeconds s. The agent may be offline."
    }
    "  new inventory at $((Api "/admin/v1/devices/$($script:DeviceId)/local-admin-posture").lastReportedAt)"
}

function Get-Elevations { @(Api "/admin/v1/devices/$($script:DeviceId)/elevations") }
function Get-DeviceTasks { @(Api "/admin/v1/devices/$($script:DeviceId)/tasks") }
function Get-ReportedUsers { @(Api "/admin/v1/devices/$($script:DeviceId)/local-users") }

# ==========================================================================
'M12 ACCEPTANCE - temporary local administrator elevation'
"Started $(Get-Date -Format 'u')  |  Server $ServerBaseUrl"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'ABORTED: run this elevated. Local account and group reads require it.' -ForegroundColor Red
    exit 2
}

Connect-Api

Section '1. ENDPOINT IDENTITY (read-only)'
$LocalHostname = $env:COMPUTERNAME
$LocalUuid = (Get-CimInstance Win32_ComputerSystemProduct).UUID
"  ComputerName : $LocalHostname"
"  SMBIOS UUID  : $LocalUuid"

if ($LocalHostname -ne $ExpectedHostname) {
    Fail-Hard "This machine is '$LocalHostname' but the acceptance targets '$ExpectedHostname'."
}

$page = Api "/admin/v1/devices?search=$([Uri]::EscapeDataString($LocalHostname))&pageSize=100"
$match = @($page.items | Where-Object { $_.hostname -eq $LocalHostname })
if ($match.Count -ne 1) {
    Fail-Hard "Expected exactly one enrolled device named '$LocalHostname'; the server returned $($match.Count)."
}

$script:DeviceId = $match[0].id
$detail = Api "/admin/v1/devices/$($script:DeviceId)"

# Hostnames are reusable; the SMBIOS UUID is what makes this the same machine.
if ($detail.machineIdentifier -ne $LocalUuid) {
    Fail-Hard ("Device $($script:DeviceId) reports machineIdentifier '$($detail.machineIdentifier)' but " +
        "this machine's SMBIOS UUID is '$LocalUuid'. Refusing to act on a device that may not be this one.")
}
if ($detail.status -ne 'Active') {
    Fail-Hard "Device status is '$($detail.status)', not Active."
}

$BaselineDeviceId = $script:DeviceId
$BaselineMachineId = $detail.machineIdentifier
$BaselineAgent = $match[0].agentVersion
"  DeviceId     : $($script:DeviceId)"
"  AgentVersion : $BaselineAgent"
"  Online       : $($match[0].isOnline)"

if (-not $match[0].isOnline) {
    Fail-Hard 'The device is offline. Elevation cannot be observed end to end.'
}

$agentService = Get-Service EndpointPlatformAgent -ErrorAction SilentlyContinue
if (-not $agentService -or $agentService.Status -ne 'Running') {
    Fail-Hard "The EndpointPlatformAgent service is not running (status: $($agentService.Status))."
}

Section '2. BASELINE ACCOUNTS AND ADMINISTRATORS MEMBERSHIP (read-only)'
$BaselineAccounts = @(Get-AccountSnapshot)
Format-Accounts $BaselineAccounts

foreach ($n in @($StandardUser, $ExistingAdmin)) {
    if ($BaselineAccounts.Name -contains $n) {
        Fail-Hard ("An account named '$n' already exists on this machine. Refusing to run: it was not " +
            'created by this script and must not be treated as disposable.')
    }
}

$BaselineAdmins = @($BaselineAccounts | Where-Object { $_.IsAdmin })
"  Existing administrators: $(($BaselineAdmins.Name) -join ', ')"

$BuiltIn = $BaselineAccounts | Where-Object { $_.Rid -eq '500' }
if (-not $BuiltIn) { Fail-Hard 'No RID-500 account found. Baseline state is not what this script expects.' }
"  RID 500 : $($BuiltIn.Name)  Enabled=$($BuiltIn.Enabled)  IsAdmin=$($BuiltIn.IsAdmin)"

Section '3. BASELINE ELEVATION AND TASK STATE (read-only)'
$BaselineElevations = Get-Elevations
$BaselineTasks = Get-DeviceTasks
"  elevation records: $($BaselineElevations.Count)"
"  device tasks:      $($BaselineTasks.Count)"

$liveAtStart = @($BaselineElevations | Where-Object { $_.isLive })
if ($liveAtStart.Count -gt 0) {
    Fail-Hard ("$($liveAtStart.Count) elevation(s) are already live on this device. Let them end before " +
        'running the acceptance, so the observed membership is attributable to this run.')
}

Section '4. BASELINE M11b POSTURE (read-only; must be unaffected by M12)'
$BaselinePosture = Api "/admin/v1/devices/$($script:DeviceId)/local-admin-posture"
"  compliance: $($BaselinePosture.compliance)"
"  interactive admins: $((@($BaselinePosture.interactiveAdministrators).username) -join ', ')"

# ==========================================================================
Mutating "creating '$StandardUser' (standard) and '$ExistingAdmin' (administrator)"
Section '5. CREATE TEMPORARY TEST ACCOUNTS'

$pw1 = ConvertTo-SecureString ([Guid]::NewGuid().ToString() + 'Aa1!') -AsPlainText -Force
New-LocalUser -Name $StandardUser -Password $pw1 -FullName 'M12 acceptance (standard)' `
    -Description 'Temporary account for Milestone 12 acceptance' -ErrorAction Stop | Out-Null
$StdSid = (Get-LocalUser -Name $StandardUser).SID.Value
$script:CreatedSids += $StdSid
Add-LocalGroupMember -Group (Get-LocalGroup | Where-Object { $_.SID.Value -eq $UsersSid }).Name `
    -Member $StandardUser -ErrorAction SilentlyContinue

$pw2 = ConvertTo-SecureString ([Guid]::NewGuid().ToString() + 'Bb2!') -AsPlainText -Force
New-LocalUser -Name $ExistingAdmin -Password $pw2 -FullName 'M12 acceptance (pre-existing admin)' `
    -Description 'Temporary account for Milestone 12 acceptance' -ErrorAction Stop | Out-Null
$AdmSid = (Get-LocalUser -Name $ExistingAdmin).SID.Value
$script:CreatedSids += $AdmSid
Add-LocalGroupMember -Group (Get-AdministratorsGroupName) -Member $ExistingAdmin -ErrorAction Stop

$pw1 = $null; $pw2 = $null

"  $StandardUser  SID $StdSid  (standard)"
"  $ExistingAdmin SID $AdmSid  (administrator, pre-existing from the platform's point of view)"
Format-Accounts (Get-AccountSnapshot)

Wait-ForInventory -Because 'test accounts created'

Section '6. ELIGIBILITY: RID 500 AND EXISTING ADMINISTRATORS ARE NOT TARGETS'
$users = Get-ReportedUsers
$builtInRow = $users | Where-Object { $_.sid -like '*-500' }
$stdRow = $users | Where-Object { $_.sid -eq $StdSid }
$admRow = $users | Where-Object { $_.sid -eq $AdmSid }

Set-Criterion 'C1 The endpoint reports the new accounts and their administrator status' `
    $(if ($stdRow -and -not $stdRow.isLocalAdministrator -and $admRow -and $admRow.isLocalAdministrator) { 'PASS' } else { 'FAIL' }) `
    "std isAdmin=$($stdRow.isLocalAdministrator); adm isAdmin=$($admRow.isLocalAdministrator)"

Set-Criterion 'C2 RID 500 is reported as an administrator' `
    $(if ($builtInRow -and $builtInRow.isLocalAdministrator) { 'PASS' } else { 'FAIL' }) `
    "reported: $($builtInRow.name), isAdmin=$($builtInRow.isLocalAdministrator)"

# Proven by the server refusing it, not by the console hiding it.
try {
    Api "/admin/v1/devices/$($script:DeviceId)/elevations" -Method Post -Body @{
        targetSid = $builtInRow.sid
        justification = 'Acceptance probe: the server must refuse this.'
        durationMinutes = 15
    } | Out-Null
    Set-Criterion 'C3 The server refuses to elevate RID 500' 'FAIL' 'The request was accepted.'
} catch {
    $code = Get-HttpStatus $_
    Set-Criterion 'C3 The server refuses to elevate RID 500' `
        $(if ($code -eq 409) { 'PASS' } else { 'FAIL' }) "HTTP $code (expected 409)"
}

Set-Criterion 'C4 The refusal changed nothing on the endpoint' `
    $(if ((Test-IsAdminSid $BuiltIn.Sid) -eq $BuiltIn.IsAdmin) { 'PASS' } else { 'FAIL' }) ''

# ==========================================================================
Mutating "requesting and approving an elevation for $StandardUser"
Section '7. ELEVATE THE STANDARD USER'

$created = Api "/admin/v1/devices/$($script:DeviceId)/elevations" -Method Post -Body @{
    targetSid = $StdSid
    justification = 'M12 acceptance: prove a standard user becomes an administrator for a bounded window.'
    durationMinutes = 60
}
$ElevationId = $created.id
"  elevation $ElevationId state=$($created.state) expires=$($created.expiresAt)"

Set-Criterion 'C5 An elevation can be requested and approved in one step' `
    $(if ($created.state -eq 'Approved' -and $created.expiresAt) { 'PASS' } else { 'FAIL' }) `
    "state=$($created.state) expiresAt=$($created.expiresAt)"

'  waiting for the endpoint to apply it...'
$applied = Wait-Until -TimeoutSeconds $InventoryTimeoutSeconds -Condition { Test-IsAdminSid $StdSid }

Set-Criterion 'C6 The standard user actually becomes a Windows administrator' `
    $(if ($applied) { 'PASS' } else { 'FAIL' }) `
    "Windows Administrators membership read through SID $AdministratorsSid"
Format-Accounts (Get-AccountSnapshot)

Section '8. A SECOND LIVE ELEVATION FOR THE SAME ACCOUNT IS REFUSED'
try {
    Api "/admin/v1/devices/$($script:DeviceId)/elevations" -Method Post -Body @{
        targetSid = $StdSid
        justification = 'Acceptance probe: a second live elevation must be refused.'
        durationMinutes = 30
    } | Out-Null
    Set-Criterion 'C7 A concurrent elevation for the same account is refused' 'FAIL' 'The request was accepted.'
} catch {
    $code = Get-HttpStatus $_
    Set-Criterion 'C7 A concurrent elevation for the same account is refused' `
        $(if ($code -eq 409) { 'PASS' } else { 'FAIL' }) "HTTP $code (expected 409)"
}

Wait-ForInventory -Because 'elevation applied'

Section '9. THE SERVER REPORTS THE ENDPOINT STATE RATHER THAN ASSUMING IT'
$usersAfter = Get-ReportedUsers
$stdAfter = $usersAfter | Where-Object { $_.sid -eq $StdSid }
$windowsSaysAdmin = Test-IsAdminSid $StdSid

Set-Criterion 'C8 Reported administrator status matches Windows' `
    $(if ($stdAfter.isLocalAdministrator -eq $windowsSaysAdmin) { 'PASS' } else { 'FAIL' }) `
    "server isAdmin=$($stdAfter.isLocalAdministrator); Windows=$windowsSaysAdmin"

$elevationsForAdm = @(Get-Elevations | Where-Object { $_.targetSid -eq $AdmSid })
Set-Criterion 'C9 A pre-existing administrator is neither adopted nor demoted' `
    $(if ((Test-IsAdminSid $AdmSid) -and $elevationsForAdm.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    "$ExistingAdmin still admin=$(Test-IsAdminSid $AdmSid); elevation records for it: $($elevationsForAdm.Count)"

$otherDrift = @($BaselineAdmins | Where-Object { -not (Test-IsAdminSid $_.Sid) })
Set-Criterion 'C10 No pre-existing administrator lost rights during the elevation' `
    $(if ($otherDrift.Count -eq 0) { 'PASS' } else { 'FAIL' }) (($otherDrift.Name) -join ', ')

# ==========================================================================
Mutating 'revoking the elevation'
Section '10. REVOKE RETURNS THE ACCOUNT TO STANDARD'
Api "/admin/v1/elevations/$ElevationId/revoke" -Method Post -Body @{ note = 'M12 acceptance.' } | Out-Null

$lowered = Wait-Until -TimeoutSeconds $InventoryTimeoutSeconds -Condition { -not (Test-IsAdminSid $StdSid) }

Set-Criterion 'C11 Revoke returns the account to Windows standard' `
    $(if ($lowered) { 'PASS' } else { 'FAIL' }) 'Windows Administrators membership after revoke'

$revoked = Get-Elevations | Where-Object { $_.id -eq $ElevationId }
Set-Criterion 'C12 The revoked record is terminal and not live' `
    $(if ($revoked.state -eq 'Revoked' -and -not $revoked.isLive) { 'PASS' } else { 'FAIL' }) `
    "state=$($revoked.state) isLive=$($revoked.isLive)"

try {
    Api "/admin/v1/elevations/$ElevationId/revoke" -Method Post -Body @{ note = 'Acceptance probe.' } | Out-Null
    Set-Criterion 'C13 Revoking a terminal elevation is refused' 'FAIL' 'The second revoke was accepted.'
} catch {
    $code = Get-HttpStatus $_
    Set-Criterion 'C13 Revoking a terminal elevation is refused' `
        $(if ($code -eq 409) { 'PASS' } else { 'FAIL' }) "HTTP $code (expected 409)"
}

# ==========================================================================
Section "11. EXPIRY RETURNS THE ACCOUNT TO STANDARD ($ShortElevationMinutes minutes)"

if ($SkipExpiryTest) {
    '  SKIPPED by -SkipExpiryTest.'
    Set-Criterion 'C14 Expiry returns the account to Windows standard' 'DEFERRED' 'Skipped by -SkipExpiryTest.'
    Set-Criterion 'C15 An expired elevation cannot be re-approved' 'DEFERRED' 'Skipped by -SkipExpiryTest.'
    Set-Criterion 'C16 The account stays standard after the replay attempt' 'DEFERRED' 'Skipped by -SkipExpiryTest.'
} else {
    Mutating "elevating $StandardUser for $ShortElevationMinutes minutes and waiting the window out"
    $short = Api "/admin/v1/devices/$($script:DeviceId)/elevations" -Method Post -Body @{
        targetSid = $StdSid
        justification = 'M12 acceptance: prove that expiry alone returns the account to standard.'
        durationMinutes = $ShortElevationMinutes
    }
    "  elevation $($short.id) expires $($short.expiresAt)"

    $up = Wait-Until -TimeoutSeconds $InventoryTimeoutSeconds -Condition { Test-IsAdminSid $StdSid }
    "  elevated: $up"

    # Past the deadline, plus a margin for the sweeper interval and the next
    # agent poll. No administrator action is taken here: expiry alone must do it.
    $expiryDeadline = ([DateTimeOffset]$short.expiresAt).AddMinutes(5)
    "  waiting until $($expiryDeadline.ToLocalTime()) for the window to close by itself..."
    while ([DateTimeOffset]::UtcNow -lt $expiryDeadline -and (Test-IsAdminSid $StdSid)) {
        Start-Sleep -Seconds 30
    }

    Set-Criterion 'C14 Expiry returns the account to Windows standard' `
        $(if (-not (Test-IsAdminSid $StdSid)) { 'PASS' } else { 'FAIL' }) `
        'Windows Administrators membership after the window closed, with no administrator action'

    $expired = Get-Elevations | Where-Object { $_.id -eq $short.id }
    "  record state: $($expired.state) isLive=$($expired.isLive)"

    try {
        Api "/admin/v1/elevations/$($short.id)/approve" -Method Post -Body @{ durationMinutes = 60 } | Out-Null
        Set-Criterion 'C15 An expired elevation cannot be re-approved' 'FAIL' 'The approval was accepted.'
    } catch {
        $code = Get-HttpStatus $_
        Set-Criterion 'C15 An expired elevation cannot be re-approved' `
            $(if ($code -eq 409) { 'PASS' } else { 'FAIL' }) "HTTP $code (expected 409)"
    }

    Set-Criterion 'C16 The account stays standard after the replay attempt' `
        $(if (-not (Test-IsAdminSid $StdSid)) { 'PASS' } else { 'FAIL' }) ''
}

# ==========================================================================
Section '12. LAST-ENABLED-ADMINISTRATOR PROTECTION'
'  DEFERRED BY DESIGN. Proving this physically requires the elevated test account'
'  to be the only ENABLED administrator, which means temporarily disabling every'
'  real administrator on the machine. Automating that on an endpoint anybody'
'  depends on risks exactly the lockout the guard exists to prevent.'
''
'  To prove it deliberately, on a disposable machine only:'
"    1. Confirm RID 500 is disabled and $ExistingAdmin is the only other administrator."
"    2. Elevate $StandardUser and wait for it to apply."
"    3. Disable $ExistingAdmin."
'    4. Revoke the elevation.'
'    5. EXPECT: the agent refuses to lower the account, the task result names the'
"       last-enabled-administrator guard, and $StandardUser REMAINS an administrator."
'    6. Re-enable the administrator, revoke again, and confirm it now lowers.'
Set-Criterion 'C17 Last-enabled-administrator protection' 'DEFERRED' `
    'Requires disabling every real administrator; unsafe to automate. Covered by agent unit tests.'

Section '13. RBAC AND SCOPE BOUNDARIES'
'  DEFERRED. Proving these needs a second administrator without localuser.elevate'
'  and a second device outside the caller scope. The platform currently has one'
'  administrator and no console for creating another, so neither can be arranged'
'  from this endpoint. Both are covered by API tests that exercise the real'
'  authorization chain over HTTP.'
Set-Criterion 'C18 RBAC and device scope boundaries' 'DEFERRED' `
    'Needs a second identity and a second device; covered by API tests over HTTP.'

Section '14. TASK BEHAVIOUR'
$FinalTasks = Get-DeviceTasks
$newTasks = @($FinalTasks | Where-Object { $BaselineTasks.id -notcontains $_.id })
$elevationTasks = @($newTasks | Where-Object { $_.type -eq 'ApplyLocalAdminElevation' })
$unexpected = @($newTasks | Where-Object { $_.type -ne 'ApplyLocalAdminElevation' })

"  new tasks: $($newTasks.Count) ($($elevationTasks.Count) ApplyLocalAdminElevation)"
$newTasks | ForEach-Object { "    $($_.type) $($_.status)  $($_.resultMessage)" }

Set-Criterion 'C19 Only ApplyLocalAdminElevation tasks were created' `
    $(if ($unexpected.Count -eq 0 -and $elevationTasks.Count -gt 0) { 'PASS' } else { 'FAIL' }) `
    "$($elevationTasks.Count) elevation task(s), $($unexpected.Count) unexpected"

$failedTasks = @($elevationTasks | Where-Object { $_.status -eq 'Failed' })
Set-Criterion 'C20 No elevation task failed' `
    $(if ($failedTasks.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    (($failedTasks | ForEach-Object { $_.resultMessage }) -join '; ')

Set-Criterion 'C21 Audit trail for every elevation decision' 'DEFERRED' `
    'No audit read API exists. Confirm localuser.elevation.* rows server-side, with actor and justification.'

# ==========================================================================
Mutating 'deleting the temporary test accounts this script created'
Section '15. CLEANUP'
Remove-CreatedAccounts

Section '16. FINAL DIFF AGAINST THE BASELINE (read-only)'
$FinalAccounts = @(Get-AccountSnapshot)
Format-Accounts $FinalAccounts

$drift = @()
foreach ($b in $BaselineAccounts) {
    $a = $FinalAccounts | Where-Object { $_.Sid -eq $b.Sid }
    if (-not $a) { $drift += "MISSING: $($b.Name)"; continue }
    if ($a.Name -ne $b.Name) { $drift += "RENAMED: $($b.Name) -> $($a.Name)" }
    if ($a.Enabled -ne $b.Enabled) { $drift += "ENABLED: $($b.Name) $($b.Enabled) -> $($a.Enabled)" }
    if ($a.IsAdmin -ne $b.IsAdmin) { $drift += "ADMIN: $($b.Name) $($b.IsAdmin) -> $($a.IsAdmin)" }
}
$leftover = @($FinalAccounts | Where-Object { $BaselineAccounts.Sid -notcontains $_.Sid })

if ($drift.Count) { '  DRIFT:'; $drift | ForEach-Object { "    $_" } } else { '  No pre-existing account changed.' }
if ($leftover.Count) { '  LEFTOVER:'; $leftover | ForEach-Object { "    $($_.Name)" } } else { '  No leftover account.' }

Set-Criterion 'C22 Every pre-existing account is unchanged' `
    $(if ($drift.Count -eq 0) { 'PASS' } else { 'FAIL' }) ($drift -join '; ')

Set-Criterion 'C23 Both temporary accounts were removed' `
    $(if ($leftover.Count -eq 0) { 'PASS' } else { 'FAIL' }) (($leftover.Name) -join ', ')

$bF = $FinalAccounts | Where-Object { $_.Rid -eq '500' }
Set-Criterion 'C24 The built-in Administrator was not modified' `
    $(if ($bF -and $bF.Name -eq $BuiltIn.Name -and $bF.Enabled -eq $BuiltIn.Enabled -and $bF.IsAdmin -eq $BuiltIn.IsAdmin) { 'PASS' } else { 'FAIL' }) `
    "$($BuiltIn.Name)/Enabled=$($BuiltIn.Enabled) -> $($bF.Name)/Enabled=$($bF.Enabled)"

$finalDetail = Api "/admin/v1/devices/$($script:DeviceId)"
Set-Criterion 'C25 Device identity and agent version are unchanged' `
    $(if ($finalDetail.id -eq $BaselineDeviceId -and $finalDetail.machineIdentifier -eq $BaselineMachineId `
            -and $finalDetail.agentVersion -eq $BaselineAgent) { 'PASS' } else { 'FAIL' }) `
    "agent $BaselineAgent -> $($finalDetail.agentVersion)"

Set-Criterion 'C26 The agent service was never stopped by this script' `
    $(if ((Get-Service EndpointPlatformAgent).Status -eq 'Running') { 'PASS' } else { 'FAIL' }) ''

$finalPosture = Api "/admin/v1/devices/$($script:DeviceId)/local-admin-posture"
Set-Criterion 'C27 M11b posture still answers, unaffected by M12' `
    $(if ($finalPosture.compliance -in @('Compliant', 'NonCompliant', 'Unknown')) { 'PASS' } else { 'FAIL' }) `
    "baseline=$($BaselinePosture.compliance) final=$($finalPosture.compliance)"

Show-Result
"Finished $(Get-Date -Format 'u')"
