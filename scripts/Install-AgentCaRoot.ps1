<#
.SYNOPSIS
    Trusts the Endpoint Platform local CA on this machine, so the agent can
    connect while the server is using a self-signed certificate.

.DESCRIPTION
    A Release build of the Windows agent validates the server certificate against
    the machine's trusted root store and has NO "accept any certificate" switch.
    While the server runs a self-signed certificate, every managed PC therefore
    needs this CA installed first, or the agent silently never enrols.

    Run this BEFORE installing the agent MSI.

    The certificate is checked against an expected fingerprint before anything is
    installed. That check is the point of the script: a trusted root can vouch for
    ANY site to this machine, so installing the wrong file is not a small mistake.
    Nothing is installed if the fingerprint does not match.

    Safe to run again — an already-trusted certificate is reported and skipped.

.PARAMETER Path
    The .crt file. Defaults to endpoint-platform-ca.crt in the current user's
    Downloads folder.

.PARAMETER Remove
    Remove the certificate instead of installing it. Use when the server moves to
    a publicly trusted certificate and this CA is no longer wanted.

.EXAMPLE
    # Normal use, in an ELEVATED PowerShell
    .\Install-AgentCaRoot.ps1

.EXAMPLE
    .\Install-AgentCaRoot.ps1 -Path D:\share\endpoint-platform-ca.crt

.EXAMPLE
    # Once the server has a real certificate
    .\Install-AgentCaRoot.ps1 -Remove
#>

[CmdletBinding()]
param(
    [string]$Path,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

# The CA this script is willing to install. SHA-1, because that is what Windows
# calls Thumbprint and matches on. Change it only alongside a new CA, and confirm
# the new value from the server itself - not from the file you were handed.
$ExpectedThumbprint = 'A7FC17625C0230235048EAA0B4FF6A16E8776DCD'
$ExpectedSubject    = 'CN=Endpoint Platform local CA'

function Fail([string]$message) {
    Write-Host ''
    Write-Host "FAILED: $message" -ForegroundColor Red
    exit 1
}

function Ok([string]$message) { Write-Host "  $message" -ForegroundColor Green }
function Info([string]$message) { Write-Host "  $message" }

Write-Host ''
Write-Host 'Endpoint Platform - trust the local CA' -ForegroundColor Cyan
Write-Host '--------------------------------------'

# --- elevation ---------------------------------------------------------------
# LocalMachine\Root is machine-wide and needs administrator. Checked first so the
# failure is one clear sentence rather than an access-denied deep in the import.
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'this must run as Administrator. Right-click PowerShell and choose "Run as administrator".'
}

# --- locate the file ---------------------------------------------------------
if (-not $Path) {
    $downloads = Join-Path $env:USERPROFILE 'Downloads'
    $Path = Join-Path $downloads 'endpoint-platform-ca.crt'

    if (-not (Test-Path -LiteralPath $Path)) {
        # Be forgiving about the name: browsers rename duplicates, and the file is
        # sometimes saved as ca.crt.
        $candidate = Get-ChildItem -LiteralPath $downloads -Filter '*.crt' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'endpoint|^ca\.crt$' } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($candidate) { $Path = $candidate.FullName }
    }
}

if (-not (Test-Path -LiteralPath $Path)) {
    Fail "certificate not found at $Path. Pass -Path with the full path to endpoint-platform-ca.crt."
}
Info "file: $Path"

# --- read and verify BEFORE trusting anything --------------------------------
try {
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Path)
} catch {
    Fail "that file is not a certificate this machine can read: $($_.Exception.Message)"
}

Info "subject: $($cert.Subject)"
Info "expires: $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Info "thumbprint: $($cert.Thumbprint)"

if ($cert.Thumbprint -ne $ExpectedThumbprint) {
    Fail (@"
this is NOT the expected certificate and nothing has been installed.

  expected thumbprint : $ExpectedThumbprint
  file thumbprint     : $($cert.Thumbprint)

A trusted root can vouch for any website to this machine, so a mismatch is
treated as a stop, not a warning. Get the file again from the server and check
with whoever runs the platform before retrying.
"@)
}
Ok 'fingerprint matches the expected CA'

if ($cert.Subject -ne $ExpectedSubject) {
    Fail "subject is '$($cert.Subject)', expected '$ExpectedSubject'."
}
if ($cert.Subject -ne $cert.Issuer) {
    Fail 'this certificate is not self-signed, so it is not the root. Install the CA, not the server certificate.'
}
if ($cert.NotAfter -lt (Get-Date)) {
    Fail "this CA expired on $($cert.NotAfter.ToString('yyyy-MM-dd')). Get a current one."
}

$store = 'Cert:\LocalMachine\Root'
$existing = Get-ChildItem $store | Where-Object { $_.Thumbprint -eq $ExpectedThumbprint }

# --- remove ------------------------------------------------------------------
if ($Remove) {
    if (-not $existing) {
        Info 'not present in the trusted root store; nothing to remove.'
        exit 0
    }
    $existing | Remove-Item -Force
    Ok 'removed from Trusted Root Certification Authorities'
    Write-Host ''
    Write-Host 'The agent will now reject this server unless it has a publicly trusted certificate.' -ForegroundColor Yellow
    exit 0
}

# --- install -----------------------------------------------------------------
if ($existing) {
    Ok 'already trusted on this machine - nothing to do'
} else {
    Import-Certificate -FilePath $Path -CertStoreLocation $store | Out-Null

    # Confirm from the store rather than assuming the import worked.
    $now = Get-ChildItem $store | Where-Object { $_.Thumbprint -eq $ExpectedThumbprint }
    if (-not $now) { Fail 'the import reported success but the certificate is not in the store.' }
    Ok 'installed into Trusted Root Certification Authorities (LocalMachine)'
}

Write-Host ''
Write-Host 'Done. This machine now trusts the platform certificate.' -ForegroundColor Green
Write-Host 'Next: install the agent MSI, then approve the device under Enrollments.'
Write-Host ''
