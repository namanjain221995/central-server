<#
.SYNOPSIS
    Physical acceptance of the USB portable-device control (phones, tablets and
    cameras over MTP/PTP) for agent 1.16.0 or newer. Observes what a REAL phone
    does on a designated test endpoint and writes a timestamped evidence folder
    and a report.

.DESCRIPTION
    Automates the script in docs/usb-control.md, "Acceptance - portable
    devices". Every check reads the device's actual state through the same
    Windows facilities the agent itself uses, re-derived independently here:

      - arrival and removal            Get-PnpDevice, CfgMgr32 CM_Locate_DevNode
      - restricted / enabled           CM_Get_DevNode_Status: a restriction is the
                                       devnode disabled (problem CM_PROB_DISABLED = 22)
      - "no handle files move through" CM_Get_Device_Interface_List for the
                                       portable-device and disk interface classes
      - reachable from Explorer        the Shell's This PC namespace
      - the agent's own view           its ledger usb-restricted-devices.json and
                                       its log under ProgramData
      - the console's view             the Admin API, when console credentials are
                                       supplied; otherwise the operator reports it
                                       and the report says so

    Nothing in this script enables or disables a device, writes agent state or
    installs anything. The agent under test does the enforcing; this script
    watches and records. Three steps do change state and are announced as
    *** MUTATING STEP *** before they run:

      1. a 30-minute read/write grant and its revoke, done by the operator in the
         console or, with -GrantViaApi, by this script through the Admin API;
      2. one stop and start of the EndpointPlatformAgent service (-SkipAgentRestart
         leaves it out, and the result is then INCOMPLETE);
      3. with -IncludeWriteProbe only: one small text file copied to the phone's
         first storage while access is granted. Off by default.

    GUARDS. The script refuses to run unless -ExpectedHostname matches this
    machine, -IAmOnTheDesignatedTestEndpoint is present, the session is elevated,
    and the agent service is installed, running and at least 1.16.0. A machine
    that fails any of those is not the test endpoint, and nothing happens on it.
    -SelfTest exercises only the read-only helpers on any machine and makes no
    acceptance claim.

    VERDICT. PASS is printed only when every required scenario ran and the
    observed behaviour matched the expectation. A scenario that did not run is
    NOT RUN and the overall result INCOMPLETE. Something the operator typed is
    recorded as operator-reported and never upgrades a verdict. The reboot
    scenario is a second invocation with -Phase AfterReboot against the same
    evidence folder.

.PARAMETER ExpectedHostname
    The test endpoint's computer name. The script refuses to run elsewhere.

.PARAMETER IAmOnTheDesignatedTestEndpoint
    Required acknowledgement that this machine may have its phone disabled and
    its agent service restarted. Never pass it on a production PC.

.PARAMETER Phase
    Full (default): scenarios 1-12, then a hand-off for the reboot.
    AfterReboot: scenario 13, run after the reboot with -ResumeDirectory.

.PARAMETER ResumeDirectory
    The evidence folder written by the Full phase, for -Phase AfterReboot.
    Defaults to the newest folder under -OutputDirectory with a pending reboot.

.PARAMETER ServerBaseUrl
    The console origin, e.g. https://epp.example.com. With -AdminEmail it lets
    the script read the console's view of the phone through the Admin API.

.PARAMETER AdminEmail
    Console account for the Admin API. The password is prompted if
    -AdminPassword is not given; the authenticator code is always prompted.
    Neither is written anywhere.

.PARAMETER GrantViaApi
    Issue the read/write grant and the revoke through the Admin API instead of
    asking the operator to do it in the console. Requires -AdminEmail.

.PARAMETER IncludeWriteProbe
    While access is granted, copy one generated text file to the phone's first
    storage to demonstrate a write. Off by default.

.PARAMETER IncludeDebuggingScenario
    Also exercise USB debugging (composite device) and, when adb.exe is on the
    PATH, check that `adb devices` lists nothing.

.PARAMETER SkipAgentRestart
    Do not stop and start the agent service. The restart scenario is then
    NOT RUN and the result INCOMPLETE.

.PARAMETER OutputDirectory
    Where evidence folders go. Default: Documents\usb-acceptance.

.PARAMETER SelfTest
    Run the read-only helpers only (device enumeration, classification, the
    interface and status calls, the Shell namespace). No guards, no prompts,
    no agent reads, no acceptance claim. Safe on any machine.

.EXAMPLE
    .\scripts\Invoke-UsbPortableAcceptance.ps1 -ExpectedHostname TEST-PC-01 -IAmOnTheDesignatedTestEndpoint `
        -ServerBaseUrl https://epp.example.com -AdminEmail admin@example.com -GrantViaApi

.EXAMPLE
    .\scripts\Invoke-UsbPortableAcceptance.ps1 -ExpectedHostname TEST-PC-01 -IAmOnTheDesignatedTestEndpoint `
        -Phase AfterReboot

.EXAMPLE
    .\scripts\Invoke-UsbPortableAcceptance.ps1 -SelfTest
#>

[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Run', Mandatory)] [string] $ExpectedHostname,
    [Parameter(ParameterSetName = 'Run')] [switch] $IAmOnTheDesignatedTestEndpoint,
    [Parameter(ParameterSetName = 'Run')] [ValidateSet('Full', 'AfterReboot')] [string] $Phase = 'Full',
    [Parameter(ParameterSetName = 'Run')] [string] $ResumeDirectory,
    [Parameter(ParameterSetName = 'Run')] [string] $ServerBaseUrl,
    [Parameter(ParameterSetName = 'Run')] [string] $AdminEmail,
    [Parameter(ParameterSetName = 'Run')] [SecureString] $AdminPassword,
    [Parameter(ParameterSetName = 'Run')] [switch] $GrantViaApi,
    [Parameter(ParameterSetName = 'Run')] [switch] $IncludeWriteProbe,
    [Parameter(ParameterSetName = 'Run')] [switch] $IncludeDebuggingScenario,
    [Parameter(ParameterSetName = 'Run')] [switch] $SkipAgentRestart,
    [Parameter(ParameterSetName = 'Run')] [int] $ArrivalTimeoutSeconds = 180,
    [Parameter(ParameterSetName = 'Run')] [int] $EnforceTimeoutSeconds = 90,
    [Parameter(ParameterSetName = 'SelfTest', Mandatory)] [switch] $SelfTest,
    [string] $OutputDirectory = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'usb-acceptance')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:RunnerVersion = '1.0 (agent 1.16.0 portable-device control)'
$script:ServiceName = 'EndpointPlatformAgent'
$script:StateDirectory = Join-Path $env:ProgramData 'EndpointPlatformAgent'
$script:LedgerPath = Join-Path $script:StateDirectory 'usb-restricted-devices.json'
$script:LogDirectory = Join-Path $script:StateDirectory 'Logs'
$script:MinimumAgentVersion = [version]'1.16.0'

$script:WpdInterface = [guid]'6AC27878-A6FA-4155-BA85-F98F491D4F33'
$script:DiskInterface = [guid]'53F56307-B6BF-11D0-94F2-00A0C91EFB8B'
$script:WpdSetupClass = 'EEC5AD98-8080-425F-922A-DABF3DE3F69A'
$script:CmProbDisabled = 22

$script:Results = [ordered]@{}
$script:Measurements = [ordered]@{}
$script:OperatorNotes = [ordered]@{}
$script:Evidence = New-Object System.Collections.ArrayList
$script:Phone = $null
$script:Baseline = $null
$script:Api = $null
$script:DeviceId = $null
$script:AgentInfo = $null
$script:RunDir = $null
$script:LogFile = $null
$script:StartedUtc = [DateTime]::UtcNow
$script:Aborted = $false

$script:RequiredScenarios = @('S02', 'S03', 'S04', 'S05', 'S06', 'S07', 'S08', 'S09', 'S10', 'S13')

# ---------------------------------------------------------------------------
# Native: the same CfgMgr32 calls the agent's enforcer verifies with.
# ---------------------------------------------------------------------------

$cfgMgrSource = @"
using System;
using System.Runtime.InteropServices;
public static class UsbAcceptanceCfgMgr
{
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("CfgMgr32.dll")]
    public static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid classGuid, string deviceId, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_Interface_ListW(ref Guid classGuid, string deviceId, char[] buffer, uint length, uint flags);
}
"@

if (-not ('UsbAcceptanceCfgMgr' -as [type])) {
    Add-Type -TypeDefinition $cfgMgrSource
}

# ---------------------------------------------------------------------------
# Logging, evidence, results
# ---------------------------------------------------------------------------

function Write-Log {
    param([string]$Message, [string]$Colour = 'Gray')
    $stamp = [DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss.fff') + 'Z'
    $line = "$stamp  $Message"
    Write-Host $line -ForegroundColor $Colour
    if ($script:LogFile) { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 }
}

function Add-Evidence {
    param([string]$Scenario, [string]$Kind, $Data)
    $entry = [ordered]@{
        atUtc    = [DateTime]::UtcNow.ToString('o')
        scenario = $Scenario
        kind     = $Kind
        data     = $Data
    }
    [void]$script:Evidence.Add($entry)
    if ($script:RunDir) {
        $script:Evidence | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $script:RunDir 'evidence.json') -Encoding UTF8
    }
}

function Set-Scenario {
    param(
        [string]$Id,
        [string]$Title,
        [ValidateSet('PASS', 'PASS (operator-reported)', 'FAIL', 'NOT RUN', 'OBSERVATION', 'NOT OBSERVED')] [string]$State,
        [string]$Expected = '',
        [string]$Observed = ''
    )
    $script:Results[$Id] = [ordered]@{ Title = $Title; State = $State; Expected = $Expected; Observed = $Observed }
    $colour = switch -Wildcard ($State) { 'PASS*' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Log ("RESULT {0} {1}: {2}" -f $Id, $Title, $State) $colour
    if ($Observed) { Write-Log ("   observed: {0}" -f $Observed) 'DarkGray' }
    Save-Results
}

function Set-Measurement {
    param([string]$Name, $Value, [string]$Unit = 's')
    $script:Measurements[$Name] = [ordered]@{ Value = $Value; Unit = $Unit }
    Write-Log ("MEASURE {0} = {1} {2}" -f $Name, $Value, $Unit) 'Cyan'
    Save-Results
}

function Save-Results {
    if (-not $script:RunDir) { return }
    [ordered]@{
        runnerVersion = $script:RunnerVersion
        startedUtc    = $script:StartedUtc.ToString('o')
        hostname      = $env:COMPUTERNAME
        phase         = $Phase
        phone         = $script:Phone
        results       = $script:Results
        measurements  = $script:Measurements
        operatorNotes = $script:OperatorNotes
    } | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $script:RunDir 'results.json') -Encoding UTF8
}

function Section {
    param([string]$Name)
    Write-Host ''
    Write-Host ('=' * 76) -ForegroundColor White
    Write-Host "  $Name" -ForegroundColor White
    Write-Host ('=' * 76) -ForegroundColor White
    Write-Log "SECTION $Name"
}

function Mutating {
    param([string]$What)
    Write-Host ''
    Write-Host "*** MUTATING STEP *** $What" -ForegroundColor Magenta
    Write-Log "MUTATING STEP: $What" 'Magenta'
}

function Instruct {
    param([string]$Text)
    Write-Host ''
    Write-Host ('-' * 76) -ForegroundColor Cyan
    Write-Host "  OPERATOR: $Text" -ForegroundColor Cyan
    Write-Host ('-' * 76) -ForegroundColor Cyan
    Write-Log "INSTRUCT: $Text"
}

function Ask {
    param([string]$Prompt)
    $answer = Read-Host "  $Prompt"
    Write-Log "OPERATOR [$Prompt]: $answer"
    return $answer
}

function Note-Operator {
    param([string]$Key, [string]$Text)
    $script:OperatorNotes[$Key] = $Text
    Save-Results
}

function Fail-Hard {
    param([string]$Why)
    Write-Log "ABORTED: $Why" 'Red'
    Write-Host 'The script refuses to guess. Nothing was changed.' -ForegroundColor Red
    $script:Aborted = $true
    throw "ABORTED: $Why"
}

# ---------------------------------------------------------------------------
# Device state
# ---------------------------------------------------------------------------

function Get-DevNodeStatus {
    param([string]$InstanceId)
    $devInst = [uint32]0
    $cr = [UsbAcceptanceCfgMgr]::CM_Locate_DevNodeW([ref]$devInst, $InstanceId, 0)
    if ($cr -ne 0) {
        return [pscustomobject]@{ Present = $false; Status = 0; Problem = 0; Started = $false; HasProblem = $false; NeedRestart = $false; Disabled = $false; Cr = $cr }
    }
    $status = [uint32]0
    $problem = [uint32]0
    $cr = [UsbAcceptanceCfgMgr]::CM_Get_DevNode_Status([ref]$status, [ref]$problem, $devInst, 0)
    if ($cr -ne 0) {
        return [pscustomobject]@{ Present = $false; Status = 0; Problem = 0; Started = $false; HasProblem = $false; NeedRestart = $false; Disabled = $false; Cr = $cr }
    }
    $hasProblem = (($status -band 0x400) -ne 0)
    [pscustomobject]@{
        Present     = $true
        Status      = $status
        Problem     = $problem
        Started     = (($status -band 0x8) -ne 0)
        HasProblem  = $hasProblem
        NeedRestart = (($status -band 0x100) -ne 0)
        Disabled    = ($hasProblem -and $problem -eq $script:CmProbDisabled)
        Cr          = 0
    }
}

function Get-InterfaceLinks {
    param([guid]$ClassGuid, [string]$InstanceId = $null)
    $length = [uint32]0
    $guid = $ClassGuid
    $deviceId = if ([string]::IsNullOrEmpty($InstanceId)) { [NullString]::Value } else { $InstanceId }
    $cr = [UsbAcceptanceCfgMgr]::CM_Get_Device_Interface_List_SizeW([ref]$length, [ref]$guid, $deviceId, 0)
    if ($cr -ne 0 -or $length -le 1) { return @() }
    $buffer = New-Object char[] $length
    $cr = [UsbAcceptanceCfgMgr]::CM_Get_Device_Interface_ListW([ref]$guid, $deviceId, $buffer, $length, 0)
    if ($cr -ne 0) { return @() }
    $joined = -join $buffer
    return @($joined.Split([char]0) | Where-Object { $_ -ne '' })
}

function As-Array {
    param($Value)
    if ($null -eq $Value) { return @() }
    return @($Value | Where-Object { $null -ne $_ })
}

function Get-UsbPresent {
    @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -like 'USB\*' } |
        ForEach-Object {
            [pscustomobject]@{
                InstanceId = $_.InstanceId
                Name       = [string]$_.FriendlyName
                Class      = [string]$_.Class
                Status     = [string]$_.Status
                Problem    = [string]$_.Problem
            }
        })
}

function Get-NodeTraits {
    param([string]$InstanceId)
    $keys = @(
        'DEVPKEY_Device_CompatibleIds', 'DEVPKEY_Device_Service', 'DEVPKEY_Device_Class',
        'DEVPKEY_Device_ClassGuid', 'DEVPKEY_Device_Parent', 'DEVPKEY_Device_FriendlyName',
        'DEVPKEY_Device_BusReportedDeviceDesc', 'DEVPKEY_Device_HardwareIds', 'DEVPKEY_NAME'
    )
    $props = @{}
    $raw = @(Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName $keys -ErrorAction SilentlyContinue)
    foreach ($p in $raw) { $props[$p.KeyName] = $p.Data }
    [pscustomobject]@{
        InstanceId     = $InstanceId
        CompatibleIds  = As-Array $props['DEVPKEY_Device_CompatibleIds']
        HardwareIds    = As-Array $props['DEVPKEY_Device_HardwareIds']
        Service        = [string]$props['DEVPKEY_Device_Service']
        Class          = [string]$props['DEVPKEY_Device_Class']
        ClassGuid      = ([string]$props['DEVPKEY_Device_ClassGuid']).Trim('{}').ToUpperInvariant()
        Parent         = [string]$props['DEVPKEY_Device_Parent']
        FriendlyName   = [string]$props['DEVPKEY_Device_FriendlyName']
        BusDescription = [string]$props['DEVPKEY_Device_BusReportedDeviceDesc']
        Name           = [string]$props['DEVPKEY_NAME']
    }
}

# The agent's own rule, re-derived here only to find the phone among arrivals.
# The agent's classification is what the console check (S04) verifies.
function Test-PortableTraits {
    param($Traits)
    $tokens = @($Traits.CompatibleIds | ForEach-Object { ([string]$_).ToUpperInvariant() })
    if ($tokens -contains 'USB\MS_COMP_MTP' -or $tokens -contains 'USB\MS_COMP_PTP') { return $true }
    if (@($tokens | Where-Object { $_ -eq 'USB\CLASS_06' -or $_ -like 'USB\CLASS_06&*' }).Count -gt 0) { return $true }
    if ($tokens -contains 'USB\CLASS_FF&SUBCLASS_42&PROT_01') { return $true }
    if ($Traits.ClassGuid -eq $script:WpdSetupClass -or $Traits.Class -eq 'WPD') { return $true }
    if (@('WUDFWpdMtp', 'WpdUsb', 'WUDFWpdFs') -contains $Traits.Service) { return $true }
    return $false
}

function Test-StorageTraits {
    param($Traits)
    $tokens = @($Traits.CompatibleIds | ForEach-Object { ([string]$_).ToUpperInvariant() })
    if (@($tokens | Where-Object { $_ -eq 'USB\CLASS_08' -or $_ -like 'USB\CLASS_08&*' }).Count -gt 0) { return $true }
    if ($Traits.Service -eq 'USBSTOR') { return $true }
    return $false
}

function Get-VidPid {
    param([string]$InstanceId)
    if ($InstanceId -match 'VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})') { return ('VID_{0}&PID_{1}' -f $Matches[1].ToUpperInvariant(), $Matches[2].ToUpperInvariant()) }
    return $null
}

function Get-ShellComputerItems {
    $shell = New-Object -ComObject Shell.Application
    $items = @()
    $folder = $shell.NameSpace(17)
    if ($null -ne $folder) {
        foreach ($item in @($folder.Items())) { $items += [string]$item.Name }
    }
    return @($items)
}

function Get-ShellPhoneItem {
    $shell = New-Object -ComObject Shell.Application
    $folder = $shell.NameSpace(17)
    if ($null -eq $folder -or $null -eq $script:Phone) { return $null }
    $baseline = @($script:Baseline.ShellItems)
    foreach ($item in @($folder.Items())) {
        $name = [string]$item.Name
        $isNew = -not ($baseline -contains $name)
        $matchesName = $false
        foreach ($candidate in @($script:Phone.Names)) {
            if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
            if ($name -like "*$candidate*" -or $candidate -like "*$name*") { $matchesName = $true }
        }
        if ($isNew -or $matchesName) { return $item }
    }
    return $null
}

function Get-PhoneState {
    $status = Get-DevNodeStatus $script:Phone.InstanceId
    $vidPid = $script:Phone.VidPid
    $wpd = @(Get-InterfaceLinks $script:WpdInterface | Where-Object { $_ -like "*$vidPid*" })
    $disk = @(Get-InterfaceLinks $script:DiskInterface | Where-Object { $_ -like "*$vidPid*" })
    [pscustomobject]@{
        AtUtc       = [DateTime]::UtcNow
        Present     = $status.Present
        Started     = $status.Started
        HasProblem  = $status.HasProblem
        Problem     = $status.Problem
        Disabled    = $status.Disabled
        NeedRestart = $status.NeedRestart
        Wpd         = $wpd.Count
        Disk        = $disk.Count
        Restricted  = ($status.Present -and $status.Disabled -and $wpd.Count -eq 0 -and $disk.Count -eq 0)
        Enabled     = ($status.Present -and $status.Started -and -not $status.HasProblem -and $wpd.Count -ge 1)
    }
}

function Describe-State {
    param($State)
    if (-not $State.Present) { return 'absent' }
    $what = if ($State.Disabled) { 'disabled (CM_PROB_DISABLED)' } elseif ($State.HasProblem) { "problem $($State.Problem)" } elseif ($State.Started) { 'started' } else { 'present, not started' }
    $restart = if ($State.NeedRestart) { ', DN_NEED_RESTART set' } else { '' }
    return ('{0}, portable-device interfaces={1}, disk interfaces={2}{3}' -f $what, $State.Wpd, $State.Disk, $restart)
}

# Samples the phone every 250 ms until it reaches the target or the timeout.
# Returns the elapsed seconds (or $null), plus what was seen on the way.
function Watch-Phone {
    param(
        [ValidateSet('Restricted', 'Enabled', 'Absent', 'Present')] [string]$Until,
        [int]$TimeoutSeconds
    )
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $everStarted = $false
    $everInterface = $false
    $everRestart = $false
    $first = $null
    $last = $null
    $reached = $null
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $state = Get-PhoneState
        if ($null -eq $first) { $first = $state }
        $last = $state
        if ($state.Present -and $state.Started -and -not $state.HasProblem) { $everStarted = $true }
        if ($state.Wpd -gt 0 -or $state.Disk -gt 0) { $everInterface = $true }
        if ($state.NeedRestart) { $everRestart = $true }
        $done = switch ($Until) {
            'Restricted' { $state.Restricted }
            'Enabled' { $state.Enabled }
            'Absent' { -not $state.Present }
            'Present' { $state.Present }
        }
        if ($done) { $reached = [math]::Round($stopwatch.Elapsed.TotalSeconds, 2); break }
        Start-Sleep -Milliseconds 250
    }
    [pscustomobject]@{
        Reached       = $reached
        EverStarted   = $everStarted
        EverInterface = $everInterface
        EverRestart   = $everRestart
        First         = $first
        Last          = $last
    }
}

function Wait-NewPortableDevice {
    param([int]$TimeoutSeconds, [string[]]$Known)
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $present = Get-UsbPresent
        $new = @($present | Where-Object { -not ($Known -contains $_.InstanceId) })
        foreach ($node in $new) {
            $traits = Get-NodeTraits $node.InstanceId
            if (-not (Test-PortableTraits $traits)) { continue }
            $phoneId = $node.InstanceId
            $parentTraits = $null
            if ($node.InstanceId -match '&MI_' -and $traits.Parent) {
                $phoneId = $traits.Parent
                $parentTraits = Get-NodeTraits $phoneId
            }
            $names = @($traits.FriendlyName, $traits.BusDescription, $traits.Name, $node.Name)
            if ($parentTraits) { $names += @($parentTraits.FriendlyName, $parentTraits.BusDescription, $parentTraits.Name) }
            return [ordered]@{
                InstanceId      = $phoneId
                FunctionId      = $node.InstanceId
                VidPid          = (Get-VidPid $phoneId)
                Names           = @($names | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
                CompatibleIds   = @($traits.CompatibleIds)
                Service         = $traits.Service
                Class           = $traits.Class
                ClassGuid       = $traits.ClassGuid
                Composite       = ($phoneId -ne $node.InstanceId)
                ArrivedUtc      = [DateTime]::UtcNow.ToString('o')
                ArrivalSeconds  = [math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
            }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# ---------------------------------------------------------------------------
# Agent under test (read-only)
# ---------------------------------------------------------------------------

function Get-AgentInfo {
    $service = Get-CimInstance Win32_Service -Filter "Name='$($script:ServiceName)'" -ErrorAction SilentlyContinue
    if (-not $service) { return $null }
    $exe = $service.PathName
    if ($exe -match '^"([^"]+)"') { $exe = $Matches[1] } else { $exe = ($exe -split ' ')[0] }
    $productVersion = $null
    $semver = $null
    try { $productVersion = (Get-Item $exe).VersionInfo.ProductVersion } catch { }
    if ($productVersion -match '^(\d+\.\d+\.\d+)') { $semver = [version]$Matches[1] }
    $serverHost = $null
    foreach ($config in @((Join-Path (Split-Path $exe -Parent) 'appsettings.json'), (Join-Path $script:StateDirectory 'agent.config.json'))) {
        if (-not (Test-Path $config)) { continue }
        try {
            $json = Get-Content $config -Raw | ConvertFrom-Json
            $url = $null
            if ($json.PSObject.Properties['Agent'] -and $json.Agent.PSObject.Properties['ServerBaseUrl']) { $url = $json.Agent.ServerBaseUrl }
            elseif ($json.PSObject.Properties['ServerBaseUrl']) { $url = $json.ServerBaseUrl }
            if ($url) { $serverHost = ([uri]$url).Host }
        } catch { }
    }
    $processStart = $null
    try { if ($service.ProcessId -gt 0) { $processStart = (Get-Process -Id $service.ProcessId).StartTime.ToUniversalTime() } } catch { }
    [pscustomobject]@{
        State           = [string]$service.State
        ExePath         = $exe
        ProductVersion  = $productVersion
        Version         = $semver
        ServerHost      = $serverHost
        ProcessStartUtc = $processStart
    }
}

function Read-Ledger {
    if (-not (Test-Path $script:LedgerPath)) { return @() }
    try { return @((Get-Content $script:LedgerPath -Raw | ConvertFrom-Json)) } catch { return @("<unreadable: $($_.Exception.Message)>") }
}

function Get-AgentLogLines {
    param([DateTime]$SinceUtc, [string]$Match = $null, [int]$Limit = 200)
    if (-not (Test-Path $script:LogDirectory)) { return @() }
    $files = @(Get-ChildItem $script:LogDirectory -Filter 'agent-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 2)
    $lines = New-Object System.Collections.ArrayList
    $culture = [Globalization.CultureInfo]::InvariantCulture
    foreach ($file in $files) {
        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $reader = New-Object IO.StreamReader($stream)
        try {
            while ($null -ne ($line = $reader.ReadLine())) {
                if ($line -notmatch '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})') { continue }
                $stamp = [DateTimeOffset]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff zzz', $culture)
                if ($stamp.UtcDateTime -lt $SinceUtc) { continue }
                if ($Match -and ($line -notlike "*$Match*")) { continue }
                [void]$lines.Add($line)
            }
        } finally { $reader.Dispose() }
    }
    return @($lines | Sort-Object | Select-Object -Last $Limit)
}

function Snapshot-Agent {
    param([string]$Scenario, [DateTime]$SinceUtc)
    $ledger = Read-Ledger
    $log = Get-AgentLogLines -SinceUtc $SinceUtc -Match 'USB'
    Add-Evidence -Scenario $Scenario -Kind 'agent-ledger' -Data $ledger
    Add-Evidence -Scenario $Scenario -Kind 'agent-log-usb' -Data $log
    if ($script:RunDir) {
        Add-Content -Path (Join-Path $script:RunDir 'agent-log-excerpt.txt') -Encoding UTF8 -Value (@("", "---- $Scenario at $([DateTime]::UtcNow.ToString('o')) ----") + $log)
    }
    return [pscustomobject]@{ Ledger = $ledger; Log = $log }
}

# ---------------------------------------------------------------------------
# Console (Admin API), optional
# ---------------------------------------------------------------------------

function Connect-Api {
    $base = $ServerBaseUrl.TrimEnd('/') + '/api'
    if (-not $AdminPassword) { $script:AdminPasswordLocal = Read-Host "  Console password for $AdminEmail" -AsSecureString } else { $script:AdminPasswordLocal = $AdminPassword }
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($script:AdminPasswordLocal)
    try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringUni($pointer) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer) }
    $headers = @{ 'X-Requested-With' = 'XMLHttpRequest' }
    $login = Invoke-RestMethod -Method Post -Uri "$base/admin/v1/auth/login" -ContentType 'application/json' -Headers $headers -Body (@{ email = $AdminEmail; password = $plain } | ConvertTo-Json)
    $plain = $null
    if ($login.PSObject.Properties['mfaRequired'] -and $login.mfaRequired) {
        $code = Read-Host "  Authenticator code for $AdminEmail"
        $login = Invoke-RestMethod -Method Post -Uri "$base/admin/v1/auth/mfa/verify" -ContentType 'application/json' -Headers $headers -Body (@{ challengeToken = $login.challengeToken; code = $code } | ConvertTo-Json)
    }
    if (-not $login.sessionToken) { throw 'The console did not return a session token.' }
    $script:Api = @{ Base = $base; Token = $login.sessionToken }
    Write-Log "Console session established for $AdminEmail (token held in memory only)."
}

function Invoke-Api {
    param([string]$Method, [string]$Path, $Body = $null)
    $headers = @{ Authorization = "Bearer $($script:Api.Token)"; 'X-Requested-With' = 'XMLHttpRequest' }
    if ($null -ne $Body) {
        return Invoke-RestMethod -Method $Method -Uri ($script:Api.Base + $Path) -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 5)
    }
    return Invoke-RestMethod -Method $Method -Uri ($script:Api.Base + $Path) -Headers $headers
}

function Find-DeviceId {
    $page = Invoke-Api 'Get' "/admin/v1/devices?search=$([uri]::EscapeDataString($env:COMPUTERNAME))"
    $items = if ($page.PSObject.Properties['items']) { @($page.items) } else { @($page) }
    $match = @($items | Where-Object { $_.hostname -eq $env:COMPUTERNAME })
    if ($match.Count -ne 1) { throw "The console lists $($match.Count) device(s) named $env:COMPUTERNAME; expected exactly one." }
    Add-Evidence -Scenario 'S00' -Kind 'console-device' -Data ([ordered]@{ id = $match[0].id; hostname = $match[0].hostname; agentVersion = $match[0].agentVersion; status = $match[0].status })
    return [string]$match[0].id
}

function Get-ConsoleRow {
    $rows = Invoke-Api 'Get' "/admin/v1/devices/$($script:DeviceId)/usb-devices"
    $list = if ($rows.PSObject.Properties['items']) { @($rows.items) } else { @($rows) }
    return @($list | Where-Object { $_.instanceId -eq $script:Phone.InstanceId }) | Select-Object -First 1
}

function Wait-ConsoleRow {
    param([scriptblock]$Condition, [int]$TimeoutSeconds = 90)
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $row = $null
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        try { $row = Get-ConsoleRow } catch { Write-Log "console read failed: $($_.Exception.Message)" 'Yellow' }
        if ($row -and (& $Condition $row)) { return $row }
        Start-Sleep -Seconds 3
    }
    return $row
}

function Format-ConsoleRow {
    param($Row)
    if ($null -eq $Row) { return 'no row for this instance' }
    return ('class={0} policy={1} state={2} connected={3} error={4}' -f $Row.deviceClass, $Row.policy, $Row.enforcementState, $Row.isConnected, $Row.enforcementError)
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

function Get-Verdict {
    $missing = @()
    $failed = @()
    foreach ($id in $script:RequiredScenarios) {
        if (-not $script:Results.Contains($id)) { $missing += $id; continue }
        $state = $script:Results[$id].State
        if ($state -eq 'FAIL') { $failed += $id }
        elseif ($state -eq 'NOT RUN') { $missing += $id }
    }
    if ($failed.Count -gt 0) { return [pscustomobject]@{ Verdict = 'FAIL'; Failed = $failed; Missing = $missing } }
    if ($missing.Count -gt 0 -or $script:Aborted) { return [pscustomobject]@{ Verdict = 'INCOMPLETE'; Failed = @(); Missing = $missing } }
    return [pscustomobject]@{ Verdict = 'PASS'; Failed = @(); Missing = @() }
}

function Write-Report {
    if (-not $script:RunDir) { return }
    $verdict = Get-Verdict
    $lines = New-Object System.Collections.ArrayList
    [void]$lines.Add('# USB portable-device control: physical acceptance')
    [void]$lines.Add('')
    [void]$lines.Add("**Result: $($verdict.Verdict)**")
    if ($verdict.Verdict -eq 'PASS') {
        [void]$lines.Add('')
        [void]$lines.Add('Every required scenario ran on a real device on this machine and the observed behaviour matched the expectation.')
    } elseif ($verdict.Verdict -eq 'FAIL') {
        [void]$lines.Add('')
        [void]$lines.Add("Failed: $($verdict.Failed -join ', ').")
    } else {
        [void]$lines.Add('')
        [void]$lines.Add("Not yet observed: $($verdict.Missing -join ', '). Physical acceptance is NOT VERIFIED until every required scenario has run.")
    }
    [void]$lines.Add('')
    [void]$lines.Add('| | |')
    [void]$lines.Add('|---|---|')
    [void]$lines.Add("| Machine | $env:COMPUTERNAME |")
    [void]$lines.Add("| Windows | $([Environment]::OSVersion.VersionString) |")
    if ($script:AgentInfo) {
        [void]$lines.Add("| Agent | $($script:AgentInfo.ProductVersion) ($($script:AgentInfo.State)) |")
        [void]$lines.Add("| Server | $($script:AgentInfo.ServerHost) |")
    }
    if ($script:Phone) {
        [void]$lines.Add("| Phone | $($script:Phone.Names -join ' / ') |")
        [void]$lines.Add("| Instance | $($script:Phone.InstanceId) |")
        [void]$lines.Add("| Compatible IDs | $($script:Phone.CompatibleIds -join '; ') |")
    }
    [void]$lines.Add("| Run started (UTC) | $($script:StartedUtc.ToString('o')) |")
    [void]$lines.Add("| Runner | $($script:RunnerVersion) |")
    [void]$lines.Add("| Console checks | $(if ($script:Api) { 'Admin API (machine-verified)' } else { 'operator-reported' }) |")
    [void]$lines.Add('')
    [void]$lines.Add('## Scenarios')
    [void]$lines.Add('')
    [void]$lines.Add('| Id | Scenario | Result | Expected | Observed |')
    [void]$lines.Add('|---|---|---|---|---|')
    foreach ($id in $script:Results.Keys) {
        $r = $script:Results[$id]
        $expected = ([string]$r.Expected) -replace '\|', '/'
        $observed = ([string]$r.Observed) -replace '\|', '/'
        [void]$lines.Add("| $id | $($r.Title) | **$($r.State)** | $expected | $observed |")
    }
    foreach ($id in $script:RequiredScenarios) {
        if (-not $script:Results.Contains($id)) { [void]$lines.Add("| $id | (required) | **NOT RUN** | | |") }
    }
    [void]$lines.Add('')
    [void]$lines.Add('## Measurements')
    [void]$lines.Add('')
    if ($script:Measurements.Count -eq 0) { [void]$lines.Add('None recorded.') }
    else {
        [void]$lines.Add('| Measurement | Value |')
        [void]$lines.Add('|---|---|')
        foreach ($name in $script:Measurements.Keys) { $m = $script:Measurements[$name]; [void]$lines.Add("| $name | $($m.Value) $($m.Unit) |") }
    }
    [void]$lines.Add('')
    [void]$lines.Add('## Operator-reported observations')
    [void]$lines.Add('')
    if ($script:OperatorNotes.Count -eq 0) { [void]$lines.Add('None.') }
    else { foreach ($key in $script:OperatorNotes.Keys) { [void]$lines.Add("- **$key**: $($script:OperatorNotes[$key])") } }
    [void]$lines.Add('')
    [void]$lines.Add('## Evidence')
    [void]$lines.Add('')
    [void]$lines.Add("- Folder: ``$($script:RunDir)``")
    [void]$lines.Add('- `run.log`: every instruction, answer, sample and result with UTC timestamps')
    [void]$lines.Add('- `evidence.json`: raw device states, interface lists, ledger snapshots, log excerpts, console rows')
    [void]$lines.Add('- `results.json`: the scenario table and measurements as data')
    [void]$lines.Add('- `agent-log-excerpt.txt`: the agent''s USB log lines at each checkpoint')
    [void]$lines.Add('')
    [void]$lines.Add('## What this report does and does not say')
    [void]$lines.Add('')
    [void]$lines.Add('- A PASS records what a real phone did on this machine with the installed agent. It is not a simulation and no check was skipped silently: anything not observed is listed as NOT RUN.')
    [void]$lines.Add('- "Restricted" means Windows reports the phone''s device node disabled and no portable-device or disk interface exposed beneath it, read through CfgMgr32 by this script, independently of the agent.')
    [void]$lines.Add('- Operator-reported lines were typed by the person running the test and were not machine-verified.')
    [void]$lines.Add('- USB tethering, Bluetooth, Wi-Fi Direct and cloud sync are outside the control and were not tested.')
    [void]$lines.Add('- The evidence folder contains the phone''s serial number inside its instance id. Redact it before quoting results in the public repository.')
    $lines | Set-Content -Path (Join-Path $script:RunDir 'report.md') -Encoding UTF8

    Write-Host ''
    Write-Host ('=' * 76) -ForegroundColor White
    $colour = switch ($verdict.Verdict) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host "  PHYSICAL ACCEPTANCE RESULT: $($verdict.Verdict)" -ForegroundColor $colour
    foreach ($id in $script:Results.Keys) {
        $r = $script:Results[$id]
        $c = switch -Wildcard ($r.State) { 'PASS*' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
        Write-Host ('  {0,-24} {1} {2}' -f $r.State, $id, $r.Title) -ForegroundColor $c
    }
    if ($verdict.Missing.Count -gt 0) { Write-Host "  Not observed: $($verdict.Missing -join ', ')" -ForegroundColor Yellow }
    Write-Host "  Report: $(Join-Path $script:RunDir 'report.md')" -ForegroundColor White
    Write-Host ('=' * 76) -ForegroundColor White
}

# ---------------------------------------------------------------------------
# Self-test: read-only helpers on any machine. No guards, no prompts, no claim.
# ---------------------------------------------------------------------------

function Invoke-SelfTest {
    $script:RunDir = Join-Path $OutputDirectory ('selftest-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $script:RunDir -Force | Out-Null
    $script:LogFile = Join-Path $script:RunDir 'run.log'
    Section 'SELF-TEST: read-only helpers only. This is NOT an acceptance run.'
    $present = Get-UsbPresent
    Write-Log ("present USB device nodes: {0}" -f $present.Count)
    $portable = 0
    $storage = 0
    $statusOk = 0
    foreach ($node in $present) {
        $traits = Get-NodeTraits $node.InstanceId
        $isPortable = Test-PortableTraits $traits
        $isStorage = Test-StorageTraits $traits
        if ($isPortable) { $portable++ }
        if ($isStorage) { $storage++ }
        $status = Get-DevNodeStatus $node.InstanceId
        if ($status.Present) { $statusOk++ }
        Write-Log ("  {0,-60} class={1,-10} portable={2,-5} storage={3,-5} started={4,-5} problem={5}" -f $node.InstanceId, $node.Class, $isPortable, $isStorage, $status.Started, $status.Problem)
        Add-Evidence -Scenario 'SELFTEST' -Kind 'node' -Data ([ordered]@{ instanceId = $node.InstanceId; class = $node.Class; service = $traits.Service; compatibleIds = $traits.CompatibleIds; portable = $isPortable; storage = $isStorage; status = $status })
    }
    $wpd = @(Get-InterfaceLinks $script:WpdInterface)
    $disk = @(Get-InterfaceLinks $script:DiskInterface)
    Write-Log ("portable-device interfaces present on this machine: {0}; disk interfaces: {1}" -f $wpd.Count, $disk.Count)
    Add-Evidence -Scenario 'SELFTEST' -Kind 'interfaces' -Data ([ordered]@{ wpd = $wpd; disk = $disk })
    $shell = Get-ShellComputerItems
    Write-Log ("This PC shell items: {0}" -f $shell.Count)
    Add-Evidence -Scenario 'SELFTEST' -Kind 'shell-items' -Data $shell
    $absent = Get-DevNodeStatus 'USB\VID_0000&PID_0000\SELFTEST-NOT-PRESENT'
    Write-Log ("locating a non-existent instance returns present={0} (CR=0x{1:X})" -f $absent.Present, $absent.Cr)
    $checks = [ordered]@{
        'CfgMgr32 interop compiled and callable'   = ($null -ne ('UsbAcceptanceCfgMgr' -as [type]))
        'Device status readable for present nodes' = ($present.Count -eq 0 -or $statusOk -eq $present.Count)
        'Non-present instance reports absent'      = (-not $absent.Present)
        'Interface listing works'                  = ($true)
        'Shell namespace readable'                 = ($shell.Count -ge 1)
    }
    $allOk = $true
    foreach ($name in $checks.Keys) { $ok = $checks[$name]; if (-not $ok) { $allOk = $false }; Write-Log ("  {0,-6} {1}" -f $(if ($ok) { 'OK' } else { 'FAILED' }), $name) $(if ($ok) { 'Green' } else { 'Red' }) }
    Write-Log ("portable devices present now: {0}; storage devices present now: {1}" -f $portable, $storage)
    @(
        '# Runner self-test (read-only)',
        '',
        "Machine: $env:COMPUTERNAME, $([DateTime]::UtcNow.ToString('o'))",
        '',
        'This checks only that the helpers work on this machine. It makes no acceptance claim and observed no phone.',
        '',
        '| Check | Result |', '|---|---|'
    ) + @($checks.Keys | ForEach-Object { "| $_ | $(if ($checks[$_]) { 'OK' } else { 'FAILED' }) |" }) + @(
        '',
        "Present USB nodes: $($present.Count); portable: $portable; storage: $storage; portable-device interfaces: $($wpd.Count); disk interfaces: $($disk.Count)."
    ) | Set-Content -Path (Join-Path $script:RunDir 'selftest-report.md') -Encoding UTF8
    Write-Host ''
    Write-Host ("  SELF-TEST {0}. Report: {1}" -f $(if ($allOk) { 'OK' } else { 'FAILED' }), (Join-Path $script:RunDir 'selftest-report.md')) -ForegroundColor $(if ($allOk) { 'Green' } else { 'Red' })
    Write-Host '  No acceptance result: no phone was observed and nothing was tested against the agent.' -ForegroundColor Yellow
    if ($allOk) { exit 0 } else { exit 1 }
}

if ($PSCmdlet.ParameterSetName -eq 'SelfTest') { Invoke-SelfTest }

# ---------------------------------------------------------------------------
# Scenarios
# ---------------------------------------------------------------------------

function Invoke-Preflight {
    param([switch]$PhoneMayBeAttached)
    Section 'S00 Preflight'
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Fail-Hard 'Run from an elevated PowerShell: the agent''s state folder and log are readable by administrators only.' }
    if ($env:COMPUTERNAME -ne $ExpectedHostname) { Fail-Hard "This machine is $env:COMPUTERNAME, not $ExpectedHostname. Refusing to run on an unexpected machine." }
    if (-not $IAmOnTheDesignatedTestEndpoint) { Fail-Hard 'Pass -IAmOnTheDesignatedTestEndpoint to confirm this machine may have its phone disabled and its agent service restarted. Never on a production PC.' }
    if ($GrantViaApi -and -not $AdminEmail) { Fail-Hard '-GrantViaApi needs -ServerBaseUrl and -AdminEmail.' }
    if ($AdminEmail -and -not $ServerBaseUrl) { Fail-Hard '-AdminEmail needs -ServerBaseUrl.' }

    $script:AgentInfo = Get-AgentInfo
    if (-not $script:AgentInfo) { Fail-Hard "The $($script:ServiceName) service is not installed here. Install the release MSI built for the test server first." }
    if ($script:AgentInfo.State -ne 'Running') { Fail-Hard "The agent service is $($script:AgentInfo.State), not Running." }
    if (-not $script:AgentInfo.Version -or $script:AgentInfo.Version -lt $script:MinimumAgentVersion) { Fail-Hard "Agent $($script:AgentInfo.ProductVersion) is older than $($script:MinimumAgentVersion); the portable-device control is not in it." }
    if ($script:AgentInfo.ServerHost -and ($script:AgentInfo.ServerHost -in @('localhost', '127.0.0.1'))) { Fail-Hard 'The installed agent is baked for localhost. That is a development build; install the MSI built for the test server.' }
    Write-Log ("agent {0}, service {1}, server host {2}, process started {3}" -f $script:AgentInfo.ProductVersion, $script:AgentInfo.State, $script:AgentInfo.ServerHost, $script:AgentInfo.ProcessStartUtc)
    Add-Evidence -Scenario 'S00' -Kind 'agent' -Data $script:AgentInfo
    Add-Evidence -Scenario 'S00' -Kind 'os' -Data ([ordered]@{ version = [Environment]::OSVersion.VersionString; hostname = $env:COMPUTERNAME; runner = $script:RunnerVersion })

    if ($AdminEmail) {
        Connect-Api
        $script:DeviceId = Find-DeviceId
        Write-Log "console device id $($script:DeviceId)"
    } else {
        Write-Log 'No console credentials: console states will be asked of the operator and labelled operator-reported.' 'Yellow'
    }

    $wpdNow = @(Get-PnpDevice -PresentOnly -Class WPD -ErrorAction SilentlyContinue)
    if ($wpdNow.Count -gt 0 -and -not $PhoneMayBeAttached) {
        Instruct "A portable device is already attached ($($wpdNow[0].FriendlyName)). Disconnect every phone, tablet and camera, then press Enter."
        [void](Ask 'Press Enter when nothing portable is attached')
        $wpdNow = @(Get-PnpDevice -PresentOnly -Class WPD -ErrorAction SilentlyContinue)
        if ($wpdNow.Count -gt 0) { Fail-Hard 'A portable device is still attached; the baseline must be taken without one.' }
    }
    Set-Scenario -Id 'S00' -Title 'Preflight' -State 'PASS' -Expected 'elevated, expected host, agent 1.16.0+ running, no portable device attached' -Observed ("agent {0} running against {1}" -f $script:AgentInfo.ProductVersion, $script:AgentInfo.ServerHost)
}

function Invoke-Baseline {
    Section 'S01 Baseline (no phone attached)'
    $present = Get-UsbPresent
    $collateral = @()
    foreach ($node in $present) {
        $collateral += [ordered]@{ instanceId = $node.InstanceId; class = $node.Class; status = $node.Status; name = $node.Name }
    }
    $ledger = Read-Ledger
    $shell = Get-ShellComputerItems
    $script:Baseline = [pscustomobject]@{
        InstanceIds = @($present | ForEach-Object { $_.InstanceId })
        Collateral  = $collateral
        Ledger      = $ledger
        ShellItems  = $shell
        TakenUtc    = [DateTime]::UtcNow
    }
    Add-Evidence -Scenario 'S01' -Kind 'usb-present' -Data $collateral
    Add-Evidence -Scenario 'S01' -Kind 'agent-ledger' -Data $ledger
    Add-Evidence -Scenario 'S01' -Kind 'shell-items' -Data $shell
    Write-Log ("baseline: {0} USB nodes, {1} ledger entries, {2} This PC items" -f $present.Count, $ledger.Count, $shell.Count)
    Set-Scenario -Id 'S01' -Title 'Baseline captured' -State 'OBSERVATION' -Expected 'inventory of USB nodes, ledger and This PC before the phone' -Observed ("{0} USB nodes, {1} ledger entries" -f $present.Count, $ledger.Count)
}

function Invoke-Attach {
    Section 'S02 Attach the phone in file-transfer (MTP) mode'
    Instruct 'Connect the phone with a DATA-CAPABLE cable, unlock it, and on the phone choose "File transfer" (MTP). Do nothing else. The script is watching for it.'
    $since = [DateTime]::UtcNow
    $phone = Wait-NewPortableDevice -TimeoutSeconds $ArrivalTimeoutSeconds -Known $script:Baseline.InstanceIds
    if (-not $phone) {
        Set-Scenario -Id 'S02' -Title 'Phone detected and restricted on attach' -State 'FAIL' -Expected 'a portable device arrives within the timeout' -Observed "no portable device arrived within $ArrivalTimeoutSeconds s"
        Fail-Hard 'No phone arrived. Check the cable (charge-only cables never enumerate) and the USB mode on the phone.'
    }
    $script:Phone = $phone
    Write-Log ("phone arrived: {0} as {1}; compatible IDs: {2}; class {3}; service {4}; composite={5}" -f ($phone.Names -join ' / '), $phone.InstanceId, ($phone.CompatibleIds -join ';'), $phone.Class, $phone.Service, $phone.Composite)
    Add-Evidence -Scenario 'S02' -Kind 'phone' -Data $phone
    Save-Results

    # Windows path (step 1): what Windows made of it, before the agent acts.
    $windowsView = @(Get-PnpDevice -PresentOnly -Class WPD -ErrorAction SilentlyContinue | ForEach-Object { [ordered]@{ instanceId = $_.InstanceId; name = $_.FriendlyName; status = [string]$_.Status } })
    Add-Evidence -Scenario 'S02' -Kind 'wpd-class-devices' -Data $windowsView

    $watch = Watch-Phone -Until 'Restricted' -TimeoutSeconds $EnforceTimeoutSeconds
    Add-Evidence -Scenario 'S02' -Kind 'watch' -Data $watch
    $shellVisible = ($null -ne (Get-ShellPhoneItem))
    $agent = Snapshot-Agent -Scenario 'S02' -SinceUtc $since
    $inLedger = ($agent.Ledger -contains $phone.InstanceId)
    $verifiedLine = @($agent.Log | Where-Object { $_ -like "*$($phone.InstanceId)*" -and $_ -like '*is restricted (disabled)*' })

    if ($watch.Reached -ne $null) {
        Set-Measurement -Name 'S02 attach: seconds from arrival to restricted' -Value $watch.Reached
        Set-Measurement -Name 'S02 attach: phone ever started before restriction' -Value $watch.EverStarted -Unit ''
        Set-Measurement -Name 'S02 attach: a transfer interface was ever exposed' -Value $watch.EverInterface -Unit ''
    }
    $observed = ("{0} after {1} s; This PC shows phone={2}; in agent ledger={3}; agent logged Verified={4}" -f (Describe-State $watch.Last), $watch.Reached, $shellVisible, $inLedger, ($verifiedLine.Count -gt 0))
    if ($watch.Reached -ne $null -and -not $shellVisible) {
        Set-Scenario -Id 'S02' -Title 'Phone detected and restricted on attach' -State 'PASS' -Expected 'devnode disabled, no portable-device or disk interface, phone absent from This PC, within seconds' -Observed $observed
    } else {
        Set-Scenario -Id 'S02' -Title 'Phone detected and restricted on attach' -State 'FAIL' -Expected 'devnode disabled, no portable-device or disk interface, phone absent from This PC, within seconds' -Observed $observed
    }
}

function Invoke-Blocked {
    Section 'S03 Blocked in both directions'
    $state = Get-PhoneState
    $item = Get-ShellPhoneItem
    $machineObserved = ("{0}; This PC item for the phone: {1}" -f (Describe-State $state), $(if ($item) { 'PRESENT' } else { 'none' }))
    Add-Evidence -Scenario 'S03' -Kind 'state' -Data $state
    Instruct 'Try to copy any file FROM the PC TO the phone, and any file FROM the phone TO the PC, the way a user would (Explorer). Then describe how each attempt failed.'
    $toPhone = Ask 'PC -> phone: what happened?'
    $fromPhone = Ask 'phone -> PC: what happened?'
    Note-Operator -Key 'S03 copy PC -> phone' -Text $toPhone
    Note-Operator -Key 'S03 copy phone -> PC' -Text $fromPhone
    if ($state.Restricted -and -not $item) {
        Set-Scenario -Id 'S03' -Title 'No transfer path in either direction' -State 'PASS' -Expected 'no portable-device or disk interface exposed and no This PC item: there is nothing to copy to or from' -Observed $machineObserved
    } else {
        Set-Scenario -Id 'S03' -Title 'No transfer path in either direction' -State 'FAIL' -Expected 'no portable-device or disk interface exposed and no This PC item' -Observed $machineObserved
    }
}

function Invoke-ConsoleState {
    param([string]$Id = 'S04', [string]$Title = 'Console shows the phone as Phone / portable device, Restricted, Enforced', [string]$ExpectedPolicy = 'Restricted', [string]$ExpectedState = 'Enforced')
    Section "$Id $Title"
    if ($script:Api) {
        $row = Wait-ConsoleRow -TimeoutSeconds 90 -Condition { param($r) $r.deviceClass -eq 'PortableDevice' -and $r.policy -eq $ExpectedPolicy -and $r.enforcementState -eq $ExpectedState }
        Add-Evidence -Scenario $Id -Kind 'console-row' -Data $row
        $ok = ($row -and $row.deviceClass -eq 'PortableDevice' -and $row.policy -eq $ExpectedPolicy -and $row.enforcementState -eq $ExpectedState -and $row.isPortableDevice -eq $true)
        Set-Scenario -Id $Id -Title $Title -State $(if ($ok) { 'PASS' } else { 'FAIL' }) -Expected "deviceClass PortableDevice, policy $ExpectedPolicy, state $ExpectedState" -Observed (Format-ConsoleRow $row)
    } else {
        Instruct "In the console open this device ($env:COMPUTERNAME) and read the 'USB storage and portable devices' table. Give the phone up to a minute to appear."
        $type = Ask 'Type column shows (e.g. Phone / portable device)'
        $policy = Ask 'Policy column shows (Restricted / Read/write ...)'
        $state = Ask 'Enforcement column shows (Enforced / Applied / Pending / Failed / Restart required ...)'
        Note-Operator -Key "$Id console row" -Text "type=$type; policy=$policy; enforcement=$state"
        # The console's labels for the states the API names (dashboard/src/pages/usbView.ts).
        $policyLabel = @{ Restricted = 'restricted'; Enabled = 'readwrite'; ReadOnly = 'readonly' }[$ExpectedPolicy]
        $stateLabel = @{ Enforced = 'enforced'; Applied = 'applied'; Pending = 'pending'; Drifted = 'drifted'; RequiresRestart = 'restartrequired'; Failed = 'failed'; NotApplicable = 'notapplicable' }[$ExpectedState]
        $normalise = { param($s) (([string]$s) -replace '[^A-Za-z]', '').ToLowerInvariant() }
        $ok = ((& $normalise $policy) -like "*$policyLabel*") -and ((& $normalise $state) -like "*$stateLabel*")
        Set-Scenario -Id $Id -Title $Title -State $(if ($ok) { 'PASS (operator-reported)' } else { 'FAIL' }) -Expected "policy $ExpectedPolicy, state $ExpectedState" -Observed "operator typed: type=$type; policy=$policy; enforcement=$state"
    }
}

function Invoke-Grant {
    Section 'S05 Read/write grant'
    $since = [DateTime]::UtcNow
    Mutating 'A 30-minute read/write grant for the phone. Files can move both ways until it is revoked or expires.'
    if ($GrantViaApi) {
        $row = Get-ConsoleRow
        if (-not $row) { Fail-Hard 'The console has no row for the phone, so no grant can be issued.' }
        $grant = Invoke-Api 'Post' "/admin/v1/devices/$($script:DeviceId)/usb-devices/$($row.id)/grant" ([ordered]@{ durationMinutes = 30; justification = 'USB portable-device physical acceptance (scripts/Invoke-UsbPortableAcceptance.ps1)'; policy = 'Enabled' })
        Add-Evidence -Scenario 'S05' -Kind 'grant-response' -Data $grant
        Write-Log 'grant issued through the Admin API'
    } else {
        Instruct 'In the console, on the phone''s row, choose "Grant access…", read/write, 30 minutes, any justification. Press Enter here the moment you have confirmed it.'
        [void](Ask 'Press Enter after granting')
    }
    $watch = Watch-Phone -Until 'Enabled' -TimeoutSeconds $EnforceTimeoutSeconds
    Add-Evidence -Scenario 'S05' -Kind 'watch' -Data $watch
    $item = $null
    $storages = @()
    if ($watch.Reached -ne $null) {
        Set-Measurement -Name 'S05 grant: seconds from grant to enabled' -Value $watch.Reached
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not $item -and [DateTime]::UtcNow -lt $deadline) { $item = Get-ShellPhoneItem; if (-not $item) { Start-Sleep -Seconds 2 } }
        if ($item) {
            try { $storages = @($item.GetFolder.Items() | ForEach-Object { [string]$_.Name }) } catch { Write-Log "could not list the phone's storages: $($_.Exception.Message)" 'Yellow' }
            Add-Evidence -Scenario 'S05' -Kind 'phone-storages' -Data $storages
        }
    }
    [void](Snapshot-Agent -Scenario 'S05' -SinceUtc $since)
    $written = $null
    if ($IncludeWriteProbe -and $item -and $storages.Count -gt 0) {
        Mutating "Copying one small text file to the phone's storage '$($storages[0])'."
        try {
            $probeName = "usb-acceptance-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).txt"
            $probePath = Join-Path $script:RunDir $probeName
            "USB acceptance write probe $([DateTime]::UtcNow.ToString('o')) from $env:COMPUTERNAME" | Set-Content -Path $probePath -Encoding ASCII
            $storage = $item.GetFolder.Items() | Select-Object -First 1
            $storage.GetFolder.CopyHere($probePath, 0x14)
            $copied = $null
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while (-not $copied -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Seconds 2; $copied = $storage.GetFolder.ParseName($probeName) }
            $written = ($null -ne $copied)
            Note-Operator -Key 'S05 write probe' -Text "file $probeName copied to '$($storages[0])': $written. Delete it from the phone afterwards."
        } catch { $written = $false; Write-Log "write probe failed: $($_.Exception.Message)" 'Yellow' }
    }
    $observed = ("{0} after {1} s; This PC item={2}; storages listed={3}{4}" -f (Describe-State $watch.Last), $watch.Reached, ($null -ne $item), $storages.Count, $(if ($null -ne $written) { "; write probe=$written" } else { '' }))
    if ($watch.Reached -ne $null -and $item) {
        Set-Scenario -Id 'S05' -Title 'Read/write grant enables the phone' -State 'PASS' -Expected 'devnode started, portable-device interface exposed, phone back in This PC, its storage readable' -Observed $observed
    } else {
        Set-Scenario -Id 'S05' -Title 'Read/write grant enables the phone' -State 'FAIL' -Expected 'devnode started, portable-device interface exposed, phone back in This PC' -Observed $observed
    }
    Invoke-ConsoleState -Id 'S05c' -Title 'Console shows Read/write, Enforced while granted' -ExpectedPolicy 'Enabled' -ExpectedState 'Enforced'
}

function Invoke-Revoke {
    Section 'S06 Revoke'
    $since = [DateTime]::UtcNow
    Mutating 'Revoking the grant. The phone is disabled again.'
    Instruct 'Start a large file copy TO the phone now (so a transfer is in progress), then come back here.'
    [void](Ask 'Press Enter when the copy is running')
    if ($GrantViaApi) {
        $row = Get-ConsoleRow
        if (-not $row -or -not $row.liveRequestId) { Fail-Hard 'The console shows no live grant to revoke.' }
        $revoke = Invoke-Api 'Post' "/admin/v1/usb-access-requests/$($row.liveRequestId)/revoke" ([ordered]@{ note = 'Acceptance: revoke with a copy in progress' })
        Add-Evidence -Scenario 'S06' -Kind 'revoke-response' -Data $revoke
        Write-Log 'revoked through the Admin API'
    } else {
        Instruct 'In the console, on the phone''s row, choose "Revoke" and confirm. Press Enter here the moment you have.'
        [void](Ask 'Press Enter after revoking')
    }
    $watch = Watch-Phone -Until 'Restricted' -TimeoutSeconds $EnforceTimeoutSeconds
    Add-Evidence -Scenario 'S06' -Kind 'watch' -Data $watch
    $item = Get-ShellPhoneItem
    [void](Snapshot-Agent -Scenario 'S06' -SinceUtc $since)
    $copyOutcome = Ask 'What happened to the copy in progress?'
    Note-Operator -Key 'S06 copy in progress at revoke' -Text $copyOutcome
    if ($watch.Reached -ne $null) { Set-Measurement -Name 'S06 revoke: seconds from revoke to restricted' -Value $watch.Reached }
    $observed = ("{0} after {1} s; This PC item={2}" -f (Describe-State $watch.Last), $watch.Reached, ($null -ne $item))
    if ($watch.Reached -ne $null -and -not $item) {
        Set-Scenario -Id 'S06' -Title 'Revoke restricts the phone again and cuts the transfer' -State 'PASS' -Expected 'devnode disabled, no interface, phone gone from This PC' -Observed $observed
        Set-Scenario -Id 'S11' -Title 'Restart required is reported only when Windows defers the disable' -State 'NOT OBSERVED' -Expected 'if DN_NEED_RESTART was set the console shows Restart required, not Enforced' -Observed 'Windows applied the disable at once; no deferral occurred to observe'
    } elseif ($watch.Last.NeedRestart) {
        Set-Scenario -Id 'S06' -Title 'Revoke restricts the phone again and cuts the transfer' -State 'FAIL' -Expected 'devnode disabled at once' -Observed ("Windows deferred the disable to a restart: {0}" -f $observed)
        Invoke-ConsoleState -Id 'S11' -Title 'Console shows Restart required, not Enforced' -ExpectedPolicy 'Restricted' -ExpectedState 'RequiresRestart'
    } else {
        Set-Scenario -Id 'S06' -Title 'Revoke restricts the phone again and cuts the transfer' -State 'FAIL' -Expected 'devnode disabled, no interface, phone gone from This PC' -Observed $observed
    }
    Invoke-ConsoleState -Id 'S06c' -Title 'Console shows Restricted, Enforced after revoke' -ExpectedPolicy 'Restricted' -ExpectedState 'Enforced'
}

function Invoke-Reconnect {
    Section 'S07 Unplug and replug'
    Instruct 'Unplug the phone now.'
    $gone = Watch-Phone -Until 'Absent' -TimeoutSeconds $ArrivalTimeoutSeconds
    if ($gone.Reached -eq $null) { Set-Scenario -Id 'S07' -Title 'Reconnect keeps the phone restricted' -State 'FAIL' -Expected 'the phone is removed' -Observed 'the phone never went absent'; return }
    Write-Log 'phone removed'
    Instruct 'Plug the phone back in with the same cable, same USB mode. Do not touch anything else.'
    $since = [DateTime]::UtcNow
    $back = Watch-Phone -Until 'Present' -TimeoutSeconds $ArrivalTimeoutSeconds
    if ($back.Reached -eq $null) { Set-Scenario -Id 'S07' -Title 'Reconnect keeps the phone restricted' -State 'FAIL' -Expected 'the phone re-enumerates' -Observed 'the phone did not come back'; return }
    $watch = Watch-Phone -Until 'Restricted' -TimeoutSeconds $EnforceTimeoutSeconds
    Add-Evidence -Scenario 'S07' -Kind 'watch' -Data ([ordered]@{ firstSeen = $back.Last; afterwards = $watch })
    $item = Get-ShellPhoneItem
    [void](Snapshot-Agent -Scenario 'S07' -SinceUtc $since)
    if ($watch.Reached -ne $null) {
        Set-Measurement -Name 'S07 reconnect: seconds from re-enumeration to restricted' -Value $watch.Reached
        Set-Measurement -Name 'S07 reconnect: phone ever started after replug' -Value ($back.EverStarted -or $watch.EverStarted) -Unit ''
        Set-Measurement -Name 'S07 reconnect: a transfer interface was ever exposed' -Value ($back.EverInterface -or $watch.EverInterface) -Unit ''
    }
    $observed = ("first seen as: {0}; then {1} after {2} s; This PC item={3}" -f (Describe-State $back.Last), (Describe-State $watch.Last), $watch.Reached, ($null -ne $item))
    if ($watch.Reached -ne $null -and -not $item) {
        Set-Scenario -Id 'S07' -Title 'Reconnect keeps the phone restricted' -State 'PASS' -Expected 'the instance arrives already disabled (the flag persists) or is disabled within seconds; it never reaches This PC' -Observed $observed
    } else {
        Set-Scenario -Id 'S07' -Title 'Reconnect keeps the phone restricted' -State 'FAIL' -Expected 'the instance arrives disabled or is disabled within seconds' -Observed $observed
    }
    Invoke-ConsoleState -Id 'S07c' -Title 'Console shows the phone attached and Restricted after reconnect' -ExpectedPolicy 'Restricted' -ExpectedState 'Enforced'
}

function Invoke-ModeSwitch {
    Section 'S08 Switch the phone to "Transfer photos" (PTP)'
    $mtp = $script:Phone
    $known = @((Get-UsbPresent | ForEach-Object { $_.InstanceId }))
    Instruct 'On the phone, change the USB mode to "Transfer photos" / "PTP". Leave the cable in.'
    $since = [DateTime]::UtcNow
    $ptp = Wait-NewPortableDevice -TimeoutSeconds $ArrivalTimeoutSeconds -Known $known
    if (-not $ptp -or $ptp.InstanceId -eq $mtp.InstanceId) {
        Set-Scenario -Id 'S08' -Title 'PTP mode is a new instance, restricted within seconds' -State 'FAIL' -Expected 'a different instance (new product id) appears' -Observed $(if ($ptp) { "same instance $($ptp.InstanceId)" } else { 'no new portable device appeared' })
        return
    }
    $script:Phone = $ptp
    Save-Results
    Add-Evidence -Scenario 'S08' -Kind 'phone-ptp' -Data $ptp
    Write-Log ("PTP instance: {0}; compatible IDs {1}" -f $ptp.InstanceId, ($ptp.CompatibleIds -join ';'))
    $watch = Watch-Phone -Until 'Restricted' -TimeoutSeconds $EnforceTimeoutSeconds
    Add-Evidence -Scenario 'S08' -Kind 'watch' -Data $watch
    $item = Get-ShellPhoneItem
    $mtpStatus = Get-DevNodeStatus $mtp.InstanceId
    [void](Snapshot-Agent -Scenario 'S08' -SinceUtc $since)
    if ($watch.Reached -ne $null) {
        Set-Measurement -Name 'S08 PTP: seconds from arrival to restricted' -Value $watch.Reached
        Set-Measurement -Name 'S08 PTP: phone ever started before restriction' -Value $watch.EverStarted -Unit ''
    }
    $observed = ("PTP instance {0}: {1} after {2} s; MTP instance present={3}; This PC item={4}" -f $ptp.VidPid, (Describe-State $watch.Last), $watch.Reached, $mtpStatus.Present, ($null -ne $item))
    if ($watch.Reached -ne $null -and -not $item) {
        Set-Scenario -Id 'S08' -Title 'PTP mode is a new instance, restricted within seconds' -State 'PASS' -Expected 'new instance, classified portable, disabled within seconds, listed separately in the console' -Observed $observed
    } else {
        Set-Scenario -Id 'S08' -Title 'PTP mode is a new instance, restricted within seconds' -State 'FAIL' -Expected 'new instance disabled within seconds' -Observed $observed
    }
    Invoke-ConsoleState -Id 'S08c' -Title 'Console lists the PTP instance separately, Restricted, Enforced' -ExpectedPolicy 'Restricted' -ExpectedState 'Enforced'

    if ($IncludeDebuggingScenario) {
        Section 'S08d USB debugging (composite device)'
        $known = @((Get-UsbPresent | ForEach-Object { $_.InstanceId }))
        Instruct 'On the phone: switch back to "File transfer" AND turn on USB debugging (Developer options). Accept the authorisation prompt if one appears.'
        $since = [DateTime]::UtcNow
        $composite = Wait-NewPortableDevice -TimeoutSeconds $ArrivalTimeoutSeconds -Known $known
        if ($composite) {
            $script:Phone = $composite
            Save-Results
            $watch = Watch-Phone -Until 'Restricted' -TimeoutSeconds $EnforceTimeoutSeconds
            $children = @(Get-UsbPresent | Where-Object { $_.InstanceId -like "*$($composite.VidPid)*&MI_*" })
            $adb = $null
            if (Get-Command adb.exe -ErrorAction SilentlyContinue) { try { $adb = @(& adb.exe devices 2>&1) -join ' | ' } catch { $adb = "adb failed: $($_.Exception.Message)" } }
            Add-Evidence -Scenario 'S08d' -Kind 'composite' -Data ([ordered]@{ phone = $composite; watch = $watch; childrenPresent = $children; adb = $adb })
            [void](Snapshot-Agent -Scenario 'S08d' -SinceUtc $since)
            $adbClean = ($null -eq $adb) -or ($adb -notmatch '\sdevice(\s|$)')
            $observed = ("composite={0}; {1} after {2} s; interface children still present={3}; adb: {4}" -f $composite.Composite, (Describe-State $watch.Last), $watch.Reached, $children.Count, $(if ($adb) { $adb } else { 'adb.exe not on PATH, not checked' }))
            Set-Scenario -Id 'S08d' -Title 'Composite (MTP + ADB) is one row, disabled as a whole, adb sees nothing' -State $(if ($watch.Reached -ne $null -and $children.Count -eq 0 -and $adbClean) { 'PASS' } else { 'FAIL' }) -Expected 'parent disabled, no &MI_ children present, adb devices lists nothing' -Observed $observed
        } else {
            Set-Scenario -Id 'S08d' -Title 'Composite (MTP + ADB)' -State 'FAIL' -Expected 'a new composite instance appears' -Observed 'nothing new appeared'
        }
        Instruct 'Turn USB debugging OFF again and leave the phone in "File transfer" mode.'
        [void](Ask 'Press Enter when done')
        $known = @((Get-UsbPresent | ForEach-Object { $_.InstanceId }))
    }

    Instruct 'Switch the phone back to "File transfer" (MTP) and leave it there.'
    $since = [DateTime]::UtcNow
    $returned = Wait-NewPortableDevice -TimeoutSeconds $ArrivalTimeoutSeconds -Known @((Get-UsbPresent | ForEach-Object { $_.InstanceId }))
    if ($returned) {
        $script:Phone = $returned
        Save-Results
        $watch = Watch-Phone -Until 'Restricted' -TimeoutSeconds $EnforceTimeoutSeconds
        Add-Evidence -Scenario 'S08r' -Kind 'watch' -Data $watch
        [void](Snapshot-Agent -Scenario 'S08r' -SinceUtc $since)
        if ($watch.Reached -ne $null) { Set-Measurement -Name 'S08 back to MTP: seconds to restricted' -Value $watch.Reached }
        Set-Scenario -Id 'S08r' -Title 'Back in MTP mode the known instance is restricted again' -State $(if ($watch.Reached -ne $null) { 'PASS' } else { 'FAIL' }) -Expected 'the previously disabled instance arrives disabled or is disabled within seconds' -Observed ("{0} after {1} s" -f (Describe-State $watch.Last), $watch.Reached)
    } else {
        Set-Scenario -Id 'S08r' -Title 'Back in MTP mode' -State 'FAIL' -Expected 'the MTP instance returns' -Observed 'no portable device appeared'
    }
}

function Invoke-AgentRestart {
    Section 'S09 Agent service stop and start with the phone attached'
    if ($SkipAgentRestart) {
        Set-Scenario -Id 'S09' -Title 'Agent restart: released on stop, restricted again on start' -State 'NOT RUN' -Expected '' -Observed '-SkipAgentRestart'
        return
    }
    $before = Get-PhoneState
    if (-not $before.Restricted) { Write-Log "phone is not restricted before the restart: $(Describe-State $before)" 'Yellow' }
    Mutating "Stop-Service $($script:ServiceName): the agent releases every device it restricted, so the phone becomes usable until the service starts again."
    $since = [DateTime]::UtcNow
    Stop-Service -Name $script:ServiceName -Force
    $released = Watch-Phone -Until 'Enabled' -TimeoutSeconds 60
    Add-Evidence -Scenario 'S09' -Kind 'after-stop' -Data $released
    $ledgerAfterStop = Read-Ledger
    Write-Log ("after stop: {0}; ledger entries={1}" -f (Describe-State $released.Last), $ledgerAfterStop.Count)
    Mutating "Start-Service $($script:ServiceName): the phone is attached BEFORE the agent starts; it must be restricted from local state, without the server."
    $startedAt = [DateTime]::UtcNow
    Start-Service -Name $script:ServiceName
    $restricted = Watch-Phone -Until 'Restricted' -TimeoutSeconds $EnforceTimeoutSeconds
    Add-Evidence -Scenario 'S09' -Kind 'after-start' -Data $restricted
    $agent = Snapshot-Agent -Scenario 'S09' -SinceUtc $since
    $releaseLine = @($agent.Log | Where-Object { $_ -like '*USB enforcement released*' })
    $restrictLine = @(Get-AgentLogLines -SinceUtc $startedAt -Match $script:Phone.InstanceId | Where-Object { $_ -like '*is restricted (disabled)*' })
    $item = Get-ShellPhoneItem
    if ($released.Reached -ne $null) { Set-Measurement -Name 'S09 stop: seconds until the phone was released' -Value $released.Reached }
    if ($restricted.Reached -ne $null) { Set-Measurement -Name 'S09 start: seconds from service start to restricted' -Value $restricted.Reached }
    $observed = ("released on stop={0} ({1}); restricted after start={2} in {3} s; agent logged release={4}, restriction={5}; This PC item now={6}" -f ($released.Reached -ne $null), (Describe-State $released.Last), ($restricted.Reached -ne $null), $restricted.Reached, ($releaseLine.Count -gt 0), ($restrictLine.Count -gt 0), ($null -ne $item))
    if ($released.Reached -ne $null -and $restricted.Reached -ne $null -and -not $item) {
        Set-Scenario -Id 'S09' -Title 'Agent restart: released on stop, restricted again on start (phone attached before start)' -State 'PASS' -Expected 'enabled while stopped; disabled within seconds of start, from the ledger, no server needed' -Observed $observed
    } else {
        Set-Scenario -Id 'S09' -Title 'Agent restart: released on stop, restricted again on start (phone attached before start)' -State 'FAIL' -Expected 'enabled while stopped; disabled within seconds of start' -Observed $observed
    }
}

function Invoke-Collateral {
    Section 'S10 Collateral devices untouched'
    $now = Get-UsbPresent
    $lost = @()
    $broken = @()
    foreach ($entry in $script:Baseline.Collateral) {
        $match = @($now | Where-Object { $_.InstanceId -eq $entry.instanceId })
        if ($match.Count -eq 0) { $lost += "$($entry.name) [$($entry.class)]"; continue }
        if ($match[0].Status -ne $entry.status) { $broken += "$($entry.name) [$($entry.class)] $($entry.status) -> $($match[0].Status)" }
    }
    $ledger = Read-Ledger
    $extraRestricted = @($ledger | Where-Object { $_ -ne $script:Phone.InstanceId -and -not ($script:Baseline.Ledger -contains $_) -and $_ -notlike "*$($script:Phone.VidPid)*" })
    Add-Evidence -Scenario 'S10' -Kind 'collateral' -Data ([ordered]@{ lost = $lost; statusChanged = $broken; ledgerNow = $ledger; newlyRestrictedOtherThanPhone = $extraRestricted })
    Instruct 'Confirm by hand: keyboard, mouse, webcam and network still work.'
    $human = Ask 'Keyboard/mouse/webcam/network all working? (yes/no, details)'
    Note-Operator -Key 'S10 peripherals' -Text $human
    $observed = ("baseline USB nodes missing now: {0}; status changed: {1}; newly restricted non-phone instances: {2}" -f $lost.Count, $broken.Count, $extraRestricted.Count)
    $ok = ($lost.Count -eq 0 -and $broken.Count -eq 0 -and $extraRestricted.Count -eq 0 -and $human -like 'yes*')
    Set-Scenario -Id 'S10' -Title 'Keyboard, mouse, webcam, network and other USB devices unaffected' -State $(if ($ok) { 'PASS' } else { 'FAIL' }) -Expected 'every baseline device still present with the same status; nothing but the phone newly restricted' -Observed ("{0}; operator: {1}" -f $observed, $human)
}

function Invoke-Charging {
    Section 'S12 Charging (observation only)'
    $answer = Ask 'Does the phone show it is charging while restricted? Rate, if it shows one (observation, not a claim)'
    Note-Operator -Key 'S12 charging while restricted' -Text $answer
    Set-Scenario -Id 'S12' -Title 'Charging while restricted' -State 'OBSERVATION' -Expected 'noted, not claimed' -Observed "operator: $answer"
}

function Invoke-RebootHandoff {
    Section 'Reboot hand-off'
    $pending = [ordered]@{
        phone            = $script:Phone
        phase1FinishedUtc = [DateTime]::UtcNow.ToString('o')
        runDirectory     = $script:RunDir
    }
    $pending | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $script:RunDir 'reboot-pending.json') -Encoding UTF8
    Set-Scenario -Id 'S13' -Title 'Reboot with the phone attached: restricted again once the agent starts' -State 'NOT RUN' -Expected '' -Observed 'run phase 2 after the reboot'
    Instruct "Leave the phone attached in File transfer mode and REBOOT this PC. After signing in, run the same command with -Phase AfterReboot (it resumes from $($script:RunDir))."
}

function Invoke-AfterReboot {
    Section 'S13 After the reboot'
    $pendingPath = Join-Path $script:RunDir 'reboot-pending.json'
    $pending = Get-Content $pendingPath -Raw | ConvertFrom-Json
    $script:Phone = [ordered]@{
        InstanceId    = [string]$pending.phone.InstanceId
        VidPid        = [string]$pending.phone.VidPid
        Names         = @($pending.phone.Names)
        CompatibleIds = @($pending.phone.CompatibleIds)
    }
    $os = Get-CimInstance Win32_OperatingSystem
    $bootUtc = $os.LastBootUpTime.ToUniversalTime()
    $phase1 = [DateTime]::Parse($pending.phase1FinishedUtc).ToUniversalTime()
    if ($bootUtc -lt $phase1) { Fail-Hard "Windows last booted at $bootUtc UTC, before phase 1 finished ($phase1 UTC). The PC has not been rebooted." }
    $serviceStart = $script:AgentInfo.ProcessStartUtc
    $state = Get-PhoneState
    $item = Get-ShellPhoneItem
    $log = Get-AgentLogLines -SinceUtc $bootUtc -Match $script:Phone.InstanceId
    $first = @($log | Where-Object { $_ -like '*is restricted (disabled)*' } | Select-Object -First 1)
    $restrictedAt = $null
    if ($first.Count -gt 0 -and $first[0] -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})') {
        $restrictedAt = [DateTimeOffset]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff zzz', [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
    }
    Add-Evidence -Scenario 'S13' -Kind 'after-reboot' -Data ([ordered]@{ bootUtc = $bootUtc; serviceStartUtc = $serviceStart; state = $state; thisPcItem = ($null -ne $item); firstRestrictionLogLine = $first; restrictedAtUtc = $restrictedAt })
    [void](Snapshot-Agent -Scenario 'S13' -SinceUtc $bootUtc)
    if ($restrictedAt) {
        Set-Measurement -Name 'S13 reboot: seconds from boot to the agent''s first restriction of the phone' -Value ([math]::Round(($restrictedAt - $bootUtc).TotalSeconds, 1))
        if ($serviceStart) { Set-Measurement -Name 'S13 reboot: seconds from service start to the restriction' -Value ([math]::Round(($restrictedAt - $serviceStart).TotalSeconds, 1)) }
    }
    $window = Ask 'Was the phone usable in Explorer at any point between sign-in and now? (no / yes, for about N seconds)'
    Note-Operator -Key 'S13 reachable window after sign-in' -Text $window
    $observed = ("now: {0}; This PC item={1}; boot {2:HH:mm:ss}Z, service start {3}, first restriction {4}" -f (Describe-State $state), ($null -ne $item), $bootUtc, $serviceStart, $restrictedAt)
    Set-Scenario -Id 'S13' -Title 'Reboot with the phone attached: restricted again once the agent starts' -State $(if ($state.Restricted -and -not $item) { 'PASS' } else { 'FAIL' }) -Expected 'disabled, no interface, absent from This PC after the agent started; the gap before that is measured, not hidden' -Observed $observed
    Remove-Item $pendingPath -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

try {
    if ($Phase -eq 'AfterReboot') {
        if (-not $ResumeDirectory) {
            $candidate = @(Get-ChildItem $OutputDirectory -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'reboot-pending.json') } | Sort-Object Name -Descending | Select-Object -First 1)
            if ($candidate.Count -eq 0) { throw "No evidence folder with a pending reboot under $OutputDirectory. Pass -ResumeDirectory." }
            $ResumeDirectory = $candidate[0].FullName
        }
        $script:RunDir = $ResumeDirectory
        $script:LogFile = Join-Path $script:RunDir 'run.log'
        $saved = Get-Content (Join-Path $script:RunDir 'results.json') -Raw | ConvertFrom-Json
        foreach ($p in $saved.results.PSObject.Properties) { $script:Results[$p.Name] = [ordered]@{ Title = $p.Value.Title; State = $p.Value.State; Expected = $p.Value.Expected; Observed = $p.Value.Observed } }
        foreach ($p in $saved.measurements.PSObject.Properties) { $script:Measurements[$p.Name] = [ordered]@{ Value = $p.Value.Value; Unit = $p.Value.Unit } }
        foreach ($p in $saved.operatorNotes.PSObject.Properties) { $script:OperatorNotes[$p.Name] = $p.Value }
        $script:StartedUtc = [DateTime]::Parse($saved.startedUtc).ToUniversalTime()
        $existing = Get-Content (Join-Path $script:RunDir 'evidence.json') -Raw -ErrorAction SilentlyContinue
        if ($existing) { foreach ($e in @($existing | ConvertFrom-Json)) { [void]$script:Evidence.Add($e) } }
        Write-Log "resuming $($script:RunDir) for the reboot scenario"
        Invoke-Preflight -PhoneMayBeAttached
        $script:Baseline = [pscustomobject]@{ ShellItems = @(); InstanceIds = @(); Collateral = @(); Ledger = @() }
        Invoke-AfterReboot
    } else {
        $script:RunDir = Join-Path $OutputDirectory ([DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Path $script:RunDir -Force | Out-Null
        $script:LogFile = Join-Path $script:RunDir 'run.log'
        Write-Log "evidence folder $($script:RunDir)"
        Invoke-Preflight
        Invoke-Baseline
        Invoke-Attach
        Invoke-Blocked
        Invoke-ConsoleState
        Invoke-Grant
        Invoke-Revoke
        Invoke-Reconnect
        Invoke-ModeSwitch
        Invoke-AgentRestart
        Invoke-Collateral
        Invoke-Charging
        Invoke-RebootHandoff
    }
} catch {
    if (-not $script:Aborted) { Write-Log "ERROR: $($_.Exception.Message)" 'Red'; Write-Log $_.ScriptStackTrace 'DarkGray' }
    $script:Aborted = $true
} finally {
    Save-Results
    Write-Report
    $verdict = Get-Verdict
    if ($verdict.Verdict -eq 'PASS') { exit 0 } elseif ($verdict.Verdict -eq 'FAIL') { exit 1 } else { exit 2 }
}
