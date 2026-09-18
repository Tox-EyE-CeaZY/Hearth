<#
.SYNOPSIS
    Quits any running Hearth cleanly, so its layout is saved and the Explorer
    desktop icons come back.

.DESCRIPTION
    Sends "Hearth.exe --quit" (through the running copy's own exe, or any of
    -Messengers), waits up to -TimeoutSeconds, and only then kills it, showing
    the desktop icons again itself. Used by start-hearth.ps1 and by the
    installer and uninstaller.
#>
[CmdletBinding()]
param(
    [string[]]$Messengers = @(),
    [int]$TimeoutSeconds = 10
)

Add-Type -Namespace HearthQuit -Name Shell -MemberDefinition @'
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

$running = @(Get-Process -Name Hearth -ErrorAction SilentlyContinue)
if ($running.Count -eq 0) { return }

Write-Host "==> Asking Hearth to exit" -ForegroundColor Cyan
# Any Hearth.exe works as the messenger; prefer the running one's own.
$messenger = @(@($running | ForEach-Object { $_.Path }) + $Messengers) |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($messenger) { Start-Process -FilePath $messenger -ArgumentList '--quit' -Wait -WindowStyle Hidden }

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and @(Get-Process -Name Hearth -ErrorAction SilentlyContinue).Count -gt 0) {
    Start-Sleep -Milliseconds 250
}

$left = @(Get-Process -Name Hearth -ErrorAction SilentlyContinue)
if ($left.Count -gt 0) {
    # Builds from before --quit existed ignore it.
    Write-Host "==> Hearth didn't exit; killing it" -ForegroundColor Cyan
    $left | Stop-Process -Force
    $left | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    [HearthQuit.Shell]::RestoreDesktopIcons()
    # Tablet mode may have hidden the taskbar; the exe knows what to put back.
    if ($messenger) { Start-Process -FilePath $messenger -ArgumentList '--restore-shell' -Wait -WindowStyle Hidden }
}
