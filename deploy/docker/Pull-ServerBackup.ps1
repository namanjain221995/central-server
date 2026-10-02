<#
.SYNOPSIS
    Copies the newest encrypted backup bundle off the server and checks it.

.DESCRIPTION
    backup.sh leaves its bundles on the server itself, which does not help when
    the server is the thing that died. Run this from an administrator's PC (a
    scheduled task once a day is enough) to keep copies somewhere else.

    The bundle is encrypted with the backup passphrase, so a copied file on its
    own opens nothing. Keep the passphrase in a password manager, never next to
    the bundles.

    Copies only bundles not already present, verifies each against the .sha256
    the server wrote, and keeps the newest -Keep locally.

.EXAMPLE
    .\Pull-ServerBackup.ps1 -SshHost 192.0.2.10 -SshUser deploy `
        -KeyPath "$HOME\.ssh\id_ed25519" -Destination D:\EppBackups
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SshHost,
    [Parameter(Mandatory)] [string] $SshUser,
    [Parameter(Mandatory)] [string] $KeyPath,
    [Parameter(Mandatory)] [string] $Destination,
    [string] $RemoteDir = '/var/backups/endpoint-platform/bundles',
    [int] $Keep = 30
)

$ErrorActionPreference = 'Stop'
$target = "$SshUser@$SshHost"
$sshArgs = @('-i', $KeyPath, '-o', 'BatchMode=yes')

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$listing = & ssh @sshArgs $target "ls -1 $RemoteDir/epp-backup-*.tar.gpg 2>/dev/null"
if ($LASTEXITCODE -ne 0 -or -not $listing) {
    throw "No bundles found in $RemoteDir on $SshHost. Has backup.sh run there?"
}

$copied = 0
foreach ($remote in @($listing)) {
    $name = Split-Path -Leaf $remote
    $local = Join-Path $Destination $name
    if (Test-Path $local) { continue }

    & scp @sshArgs -q "${target}:$remote" "${local}.partial"
    if ($LASTEXITCODE -ne 0) { throw "scp of $name failed" }
    & scp @sshArgs -q "${target}:$remote.sha256" "$local.sha256"
    if ($LASTEXITCODE -ne 0) { throw "scp of $name.sha256 failed" }

    $expected = ((Get-Content "$local.sha256" -Raw).Trim() -split '\s+')[0]
    $actual = (Get-FileHash "${local}.partial" -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) {
        Remove-Item "${local}.partial", "$local.sha256" -Force
        throw "$name arrived damaged (checksum mismatch); not kept"
    }
    Move-Item "${local}.partial" $local
    Write-Host "copied and verified $name"
    $copied++
}
if ($copied -eq 0) { Write-Host 'already up to date' }

Get-ChildItem $Destination -Filter 'epp-backup-*.tar.gpg' |
    Sort-Object Name -Descending |
    Select-Object -Skip $Keep |
    ForEach-Object {
        Remove-Item $_.FullName, "$($_.FullName).sha256" -Force -ErrorAction SilentlyContinue
        Write-Host "pruned $($_.Name)"
    }

$newest = Get-ChildItem $Destination -Filter 'epp-backup-*.tar.gpg' | Sort-Object Name -Descending | Select-Object -First 1
# Age from the UTC stamp in the name: the local file time is when it was copied.
$stamp = [regex]::Match($newest.Name, '\d{8}T\d{6}Z').Value
$taken = [datetime]::ParseExact($stamp, "yyyyMMdd'T'HHmmss'Z'", [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)
$age = [datetime]::UtcNow - $taken
Write-Host "newest local bundle: $($newest.Name)"
if ($age.TotalHours -gt 36) {
    Write-Warning "the newest bundle is $([int]$age.TotalHours) hours old. Check the backup timer on the server."
}
