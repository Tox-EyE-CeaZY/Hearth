<#
.SYNOPSIS
    Builds Hearth and (re)starts it from a fresh copy of the build.

.DESCRIPTION
    1. Builds Hearth.sln (Release unless -Configuration Debug).
    2. Asks a running Hearth to exit cleanly (installer/quit-hearth.ps1), which
       saves the layout and puts the Explorer desktop icons back. If it doesn't
       exit in time it is killed and the icons are restored.
    3. Copies the build into artifacts\run\ and starts Hearth from there.

    Hearth runs from artifacts\run\ rather than bin\ so the running copy never
    locks the build output, and a failed build leaves the running copy alone.

.EXAMPLE
    .\start-hearth.ps1                 # build Release, restart
    .\start-hearth.ps1 -Configuration Debug
    .\start-hearth.ps1 -NoBuild        # restart the last build
    .\start-hearth.ps1 -Stop           # just quit Hearth
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$buildDir = Join-Path $root "src\Hearth.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0"
$runDir = Join-Path $root 'artifacts\run'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Stop-Hearth {
    $quit = Join-Path $root 'installer\quit-hearth.ps1'
    & $quit -Messengers @((Join-Path $runDir 'Hearth.exe'), (Join-Path $buildDir 'Hearth.exe'))
}

if ($Stop) {
    Stop-Hearth
    return
}

if (-not $NoBuild) {
    Write-Step "Building ($Configuration)"
    & dotnet build (Join-Path $root 'Hearth.sln') -c $Configuration -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed; the running Hearth was left alone." }
}

if (-not (Test-Path (Join-Path $buildDir 'Hearth.exe'))) {
    throw "No build at $buildDir. Run without -NoBuild."
}

Stop-Hearth

Write-Step "Copying build to $runDir"
if (Test-Path $runDir) { Remove-Item $runDir -Recurse -Force }
New-Item -ItemType Directory -Path $runDir | Out-Null
Copy-Item -Path (Join-Path $buildDir '*') -Destination $runDir -Recurse

Write-Step "Starting Hearth"
$process = Start-Process -FilePath (Join-Path $runDir 'Hearth.exe') -WorkingDirectory $runDir -PassThru
Start-Sleep -Seconds 3
if ($process.HasExited) {
    $log = Join-Path $env:LOCALAPPDATA 'Hearth\hearth.log'
    throw "Hearth exited right away (code $($process.ExitCode)). Check $log."
}
Write-Host "Hearth is running (pid $($process.Id))." -ForegroundColor Green
