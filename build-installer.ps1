<#
.SYNOPSIS
    Builds the Hearth installer: artifacts\installer\Hearth-Setup-<version>.exe

.DESCRIPTION
    1. Publishes Hearth self-contained for win-x64 (no .NET install needed on
       the target PC) into artifacts\publish.
    2. Compiles installer\Hearth.iss with Inno Setup 6.

    The version is <Version> from Directory.Build.props (Main.Sub.ActionSet),
    stamped with the build time as Main.Sub.ActionSet.yyyymmddhhmm.

    Needs Inno Setup 6: winget install JRSoftware.InnoSetup --scope user
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'
$output = Join-Path $root 'artifacts\installer'
$icon = Join-Path $root 'assets\Hearth.ico'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$full = "$version.$(Get-Date -Format yyyyMMddHHmm)"

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | ForEach-Object Source)
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup --scope user" }

if (-not $SkipPublish) {
    Write-Step "Publishing Hearth $full (self-contained, win-x64)"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    & dotnet publish (Join-Path $root 'src\Hearth.App\Hearth.App.csproj') `
        -c Release -r win-x64 --self-contained true -p:Platform=x64 `
        -p:PublishReadyToRun=true `
        "-p:InformationalVersion=$full" -p:IncludeSourceRevisionInInformationalVersion=false `
        -o $publish -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
}

Write-Step "Compiling the installer"
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $iscc /Qp "/DAppVersion=$version" "/DAppFullVersion=$full" "/DPublishDir=$publish" `
    "/DOutputDir=$output" "/DIconFile=$icon" (Join-Path $root 'installer\Hearth.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }

$setup = Join-Path $output "Hearth-Setup-$version.exe"
$size = [Math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "Built $setup ($size MB)" -ForegroundColor Green
