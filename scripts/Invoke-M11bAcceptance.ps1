<#
.SYNOPSIS
    Milestone 11b (Local User Security Posture) - Option A physical acceptance.

.DESCRIPTION
    Reconstructed from the implemented M11b contracts only. Every route, field
    name and authentication step below was read out of the repository:

      POST /admin/v1/auth/login                                 AuthEndpoints
           body {email,password} -> {sessionToken}
           header X-Requested-With: XMLHttpRequest
           then Authorization: Bearer <sessionToken>
      GET  /admin/v1/devices                                    DeviceReadService.DeviceListItem
      GET  /admin/v1/devices/{id}                               device detail (machineIdentifier)
      GET  /admin/v1/devices/{id}/local-admin-posture           LocalAccountEndpoints (M11b)
      GET  /admin/v1/devices/{id}/local-users                   LocalAccountEndpoints
      GET  /admin/v1/devices/{id}/usb-devices                   UsbEndpoints / UsbDeviceView
      GET  /admin/v1/tasks?pageSize=200                         TaskEndpoints
      POST /admin/v1/devices/{id}/refresh-inventory             DeviceEndpoints

    NOTHING ELSE IS CALLED. No route is guessed.

.NOTES
    WHAT THIS TOUCHES ON THE MACHINE
    --------------------------------
    Sections 1-3  : READ-ONLY.
    Sections 4-12 : create, promote, disable and delete ONE account, EPP-11B-STD.
    Section 13-15 : READ-ONLY comparison.

    It never modifies a pre-existing account, never touches the built-in
    Administrator, never stops or restarts the agent service, never touches a
    USB device, and never deploys or publishes anything.

    ON THE REPORTING CYCLE
    ----------------------
    Account inventory is NOT collected on a timer. The agent uploads it only
    when the server sets InventoryRequested, which happens when the device has
    never reported or when an administrator calls refresh-inventory
    (Device.IsInventoryRefreshPending). That endpoint sets a flag and writes an
    audit row; it does NOT create a DeviceTask. It is therefore the platform's
    own reporting cycle, not a substitute mechanism, and using it leaves the
    "no unexpected task" check meaningful.

    ON AUDIT VERIFICATION
    ---------------------
    The platform exposes NO audit read API - /audit is a placeholder page in the
    dashboard and there is no MapGet for it in the Admin API. This script will
    NOT invent one. The three audit criteria are therefore reported as DEFERRED
    and must be confirmed server-side. They are never reported as PASS.

.EXAMPLE
    See "HOW TO RUN" printed at the end of the plan message.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ServerBaseUrl,
    [Parameter(Mandatory)] [string] $AdminEmail,
    [Parameter(Mandatory)] [SecureString] $AdminPassword,

    [string]   $ExpectedHostname = 'LAPTOP-LVCHEQ2H',
    [string]   $TestUser         = 'EPP-11B-STD',

    # How long to wait for one inventory round-trip before failing.
    [int]      $InventoryTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

# --------------------------------------------------------------------------
# Result ledger. Criteria are recorded explicitly; nothing is implied.
# --------------------------------------------------------------------------
$script:Results = [ordered]@{}

function Set-Criterion {
    param([string]$Id, [string]$State, [string]$Detail = '')
    $script:Results[$Id] = [PSCustomObject]@{ State = $State; Detail = $Detail }
}

function Section { param([string]$Name) "`n$('=' * 74)`n  $Name`n$('=' * 74)" }
function Mutating { param([string]$What) "`n*** MUTATING STEP *** $What" }

function Show-Result {
    Section 'M11b ACCEPTANCE RESULT'
    foreach ($k in $script:Results.Keys) {
        $r = $script:Results[$k]
        $colour = switch ($r.State) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
        Write-Host ('  {0,-8} {1}' -f $r.State, $k) -ForegroundColor $colour
        if ($r.Detail) { Write-Host "           $($r.Detail)" -ForegroundColor DarkGray }
    }

    $failed   = @($script:Results.Values | Where-Object { $_.State -eq 'FAIL' }).Count
    $deferred = @($script:Results.Values | Where-Object { $_.State -eq 'DEFERRED' }).Count

    ''
    if ($failed -gt 0) {
        Write-Host "  M11b ACCEPTANCE RESULT: FAIL  ($failed criterion/criteria failed)" -ForegroundColor Red
    } else {
        Write-Host '  M11b ACCEPTANCE RESULT: PASS  (all script-verifiable criteria)' -ForegroundColor Green
    }
    if ($deferred -gt 0) {
        Write-Host "  $deferred criterion/criteria DEFERRED - not verifiable from this endpoint." -ForegroundColor Yellow
        Write-Host '  Milestone 11b is NOT closed until those are confirmed server-side.' -ForegroundColor Yellow
    }
    ''
}

function Fail-Hard {
    param([string]$Why)
    Write-Host "`nABORTED: $Why" -ForegroundColor Red
    Write-Host 'The script refuses to guess. Nothing further was attempted.' -ForegroundColor Red
    Show-Result
    exit 2
}

# --------------------------------------------------------------------------
# API. Auth exactly as AuthEndpoints implements it.
# --------------------------------------------------------------------------
$script:Token = $null

function Connect-Api {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($AdminPassword))
    try {
        $body = @{ email = $AdminEmail; password = $plain } | ConvertTo-Json
        $r = Invoke-RestMethod -Method Post -Uri "$ServerBaseUrl/api/admin/v1/auth/login" `
            -Headers @{ 'X-Requested-With' = 'XMLHttpRequest' } `
            -ContentType 'application/json' -Body $body -TimeoutSec 30
    } catch {
        Fail-Hard "Could not authenticate to $ServerBaseUrl as $AdminEmail. $($_.Exception.Message)"
    } finally {
        $plain = $null
    }

    if (-not $r.sessionToken) { Fail-Hard 'Login returned no sessionToken; the auth contract is not what this script expects.' }
    $script:Token = $r.sessionToken
}

function Api {
    param([string]$Path, [string]$Method = 'Get')
    if (-not $script:Token) { Fail-Hard 'Api called before Connect-Api.' }

    # Returned plainly. A function emitting an array unrolls it into the
    # pipeline, and every call site wraps in @( ) which recollects it -- that
    # round trip is correct and was verified in isolation. Write-Output
    # -NoEnumerate was tried here and is WRONG: @( ) then wraps the whole array
    # in a further one-element array, and the comparison silently degrades to
    # comparing System.Object[] against itself.
    Invoke-RestMethod -Method $Method -Uri "$ServerBaseUrl/api$Path" -TimeoutSec 60 -Headers @{
        'Authorization'    = "Bearer $($script:Token)"
        'X-Requested-With' = 'XMLHttpRequest'
    }
}

<#
.SYNOPSIS
    Deterministic identity for a set of USB records.
.DESCRIPTION
    Sorted by instanceId so ordering cannot affect the comparison: the server
    orders by IsConnected, DeviceClass and LastSeenAt, and LastSeenAt moves on
    every USB report, so server order is not stable between two reads.

    Deliberately excludes lastSeenAt, firstSeenAt, enforcedAt and disconnectedAt.
    Those are inventory timestamps that change as a matter of normal reporting;
    including them would make this assert "no USB report happened", which is not
    what the criterion is about. What must not change is the device set and the
    policy applied to each one.
#>
function Get-UsbStateKey {
    param([object[]]$Devices)

    if (-not $Devices -or $Devices.Count -eq 0) { return '' }

    # Refuse to build a key from something that is not a USB record. Without
    # this, a response arriving in an unexpected shape stringifies to
    # 'System.Object[]|...' and the comparison still "works" -- returning PASS
    # or FAIL for reasons unrelated to USB. Failing loudly is the point: the
    # previous C13 result was misleading precisely because a malformed capture
    # compared cleanly against itself.
    $malformed = @($Devices | Where-Object { -not $_.PSObject.Properties['instanceId'] -or -not $_.instanceId })
    if ($malformed.Count -gt 0) {
        Fail-Hard ("The USB response did not have the expected shape: $($malformed.Count) of " +
            "$($Devices.Count) record(s) have no instanceId. First element type: " +
            "$(@($Devices)[0].GetType().FullName). Refusing to compare USB state on a guess.")
    }

    ($Devices | Sort-Object -Property instanceId | ForEach-Object {
        '{0}|{1}|{2}|{3}' -f $_.instanceId, $_.policy, $_.enforcementState, $_.isConnected
    }) -join "`n"
}

# --------------------------------------------------------------------------
# Windows helpers. Membership is resolved by SID, never by group name.
# --------------------------------------------------------------------------
$AdministratorsSid = 'S-1-5-32-544'

function Get-AdministratorsGroupName {
    $g = Get-LocalGroup | Where-Object { $_.SID.Value -eq $AdministratorsSid }
    if (-not $g) { Fail-Hard "Could not resolve the local Administrators group by SID $AdministratorsSid." }
    $g.Name
}

function Get-AccountSnapshot {
    $groupName = Get-AdministratorsGroupName
    try {
        $adminSids = @(Get-LocalGroupMember -Group $groupName -ErrorAction Stop |
            ForEach-Object { $_.SID.Value })
    } catch {
        Fail-Hard "Could not enumerate members of '$groupName': $($_.Exception.Message)"
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

function Format-Accounts { param($Snapshot) $Snapshot | Format-Table Name, Rid, Enabled, IsAdmin, Sid -AutoSize | Out-String }

# --------------------------------------------------------------------------
# One inventory round-trip through the platform's own mechanism.
# --------------------------------------------------------------------------
function Wait-ForInventory {
    param([string]$DeviceId, [string]$Because)

    $before = (Api "/admin/v1/devices/$DeviceId/local-admin-posture").lastReportedAt
    "  requesting inventory refresh ($Because); previous report: $(if ($before) { $before } else { '(none)' })"

    Api "/admin/v1/devices/$DeviceId/refresh-inventory" -Method Post | Out-Null

    $deadline = (Get-Date).AddSeconds($InventoryTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 10
        $p = Api "/admin/v1/devices/$DeviceId/local-admin-posture"
        if ($p.lastReportedAt -and $p.lastReportedAt -ne $before) {
            "  new inventory observed at $($p.lastReportedAt)"
            return $p
        }
    }

    Fail-Hard ("No new inventory was observed within $InventoryTimeoutSeconds s. " +
        'The agent may be offline or not reporting. The script will not assume a result.')
}

# ==========================================================================
"M11b ACCEPTANCE - Option A"
"Started $(Get-Date -Format 'u')  |  Server $ServerBaseUrl"

Connect-Api

Section '1. ENDPOINT IDENTITY (read-only)'
$LocalHostname = $env:COMPUTERNAME
$LocalUuid     = (Get-CimInstance Win32_ComputerSystemProduct).UUID
"  ComputerName : $LocalHostname"
"  SMBIOS UUID  : $LocalUuid"

if ($LocalHostname -ne $ExpectedHostname) {
    Fail-Hard "This machine is '$LocalHostname' but the acceptance targets '$ExpectedHostname'."
}

$devicePage = Api '/admin/v1/devices?pageSize=200'
$match = @($devicePage.items | Where-Object { $_.hostname -eq $LocalHostname })
if ($match.Count -ne 1) {
    Fail-Hard "Expected exactly one device named '$LocalHostname'; the server returned $($match.Count)."
}

$DeviceId = $match[0].id
$detail   = Api "/admin/v1/devices/$DeviceId"

if ($detail.machineIdentifier -ne $LocalUuid) {
    Fail-Hard ("Device $DeviceId reports machineIdentifier '$($detail.machineIdentifier)' " +
        "but this machine's SMBIOS UUID is '$LocalUuid'. Refusing to act on a device that may not be this one.")
}

"  DeviceId          : $DeviceId"
"  MachineIdentifier : $($detail.machineIdentifier)   (matches this machine)"
"  AgentVersion      : $($match[0].agentVersion)"
"  Status / Online   : $($match[0].status) / $($match[0].isOnline)"

$BaselineDeviceId  = $DeviceId
$BaselineMachineId = $detail.machineIdentifier
$BaselineAgent     = $match[0].agentVersion
$BaselineDeviceCount = $devicePage.totalCount

Section '2. BASELINE STATE (read-only, immutable reference)'
$BaselineAccounts = Get-AccountSnapshot
'  Local accounts and Administrators membership:'
Format-Accounts $BaselineAccounts

if ($BaselineAccounts.Name -contains $TestUser) {
    Fail-Hard "'$TestUser' already exists on this machine. Refusing to run: it could be a real account."
}

$BaselineTasks = @((Api '/admin/v1/tasks?pageSize=200').items | Where-Object { $_.deviceId -eq $DeviceId })
"  Tasks for this device (baseline): $($BaselineTasks.Count)"

$BaselineUsb = @(Api "/admin/v1/devices/$DeviceId/usb-devices")
"  USB devices (baseline): $($BaselineUsb.Count)"
foreach ($u in ($BaselineUsb | Sort-Object -Property instanceId)) {
    "    $($u.policy)/$($u.enforcementState) connected=$($u.isConnected)  $($u.instanceId)"
}
$BaselineUsbKey = Get-UsbStateKey -Devices $BaselineUsb

Section '3. CURRENT POSTURE, BEFORE ANY CHANGE (read-only)'
$Posture0 = Api "/admin/v1/devices/$DeviceId/local-admin-posture"
"  compliance     : $($Posture0.compliance)"
"  lastReportedAt : $($Posture0.lastReportedAt)"
"  interactive admins: $((@($Posture0.interactiveAdministrators).username) -join ', ')"
"  findings       : $(@($Posture0.findings).Count)"
foreach ($f in @($Posture0.findings | Where-Object { $_.isAdministrator })) {
    "     admin '$($f.username)' counts=$($f.countsAgainstCompliance) reason='$($f.excludedReason)'"
}
"  limitation     : $($Posture0.limitation)"

$BaselineCompliance = $Posture0.compliance
$BaselineOffenderSids = @(@($Posture0.interactiveAdministrators).sid)
"  baseline device verdict: $BaselineCompliance"
"  baseline offenders     : $(if ($BaselineOffenderSids.Count) { $BaselineOffenderSids -join ', ' } else { '(none)' })"

# The built-in Administrator must never count, whatever it is called.
$builtIn = @($Posture0.findings | Where-Object { $_.sid -like '*-500' })
if ($builtIn.Count -eq 1) {
    $ok = -not $builtIn[0].countsAgainstCompliance
    Set-Criterion 'C3  Built-in Administrator (RID 500) is discounted regardless of its name' `
        $(if ($ok) { 'PASS' } else { 'FAIL' }) "named '$($builtIn[0].username)', reason '$($builtIn[0].excludedReason)'"
} else {
    Set-Criterion 'C3  Built-in Administrator (RID 500) is discounted regardless of its name' `
        'FAIL' "expected exactly one RID-500 finding, saw $($builtIn.Count)"
}

Set-Criterion 'C9  Limitation is stated in the payload' `
    $(if ($Posture0.limitation -match 'nested group') { 'PASS' } else { 'FAIL' }) $Posture0.limitation

# ==========================================================================
Mutating "creating '$TestUser' as a STANDARD user (no other account is touched)"
Section "4. CREATE $TestUser AS A STANDARD USER"

$pw = ConvertTo-SecureString -String ([Guid]::NewGuid().ToString() + 'Aa1!') -AsPlainText -Force
New-LocalUser -Name $TestUser -Password $pw -FullName 'M11b acceptance' `
    -Description 'Temporary account for Milestone 11b acceptance' -ErrorAction Stop | Out-Null
$pw = $null

$usersGroup = (Get-LocalGroup | Where-Object { $_.SID.Value -eq 'S-1-5-32-545' }).Name
if ($usersGroup) { Add-LocalGroupMember -Group $usersGroup -Member $TestUser -ErrorAction SilentlyContinue }

$TestUserSid = (Get-LocalUser -Name $TestUser).SID.Value
"  created $TestUser (SID $TestUserSid), standard user"
Format-Accounts (Get-AccountSnapshot)

Section '5. VERIFY THE STANDARD USER IS NOT AN OFFENDER'
#
# Deliberately NOT asserting device-level Compliant. The verdict is universally
# quantified over every account -- Evaluate() returns Compliant only when NO
# account counts against it -- and this endpoint already has Techsara as an
# enabled, non-built-in administrator. No action on the test account can make
# the device Compliant, so requiring it here would assert something the model
# cannot produce, and would fail while the implementation was behaving exactly
# as specified.
#
# What is actually under test is account-level: a standard user contributes
# nothing to the verdict. The device-level assertion is therefore that the
# verdict is UNCHANGED from the baseline.
#
$P1 = Wait-ForInventory -DeviceId $DeviceId -Because 'standard user created'
"  compliance: $($P1.compliance)  (baseline was $BaselineCompliance)"
$f1 = @($P1.findings | Where-Object { $_.sid -eq $TestUserSid })
$f1InAdmins = @($P1.interactiveAdministrators | Where-Object { $_.sid -eq $TestUserSid }).Count

$c1 = $f1.Count -eq 1 `
    -and $f1[0].isAdministrator -eq $false `
    -and $f1[0].countsAgainstCompliance -eq $false `
    -and $f1InAdmins -eq 0 `
    -and $P1.compliance -eq $BaselineCompliance

"  $TestUser -> present=$($f1.Count -eq 1) isAdmin=$(if($f1.Count){$f1[0].isAdministrator}) counts=$(if($f1.Count){$f1[0].countsAgainstCompliance}) inOffenderList=$($f1InAdmins -ne 0)"

Set-Criterion 'C1  A standard user is reported and is not an offender; device verdict unchanged' `
    $(if ($c1) { 'PASS' } else { 'FAIL' }) `
    ("present=$($f1.Count -eq 1), isAdmin=$(if($f1.Count){$f1[0].isAdministrator}), " +
     "counts=$(if($f1.Count){$f1[0].countsAgainstCompliance}), inOffenders=$($f1InAdmins -ne 0), " +
     "verdict $BaselineCompliance -> $($P1.compliance)")

# ==========================================================================
Mutating "adding '$TestUser' to the local Administrators group"
Section "6. PROMOTE $TestUser TO ADMINISTRATOR"
Add-LocalGroupMember -Group (Get-AdministratorsGroupName) -Member $TestUser -ErrorAction Stop
"  $TestUser added to $(Get-AdministratorsGroupName)"
Format-Accounts (Get-AccountSnapshot)

Section '7. VERIFY THE PROMOTED ACCOUNT BECOMES AN OFFENDER'
#
# NonCompliant alone proves nothing here: the device was already NonCompliant
# because of Techsara, so that assertion would pass even if the promotion had
# had no effect whatsoever. The subject is the TRANSITION of this one account
# from non-offender to offender, so the assertions are account-level and the
# device-level check is only that it stays NonCompliant.
#
$P2 = Wait-ForInventory -DeviceId $DeviceId -Because 'test user promoted'
"  compliance: $($P2.compliance)"
"  interactive admins: $((@($P2.interactiveAdministrators).username) -join ', ')"
$f2 = @($P2.findings | Where-Object { $_.sid -eq $TestUserSid })
$f2InAdmins = @($P2.interactiveAdministrators | Where-Object { $_.sid -eq $TestUserSid }).Count -eq 1

$c2 = $f2.Count -eq 1 `
    -and $f2[0].isAdministrator -eq $true `
    -and $f2[0].countsAgainstCompliance -eq $true `
    -and $f2InAdmins `
    -and $P2.compliance -eq 'NonCompliant'

"  $TestUser -> present=$($f2.Count -eq 1) isAdmin=$(if($f2.Count){$f2[0].isAdministrator}) counts=$(if($f2.Count){$f2[0].countsAgainstCompliance}) inOffenderList=$f2InAdmins"

Set-Criterion 'C2  A promoted account becomes a counted offender and is named' `
    $(if ($c2) { 'PASS' } else { 'FAIL' }) `
    ("inFindings=$($f2.Count -eq 1), isAdmin=$(if($f2.Count){$f2[0].isAdministrator}), " +
     "counts=$(if($f2.Count){$f2[0].countsAgainstCompliance}), inOffenders=$f2InAdmins, " +
     "device=$($P2.compliance)")

# ==========================================================================
Mutating "disabling '$TestUser' (it remains in Administrators)"
Section "8. DISABLE $TestUser"
Disable-LocalUser -Name $TestUser -ErrorAction Stop
"  $TestUser disabled, still a member of $(Get-AdministratorsGroupName)"
Format-Accounts (Get-AccountSnapshot)

Section '9. VERIFY THE DISABLED ADMINISTRATOR IS DISCOUNTED, WITH A REASON'
$P3 = Wait-ForInventory -DeviceId $DeviceId -Because 'test user disabled'
"  compliance: $($P3.compliance)"
$f3 = @($P3.findings | Where-Object { $_.sid -eq $TestUserSid })
$reason = if ($f3.Count) { $f3[0].excludedReason } else { $null }
"  finding for $TestUser (NOT the built-in Administrator):"
"    isAdmin=$(if($f3.Count){$f3[0].isAdministrator}) counts=$(if($f3.Count){$f3[0].countsAgainstCompliance}) reason='$reason'"

# Substring match on 'Disabled': the server's reason contains an em dash, and
# asserting the whole string would make this brittle to encoding rather than to
# behaviour.
#
# As in C1, device-level Compliant is NOT required. Disabling this account
# removes THIS account as an offender; Techsara remains one, so the device stays
# NonCompliant. Requiring Compliant here asserted a device-wide outcome to prove
# an account-level exclusion, which is what made this criterion fail against a
# correct implementation.
#
# The real proof that the exclusion took effect is that the account leaves the
# offender list while REMAINING in findings with its reason -- excluded, not
# hidden.
$f3InAdmins = @($P3.interactiveAdministrators | Where-Object { $_.sid -eq $TestUserSid }).Count

$disabledOk = $f3.Count -eq 1 `
    -and $f3[0].isAdministrator -eq $true `
    -and $f3[0].countsAgainstCompliance -eq $false `
    -and $f3InAdmins -eq 0 `
    -and $reason -match 'Disabled' `
    -and $P3.compliance -eq $BaselineCompliance

Set-Criterion 'C4  A disabled administrator is discounted with a reason, still reported' `
    $(if ($disabledOk) { 'PASS' } else { 'FAIL' }) `
    ("$TestUser isAdmin=$(if($f3.Count){$f3[0].isAdministrator}), " +
     "counts=$(if($f3.Count){$f3[0].countsAgainstCompliance}), " +
     "inOffenders=$($f3InAdmins -ne 0), reason='$reason', " +
     "device $BaselineCompliance -> $($P3.compliance)")

# ==========================================================================
Mutating "deleting '$TestUser'"
Section "10. DELETE $TestUser"
Remove-LocalUser -Name $TestUser -ErrorAction Stop
"  $TestUser removed"

Section '11. FINAL INVENTORY ROUND-TRIP'
$P4 = Wait-ForInventory -DeviceId $DeviceId -Because 'test user deleted'
"  compliance: $($P4.compliance)"

$stillThere = @($P4.findings | Where-Object { $_.sid -eq $TestUserSid }).Count
Set-Criterion 'C6  The temporary account is fully removed and no longer reported' `
    $(if ($stillThere -eq 0) { 'PASS' } else { 'FAIL' }) "server still reports it: $($stillThere -ne 0)"

Section '12. IDEMPOTENCE - a second report with no change'
$P5 = Wait-ForInventory -DeviceId $DeviceId -Because 'repeat report, nothing changed'
Set-Criterion 'C10 Repeated reporting does not change the verdict' `
    $(if ($P5.compliance -eq $P4.compliance) { 'PASS' } else { 'FAIL' }) `
    "$($P4.compliance) then $($P5.compliance)"

# ==========================================================================
Section '13. FINAL ACCOUNT STATE (read-only)'
$FinalAccounts = Get-AccountSnapshot
Format-Accounts $FinalAccounts

Section '14. COMPARISON AGAINST THE BASELINE'

$drift = @()
foreach ($b in $BaselineAccounts) {
    $a = $FinalAccounts | Where-Object { $_.Sid -eq $b.Sid }
    if (-not $a)                        { $drift += "MISSING: $($b.Name) ($($b.Sid))" ; continue }
    if ($a.Name    -ne $b.Name)         { $drift += "RENAMED: $($b.Name) -> $($a.Name)" }
    if ($a.Enabled -ne $b.Enabled)      { $drift += "ENABLED CHANGED: $($b.Name) $($b.Enabled) -> $($a.Enabled)" }
    if ($a.IsAdmin -ne $b.IsAdmin)      { $drift += "ADMIN CHANGED: $($b.Name) $($b.IsAdmin) -> $($a.IsAdmin)" }
}
$unexpected = @($FinalAccounts | Where-Object { $BaselineAccounts.Sid -notcontains $_.Sid })

if ($drift.Count) { '  DRIFT DETECTED:'; $drift | ForEach-Object { "    $_" } } else { '  No pre-existing account changed.' }
if ($unexpected.Count) { '  UNEXPECTED ACCOUNTS:'; $unexpected | ForEach-Object { "    $($_.Name) ($($_.Sid))" } } else { '  No unexpected account exists.' }

Set-Criterion 'C7  Every pre-existing account is unchanged (name, enabled, admin)' `
    $(if ($drift.Count -eq 0) { 'PASS' } else { 'FAIL' }) ($drift -join '; ')
Set-Criterion 'C8  No unexpected account remains' `
    $(if ($unexpected.Count -eq 0) { 'PASS' } else { 'FAIL' }) (($unexpected.Name) -join ', ')

$builtInFinal = $FinalAccounts | Where-Object { $_.Sid -like '*-500' }
$builtInBase  = $BaselineAccounts | Where-Object { $_.Sid -like '*-500' }
Set-Criterion 'C11 The built-in Administrator was not modified' `
    $(if ($builtInFinal.Name -eq $builtInBase.Name -and $builtInFinal.Enabled -eq $builtInBase.Enabled `
          -and $builtInFinal.IsAdmin -eq $builtInBase.IsAdmin) { 'PASS' } else { 'FAIL' }) `
    "$($builtInBase.Name)/$($builtInBase.Enabled) -> $($builtInFinal.Name)/$($builtInFinal.Enabled)"

$FinalTasks = @((Api '/admin/v1/tasks?pageSize=200').items | Where-Object { $_.deviceId -eq $DeviceId })
$newTasks = @($FinalTasks | Where-Object { $BaselineTasks.id -notcontains $_.id })
if ($newTasks.Count) { '  NEW TASKS:'; $newTasks | ForEach-Object { "    $($_.type) $($_.status) $($_.createdAt)" } }
Set-Criterion 'C12 No task was created (11b observes; it does not remediate)' `
    $(if ($newTasks.Count -eq 0) { 'PASS' } else { 'FAIL' }) "$($newTasks.Count) new task(s): $((($newTasks).type) -join ', ')"

$FinalUsb = @(Api "/admin/v1/devices/$DeviceId/usb-devices")
$FinalUsbKey = Get-UsbStateKey -Devices $FinalUsb

# Report WHAT differs, not merely that something did. A bare count told us
# nothing last time and actively misled: it read "1 -> 1" while twelve devices
# were being compared one row at a time.
$usbBaseLines  = @($BaselineUsbKey -split "`n" | Where-Object { $_ })
$usbFinalLines = @($FinalUsbKey    -split "`n" | Where-Object { $_ })
$usbAdded   = @($usbFinalLines | Where-Object { $usbBaseLines -notcontains $_ })
$usbRemoved = @($usbBaseLines  | Where-Object { $usbFinalLines -notcontains $_ })

"  USB devices: baseline $($BaselineUsb.Count), final $($FinalUsb.Count)"
if ($usbAdded.Count -or $usbRemoved.Count) {
    '  DIFFERENCES (instanceId|policy|enforcementState|isConnected):'
    $usbRemoved | ForEach-Object { "    - $_" }
    $usbAdded   | ForEach-Object { "    + $_" }
} else {
    '  Every USB record is identical in instance, policy, enforcement and connection state.'
}

Set-Criterion 'C13 USB state is unchanged' `
    $(if ($FinalUsbKey -eq $BaselineUsbKey) { 'PASS' } else { 'FAIL' }) `
    ("$($BaselineUsb.Count) -> $($FinalUsb.Count) device(s); " +
     "$($usbRemoved.Count) changed/removed, $($usbAdded.Count) changed/added" +
     $(if ($usbRemoved.Count -or $usbAdded.Count) { "; first diff: $(@($usbRemoved + $usbAdded)[0])" } else { '' }))

$finalPage   = Api '/admin/v1/devices?pageSize=200'
$finalDetail = Api "/admin/v1/devices/$DeviceId"
$finalItem   = @($finalPage.items | Where-Object { $_.id -eq $DeviceId })[0]
Set-Criterion 'C14 Device identity and agent state are unchanged' `
    $(if ($finalDetail.machineIdentifier -eq $BaselineMachineId -and $finalItem.agentVersion -eq $BaselineAgent `
          -and $finalPage.totalCount -eq $BaselineDeviceCount) { 'PASS' } else { 'FAIL' }) `
    "mid ok=$($finalDetail.machineIdentifier -eq $BaselineMachineId), agent $BaselineAgent -> $($finalItem.agentVersion), devices $BaselineDeviceCount -> $($finalPage.totalCount)"

Set-Criterion 'C15 The agent service was never stopped or restarted by this script' `
    $(if ((Get-Service EndpointPlatformAgent).Status -eq 'Running') { 'PASS' } else { 'FAIL' }) `
    "service is $((Get-Service EndpointPlatformAgent).Status)"

# --------------------------------------------------------------------------
# Criteria this script cannot honestly verify. Never reported as PASS.
# --------------------------------------------------------------------------
Set-Criterion 'C5  Unknown posture for a device that has never reported' 'DEFERRED' `
    'Not reproducible here: this endpoint has already reported. Covered by automated tests and verifiable only on a freshly enrolled device.'
Set-Criterion 'A1  localuser.posture.changed fires only on a real transition' 'DEFERRED' `
    'No audit read API exists (the dashboard /audit route is a placeholder). Must be confirmed server-side.'
Set-Criterion 'A2  Repeated reporting creates no duplicate posture-change events' 'DEFERRED' `
    'Same reason as A1.'
Set-Criterion 'A3  The first observation is not treated as a transition' 'DEFERRED' `
    'Same reason as A1.'


Show-Result
"Finished $(Get-Date -Format 'u')"
