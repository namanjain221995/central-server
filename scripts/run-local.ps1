<#
.SYNOPSIS
    Starts the whole platform locally: migrations, both APIs, and the dashboard.

.DESCRIPTION
    PostgreSQL and Redis are expected to be ALREADY RUNNING as locally installed
    services. Nothing here starts them, and nothing here runs in a container.
    See docs/development.md for installing them once.

    Reads every credential from infra/.env, so no secret is ever typed on a command
    line or baked into this file. Each service runs in its own window, which keeps
    their logs readable and lets you stop one without killing the rest.

    The Windows agent is deliberately NOT started by default. It needs administrator
    privilege to manage local accounts, and elevation should be a conscious act -
    see -WithAgent and the instructions this script prints at the end.

.EXAMPLE
    .\scripts\run-local.ps1
#>

[CmdletBinding()]
param(
    # Skip creating the database and its two roles when they already exist.
    [switch]$SkipDatabaseSetup,

    # Skip the migration job when the schema is known to be current.
    [switch]$SkipMigrations,

    # Also start the Windows agent, elevated. Raises one UAC prompt: managing local
    # accounts requires administrator privilege, and nothing here can or should
    # bypass that.
    [switch]$WithAgent
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# --- configuration ---------------------------------------------------------
$envFile = Join-Path $root 'infra\.env'
if (-not (Test-Path $envFile)) {
    throw "infra/.env not found. Copy infra/.env.example and fill in the values."
}

$cfg = @{}
Get-Content $envFile | Where-Object { $_ -match '^\s*[^#].*=' } | ForEach-Object {
    $pair = $_ -split '=', 2
    $cfg[$pair[0].Trim()] = $pair[1].Trim()
}

$pgHost    = if ($cfg['POSTGRES_HOST']) { $cfg['POSTGRES_HOST'] } else { 'localhost' }
$pgPort    = if ($cfg['POSTGRES_PORT']) { $cfg['POSTGRES_PORT'] } else { '5432' }
$pgDb      = $cfg['POSTGRES_DB']
$appUser   = $cfg['POSTGRES_APP_USER']
$appPass   = $cfg['POSTGRES_APP_PASSWORD']
$ownerUser = $cfg['POSTGRES_SUPERUSER']
$ownerPass = $cfg['POSTGRES_SUPERUSER_PASSWORD']
$redisHost = if ($cfg['REDIS_HOST']) { $cfg['REDIS_HOST'] } else { 'localhost' }
$redisPort = if ($cfg['REDIS_PORT']) { $cfg['REDIS_PORT'] } else { '6379' }
$redisPass = $cfg['REDIS_PASSWORD']
$secretKey = $cfg['SECRET_PROTECTION_KEY']
$escrowKey = $cfg['RECOVERY_ESCROW_KEY']
$escrowKeyVersion = if ($cfg['RECOVERY_ESCROW_KEY_VERSION']) { $cfg['RECOVERY_ESCROW_KEY_VERSION'] } else { '1' }

if (-not $secretKey) {
    # Both API processes must seal and unseal with the SAME key, so a per-process
    # fallback key would break local-account password delivery across hosts.
    throw "SECRET_PROTECTION_KEY is missing from infra/.env. Generate one with: [Convert]::ToBase64String((1..32|%{Get-Random -Max 256}))"
}

# --- backing services ------------------------------------------------------
# Checked, never started: they are ordinary local services with their own
# lifecycle, and a script that starts them would also have to own stopping them.
function Test-Listening {
    param([string]$TargetHost, [int]$Port)
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $async = $client.BeginConnect($TargetHost, $Port, $null, $null)
        $ok = $async.AsyncWaitHandle.WaitOne(2000)
        if ($ok) { $client.EndConnect($async) }
        return $ok
    }
    catch { return $false }
    finally { if ($client) { $client.Dispose() } }
}

Write-Host 'Checking PostgreSQL and Redis...' -ForegroundColor Cyan
foreach ($svc in @(
    @{ Name = 'PostgreSQL'; Target = $pgHost;    Port = [int]$pgPort },
    @{ Name = 'Redis';      Target = $redisHost; Port = [int]$redisPort }
)) {
    if (Test-Listening -TargetHost $svc.Target -Port $svc.Port) {
        Write-Host ("  {0,-12} listening on {1}:{2}" -f $svc.Name, $svc.Target, $svc.Port) -ForegroundColor Green
    }
    else {
        throw ("{0} is not listening on {1}:{2}. Start the service, or install it - see docs/development.md." -f `
            $svc.Name, $svc.Target, $svc.Port)
    }
}

# --- database and roles ----------------------------------------------------
# Same SQL the Ubuntu host runs, so local and deployed databases are created
# identically. Idempotent, so this is cheap on every run.
if (-not $SkipDatabaseSetup) {
    $psql = Get-Command psql -ErrorAction SilentlyContinue
    if (-not $psql) {
        Write-Host '  psql is not on PATH; skipping database setup.' -ForegroundColor Yellow
        Write-Host '  If the database and roles do not exist yet, see docs/development.md.' -ForegroundColor Yellow
    }
    else {
        Write-Host 'Creating the database and its two roles (idempotent)...' -ForegroundColor Cyan

        $superuser = if ($cfg['POSTGRES_ADMIN_USER']) { $cfg['POSTGRES_ADMIN_USER'] } else { 'postgres' }
        $adminPass = $cfg['POSTGRES_ADMIN_PASSWORD']
        if (-not $adminPass) {
            throw "POSTGRES_ADMIN_PASSWORD is missing from infra/.env. It is the password of the PostgreSQL superuser ('$superuser'), used only to create the database and roles."
        }

        # Values reach psql through the ENVIRONMENT and are read with \getenv, never
        # as arguments: argv is visible to every process on the machine.
        $env:PGPASSWORD              = $adminPass
        $env:EPP_PG_OWNER            = $ownerUser
        $env:EPP_PG_OWNER_PASSWORD   = $ownerPass
        $env:EPP_PG_APP              = $appUser
        $env:EPP_PG_APP_PASSWORD     = $appPass
        $env:EPP_PG_DB               = $pgDb
        try {
            $script = @(
                '\getenv owner EPP_PG_OWNER'
                '\getenv ownerpw EPP_PG_OWNER_PASSWORD'
                '\getenv app EPP_PG_APP'
                '\getenv apppw EPP_PG_APP_PASSWORD'
                '\getenv db EPP_PG_DB'
                (Get-Content (Join-Path $root 'infra\postgres\setup-database.sql') -Raw)
            ) -join "`n"

            $script | & psql -X -q -v ON_ERROR_STOP=1 -h $pgHost -p $pgPort -U $superuser -d postgres
            if ($LASTEXITCODE -ne 0) { throw "psql exited with code $LASTEXITCODE while creating the database and roles." }
            Write-Host '  database and roles are ready.' -ForegroundColor Green
        }
        finally {
            foreach ($name in 'PGPASSWORD', 'EPP_PG_OWNER', 'EPP_PG_OWNER_PASSWORD', 'EPP_PG_APP', 'EPP_PG_APP_PASSWORD', 'EPP_PG_DB') {
                Remove-Item ("Env:\" + $name) -ErrorAction SilentlyContinue
            }
        }
    }
}

# --- migrations ------------------------------------------------------------
if (-not $SkipMigrations) {
    Write-Host 'Applying migrations + seeding (owner role)...' -ForegroundColor Cyan
    $env:ENDPOINTPLATFORM_Database__ConnectionString =
        "Host=$pgHost;Port=$pgPort;Database=$pgDb;Username=$ownerUser;Password=$ownerPass"
    $env:ENDPOINTPLATFORM_Database__RuntimeRoleName = $appUser
    dotnet run --project server\Migrations\EndpointPlatform.Migrations.csproj | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The migration job exited with code $LASTEXITCODE." }
    Remove-Item Env:\ENDPOINTPLATFORM_Database__ConnectionString
    Remove-Item Env:\ENDPOINTPLATFORM_Database__RuntimeRoleName
}

# --- services --------------------------------------------------------------
# The APIs run under the RESTRICTED role: they never need DDL, and running them
# as the owner would quietly undo the privilege split the audit trail relies on.
$sharedEnv = @{
    ENDPOINTPLATFORM_Database__ConnectionString =
        "Host=$pgHost;Port=$pgPort;Database=$pgDb;Username=$appUser;Password=$appPass"
    ENDPOINTPLATFORM_Redis__ConnectionString    = "${redisHost}:$redisPort,password=$redisPass"
    # Identical in both, deliberately: the Admin API seals an ephemeral secret
    # and the Agent API redeems it. Different values fail at redemption, not at
    # startup, with a misleading Redis-shaped error.
    ENDPOINTPLATFORM_SecretProtection__Key      = $secretKey
    ENDPOINTPLATFORM_PackageStorage__Directory  = (Join-Path $root '.package-content')
    ASPNETCORE_ENVIRONMENT                      = 'Development'
}

# The two hosts get DIFFERENT environments, and that difference is a security
# boundary rather than a convenience:
#
#   RecoveryEscrow:Key decrypts escrowed BitLocker recovery passwords. The Admin
#   API validates it on start and REFUSES TO BOOT without it. The Agent API -
#   reachable by every managed endpoint - must never hold it, and
#   AgentApiKeyBoundaryGuard fails that host at startup if it ever appears.
#
# Handing both hosts one hashtable would therefore break the Agent API as surely
# as omitting it breaks the Admin API. The deployed host enforces the same split
# through separate systemd environment files; see infra/ubuntu/gen-env.sh.
if (-not $escrowKey) {
    throw "RECOVERY_ESCROW_KEY is missing from infra/.env. The Admin API validates it on startup and refuses to run without it. Generate one with: `$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); `$b = New-Object byte[] 32; `$rng.GetBytes(`$b); [Convert]::ToBase64String(`$b)"
}

# Generated here rather than read from infra/.env when absent, unlike the escrow
# key. Losing a LOCAL TOTP key costs a re-enrolment against a throwaway database;
# losing the escrow key would silently make escrowed recovery passwords
# undecryptable, so that one is required rather than invented. On a deployed host
# both are persisted by gen-env.sh.
$mfaKey = $cfg['MFA_TOTP_KEY']
if (-not $mfaKey) {
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] 32
    $rng.GetBytes($bytes)
    $mfaKey = [Convert]::ToBase64String($bytes)
    Write-Host '  MFA_TOTP_KEY not in infra/.env; generated one for this run.' -ForegroundColor DarkGray
    Write-Host '  Authenticator enrolments will not survive a change to it.' -ForegroundColor DarkGray
}

$adminEnv = $sharedEnv.Clone()
$adminEnv['ENDPOINTPLATFORM_RecoveryEscrow__Key'] = $escrowKey
$adminEnv['ENDPOINTPLATFORM_RecoveryEscrow__KeyVersion'] = $escrowKeyVersion

# Admin API ONLY. AgentApiKeyBoundaryGuard fails the Agent API at startup if this
# reaches it: that process is reachable by every managed endpoint, and holding
# this key would let it derive a valid second factor for any administrator.
$adminEnv['ENDPOINTPLATFORM_Mfa__TotpKey'] = $mfaKey

$agentEnv = $sharedEnv.Clone()

function Start-Service-Window {
    param([string]$Title, [string]$Command, [hashtable]$EnvVars)

    # The values are set on THIS process and inherited by the child, rather than
    # written into its -Command string. A command line is readable by every
    # account on the machine (Get-CimInstance Win32_Process), and these carry the
    # database password, the Redis password and the keys that seal account
    # secrets and escrowed recovery passwords. The child inherits at creation, so
    # removing them immediately afterwards does not race it.
    foreach ($name in $EnvVars.Keys) {
        Set-Item -Path ("Env:\" + $name) -Value $EnvVars[$name]
    }
    try {
        Start-Process powershell -ArgumentList @(
            '-NoExit', '-Command',
            "`$Host.UI.RawUI.WindowTitle='$Title'; Set-Location '$root'; $Command"
        ) | Out-Null
    }
    finally {
        foreach ($name in $EnvVars.Keys) {
            Remove-Item -Path ("Env:\" + $name) -ErrorAction SilentlyContinue
        }
    }
}

Write-Host 'Starting Admin API (5080)...' -ForegroundColor Cyan
Start-Service-Window 'Admin API :5080' 'dotnet run --project server\Api\EndpointPlatform.Api.csproj' $adminEnv

Write-Host 'Starting Agent API (5081)...' -ForegroundColor Cyan
Start-Service-Window 'Agent API :5081' 'dotnet run --project server\AgentApi\EndpointPlatform.AgentApi.csproj' $agentEnv

Write-Host 'Starting dashboard (5173)...' -ForegroundColor Cyan
Start-Service-Window 'Dashboard :5173' 'npm run dev --prefix dashboard' @{}

# --- windows agent (elevated) ----------------------------------------------
# Started via -Verb RunAs rather than by elevating this whole script: only the
# agent needs administrator privilege, and the APIs deliberately do not run with
# it. Declining the UAC prompt leaves the rest of the stack running.
if ($WithAgent) {
    Write-Host 'Starting Windows agent (elevated - approve the UAC prompt)...' -ForegroundColor Cyan

    $agentCommand =
        "`$Host.UI.RawUI.WindowTitle='Windows Agent (elevated)'; " +
        "Set-Location '$root'; " +
        "`$env:ENDPOINTAGENT_Agent__ServerBaseUrl='http://localhost:5081'; " +
        'dotnet run --project agent\EndpointAgent.Service\EndpointAgent.Service.csproj'

    try {
        Start-Process powershell -Verb RunAs -ArgumentList '-NoExit', '-Command', $agentCommand | Out-Null
    }
    catch {
        Write-Host '  UAC declined - the agent is not running.' -ForegroundColor Yellow
        Write-Host '  Everything else is still up; local account tasks will queue until an agent checks in.' -ForegroundColor Yellow
    }
}

# --- readiness -------------------------------------------------------------
Write-Host ''
Write-Host 'Waiting for services...' -ForegroundColor Cyan
foreach ($svc in @(
    @{ Name = 'Admin API'; Url = 'http://localhost:5080/health/ready' },
    @{ Name = 'Agent API'; Url = 'http://localhost:5081/health/ready' },
    @{ Name = 'Dashboard'; Url = 'http://localhost:5173' }
)) {
    $ok = $false
    for ($i = 0; $i -lt 40; $i++) {
        try {
            $null = Invoke-WebRequest -Uri $svc.Url -UseBasicParsing -TimeoutSec 3
            $ok = $true; break
        } catch { Start-Sleep -Seconds 2 }
    }
    $colour = if ($ok) { 'Green' } else { 'Red' }
    $status = if ($ok) { 'ready' } else { 'NOT READY - check its window' }
    Write-Host ("  {0,-12} {1}" -f $svc.Name, $status) -ForegroundColor $colour
}

Write-Host ''
Write-Host 'Dashboard : http://localhost:5173' -ForegroundColor Green
Write-Host 'Swagger   : http://localhost:5080/swagger' -ForegroundColor Green
Write-Host ''
if ($WithAgent) {
    $agent = Get-Process -Name EndpointAgent.Service -ErrorAction SilentlyContinue
    if ($agent) {
        Write-Host ("Windows agent : running (PID {0}, elevated)" -f $agent.Id) -ForegroundColor Green
    }
    else {
        Write-Host 'Windows agent : still starting - check its window for the first heartbeat.' -ForegroundColor Yellow
    }
}
else {
    Write-Host 'The Windows agent is NOT running.' -ForegroundColor Yellow
    Write-Host 'Local-account management needs it. Re-run with -WithAgent, or start it' -ForegroundColor Yellow
    Write-Host 'yourself from an ELEVATED PowerShell window:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "  cd $root" -ForegroundColor DarkGray
    Write-Host "  `$env:ENDPOINTAGENT_Agent__ServerBaseUrl='http://localhost:5081'" -ForegroundColor DarkGray
    Write-Host '  dotnet run --project agent\EndpointAgent.Service\EndpointAgent.Service.csproj' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'Stop everything with: .\scripts\stop-local.ps1' -ForegroundColor DarkGray
