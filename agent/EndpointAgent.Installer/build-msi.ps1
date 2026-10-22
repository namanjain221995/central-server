<#
.SYNOPSIS
    Builds EndpointPlatformAgent-<version>-x64.msi.

.DESCRIPTION
    Publishes the agent self-contained (so the endpoint needs no .NET runtime)
    and packages it with WiX into an MSI that installs the Windows Service.

    The MSI contains NO secret. It is a common, universal binary: identical for
    every machine and every customer, so it can be cached, mirrored and signed
    once. Enrolment happens after installation and is gated by an administrator
    approving the machine in the dashboard.

    The management server URL is baked in at build time. That is not a secret -
    it is a public endpoint - and baking it keeps the double-click install free
    of any configuration step.

.PARAMETER ServerBaseUrl
    Management server the installed agent will contact. Must be https:// for any
    real deployment; http:// is permitted only for localhost development.

.PARAMETER Version
    Product version, e.g. 1.0.0. Defaults to the agent's VersionPrefix.

.EXAMPLE
    .\build-msi.ps1 -ServerBaseUrl https://65.2.37.254.nip.io
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerBaseUrl,

    [string]$Version = '1.22.1',

    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$repoRoot     = Split-Path -Parent (Split-Path -Parent $installerDir)
$publishDir   = Join-Path $repoRoot 'build\agent-publish'
$outputDir    = Join-Path $repoRoot 'build\installer'

# --- refuse to build an installer that would downgrade transport security ----
# An agent that talks plain HTTP to a real server would send its device
# credential in clear. Localhost is exempt because that is the development loop.
if ($ServerBaseUrl -notmatch '^https://') {
    $isLocal = $ServerBaseUrl -match '^http://(localhost|127\.0\.0\.1)(:\d+)?(/|$)'
    if (-not $isLocal) {
        throw "ServerBaseUrl must be https:// (got '$ServerBaseUrl'). Plain HTTP is only allowed for localhost."
    }
    Write-Host "WARNING: building a development installer against $ServerBaseUrl" -ForegroundColor Yellow
}

# --- a development installer must never look like a release -----------------
# A package built against localhost was once uploaded as a release and rolled
# out to production endpoints. One built for this machine is named so that it
# cannot be mistaken for a release, whichever scheme it uses.
$serverHost = ([Uri]$ServerBaseUrl).Host
$isDevelopmentBuild = $serverHost -eq 'localhost' -or $serverHost -like '*.localhost' `
    -or $serverHost -like '127.*' -or $serverHost -eq '[::1]'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be three-part, e.g. 1.0.0 (got '$Version')."
}

# --- publish ----------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host "Publishing agent (self-contained, win-x64)..." -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    & dotnet publish (Join-Path $repoRoot 'agent\EndpointAgent.Service\EndpointAgent.Service.csproj') `
        -c Release -p:PublishAgent=true -p:Version=$Version -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    # The session notifier and the Company Portal go into the SAME folder, with
    # the same runtime identifier and self-contained runtime, so they reuse the
    # runtime files the service just published rather than shipping a second set.
    # The portal is WinForms, so it adds the desktop runtime's assemblies on top;
    # the core runtime is still shared. AgentBinaries.wxs globs the folder, so
    # both are packaged without being listed.
    Write-Host "Publishing session notifier into the same folder..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $repoRoot 'agent\EndpointAgent.SessionNotice\EndpointAgent.SessionNotice.csproj') `
        -c Release -p:PublishAgent=true -p:Version=$Version -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (session notifier) failed with exit code $LASTEXITCODE." }

    Write-Host "Publishing Company Portal into the same folder..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $repoRoot 'agent\EndpointAgent.CompanyPortal\EndpointAgent.CompanyPortal.csproj') `
        -c Release -p:PublishAgent=true -p:Version=$Version -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (Company Portal) failed with exit code $LASTEXITCODE." }

    # The lock overlay joins the same folder, same runtime identifier and
    # self-contained runtime, so it reuses the runtime the service published and the
    # desktop runtime the portal added rather than shipping a third set.
    # AgentBinaries.wxs globs the folder, so it is packaged without being listed.
    Write-Host "Publishing lock overlay into the same folder..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $repoRoot 'agent\EndpointAgent.LockScreen\EndpointAgent.LockScreen.csproj') `
        -c Release -p:PublishAgent=true -p:Version=$Version -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (lock overlay) failed with exit code $LASTEXITCODE." }

    # The live-chat window joins the same folder, same runtime identifier and
    # self-contained runtime, so it reuses the runtime the service published and the
    # desktop runtime the portal/overlay added rather than shipping a fourth set.
    # AgentBinaries.wxs globs the folder, so it is packaged without being listed.
    Write-Host "Publishing live-chat window into the same folder..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $repoRoot 'agent\EndpointAgent.LiveChat\EndpointAgent.LiveChat.csproj') `
        -c Release -p:PublishAgent=true -p:Version=$Version -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (live-chat window) failed with exit code $LASTEXITCODE." }
}

$exe = Join-Path $publishDir 'EndpointAgent.Service.exe'
if (-not (Test-Path $exe)) { throw "Publish output is missing $exe." }

$notifier = Join-Path $publishDir 'EndpointAgent.SessionNotice.exe'
if (-not (Test-Path $notifier)) { throw "Publish output is missing $notifier." }

$portal = Join-Path $publishDir 'EndpointAgent.CompanyPortal.exe'
if (-not (Test-Path $portal)) { throw "Publish output is missing $portal." }

$overlay = Join-Path $publishDir 'EndpointAgent.LockScreen.exe'
if (-not (Test-Path $overlay)) { throw "Publish output is missing $overlay." }

$chat = Join-Path $publishDir 'EndpointAgent.LiveChat.exe'
if (-not (Test-Path $chat)) { throw "Publish output is missing $chat." }
Write-Host ("  published {0} files, {1:N0} MB" -f `
    (Get-ChildItem $publishDir -Recurse -File).Count, `
    ((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

# --- machine configuration written into the package -------------------------
# Generated from a template so the URL exists in exactly one place per build.
$configTemplate = Get-Content (Join-Path $installerDir 'agent.config.json.template') -Raw
$configPath     = Join-Path $installerDir 'agent.config.json'
$configTemplate.Replace('__SERVER_BASE_URL__', $ServerBaseUrl) | Set-Content -Path $configPath -Encoding UTF8 -NoNewline

# --- WiX --------------------------------------------------------------------
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "The WiX CLI is not installed. Install it with: dotnet tool install --global wix"
}

# Extensions MUST be pinned to the WiX version. An unversioned
# `wix extension add` resolves to the newest package on nuget.org -- currently
# the v7 line -- which WiX 5 then refuses with "Could not find expected package
# root folder wixext5". A developer machine with the right extensions already
# cached never sees this; a clean runner fails every time.
$wixVersion = (& wix --version) -replace '^\s*(\d+\.\d+\.\d+).*$', '$1'
if ($wixVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Could not determine the WiX CLI version (got '$wixVersion')." }

# `wix extension list` returns one line per extension. Joining first matters:
# in PowerShell `$array -notmatch 'x'` FILTERS the array rather than returning a
# boolean, so the naive check is always truthy and re-adds an existing extension.
$installedExtensions = (& wix extension list --global 2>$null) -join "`n"
foreach ($ext in @('WixToolset.Util.wixext', 'WixToolset.UI.wixext')) {
    $pinned = "$ext/$wixVersion"
    # Match name AND version: an extension present at the wrong major version is
    # worse than one that is absent, because the build fails later and vaguer.
    # `wix extension add` takes name/version while `list` prints "name version",
    # so the check tolerates either separator -- otherwise every build re-adds
    # an extension that is already correctly installed.
    $installedPattern = [regex]::Escape($ext) + '[\s/]+' + [regex]::Escape($wixVersion)
    if ($installedExtensions -notmatch $installedPattern) {
        Write-Host "Adding WiX extension $pinned..." -ForegroundColor Cyan
        & wix extension add --global $pinned
        if ($LASTEXITCODE -ne 0) { throw "Failed to add WiX extension $pinned." }
    }
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$msiName = if ($isDevelopmentBuild) {
    "EndpointPlatformAgent-$Version-x64.LOCALHOST-DO-NOT-INSTALL.msi"
} else {
    "EndpointPlatformAgent-$Version-x64.msi"
}
$msiPath = Join-Path $outputDir $msiName

Write-Host "Building MSI..." -ForegroundColor Cyan
& wix build `
    (Join-Path $installerDir 'Package.wxs') `
    (Join-Path $installerDir 'AgentBinaries.wxs') `
    -arch x64 `
    -d "AgentPublishDir=$publishDir" `
    -d "ProductVersion=$Version" `
    -d "ServerBaseUrl=$ServerBaseUrl" `
    -ext WixToolset.Util.wixext `
    -ext WixToolset.UI.wixext `
    -bindpath $installerDir `
    -o $msiPath

if ($LASTEXITCODE -ne 0) { throw "wix build failed with exit code $LASTEXITCODE." }

$msi = Get-Item $msiPath
Write-Host ''
Write-Host "MSI: $($msi.FullName)" -ForegroundColor Green
Write-Host ("Size: {0:N1} MB" -f ($msi.Length / 1MB)) -ForegroundColor Green
Write-Host ''

# --- signing ----------------------------------------------------------------
# Deliberately not signed here. There is no organisation certificate, and a
# self-signed one would create the appearance of trust without the substance:
# Windows would still warn, and users would learn to click through warnings.
# To sign a real release, after this script:
#   signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
#            /f <cert.pfx> /p <password> "$msiPath"
Write-Host 'NOTE: this MSI is UNSIGNED. SmartScreen and UAC will warn on first run.' -ForegroundColor Yellow
Write-Host '      See docs/agent-installation.md for signing a release build.' -ForegroundColor Yellow
