<#
.SYNOPSIS
    Deploys the endpoint platform to an Ubuntu machine over SSH, from Windows.

.DESCRIPTION
    Runs on a Windows machine with nothing but ssh, scp and git on PATH. The
    Ubuntu target needs no preparation at all: this script packages the WORKING
    TREE (tracked plus untracked, non-ignored files), uploads it, and drives the
    bash scripts that live next to this file:

        install.sh          which in turn runs, all on the Ubuntu machine:
          host-prep.sh      PostgreSQL 17, Redis, .NET 10 SDK, Node 24,
                            nginx, certbot, service accounts, swap      (sudo)
          gen-env.sh        secrets once; per-service environment files (sudo)
          setup-postgres.sh the database and its two roles              (sudo)
          setup-redis.sh    loopback-only, password-protected Redis     (sudo)
          setup-nginx.sh    reverse proxy + Let's Encrypt certificate   (sudo)
          deploy.sh         dotnet publish, npm build, install, migrate,
                            start, health-check, roll back on failure   (user)
        bootstrap-admin.sh  the first Super Administrator               (sudo)

    Nothing runs in a container. The platform ends up as three .NET processes
    under systemd behind nginx.

    NOTHING ON THIS WINDOWS MACHINE IS UPLOADED except the source tree, and
    infra/.env is never part of it: git archive honours .gitignore. The Ubuntu
    machine generates its own secrets into /etc/endpoint-platform/secrets.env on
    first install and keeps them across every redeploy.

    The FIRST run takes 10-25 minutes, most of it apt and the .NET SDK download.
    A redeploy with -SkipHostPrep -SkipCert takes about three minutes.

    Windows PowerShell 5.1 only: no &&, no ternary, no ?? / ?. operators, native
    tools invoked through the call operator with argument arrays, and
    $LASTEXITCODE checked after every ssh/scp/git call because
    $ErrorActionPreference = 'Stop' does not see native exit codes.

.PARAMETER SshHost
    IP address or DNS name the SSH connection goes to. For a machine on your own
    network this is usually its LAN address, e.g. 192.168.1.50.

.PARAMETER SshUser
    SSH login. Must be able to sudo WITHOUT a password prompt, because the steps
    run non-interactively. Ubuntu cloud images' "ubuntu" user already can; on a
    machine you installed yourself, add one line with `sudo visudo`:
        <user> ALL=(ALL) NOPASSWD:ALL
    Default: ubuntu.

.PARAMETER KeyPath
    Private key file for the SSH login. Omit it to authenticate with a password
    instead (you will be prompted by ssh on each step).

.PARAMETER PublicHostName
    The name browsers and Windows agents will use, WITHOUT a scheme. It becomes
    PUBLIC_ORIGIN (https://<name>) on the host and the subject of the TLS
    certificate.

    Plain Let's Encrypt (HTTP-01, the default) needs a public DNS name resolving
    to this machine from the internet with port 80 reachable. A machine on a
    private LAN cannot satisfy that - use -Cloudflare instead, which proves
    control of the NAME over DNS and needs no inbound connectivity at all.

    Plain HTTP is never a working option: the session cookie is __Host-/Secure
    and browsers discard it, so nobody can sign in.

.PARAMETER CertbotEmail
    Let's Encrypt account e-mail (expiry notices). Required only for the default
    HTTP-01 path - -Cloudflare, -SelfSigned and -SkipCert all do without it.

.PARAMETER AdminEmail
    When given, bootstrap the first Super Administrator with this e-mail.

.PARAMETER RemoteDir
    Directory under the SSH user's home that holds the source tree. Default: app.

.PARAMETER SkipHostPrep
    Skip host-prep.sh. Safe on a machine that has already been prepared; running
    it again is also safe and is how a .NET patch release gets picked up.

.PARAMETER Cloudflare
    Get a real Let's Encrypt certificate by DNS-01 through Cloudflare, instead of
    HTTP-01. Works with NO inbound internet, so it is the right choice for a
    machine on a private LAN whose domain is hosted at Cloudflare.

    Needs an API token ON THE UBUNTU MACHINE at
    /etc/endpoint-platform/cloudflare.ini (Zone:DNS:Edit + Zone:Zone:Read, scoped
    to the one zone). The token is never uploaded from this machine; this script
    only checks that the file exists before starting a long install.

    Strongly preferred over -SelfSigned: a Release build of the Windows agent
    validates the server certificate against the machine's trusted roots and has
    no override, so a publicly trusted certificate means nothing has to be
    installed on any managed endpoint.

.PARAMETER SelfSigned
    Issue TLS from a local CA. The fallback for a private network with no
    suitable domain, and it costs a CA root deployed to EVERY managed PC before
    any agent can connect. Prefer -Cloudflare.

.PARAMETER SkipCert
    Skip setup-nginx.sh entirely. Leaves whatever certificate is already there
    untouched - the right choice for a routine redeploy.

.PARAMETER SkipBootstrap
    Do not run bootstrap-admin.sh even if -AdminEmail is given.

.PARAMETER GenerateAdminPassword
    Let the Ubuntu machine generate a 24-character random administrator password
    and print it ONCE, instead of prompting for one here.

.EXAMPLE
    .\infra\ubuntu\Deploy-Ubuntu.ps1 -SshHost 192.168.1.50 -SshUser ops `
        -KeyPath C:\keys\epp.pem -PublicHostName epp.example.com `
        -CertbotEmail ops@example.com -AdminEmail admin@example.com `
        -GenerateAdminPassword

.EXAMPLE
    # A machine on a private LAN, domain hosted at Cloudflare. No inbound
    # internet, no port forwarding, and a certificate every endpoint trusts.
    .\infra\ubuntu\Deploy-Ubuntu.ps1 -SshHost 192.168.1.50 -SshUser ops `
        -KeyPath C:\keys\epp.pem -PublicHostName epp.example.com -Cloudflare `
        -AdminEmail admin@example.com -GenerateAdminPassword

.EXAMPLE
    # Redeploy after a code change (machine already prepared, certificate in place):
    .\infra\ubuntu\Deploy-Ubuntu.ps1 -SshHost 192.168.1.50 -SshUser ops `
        -KeyPath C:\keys\epp.pem -PublicHostName epp.example.com -SkipHostPrep -SkipCert
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SshHost,

    [string]$SshUser = 'ubuntu',

    [string]$KeyPath,

    [Parameter(Mandatory = $true)]
    [string]$PublicHostName,

    [string]$CertbotEmail,

    [string]$AdminEmail,

    [string]$RemoteDir = 'app',

    [switch]$SkipHostPrep,
    [switch]$SkipCert,
    [switch]$Cloudflare,
    [switch]$SelfSigned,
    [switch]$SkipBootstrap,
    [switch]$GenerateAdminPassword
)

$ErrorActionPreference = 'Stop'

# --- helpers -----------------------------------------------------------------

$script:stepNumber = 0
function Write-Banner {
    param([string]$Text)
    $script:stepNumber = $script:stepNumber + 1
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Cyan
    Write-Host ('  [{0}] {1}' -f $script:stepNumber, $Text) -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor Cyan
}

function Write-Warn {
    param([string]$Text)
    Write-Host ''
    Write-Host ('  WARNING: ' + $Text) -ForegroundColor Yellow
    Write-Host ''
}

function Fail {
    param([string]$Text)
    Write-Host ''
    Write-Host ('  FAILED: ' + $Text) -ForegroundColor Red
    Write-Host ''
    exit 1
}

# Common ssh/scp options. accept-new records an unknown host key on first contact
# but still refuses a CHANGED one. BatchMode is only added when a key was given:
# with password authentication ssh must be allowed to prompt.
$sshOpts = @(
    '-o', 'StrictHostKeyChecking=accept-new',
    '-o', 'ConnectTimeout=20',
    '-o', 'ServerAliveInterval=30'
)
if (-not [string]::IsNullOrWhiteSpace($KeyPath)) {
    $sshOpts = @('-i', $KeyPath, '-o', 'BatchMode=yes') + $sshOpts
}
$target = '{0}@{1}' -f $SshUser, $SshHost

# Windows PowerShell 5.1 hands an argument to a native program by wrapping it in
# double quotes when it contains spaces, but it does NOT escape double quotes
# INSIDE the argument. ssh.exe's C runtime then treats each inner quote as the
# end of the quoted region, drops it, and splits the rest on spaces. This applies
# the C runtime's own rules: every inner quote becomes \", backslashes directly
# before a quote are doubled, and a trailing backslash is doubled so it cannot
# eat the closing quote.
function ConvertTo-NativeArgument {
    param([string]$Text)
    $escaped = [regex]::Replace($Text, '(\\*)"', [System.Text.RegularExpressions.MatchEvaluator]{
        param($m) ($m.Groups[1].Value * 2) + '\"'
    })
    $escaped = [regex]::Replace($escaped, '(\\+)$', [System.Text.RegularExpressions.MatchEvaluator]{
        param($m) $m.Groups[1].Value * 2
    })
    return $escaped
}

# Runs a remote command, streams its output to the console, returns the exit
# code. Remote command strings are built from SINGLE-quoted templates so that
# PowerShell never expands the $variables meant for the remote shell.
function Invoke-Remote {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [switch]$AllowFailure
    )
    $nativeArg = ConvertTo-NativeArgument -Text $Command
    & ssh @sshOpts $target $nativeArg | Out-Host
    $code = $LASTEXITCODE
    if (-not $AllowFailure -and $code -ne 0) {
        Fail ('remote command exited with code {0}. Command: {1}' -f $code, $Command)
    }
    return $code
}

# Substitutes __TOKEN__ placeholders in a single-quoted remote template. Values
# are validated against strict character sets before they get here, so nothing
# can break out of the remote quoting.
function Expand-Template {
    param([string]$Template, [hashtable]$Values)
    $result = $Template
    foreach ($key in $Values.Keys) {
        $result = $result.Replace(('__{0}__' -f $key), [string]$Values[$key])
    }
    return $result
}

# --- 1. preflight ------------------------------------------------------------

Write-Banner 'Preflight'

foreach ($tool in @('ssh', 'scp', 'git')) {
    $found = Get-Command $tool -ErrorAction SilentlyContinue
    if ($null -eq $found) {
        Fail ('"{0}" is not on PATH. Windows 10/11 ship OpenSSH as an optional feature (Settings > System > Optional features); git is from git-scm.com.' -f $tool)
    }
    Write-Host ('  {0,-5} {1}' -f $tool, $found.Source)
}

if (-not [string]::IsNullOrWhiteSpace($KeyPath)) {
    if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf)) {
        Fail ('SSH key not found: {0}' -f $KeyPath)
    }
    $KeyPath = (Resolve-Path -LiteralPath $KeyPath).Path
}
else {
    Write-Host '  key   (none given - ssh will ask for a password on each step)'
}

# Strict character sets: every one of these ends up inside a remote command
# line or a URL, and a stray quote or space would corrupt the command.
if ($PublicHostName -notmatch '^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$') {
    Fail ('-PublicHostName "{0}" is not a plain DNS host name (no scheme, no port, no path).' -f $PublicHostName)
}
if ($RemoteDir -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*$') {
    Fail ('-RemoteDir "{0}" must be a relative path under the SSH home, e.g. "app" or "srv/epp".' -f $RemoteDir)
}
# Exactly one TLS mode, mirroring install.sh. -SkipCert wins over nothing: it
# means "do not touch nginx at all", so pairing it with a mode is a contradiction
# rather than a harmless redundancy.
$certModes = @()
if ($Cloudflare) { $certModes += '-Cloudflare' }
if ($SelfSigned) { $certModes += '-SelfSigned' }
if ($SkipCert)   { $certModes += '-SkipCert' }
if ($certModes.Count -gt 1) {
    Fail ('Choose one of {0}; they are mutually exclusive.' -f ($certModes -join ', '))
}

$emailPattern = '^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$'

# Only HTTP-01 needs a contact address. DNS-01 registers without one and a local
# CA has nobody to notify, which is why install.sh asks for it on the same
# condition (install.sh: --email is required unless --skip-cert, --cloudflare or
# --self-signed).
if (-not $SkipCert -and -not $Cloudflare -and -not $SelfSigned) {
    if ([string]::IsNullOrWhiteSpace($CertbotEmail)) {
        Fail '-CertbotEmail is required unless -SkipCert, -Cloudflare or -SelfSigned is given (HTTP-01 needs a contact address; DNS-01 and a local CA do not).'
    }
    if ($CertbotEmail -notmatch $emailPattern) {
        Fail ('-CertbotEmail "{0}" does not look like an e-mail address.' -f $CertbotEmail)
    }
}

$doBootstrap = $false
if (-not [string]::IsNullOrWhiteSpace($AdminEmail) -and -not $SkipBootstrap) {
    if ($AdminEmail -notmatch $emailPattern) {
        Fail ('-AdminEmail "{0}" does not look like an e-mail address.' -f $AdminEmail)
    }
    $doBootstrap = $true
}

# infra/ubuntu -> infra -> repository root.
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'EndpointPlatform.slnx') -PathType Leaf)) {
    Fail ('Repository root not recognised at {0} (EndpointPlatform.slnx missing).' -f $repoRoot)
}
Write-Host ('  repo  {0}' -f $repoRoot)

$headSha = & git -C $repoRoot rev-parse --short HEAD
if ($LASTEXITCODE -ne 0) { Fail 'git rev-parse HEAD failed; is this a git working tree?' }
$headSha = ([string]$headSha).Trim()

# The count is printed, not enforced: the whole point of packaging the working
# tree is that unpushed and uncommitted work ships. The operator just needs to
# know that it did.
$statusLines = @(& git -C $repoRoot status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0) { Fail 'git status failed.' }
$dirtyCount = @($statusLines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
$deployedLabel = $headSha
if ($dirtyCount -gt 0) { $deployedLabel = $headSha + '-dirty' }
Write-Host ('  HEAD  {0}   uncommitted/untracked files: {1}' -f $headSha, $dirtyCount)
if ($dirtyCount -gt 0) {
    Write-Host '        (the working tree is what ships, including those files; ignored files are excluded)'
}

# DNS sanity. Certbot's HTTP-01 challenge needs the public name to resolve to
# this machine from the internet, so a mismatch is worth saying out loud now
# rather than as a certbot failure ten minutes in. Warn, never block: the
# laptop's resolver may simply not see a record that was just created.
if (-not $SkipCert) {
    $resolver = Get-Command Resolve-DnsName -ErrorAction SilentlyContinue
    if ($null -ne $resolver) {
        $resolved = @()
        try {
            $answers = Resolve-DnsName -Name $PublicHostName -Type A -ErrorAction Stop
            foreach ($a in $answers) {
                if ($a.PSObject.Properties['IPAddress'] -and $a.IPAddress) { $resolved += [string]$a.IPAddress }
            }
        }
        catch {
            $resolved = @()
        }
        if ($resolved.Count -eq 0) {
            Write-Warn ('{0} does not resolve from this machine. Let''s Encrypt needs a PUBLIC A record for it, and port 80 reachable from the internet. If this Ubuntu machine is on a private network, stop and re-run with -SkipCert, then install a certificate from your own CA (infra/ubuntu/README.md).' -f $PublicHostName)
        }
        else {
            Write-Host ('  DNS   {0} -> {1}' -f $PublicHostName, ($resolved -join ', '))
            if ($resolved -notcontains $SshHost) {
                Write-Warn ('{0} resolves to {1}, but this deploy targets {2}. That is fine if {2} is a LAN address and the name points at the public address of the same machine; it is a problem if they are different machines - certbot would then fail.' -f $PublicHostName, ($resolved -join ', '), $SshHost)
            }
        }
    }
}

# --- 2. SSH connectivity -----------------------------------------------------

Write-Banner ('SSH connectivity: {0}' -f $target)

$probe = @(& ssh @sshOpts $target 'echo EPP_SSH_OK; . /etc/os-release 2>/dev/null && echo "$PRETTY_NAME"; uname -m; id -un')
if ($LASTEXITCODE -ne 0 -or (($probe -join "`n") -notmatch 'EPP_SSH_OK')) {
    Fail ('cannot reach {0}. Check the address, that sshd is running and reachable on port 22, the user name, and the key or password.' -f $target)
}
$probe | ForEach-Object { Write-Host ('  ' + $_) }

if (($probe -join "`n") -notmatch 'Ubuntu') {
    Write-Warn 'The target does not report itself as Ubuntu. host-prep.sh targets Ubuntu 22.04 / 24.04 and will refuse to run on anything else.'
}

# Every sudo step runs without a TTY, so the login must be NOPASSWD. Find that
# out now, not half way through a 20-minute install.
& ssh @sshOpts $target 'sudo -n true' | Out-Host
if ($LASTEXITCODE -ne 0) {
    Fail ('{0} cannot sudo without a password. On the Ubuntu machine run "sudo visudo" and add:  {1} ALL=(ALL) NOPASSWD:ALL' -f $target, $SshUser)
}
Write-Host '  sudo  passwordless: ok'

# -Cloudflare reads an API token that lives ONLY on the Ubuntu machine; nothing
# is ever sent from here. Checked at this point rather than in the preflight
# because it is the first thing needing a proven SSH session, and checked at all
# so a missing token fails in seconds instead of after a full build.
if ($Cloudflare) {
    & ssh @sshOpts $target 'sudo test -s /etc/endpoint-platform/cloudflare.ini' | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Fail (@'
-Cloudflare needs an API token on the Ubuntu machine at
/etc/endpoint-platform/cloudflare.ini

Create it THERE (it is never uploaded from this machine):

    sudo install -d -m 0750 /etc/endpoint-platform
    sudo tee /etc/endpoint-platform/cloudflare.ini >/dev/null <<INI
dns_cloudflare_api_token = YOUR_TOKEN_HERE
INI
    sudo chmod 600 /etc/endpoint-platform/cloudflare.ini

Make the token at https://dash.cloudflare.com/profile/api-tokens with the
"Edit zone DNS" template, scoped to that one zone, granting Zone:DNS:Edit AND
Zone:Zone:Read. Do not use a Global API Key.
'@)
    }
    Write-Host '  cloudflare.ini: present'
}

# --- 3. package and upload ---------------------------------------------------

Write-Banner 'Package the working tree and upload'

# A TEMPORARY git index: read-tree HEAD + add -A builds an index of the working
# tree (tracked and untracked, .gitignore honoured) without touching the real
# index, so this never stages anything for the operator's next commit.
# git archive then applies .gitattributes, which is what turns *.sh into LF.
$tmpDir = Join-Path ([IO.Path]::GetTempPath()) ('epp-deploy-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpDir | Out-Null
$tarPath = Join-Path $tmpDir 'src.tar'
$indexPath = Join-Path $tmpDir 'index'
$remoteTar = 'epp-src-upload.tar'

try {
    $env:GIT_INDEX_FILE = $indexPath
    try {
        & git -C $repoRoot read-tree HEAD | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail 'git read-tree HEAD failed.' }
        & git -C $repoRoot add -A | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail 'git add -A (temporary index) failed.' }
        $tree = & git -C $repoRoot write-tree
        if ($LASTEXITCODE -ne 0) { Fail 'git write-tree failed.' }
        $tree = ([string]$tree).Trim()
    }
    finally {
        Remove-Item Env:\GIT_INDEX_FILE -ErrorAction SilentlyContinue
    }

    & git -C $repoRoot archive --format=tar -o $tarPath $tree | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 'git archive failed.' }
    $tarSize = (Get-Item -LiteralPath $tarPath).Length
    Write-Host ('  tree  {0}   archive {1:N1} MB' -f $tree, ($tarSize / 1MB))

    & scp @sshOpts $tarPath ('{0}:{1}' -f $target, $remoteTar) | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 'scp upload failed.' }
}
finally {
    Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Replace the source tree outright. Nothing of value lives in it: the secrets are
# in /etc/endpoint-platform and the installed releases in /opt/endpoint-platform,
# both untouched by this.
$extractTemplate = @(
    'set -e',
    'D=$HOME/__RD__',
    'N=$HOME/__RD__.new',
    'T=$HOME/__TAR__',
    'rm -rf "$N" "$D.old"',
    'mkdir -p "$N"',
    'tar -xf "$T" -C "$N"',
    'printf "%s\n" __LABEL__ > "$N/.deployed-commit"',
    'if [ -d "$D" ]; then mv "$D" "$D.old"; fi',
    'mv "$N" "$D"',
    'sed -i "s/\r$//" "$D"/infra/ubuntu/*.sh "$D"/infra/gcp/*.sh',
    'chmod 0755 "$D"/infra/ubuntu/*.sh "$D"/infra/gcp/*.sh',
    'rm -rf "$D.old"',
    'rm -f "$T"',
    'echo "extracted to $D"'
) -join '; '
$extractCmd = Expand-Template -Template $extractTemplate -Values @{
    RD    = $RemoteDir
    TAR   = $remoteTar
    LABEL = $deployedLabel
}
[void](Invoke-Remote -Command $extractCmd)

$remoteKit = Expand-Template -Template '$HOME/__RD__/infra/ubuntu' -Values @{ RD = $RemoteDir }

# --- 4. install --------------------------------------------------------------
#
# install.sh orchestrates host-prep, gen-env, postgres, redis, nginx and deploy.
# The administrator is bootstrapped separately below, so that the prompted-
# password path can hand the password to ssh on STDIN.

Write-Banner 'Install (packages, database, Redis, TLS, build, start)'
if ($SkipHostPrep) { Write-Host '  -SkipHostPrep: packages and accounts are assumed to be in place' }
if ($SkipCert) {
    Write-Warn ('TLS step skipped. If this machine has no certificate yet, the platform is unusable: the session cookie is __Host-/Secure and browsers discard it over plain HTTP on any origin other than localhost, so NOBODY CAN SIGN IN at http://{0}. Release agents also reject an untrusted certificate.' -f $PublicHostName)
}
Write-Host '  The first run takes 10-25 minutes (apt, the .NET SDK, then the build).' -ForegroundColor DarkGray
Write-Host ''

$installArgs = '--host {0}' -f $PublicHostName

# One TLS mode, already proven mutually exclusive in the preflight. --email goes
# only to the HTTP-01 path: install.sh rejects the combination otherwise.
if ($Cloudflare) {
    $installArgs = $installArgs + ' --cloudflare'
} elseif ($SelfSigned) {
    $installArgs = $installArgs + ' --self-signed'
} elseif (-not $SkipCert) {
    $installArgs = $installArgs + (' --email {0}' -f $CertbotEmail)
}

if ($SkipHostPrep) { $installArgs = $installArgs + ' --skip-host-prep' }
if ($SkipCert) { $installArgs = $installArgs + ' --skip-cert' }

[void](Invoke-Remote -Command ('bash {0}/install.sh {1}' -f $remoteKit, $installArgs))

# --- 5. bootstrap administrator ---------------------------------------------

Write-Banner 'First administrator'
if (-not $doBootstrap) {
    if ($SkipBootstrap) { Write-Host '  skipped (-SkipBootstrap)' }
    else { Write-Host '  skipped (no -AdminEmail). Run again with -AdminEmail to create the first Super Administrator.' }
}
elseif ($GenerateAdminPassword) {
    # The Ubuntu machine generates and prints the password exactly once; it flows
    # through this console and nowhere else.
    $code = Invoke-Remote -Command ('sudo bash {0}/bootstrap-admin.sh {1} --generate' -f $remoteKit, $AdminEmail) -AllowFailure
    if ($code -ne 0) { Fail ('bootstrap-admin.sh exited with code {0}.' -f $code) }
}
else {
    # Prompt twice as SecureString, compare, then hand the plaintext to ssh via
    # STDIN only. It never becomes an argv element (visible in ps / audit logs
    # on both machines), a log line, or a file.
    $plain = $null
    $bstr = [IntPtr]::Zero
    $bstr2 = [IntPtr]::Zero
    $savedOutputEncoding = $OutputEncoding
    $savedConsoleEncoding = [Console]::OutputEncoding
    try {
        while ($true) {
            $secure1 = Read-Host -AsSecureString -Prompt ('  Password for {0} (12+ characters)' -f $AdminEmail)
            $secure2 = Read-Host -AsSecureString -Prompt '  Repeat password'
            $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure1)
            $bstr2 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure2)
            $p1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
            $p2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr2)
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr);  $bstr = [IntPtr]::Zero
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr2); $bstr2 = [IntPtr]::Zero
            if ($p1 -cne $p2) {
                Write-Host '  The two entries differ; try again.' -ForegroundColor Yellow
                continue
            }
            if ($p1.Length -lt 12) {
                Write-Host '  At least 12 characters are required; try again.' -ForegroundColor Yellow
                continue
            }
            if ($p1 -match '[\r\n]') {
                Write-Host '  Line breaks are not allowed in the password; try again.' -ForegroundColor Yellow
                continue
            }
            $plain = $p1
            $p1 = $null
            $p2 = $null
            break
        }

        # UTF-8 without BOM on the pipe: PowerShell encodes what it writes to a
        # native process's stdin with $OutputEncoding (the default is the OEM code
        # page, which would mangle any non-ASCII character). PowerShell ends the
        # line with CRLF; bootstrap-admin.sh strips the trailing CR.
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        $OutputEncoding = $utf8NoBom
        [Console]::OutputEncoding = $utf8NoBom

        $plain | & ssh @sshOpts $target ('sudo bash {0}/bootstrap-admin.sh {1}' -f $remoteKit, $AdminEmail) | Out-Host
        $code = $LASTEXITCODE
        if ($code -ne 0) { Fail ('bootstrap-admin.sh exited with code {0}.' -f $code) }
    }
    finally {
        if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        if ($bstr2 -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr2) }
        $plain = $null
        Remove-Variable -Name plain, p1, p2, secure1, secure2 -ErrorAction SilentlyContinue
        $OutputEncoding = $savedOutputEncoding
        [Console]::OutputEncoding = $savedConsoleEncoding
    }
}

# --- 6. summary --------------------------------------------------------------

Write-Banner 'Done'
$dashboardUrl = 'https://{0}' -f $PublicHostName
Write-Host ('  Dashboard        {0}' -f $dashboardUrl)
Write-Host ('  Health           {0}/api/health/ready' -f $dashboardUrl)
Write-Host ('  Deployed         {0} into ~/{1} on {2}' -f $deployedLabel, $RemoteDir, $target)
Write-Host ''
Write-Host '  Windows agent install (elevated, on each managed endpoint):'
Write-Host ('    msiexec /i EndpointPlatformAgent-<version>-x64.msi SERVERBASEURL={0}' -f $dashboardUrl)
Write-Host '    then approve the device in the dashboard under Enrollments.'
Write-Host ''
Write-Host '  Redeploy after a code change:'
$keyArgument = ''
if (-not [string]::IsNullOrWhiteSpace($KeyPath)) { $keyArgument = ' -KeyPath "{0}"' -f $KeyPath }
Write-Host ('    .\infra\ubuntu\Deploy-Ubuntu.ps1 -SshHost {0} -SshUser {1}{2} -PublicHostName {3} -SkipHostPrep -SkipCert' -f $SshHost, $SshUser, $keyArgument, $PublicHostName)
Write-Host ''
Write-Host '  Logs, on the Ubuntu machine:'
Write-Host ('    ssh{0} {1}' -f $keyArgument.Replace(' -KeyPath', ' -i'), $target)
Write-Host "    journalctl -u 'endpoint-platform-*' -f"
Write-Host ''
Write-Host '  Back up: /etc/endpoint-platform/secrets.env, a pg_dump of the database,'
Write-Host '           and /var/lib/endpoint-platform/packages. See infra/ubuntu/README.md.'
Write-Host ''
