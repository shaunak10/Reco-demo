<#
.SYNOPSIS
  Starts the ReconAI demo: the local Python AI service and the web app, waits until both really answer,
  then opens the site in your browser.

.EXAMPLE
  .\run-demo.ps1            # start (first run builds the app: allow about a minute) and open http://localhost:5080
  .\run-demo.ps1 -Stop      # stop both services
  .\run-demo.ps1 -NoBrowser # start without opening a browser

Needs (already set up on this machine in %USERPROFILE%\.reconai):
  * the .NET 10 SDK      -> %USERPROFILE%\.reconai\dotnet   (or `dotnet` on PATH)
  * the Python venv      -> %USERPROFILE%\.reconai\venv     (created automatically from ai\requirements.txt if missing)
Logs are written to the logs\ folder next to this script.
#>
[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$NoBrowser,
    [int]$WebPort = 5080,
    [int]$AiPort = 8765
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$logs = Join-Path $here 'logs'
$stateFile = Join-Path $logs 'run-state.json'

function Say($text) { Write-Host $text }
function Fail($text) { Write-Host ""; Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }
function Quote($s) { '"' + $s + '"' }
function Port-Owner($port) { Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1 }
function Kill-Tree($processId) { if ($processId) { & taskkill.exe /PID $processId /T /F 2>&1 | Out-Null } }
function Tail($file, $n = 15) { if (Test-Path $file) { Get-Content $file -Tail $n } }
function Is-Up($url) { try { (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { $false } }

# ------------------------------------------------------------------ stop
if ($Stop) {
    if (-not (Test-Path $stateFile)) { Say "Nothing recorded as running."; exit 0 }
    $state = Get-Content $stateFile -Raw | ConvertFrom-Json
    foreach ($processId in @($state.webPid, $state.aiPid)) { Kill-Tree $processId }
    if ($state.mappedDrive) { & subst.exe "$($state.mappedDrive):" /D 2>&1 | Out-Null }
    Remove-Item $stateFile -Force
    Say "Stopped the ReconAI web app and AI service."
    exit 0
}

# ------------------------------------------------------------------ already running?
if (Test-Path $stateFile) {
    $state = Get-Content $stateFile -Raw | ConvertFrom-Json
    if ((Is-Up "http://localhost:$($state.webPort)/Account/Login") -and (Is-Up "http://127.0.0.1:$($state.aiPort)/health")) {
        Say "ReconAI is already running at http://localhost:$($state.webPort)"
        if (-not $NoBrowser) { Start-Process "http://localhost:$($state.webPort)" }
        exit 0
    }
    Say "Found a stale record of an earlier run; cleaning it up."
    foreach ($processId in @($state.webPid, $state.aiPid)) { Kill-Tree $processId }
    if ($state.mappedDrive) { & subst.exe "$($state.mappedDrive):" /D 2>&1 | Out-Null }
    Remove-Item $stateFile -Force
}

# ------------------------------------------------------------------ tools
$dotnet = $null
$local = Join-Path $env:USERPROFILE '.reconai\dotnet\dotnet.exe'
if (Test-Path $local) {
    $dotnet = $local
    $env:DOTNET_ROOT = Split-Path $local
    $env:PATH = "$(Split-Path $local);$env:PATH"
} elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $dotnet = (Get-Command dotnet).Source
}
if (-not $dotnet) { Fail "The .NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again." }
$dotnetVersion = (& $dotnet --version).Trim()
if ([int]($dotnetVersion.Split('.')[0]) -lt 10) { Fail ".NET SDK $dotnetVersion found, but this project needs .NET 10 or newer." }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$py = Join-Path $env:USERPROFILE '.reconai\venv\Scripts\python.exe'
$requirements = Join-Path $here 'ai\requirements.txt'
if (-not (Test-Path $py)) {
    Say "Creating the Python environment (one time, needs internet)..."
    New-Item -ItemType Directory -Force (Split-Path (Split-Path $py)) | Out-Null
    $launcher = Get-Command py -ErrorAction SilentlyContinue
    if ($launcher) { & py -3.13 -m venv (Split-Path (Split-Path $py)) 2>$null; if (-not (Test-Path $py)) { & py -3 -m venv (Split-Path (Split-Path $py)) } }
    else { & python -m venv (Split-Path (Split-Path $py)) }
    if (-not (Test-Path $py)) { Fail "Could not create the Python environment. Install Python 3.11 or newer from https://www.python.org/downloads/ and run this again." }
    & $py -m pip install --disable-pip-version-check -r $requirements
}
& $py -c "import fastapi, uvicorn, numpy, scipy, rapidfuzz" 2>$null
if ($LASTEXITCODE -ne 0) {
    Say "Installing the Python packages (one time, needs internet)..."
    & $py -m pip install --disable-pip-version-check -r $requirements
}

# ------------------------------------------------------------------ very long folder paths break .NET builds on Windows (260-character limit)
$root = $here
$mappedDrive = $null
if ($here.Length -gt 100) {
    $free = @('R', 'S', 'T', 'U', 'V', 'W') | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -First 1
    if (-not $free) { Fail "This folder's path is very long ($($here.Length) characters) and no spare drive letter is free. Move the project to a shorter path such as C:\Projects\ReconAI." }
    & subst.exe "$($free):" $here | Out-Null
    $root = "$($free):\"
    $mappedDrive = $free
    Say "The folder path is long, so this run uses drive $($free): as a short alias. (Moving the project to a short path avoids this.)"
}
$web = Join-Path $root 'src\ReconAI.Web'
$ai = Join-Path $root 'ai'
New-Item -ItemType Directory -Force $logs | Out-Null

# ------------------------------------------------------------------ ports
foreach ($pair in @(@('web app', $WebPort), @('AI service', $AiPort))) {
    $owner = Port-Owner $pair[1]
    if ($owner) {
        $name = (Get-Process -Id $owner.OwningProcess -ErrorAction SilentlyContinue).ProcessName
        if ($mappedDrive) { & subst.exe "$($mappedDrive):" /D 2>&1 | Out-Null }
        Fail "Port $($pair[1]) (needed by the $($pair[0])) is already used by '$name' (process $($owner.OwningProcess)). Close it, or choose another port with -WebPort / -AiPort."
    }
}

# ------------------------------------------------------------------ start
Say "Starting the AI service on port $AiPort ..."
$env:PYTHONIOENCODING = 'utf-8'
$aiProc = Start-Process -FilePath $py -PassThru -WindowStyle Hidden -WorkingDirectory $ai `
    -ArgumentList @('-m', 'uvicorn', 'app.main:app', '--host', '127.0.0.1', '--port', $AiPort, '--app-dir', (Quote $ai)) `
    -RedirectStandardOutput (Join-Path $logs 'ai.out.log') -RedirectStandardError (Join-Path $logs 'ai.err.log')

Say "Starting the web app on port $WebPort (the first run compiles it: allow about a minute) ..."
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$webProc = Start-Process -FilePath $dotnet -PassThru -WindowStyle Hidden -WorkingDirectory $root `
    -ArgumentList @('run', '--project', (Quote $web), '--no-launch-profile', '--urls', "http://localhost:$WebPort", '--', '--Ai:BaseUrl', "http://127.0.0.1:$AiPort") `
    -RedirectStandardOutput (Join-Path $logs 'web.out.log') -RedirectStandardError (Join-Path $logs 'web.err.log')

@{ webPid = $webProc.Id; aiPid = $aiProc.Id; webPort = $WebPort; aiPort = $AiPort; mappedDrive = $mappedDrive; started = (Get-Date).ToString('s') } |
    ConvertTo-Json | Set-Content $stateFile

function Wait-Ready($label, $url, $proc, $errLog, $outLog, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Is-Up $url) { Say "  $label is ready."; return }
        if ($proc.HasExited) {
            Write-Host ""; Write-Host "The $label stopped unexpectedly. Last log lines:" -ForegroundColor Red
            Tail $errLog; Tail $outLog
            & $PSCommandPath -Stop | Out-Null
            Fail "$label failed to start. Full logs are in $logs"
        }
        Start-Sleep -Seconds 2
    }
    & $PSCommandPath -Stop | Out-Null
    Fail "$label did not answer within $seconds seconds. Logs are in $logs"
}

Wait-Ready 'AI service' "http://127.0.0.1:$AiPort/health" $aiProc (Join-Path $logs 'ai.err.log') (Join-Path $logs 'ai.out.log') 120
Wait-Ready 'Web app' "http://localhost:$WebPort/Account/Login" $webProc (Join-Path $logs 'web.err.log') (Join-Path $logs 'web.out.log') 300

Say ""
Say "ReconAI is running:  http://localhost:$WebPort"
Say "  Sign in with the 'Sign in as Operator' / 'Sign in as Company Admin' buttons on the login page."
Say "  AI service:  http://127.0.0.1:$AiPort/health      Logs: $logs"
Say "  To stop:     .\run-demo.ps1 -Stop   (or double-click stop-demo.cmd)"
if (-not $NoBrowser) { Start-Process "http://localhost:$WebPort" }
