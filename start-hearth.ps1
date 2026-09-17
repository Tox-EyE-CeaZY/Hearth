<#
.SYNOPSIS
    Builds Hearth and (re)starts it from a fresh copy of the build.

.DESCRIPTION
    1. Builds Hearth.sln (Release unless -Configuration Debug).
    2. Asks a running Hearth to exit cleanly ("Hearth.exe --quit"), which saves
       the layout and puts the Explorer desktop icons back. If it doesn't exit
       in time it is killed and the icons are restored from here.
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

Add-Type -Namespace HearthScript -Name Shell -MemberDefinition @'
[DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);

// SHELLDLL_DefView lives under Progman or one of the WorkerW windows.
public static void RestoreDesktopIcons()
{
    IntPtr view = FindWindowEx(FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
    IntPtr worker = IntPtr.Zero;
    while (view == IntPtr.Zero)
    {
        worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
        if (worker == IntPtr.Zero) return;
        view = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
    }
    ShowWindow(view, 8); // SW_SHOWNA
}
'@

function Stop-Hearth {
    $running = @(Get-Process -Name Hearth -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }

    Write-Step "Asking Hearth to exit"
    # Any Hearth.exe works as the messenger; prefer the running one's own.
    $messenger = @($running[0].Path, (Join-Path $runDir 'Hearth.exe'), (Join-Path $buildDir 'Hearth.exe')) |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($messenger) { Start-Process -FilePath $messenger -ArgumentList '--quit' -Wait }

    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @(Get-Process -Name Hearth -ErrorAction SilentlyContinue).Count -gt 0) {
        Start-Sleep -Milliseconds 250
    }

    $left = @(Get-Process -Name Hearth -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) {
        # Builds from before --quit existed ignore it.
        Write-Step "Hearth didn't exit; killing it"
        $left | Stop-Process -Force
        $left | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
        [HearthScript.Shell]::RestoreDesktopIcons()
    }
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
