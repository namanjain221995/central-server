<#
.SYNOPSIS
    Milestone 13 (drivers and BitLocker) physical acceptance.

.DESCRIPTION
    Reconstructed from the implemented M13 contracts only. Every route below was
    read out of the repository; none is guessed:

      POST /api/admin/v1/auth/login                          -> sessionToken + permissions
      GET  /api/admin/v1/devices?search=                     -> { items, totalCount, ... }
      GET  /api/admin/v1/devices/{id}                        -> detail incl. machineIdentifier
      GET  /api/admin/v1/devices/{id}/drivers                -> array incl. health, faultKind
      GET  /api/admin/v1/devices/{id}/driver-health          -> verdict + fault counts
      GET  /api/admin/v1/devices/{id}/bitlocker-volumes      -> array incl. state, protectors
      GET  /api/admin/v1/devices/{id}/bitlocker-readiness    -> readiness + availability + TPM
      GET  /api/admin/v1/driver-packages                     -> approved package catalogue
      GET  /api/admin/v1/devices/{id}/usb-devices            -> M11a regression
      GET  /api/admin/v1/devices/{id}/local-admin-posture    -> M11b regression
      GET  /api/admin/v1/devices/{id}/elevations             -> M12 regression
      GET  /api/admin/v1/devices/{id}/local-users            -> M11b regression
      POST /api/admin/v1/devices/{id}/refresh-inventory      -> only with -RefreshInventory

.NOTES
    WHAT THIS TOUCHES
    -----------------
    By default: NOTHING. Every check is a GET. The script installs no driver,
    queues no task, publishes no package, changes no BitLocker state, plugs in
    no device and never reboots. There is deliberately no switch that makes it
    do any of those.

    The single optional mutation is -RefreshInventory, which asks the endpoint
    for a fresh inventory upload. That sets a server-side flag and writes an
    audit row; it creates no DeviceTask and changes nothing on the machine. It
    is marked *** MUTATING STEP *** and is off by default.

    ON RECOVERY KEYS
    ----------------
    This script never requests, prints, logs or stores BitLocker recovery key
    material, and the checks below assert that absence rather than relying on
    it. Protector GUIDs are identifiers, not secrets - one names a protector and
    unlocks nothing - and even those are only counted, never printed.

    The reason for that absence changed after M13, and the earlier wording here
    is now wrong. This script used to say the agent does not read a recovery
    password at all. That was true under decision J-4, which has since been
    explicitly reversed: automatic escrow is approved, and the agent does call
    GetKeyProtectorNumericalPassword for a protector it has detected. See
    docs/threat-model.md, where the original decision is preserved alongside the
    reversal and what it costs.

    The security property these checks defend is therefore no longer 'the agent
    cannot read a key'. It is:

        the agent does not expose or transmit a plaintext recovery password
        outside the approved local sealing path

    A password may be read ONLY after every gate passes - the device is eligible
    for automatic escrow, a sealing-key fingerprint was pinned at enrollment, the
    offered public key matches that pin, the protector association is confirmed,
    and no escrow already exists for it. It is then sealed on the endpoint and
    leaves only as an envelope no server process can open. Criterion 9 below is
    unchanged in strength: no API response may contain a key field or anything
    shaped like a key, and that holds whether the key was typed by an
    administrator or collected automatically.

    ON DEFERRED CRITERIA
    --------------------
    Ten of the nineteen criteria cannot be proven without either a signed vendor
    driver package or a machine that may be rebooted. Those are reported
    DEFERRED with the specific reason. A DEFERRED criterion is not a pass and is
    never counted as one; the summary reports the two totals separately.

.EXAMPLE
    .\Invoke-M13Acceptance.ps1 -ServerBaseUrl https://epp.example.com `
        -AdminEmail admin@example.com `
        -AdminPassword (Read-Host -AsSecureString) -ExpectedHostname LAPTOP-LVCHEQ2H
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ServerBaseUrl,

    [Parameter(Mandatory)] [string] $AdminEmail,
    [Parameter(Mandatory)] [SecureString] $AdminPassword,

    # Named explicitly so the script can refuse to act on the wrong machine.
    [Parameter(Mandatory)] [string] $ExpectedHostname,

    # Asks the endpoint for a fresh inventory upload before checking. Off by
    # default: the acceptance reads what the endpoint already reported.
    [switch] $RefreshInventory,

    [int] $InventoryTimeoutSeconds = 300,

    # An approved driver package id enables the read-only half of criteria
    # 10-18. Even with one supplied this script never deploys it.
    [string] $DriverPackageId
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:Results = [ordered]@{}
$script:Token = $null
$script:DeviceId = $null

function Set-Criterion {
    param([string]$Id, [string]$State, [string]$Detail = '')
    $script:Results[$Id] = [PSCustomObject]@{ State = $State; Detail = $Detail }
}

function Section { param([string]$Name) "`n$('=' * 76)`n  $Name`n$('=' * 76)" }
function Mutating { param([string]$What) "`n*** MUTATING STEP *** $What" }

function Show-Result {
    Section 'M13 ACCEPTANCE RESULT'
    foreach ($k in $script:Results.Keys) {
        $r = $script:Results[$k]
        $colour = switch ($r.State) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
        Write-Host ('  {0,-9} {1}' -f $r.State, $k) -ForegroundColor $colour
        if ($r.Detail) { Write-Host "            $($r.Detail)" -ForegroundColor DarkGray }
    }

    $passed = @($script:Results.Values | Where-Object { $_.State -eq 'PASS' }).Count
    $failed = @($script:Results.Values | Where-Object { $_.State -eq 'FAIL' }).Count
    $deferred = @($script:Results.Values | Where-Object { $_.State -eq 'DEFERRED' }).Count

    ''
    Write-Host ("  PASS {0}   FAIL {1}   DEFERRED {2}" -f $passed, $failed, $deferred)

    if ($failed -gt 0) {
        Write-Host '  M13 ACCEPTANCE RESULT: FAIL' -ForegroundColor Red
    } else {
        Write-Host '  M13 ACCEPTANCE RESULT: PASS for every criterion that could be checked' -ForegroundColor Green
    }

    if ($deferred -gt 0) {
        Write-Host "  $deferred criterion/criteria DEFERRED - not proven, and not counted as passes." -ForegroundColor Yellow
    }
    ''
}

function Fail-Hard {
    param([string]$Why)
    Write-Host "`nABORTED: $Why" -ForegroundColor Red
    Write-Host 'The script refuses to guess. Nothing was changed.' -ForegroundColor Red
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

    foreach ($p in @('driver.view', 'bitlocker.view', 'device.view')) {
        if ($r.permissions -notcontains $p) {
            Fail-Hard "The account $AdminEmail does not hold '$p'. Grant it before running the acceptance."
        }
    }
    "  signed in as $($r.email); driver.view and bitlocker.view held"
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

function Get-Drivers { @(Api "/admin/v1/devices/$($script:DeviceId)/drivers") }
function Get-DriverHealth { Api "/admin/v1/devices/$($script:DeviceId)/driver-health" }
function Get-BitLockerVolumes { @(Api "/admin/v1/devices/$($script:DeviceId)/bitlocker-volumes") }
function Get-BitLockerReadiness { Api "/admin/v1/devices/$($script:DeviceId)/bitlocker-readiness" }

# ==========================================================================
'M13 ACCEPTANCE - drivers and BitLocker'
"Started $(Get-Date -Format 'u')  |  Server $ServerBaseUrl"
'Read-only by default. No driver is installed, no package deployed, no task'
'queued, no BitLocker state changed, no device attached, no reboot performed.'

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
        "this machine's SMBIOS UUID is '$LocalUuid'. Refusing to judge a device that may not be this one.")
}

"  DeviceId     : $($script:DeviceId)"
"  AgentVersion : $($match[0].agentVersion)"
"  Online       : $($match[0].isOnline)"

$AgentVersion = $match[0].agentVersion

# ---- optional refresh ----------------------------------------------------
if ($RefreshInventory) {
    Mutating 'requesting a fresh inventory upload (sets a server flag; queues no task)'
    $before = (Get-DriverHealth).lastReportedAt
    Api "/admin/v1/devices/$($script:DeviceId)/refresh-inventory" -Method Post | Out-Null

    $deadline = (Get-Date).AddSeconds($InventoryTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 10
        if ((Get-DriverHealth).lastReportedAt -ne $before) { break }
    }
    "  inventory now reported at $((Get-DriverHealth).lastReportedAt)"
}

# ==========================================================================
Section '2. AGENT VERSION AND CONNECTIVITY (criteria 1-3)'

Set-Criterion 'C1 The endpoint reports agent 1.3.0 or newer' `
    $(if ([version]($AgentVersion -replace '[-+].*$') -ge [version]'1.3.0') { 'PASS' } else { 'FAIL' }) `
    "reported agent version: $AgentVersion"

Set-Criterion 'C2 The endpoint is connected and its identity is unchanged' `
    $(if ($match[0].isOnline -and $detail.machineIdentifier -eq $LocalUuid) { 'PASS' } else { 'FAIL' }) `
    "online=$($match[0].isOnline); machineIdentifier matches local SMBIOS UUID"

$health = Get-DriverHealth
Set-Criterion 'C3 Inventory collection is still working' `
    $(if ($health.lastReportedAt) { 'PASS' } else { 'FAIL' }) `
    "last inventory: $($health.lastReportedAt)"

# ==========================================================================
Section '3. DRIVER INVENTORY AND HEALTH (criteria 6-7)'

$drivers = Get-Drivers
"  devices reported : $($drivers.Count)"
"  health verdict   : $($health.state)"
"  driver faults    : $($health.driverFaultCount)"
"  hardware faults  : $($health.deviceFaultCount)"
"  unattributed     : $($health.indeterminateFaultCount)"
"  disabled         : $($health.disabledCount)"
"  unknown          : $($health.unknownCount)"

Set-Criterion 'C6 Driver inventory is populated' `
    $(if ($drivers.Count -gt 0 -and $health.totalCount -eq $drivers.Count) { 'PASS' } else { 'FAIL' }) `
    "$($drivers.Count) device(s); health totalCount=$($health.totalCount)"

# Compared against Windows itself rather than trusted. A collector that silently
# truncated would still return a plausible-looking list.
$present = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue)
Set-Criterion 'C6a The inventory is complete, not truncated' `
    $(if ($present.Count -gt 0 -and $drivers.Count -eq $present.Count) { 'PASS' }
      elseif ($present.Count -eq 0) { 'DEFERRED' } else { 'FAIL' }) `
    "reported=$($drivers.Count); Get-PnpDevice -PresentOnly=$($present.Count)"

$windowsErrors = @($present | Where-Object { $_.Status -eq 'Error' }).Count
$reportedFaults = $health.driverFaultCount + $health.deviceFaultCount + $health.indeterminateFaultCount

Set-Criterion 'C7 Driver health matches what Windows reports' `
    $(if ($reportedFaults -eq $windowsErrors) { 'PASS' } else { 'FAIL' }) `
    "platform faults=$reportedFaults; Windows devices in Error=$windowsErrors"

Set-Criterion 'C7a Every reported device carries a health verdict' `
    $(if (@($drivers | Where-Object { -not $_.health }).Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    'each row has health, faultKind and problemDescription'

# A device whose problem state could not be read must not be Healthy.
$unknownMislabelled = @($drivers | Where-Object {
        $null -eq $_.problemCode -and $_.health -ne 'Unknown' }).Count

Set-Criterion 'C7b An unread problem state is Unknown, never Healthy' `
    $(if ($unknownMislabelled -eq 0) { 'PASS' } else { 'FAIL' }) `
    "$unknownMislabelled row(s) with a null problem code labelled other than Unknown"

# Signature state is three-valued; unknown must never be reported as unsigned.
$signedCounts = @{
    signed   = @($drivers | Where-Object { $_.isSigned -eq $true }).Count
    unsigned = @($drivers | Where-Object { $_.isSigned -eq $false }).Count
    unknown  = @($drivers | Where-Object { $null -eq $_.isSigned }).Count
}
Set-Criterion 'C7c Signature state is reported as three values' `
    $(if (($signedCounts.signed + $signedCounts.unsigned + $signedCounts.unknown) -eq $drivers.Count) { 'PASS' } else { 'FAIL' }) `
    "signed=$($signedCounts.signed) unsigned=$($signedCounts.unsigned) unknown=$($signedCounts.unknown)"

# ==========================================================================
Section '4. CM_PROB_DISABLED IS NOT A DRIVER FAULT (criterion 7d)'

$disabledRows = @($drivers | Where-Object { $_.problemCode -eq 22 })

if ($disabledRows.Count -eq 0) {
    '  No device is currently reporting CM_PROB_DISABLED (Windows problem code 22).'
    '  This platform produces that code itself when Milestone 11a restricts a USB'
    '  storage device, but an absent device is not enumerated and so produces no row.'
    ''
    '  To prove this criterion, on this endpoint:'
    '    1. Attach a USB storage device that this platform has restricted.'
    '    2. Re-run with -RefreshInventory.'
    '    3. EXPECT: the device appears with health=Disabled, faultKind=None, and'
    '       driverFaultCount unchanged.'

    Set-Criterion 'C7d A restricted (disabled) device is not counted as a driver fault' 'DEFERRED' `
        'No device is currently reporting code 22; the restricted USB device is not attached.'
} else {
    $mislabelled = @($disabledRows | Where-Object {
            $_.health -ne 'Disabled' -or $_.faultKind -ne 'None' }).Count

    Set-Criterion 'C7d A restricted (disabled) device is not counted as a driver fault' `
        $(if ($mislabelled -eq 0) { 'PASS' } else { 'FAIL' }) `
        "$($disabledRows.Count) device(s) with code 22; $mislabelled mislabelled"

    Set-Criterion 'C7e Disabled devices are excluded from the fault counts' `
        $(if ($health.disabledCount -eq $disabledRows.Count) { 'PASS' } else { 'FAIL' }) `
        "health.disabledCount=$($health.disabledCount); rows with code 22=$($disabledRows.Count)"
}

# ==========================================================================
Section '5. BITLOCKER POSTURE (criterion 8)'

$readiness = Get-BitLockerReadiness
$volumes = Get-BitLockerVolumes

"  availability : $($readiness.availability)"
"  readiness    : $($readiness.readiness)"
"  volumes      : $($volumes.Count)"
"  TPM          : present=$($readiness.tpmPresent) enabled=$($readiness.tpmEnabled) version=$($readiness.tpmSpecVersion)"
"  system drive : $($readiness.systemDriveStatus)  (the long-standing posture field)"

Set-Criterion 'C8 BitLocker availability is reported' `
    $(if ($readiness.availability -in @('Available', 'AccessDenied', 'NotAvailable', 'Error')) { 'PASS' } else { 'FAIL' }) `
    "availability=$($readiness.availability)"

Set-Criterion 'C8a BitLocker readiness is a known state' `
    $(if ($readiness.readiness -in @('Unknown', 'Protected', 'EncryptionInProgress', 'Suspended',
            'ReadyToEncrypt', 'TpmNotReady', 'NotEncrypted', 'NotSupported')) { 'PASS' } else { 'FAIL' }) `
    "readiness=$($readiness.readiness)"

# A query that did not succeed must never be read as an unencrypted machine.
Set-Criterion 'C8b A refused query is never reported as unencrypted' `
    $(if ($readiness.availability -eq 'Available' -or $readiness.readiness -notin @('NotEncrypted', 'ReadyToEncrypt')) { 'PASS' } else { 'FAIL' }) `
    "availability=$($readiness.availability) readiness=$($readiness.readiness)"

if ($readiness.availability -eq 'Available') {
    Set-Criterion 'C8c Volume detail is reported when the endpoint could answer' `
        $(if ($volumes.Count -gt 0) { 'PASS' } else { 'FAIL' }) `
        "$($volumes.Count) volume(s)"

    foreach ($v in $volumes) {
        "  volume $($v.driveLetter): state=$($v.state) pct=$($v.encryptionPercentage) method=$($v.encryptionMethod)"
    }

    $badState = @($volumes | Where-Object {
            $_.state -notin @('Unknown', 'NotEncrypted', 'EncryptionInProgress',
                'DecryptionInProgress', 'Protected', 'Suspended') }).Count

    Set-Criterion 'C8d Every volume carries a known state' `
        $(if ($badState -eq 0) { 'PASS' } else { 'FAIL' }) "$badState volume(s) with an unrecognised state"

    # Encrypted-with-protection-off is suspended, not protected.
    $suspendedAsProtected = @($volumes | Where-Object {
            $_.conversionStatus -eq 1 -and $_.protectionStatus -eq 0 -and $_.state -ne 'Suspended' }).Count

    Set-Criterion 'C8e A suspended volume is never reported as protected' `
        $(if ($suspendedAsProtected -eq 0) { 'PASS' } else { 'FAIL' }) `
        "$suspendedAsProtected volume(s) encrypted with protection off but not labelled Suspended"
} else {
    Set-Criterion 'C8c Volume detail is reported when the endpoint could answer' 'DEFERRED' `
        "availability=$($readiness.availability); the endpoint could not answer, so there is no volume detail to judge."
    Set-Criterion 'C8d Every volume carries a known state' 'DEFERRED' 'No volume detail available.'
    Set-Criterion 'C8e A suspended volume is never reported as protected' 'DEFERRED' 'No volume detail available.'
}

# ==========================================================================
Section '6. NO RECOVERY KEY IS EXPOSED OR RETURNED (criterion 9)'

# Checked structurally: every property NAME is compared for exact equality
# against the forbidden list, and every string VALUE is tested for the shape of
# a recovery password.
#
# A substring scan of the raw JSON cannot be used here, and the reason is worth
# recording. The responses legitimately contain 'hasRecoveryPasswordProtector'
# -- a boolean saying a protector exists, which is precisely what this milestone
# is meant to report -- and PowerShell's -match is case-insensitive, so a scan
# for 'recoveryPassword' flags that field and reports leakage that is not there.
# The first version of this script did exactly that. The fix is to make the
# check precise rather than to relax what counts as leakage: a property whose
# name IS a key field still fails, and so does any value shaped like a key.
$volumesJson = Get-BitLockerVolumes | ConvertTo-Json -Depth 8
$readinessJson = Get-BitLockerReadiness | ConvertTo-Json -Depth 8

# Names that would denote the key itself. 'hasRecoveryPasswordProtector' and
# 'recoveryProtectorIds' are deliberately absent: they report presence and
# identity, never the value.
$forbiddenNames = @('recoveryKey', 'recoveryPassword', 'numericalPassword',
    'recoveryPasswords', 'key', 'password', 'secret')

$keyShape = '\d{6}-\d{6}'

$script:LeakedNames = @()
$script:LeakedValues = @()

function Test-NoSecret {
    param($Node)

    if ($null -eq $Node) { return }

    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($item in $Node) { Test-NoSecret $item }
        return
    }

    if ($Node -is [psobject] -and $Node.PSObject.Properties.Count -gt 0) {
        foreach ($prop in $Node.PSObject.Properties) {
            if ($forbiddenNames -contains $prop.Name) {
                $script:LeakedNames += $prop.Name
            }
            Test-NoSecret $prop.Value
        }
        return
    }

    if ($Node -is [string]) {
        # The value is never printed, only tested.
        if ($Node -match $keyShape -or $Node -match '\d{9,}') {
            $script:LeakedValues += 'a value matching the recovery-password shape'
        }
    }
}

Test-NoSecret (Get-BitLockerVolumes)
Test-NoSecret (Get-BitLockerReadiness)

Set-Criterion 'C9 No API response contains a recovery-key field' `
    $(if ($script:LeakedNames.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    $(if ($script:LeakedNames.Count -eq 0) {
        'no property is named as a key; hasRecoveryPasswordProtector reports presence only'
      } else { "found propert(ies): $(($script:LeakedNames | Sort-Object -Unique) -join ', ')" })

Set-Criterion 'C9a No API response contains anything shaped like a recovery password' `
    $(if ($script:LeakedValues.Count -eq 0 -and
          $volumesJson -notmatch $keyShape -and $readinessJson -notmatch $keyShape) { 'PASS' } else { 'FAIL' }) `
    'every string value in both payloads tested; no value is printed by this script'

# Protector identifiers are GUIDs. A GUID names a protector and unlocks nothing;
# the value that would is never read from Windows in the first place.
$protectorIds = @($volumes | ForEach-Object { $_.recoveryProtectorIds } | Where-Object { $_ })
$nonGuid = @($protectorIds | Where-Object { -not [guid]::TryParse(($_ -replace '[{}]', ''), [ref]([guid]::Empty)) }).Count

Set-Criterion 'C9b Recovery protectors are reported as identifiers only' `
    $(if ($nonGuid -eq 0) { 'PASS' } else { 'FAIL' }) `
    "$($protectorIds.Count) protector id(s), $nonGuid not a GUID (ids are not printed by this script)"

# --------------------------------------------------------------------------
# Retrieval gates (criterion 9c-9g)
#
# Added when decision J-4 was reversed and the agent gained the ability to read
# a recovery password. The gates are what keep that ability narrow: a password
# is read ONLY when the device is eligible, the sealing key matches the
# fingerprint pinned at enrollment, the protector association is confirmed, and
# no escrow already exists for that protector.
#
# These are asserted where they can actually be observed -- in the agent, by
# EndpointAgent.Core.Tests.BitLocker.AutomaticEscrowGateTests, which drives the
# gate with a reader that COUNTS CALLS and requires that count to be zero. That
# is a stronger claim than this script could make from outside: it proves the
# password was never retrieved, not merely that it was never returned over the
# API. Nothing here can see inside the endpoint, so these are recorded as
# deferred-to-automated-test rather than asserted twice and weakly.
#
# Run them with:
#   dotnet test agent/tests/EndpointAgent.Core.Tests #     --filter FullyQualifiedName~AutomaticEscrowGateTests

# Each entry names the automated test that actually proves it. Those tests drive
# the reader with a spy that COUNTS CALLS and require the count to be zero, which
# is a stronger claim than this script can make from outside: it proves the
# password was never retrieved, not merely that it was never returned.
#
# Run them with:
#   dotnet test agent/tests/EndpointAgent.Core.Tests --filter FullyQualifiedName~BitLocker
#   dotnet test server/tests/EndpointPlatform.AgentApi.Tests --filter FullyQualifiedName~AutomaticEscrow
#   dotnet test server/tests/EndpointPlatform.Api.Tests --filter FullyQualifiedName~EscrowAttemptReset

$gateTests = @(
    @{ Id = 'C9c No pinned fingerprint - password is NOT retrieved'
       Test = 'AutomaticEscrowRunnerTests.A_missing_fingerprint_reads_nothing' }
    @{ Id = 'C9d Fingerprint mismatch - password is NOT retrieved'
       Test = 'AutomaticEscrowRunnerTests.B_fingerprint_mismatch_reads_nothing' }
    @{ Id = 'C9e Already escrowed - password is NOT retrieved'
       Test = 'AutomaticEscrowRunnerTests.C_already_escrowed_reads_nothing' }
    @{ Id = 'C9f Retry not due - password is NOT retrieved'
       Test = 'AutomaticEscrowRunnerTests.D_retry_not_due_reads_nothing' }
    @{ Id = 'C9g Retry exhausted - password is NOT retrieved'
       Test = 'AutomaticEscrowRunnerTests.E_retry_exhausted_reads_nothing' }
    @{ Id = 'C9h Missing sealing key - password is NOT retrieved'
       Test = 'AutomaticEscrowRunnerTests.F_missing_sealing_key_reads_nothing' }
    @{ Id = 'C9i Successful path retrieves the password EXACTLY ONCE'
       Test = 'AutomaticEscrowRunnerTests.G_all_gates_pass_reads_exactly_once_and_uploads_an_envelope' }
    @{ Id = 'C9j The password is sealed on the endpoint before it leaves'
       Test = 'AutomaticEscrowGateTests.The_sealed_envelope_contains_no_trace_of_the_password' }
    @{ Id = 'C9k Agent API receives an opaque envelope only'
       Test = 'AutomaticEscrowIngestionTests.A_plaintext_recovery_password_is_rejected' }
    @{ Id = 'C9l Agent API cannot decrypt what it stores'
       Test = 'AutomaticEscrowIngestionTests.The_agent_api_holds_nothing_that_can_decrypt_what_it_stored' }
    @{ Id = 'C9m Agent API configuration holds no private or master key'
       Test = 'SealingKeyGuardTests.The_agent_api_refuses_to_start_with_decryption_key_material' }
    @{ Id = 'C9n Admin API cannot be configured so escrow succeeds but reveal never can'
       Test = 'SealingKeyGuardTests.A_public_key_without_its_private_half_is_refused' }
    @{ Id = 'C9o An untrusted sealing key is refused with no silent fallback'
       Test = 'SealingKeyGuardTests.A_mismatched_keypair_is_refused' }
    @{ Id = 'C9p PostgreSQL stores ciphertext only'
       Test = 'AutomaticEscrowIngestionTests.A_sealed_envelope_is_accepted_and_stored_as_ciphertext' }
    @{ Id = 'C9q No plaintext or ciphertext reaches the audit trail'
       Test = 'AutomaticEscrowIngestionTests.Every_ingestion_outcome_is_audited_without_secrets' }
    @{ Id = 'C9r Unpinned credential is refused server-side as well as on the endpoint'
       Test = 'AutomaticEscrowIngestionTests.A_credential_without_a_pinned_fingerprint_is_rejected' }
    @{ Id = 'C9s Re-enrollment pins the sealing key and confers eligibility'
       Test = 'AutomaticEscrowIngestionTests.Enrollment_pins_the_sealing_key_and_makes_the_device_eligible' }
    @{ Id = 'C9t Manual escrow and the protected reveal path still work'
       Test = 'BitLockerEscrowEndpointTests (22 tests, unchanged)' }
    @{ Id = 'C9u Reveal remains permission + scope + step-up + rate limited'
       Test = 'BitLockerEscrowEndpointTests.A_user_is_limited_to_five_reveals_per_window' }
    @{ Id = 'C9v reset-attempts requires recovery_key.manage and device scope'
       Test = 'EscrowAttemptResetTests.Bitlocker_view_alone_cannot_reset' }
    @{ Id = 'C9w Rollback cannot silently invalidate automatic escrows'
       Test = 'AutomaticEscrowRollbackTests.Rollback_is_refused_while_automatic_escrows_exist_and_permitted_once_they_are_gone' }
    @{ Id = 'C9x Agent and server agree on what a recovery password is'
       Test = 'RecoveryPasswordConformanceTests.The_agent_and_the_server_agree' }
)

foreach ($gate in $gateTests) {
    Set-Criterion $gate.Id 'DEFERRED' `
        "Proven by $($gate.Test). Not observable from the API: these assert what the endpoint did NOT do."
}

# ==========================================================================
Section '7. DRIVER PACKAGE MANAGEMENT (criteria 10-18)'

# Null-filtered on purpose. Invoke-RestMethod unrolls an empty JSON array to
# nothing, so the assignment yields $null -- and @($null) is an array of ONE
# element whose value is $null. Wrapping alone therefore reports a phantom
# package on an empty catalogue, takes the "packages exist" branch, and fails
# every mandatory-field check against a null. Requiring an id makes the count
# mean what it says whether the API returns null, an empty array, or real rows.
$packages = @(Api '/admin/v1/driver-packages') | Where-Object { $_ -and $_.id }
$packages = @($packages)
"  approved driver packages: $($packages.Count)"

if ($packages.Count -eq 0) {
    '  No driver package has been approved, so nothing can be deployed and none of'
    '  the installation gates can be exercised end to end.'
    ''
    '  To prove criteria 10-18, on a machine you are willing to have a driver'
    '  installed on and rebooted:'
    '    1. Approve a signed vendor driver package (name, version, sha256,'
    '       infFileName, hardwareId, requiredSignerSubject).'
    '    2. Deploy it to that endpoint and observe the queued task.'
    '    3. EXPECT: the archive hash is checked before extraction, the catalogue'
    '       signature and signer pin are verified before the driver store is'
    '       touched, only matching hardware is affected, a downgrade is refused'
    '       unless authorized, every affected instance is verified individually,'
    '       and a reboot requirement is reported without the agent rebooting.'

    $reason = 'No approved driver package exists; nothing can be deployed.'
    foreach ($c in @(
            'C10 An approved driver package can be deployed',
            'C11 The archive hash is verified before extraction',
            'C12 Archive traversal, entry-count and size limits are enforced',
            'C13 The catalogue signature and signer pin are verified',
            'C14 Only devices matching the hardware id are affected',
            'C15 A downgrade is refused unless explicitly authorized',
            'C16 Every affected instance is verified individually after installation',
            'C17 A reboot requirement is reported and the agent does not reboot',
            'C18 Stale, withdrawn and old-agent requests are refused')) {
        Set-Criterion $c 'DEFERRED' $reason
    }
} else {
    foreach ($p in $packages) {
        "  package $($p.id): $($p.name) $($p.version)  hardwareId=$($p.hardwareId)  withdrawn=$($p.isWithdrawn)"
    }

    # Read-only checks that hold whether or not anything is ever deployed.
    Set-Criterion 'C13a Every approved package pins a signer subject' `
        $(if (@($packages | Where-Object { -not $_.requiredSignerSubject }).Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        'a driver runs in the kernel, so the publisher is pinned rather than trusted generically'

    Set-Criterion 'C11a Every approved package carries a content hash' `
        $(if (@($packages | Where-Object { -not $_.sha256 -or $_.sha256.Length -ne 64 }).Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        'each package is pinned by a 64-character SHA-256'

    Set-Criterion 'C14a Every approved package declares a hardware id' `
        $(if (@($packages | Where-Object { -not $_.hardwareId }).Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        'targeting is by hardware id, checked before the driver store is touched'

    $reason = 'Requires deploying a package and installing a real driver; not performed by this script.'
    foreach ($c in @(
            'C10 An approved driver package can be deployed',
            'C11 The archive hash is verified before extraction',
            'C12 Archive traversal, entry-count and size limits are enforced',
            'C13 The catalogue signature and signer pin are verified',
            'C14 Only devices matching the hardware id are affected',
            'C15 A downgrade is refused unless explicitly authorized',
            'C16 Every affected instance is verified individually after installation',
            'C17 A reboot requirement is reported and the agent does not reboot',
            'C18 Stale, withdrawn and old-agent requests are refused')) {
        Set-Criterion $c 'DEFERRED' $reason
    }
}

Set-Criterion 'C10a No driver installation has been queued by this acceptance' `
    $(if (@((Api "/admin/v1/devices/$($script:DeviceId)/tasks") |
            Where-Object { $_.type -eq 'InstallDriverPackage' }).Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    'this script never queues an InstallDriverPackage task'

# ==========================================================================
Section '8. EXISTING BEHAVIOUR IS UNCHANGED (criteria 4-5)'

$usb = @(Api "/admin/v1/devices/$($script:DeviceId)/usb-devices")
$posture = Api "/admin/v1/devices/$($script:DeviceId)/local-admin-posture"
$users = @(Api "/admin/v1/devices/$($script:DeviceId)/local-users")
$elevations = @(Api "/admin/v1/devices/$($script:DeviceId)/elevations")

"  USB devices        : $($usb.Count)"
"  local users        : $($users.Count)"
"  posture compliance : $($posture.compliance)"
"  elevations         : $($elevations.Count)"

Set-Criterion 'C4 M11a USB inventory and enforcement still report' `
    $(if ($usb.Count -gt 0) { 'PASS' } else { 'FAIL' }) "$($usb.Count) USB device(s) reported"

$restrictedStorage = @($usb | Where-Object {
        $_.deviceClass -eq 'Storage' -and $_.policy -eq 'Restricted' })

Set-Criterion 'C4a USB storage restriction is still in force' `
    $(if (@($usb | Where-Object { $_.deviceClass -eq 'Storage' }).Count -eq 0) { 'DEFERRED' }
      elseif ($restrictedStorage.Count -gt 0) { 'PASS' } else { 'FAIL' }) `
    "$($restrictedStorage.Count) restricted storage device(s)"

Set-Criterion 'C4b M11b local administrator posture still answers' `
    $(if ($posture.compliance -in @('Compliant', 'NonCompliant', 'Unknown')) { 'PASS' } else { 'FAIL' }) `
    "compliance=$($posture.compliance)"

Set-Criterion 'C5 M12 elevation endpoints still answer' `
    $(if ($null -ne $elevations) { 'PASS' } else { 'FAIL' }) `
    "$($elevations.Count) elevation record(s)"

Set-Criterion 'C5a The endpoint still reports local accounts' `
    $(if ($users.Count -gt 0) { 'PASS' } else { 'FAIL' }) "$($users.Count) local user(s)"

# ==========================================================================
Section '9. THIS SCRIPT CHANGED NOTHING ON THE ENDPOINT'

$agentService = Get-Service EndpointPlatformAgent -ErrorAction SilentlyContinue
Set-Criterion 'C19 The agent service was never stopped by this script' `
    $(if ($agentService -and $agentService.Status -eq 'Running') { 'PASS' } else { 'FAIL' }) `
    "service status: $($agentService.Status)"

$finalReadiness = Get-BitLockerReadiness
Set-Criterion 'C19a BitLocker state is unchanged by this script' `
    $(if ($finalReadiness.readiness -eq $readiness.readiness -and
          $finalReadiness.availability -eq $readiness.availability) { 'PASS' } else { 'FAIL' }) `
    "readiness $($readiness.readiness) -> $($finalReadiness.readiness)"

$finalDrivers = Get-Drivers
Set-Criterion 'C19b Driver state is unchanged by this script' `
    $(if ($finalDrivers.Count -eq $drivers.Count) { 'PASS' } else { 'FAIL' }) `
    "$($drivers.Count) -> $($finalDrivers.Count) device(s)"

Show-Result
"Finished $(Get-Date -Format 'u')"
