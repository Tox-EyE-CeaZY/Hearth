<#
.SYNOPSIS
    Checks widget work before it's kept. Run it after every widget change;
    it must end with "PASSED".

.DESCRIPTION
    Errors (fail the check):
      - files changed outside src/Hearth.App/Widgets (unless -AllowOutside)
      - invisible private-use characters, stray control characters or lone CRs in sources
      - a widget folder without README.md or a *Widget.cs file
      - a folder named after a common type, or a class named after its folder
      - a namespace that doesn't match the folder
      - a widget class that won't be discovered, a duplicate or badly formed Id
      - forbidden calls in changed widget folders (Process.Start, Environment.Exit, ...)
      - a Segoe Fluent Icons glyph that doesn't exist
      - a Release build with any error or warning
    A line that breaks a rule on purpose ends with a comment saying why:
      File.Delete(link); // check-widgets: allow - removing a Recent shortcut, not a user file
    Warnings (look at them, but they don't fail):
      - changes to Widgets/Framework or IWidget.cs (shared by every widget)
      - risky calls in changed widget folders

.EXAMPLE
    .\tools\check-widgets.ps1              # check changed widgets, then build
    .\tools\check-widgets.ps1 -All         # also scan every widget folder for risky calls
    .\tools\check-widgets.ps1 -NoBuild     # skip the build (faster, not a full check)
#>
[CmdletBinding()]
param(
    [switch]$All,
    [switch]$NoBuild,
    [switch]$AllowOutside
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$widgetsRel = 'src/Hearth.App/Widgets'
$widgets = Join-Path $root 'src\Hearth.App\Widgets'
$shared = @('Framework', '_Template')
$errors = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
function Fail([string]$m) { $errors.Add($m) }
function Warn([string]$m) { $warnings.Add($m) }
function Rel([string]$path) { $path.Substring($root.Length + 1).Replace([char]92, '/') }

$bs = [string][char]92
$wordEnd = '(?![A-Za-z0-9_])'
$wordStart = '(?<![A-Za-z0-9_])'

# ---- 1. What changed --------------------------------------------------------
$changed = @()
try {
    $changed = @(git -C $root status --porcelain --untracked-files=all 2>$null | ForEach-Object {
        $path = $_.Substring(3)
        if ($path -match ' -> ') { $path = ($path -split ' -> ')[-1] }
        $path.Trim('"')
    })
} catch {
    Warn "git isn't available, so changes outside Widgets/ can't be checked."
}

foreach ($path in $changed) {
    if ($path -notlike "$widgetsRel/*") {
        if ($path -like 'artifacts/*') { continue }
        if ($AllowOutside) { Warn "Changed outside Widgets/: $path" }
        else { Fail "Changed outside Widgets/: $path. Widget work must stay inside its own folder (use -AllowOutside only if the user asked for this)." }
    }
    elseif ($path -like "$widgetsRel/Framework/*" -or $path -eq "$widgetsRel/IWidget.cs") {
        Warn "Shared widget code changed: $path. This affects every widget; only do it if the user asked."
    }
}

$changedFolders = @($changed | Where-Object { $_ -like "$widgetsRel/*/*" } | ForEach-Object {
    ($_.Substring($widgetsRel.Length + 1) -split '/')[0]
} | Sort-Object -Unique | Where-Object { $shared -notcontains $_ })

# ---- 2. Text hygiene --------------------------------------------------------
$pua = '[' + [char]0xE000 + '-' + [char]0xF8FF + ']'
$control = '[' + [char]0 + '-' + [char]8 + [char]11 + [char]12 + [char]14 + '-' + [char]31 + ']'
$loneCr = [string][char]13 + '(?!' + [char]10 + ')'
$textFiles = Get-ChildItem (Join-Path $root 'src') -Recurse -File -Include *.cs, *.md, *.xaml, *.txt |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
foreach ($file in $textFiles) {
    $text = [IO.File]::ReadAllText($file.FullName)
    if ($text -match $pua) { Fail "$(Rel $file.FullName): contains an invisible private-use character. Write glyphs as ${bs}u escapes." }
    if ($text -match $control) { Fail "$(Rel $file.FullName): contains a control character (a mangled ${bs}b or similar escape)." }
    if ($text -match $loneCr) { Fail "$(Rel $file.FullName): contains a lone carriage return (a mangled ${bs}r escape?)." }
}

# ---- 3. Folders -------------------------------------------------------------
$badNames = 'System', 'Timer', 'Task', 'Path', 'File', 'Directory', 'Window', 'Windows', 'Application',
    'Thread', 'Math', 'Point', 'Size', 'Rect', 'Brush', 'Brushes', 'Color', 'Colors', 'Image', 'Button',
    'Border', 'Grid', 'Log', 'App', 'IWidget', 'WidgetView', 'Hearth'
$folders = @(Get-ChildItem $widgets -Directory | Where-Object { $shared -notcontains $_.Name })
foreach ($folder in $folders) {
    $name = $folder.Name
    if ($badNames -contains $name) { Fail "Widgets/${name}: this folder name hides a type every widget uses. Pick another (e.g. ${name}Info)." }
    if ($name -cnotmatch '^[A-Z][A-Za-z0-9]*$') { Fail "Widgets/${name}: folder names are PascalCase letters and digits only." }
    if (-not (Test-Path (Join-Path $folder.FullName 'README.md'))) { Fail "Widgets/${name}: README.md is missing." }
    $widgetFiles = @(Get-ChildItem $folder.FullName -Filter '*Widget.cs')
    if ($widgetFiles.Count -eq 0) { Fail "Widgets/${name}: no *Widget.cs file." }

    foreach ($cs in Get-ChildItem $folder.FullName -Recurse -Filter *.cs) {
        $text = [IO.File]::ReadAllText($cs.FullName)
        $expected = "namespace Hearth.App.Widgets.$name;"
        if (-not $text.Contains($expected)) { Fail "$(Rel $cs.FullName): must use '$expected' (file-scoped, matching the folder)." }
        if ($text -cmatch "$wordStart(class|record|struct|enum|interface)\s+$name$wordEnd") {
            Fail "$(Rel $cs.FullName): a type named '$name' clashes with its namespace. Rename it."
        }
    }
}

# ---- 4. Widget classes and ids ----------------------------------------------
$ids = @{}
foreach ($cs in $folders | ForEach-Object { Get-ChildItem $_.FullName -Filter '*Widget.cs' }) {
    $text = [IO.File]::ReadAllText($cs.FullName)
    $rel = Rel $cs.FullName
    if ($text -notmatch 'public\s+sealed\s+(partial\s+)?class\s+\w+Widget\s*:\s*[^{]*IWidget') {
        Fail "${rel}: the widget class must be 'public sealed class <Name>Widget : IWidget' (implementing IWidget directly), or Hearth won't find it."
        continue
    }
    $match = [regex]::Match($text, 'public\s+string\s+Id\s*=>\s*"([^"]*)"')
    if (-not $match.Success) { Fail "${rel}: no 'public string Id => `"...`"' found."; continue }
    $id = $match.Groups[1].Value
    if ($id -cnotmatch '^[a-z][a-z0-9-]*$') { Fail "${rel}: Id '$id' must be lower-case letters, digits and dashes." }
    if ($ids.ContainsKey($id)) { Fail "${rel}: Id '$id' is already used by $($ids[$id])." } else { $ids[$id] = $rel }
    if ($text -notmatch 'public\s+int\s+Order\s*=>') { Warn "${rel}: no Order; it will be listed last (1000)." }
    if ($text -notmatch 'BoardHeight') { Warn "${rel}: no BoardHeight; the Widgets board will guess its height." }
}

# ---- 5. Risky calls in changed (or all) widget folders ----------------------
$rules = @(
    @{ Pattern = 'Process\.Start'; Error = $true; Message = 'use ShellLauncher.Open or ShellLauncher.Launch' },
    @{ Pattern = 'Environment\.Exit|Application\.Current\.Shutdown'; Error = $true; Message = 'a widget must never close Hearth' },
    @{ Pattern = '(Begin|End)KeyboardInput'; Error = $true; Message = 'use InlineInput or WidgetKeyboard' },
    @{ Pattern = 'App\.Settings\.Save|HearthSettings'; Error = $true; Message = "keep widget settings in the widget's own WidgetStore" },
    @{ Pattern = 'Thread\.Sleep|\.Wait\(\)|\)\.Result' + $wordEnd + '|GetAwaiter\(\)\.GetResult'; Error = $false; Message = 'blocks the UI thread; use async/await or Post' },
    @{ Pattern = 'async\s+void'; Error = $false; Message = 'only for event handlers, and catch every exception inside' },
    @{ Pattern = 'new\s+HttpClient\('; Error = $false; Message = 'create one static HttpClient, not one per call' },
    @{ Pattern = 'File\.Delete|Directory\.Delete'; Error = $false; Message = 'prefer ShellLauncher.Recycle so the user can undo' },
    @{ Pattern = 'Registry\.'; Error = $false; Message = 'widgets should not write the registry' },
    @{ Pattern = 'using\s+Windows\.'; Error = $false; Message = 'WinRT costs 2.6 s to load; only touch it off the UI thread (see README, Background work)' },
    @{ Pattern = 'new\s+DispatcherTimer'; Error = $false; ViewsOnly = $true; Message = 'in a WidgetView, use Every(...) so it stops when the view goes' }
)
$scan = if ($All) { @($folders | ForEach-Object Name) } else { $changedFolders }
foreach ($name in $scan) {
    $dir = Join-Path $widgets $name
    if (-not (Test-Path $dir)) { continue }
    foreach ($cs in Get-ChildItem $dir -Recurse -Filter *.cs) {
        $lines = [IO.File]::ReadAllLines($cs.FullName)
        $isView = ($lines -join ' ') -match ':\s*WidgetView'
        for ($i = 0; $i -lt $lines.Count; $i++) {
            # A deliberate exception is marked on the line itself, with the reason.
            if ($lines[$i] -match 'check-widgets:\s*allow') { continue }
            foreach ($rule in $rules) {
                if ($rule.ViewsOnly -and -not $isView) { continue }
                if ($lines[$i] -match $rule.Pattern) {
                    $msg = "$(Rel $cs.FullName):$($i + 1): '$($Matches[0])': $($rule.Message)"
                    if ($rule.Error) { Fail $msg } else { Warn $msg }
                }
            }
        }
    }
}

# ---- 6. Glyphs exist ----------------------------------------------------------
try {
    Add-Type -AssemblyName PresentationCore
    $typeface = New-Object System.Windows.Media.Typeface 'Segoe Fluent Icons'
    $glyphs = $null
    if ($typeface.TryGetGlyphTypeface([ref]$glyphs)) {
        $escape = $bs + $bs + 'u([EF][0-9A-Fa-f]{3})'
        foreach ($cs in Get-ChildItem $widgets -Recurse -Include *.cs, *.txt) {
            foreach ($m in [regex]::Matches([IO.File]::ReadAllText($cs.FullName), $escape)) {
                $code = [Convert]::ToInt32($m.Groups[1].Value, 16)
                if (-not $glyphs.CharacterToGlyphMap.ContainsKey($code)) {
                    Fail "$(Rel $cs.FullName): glyph U+$($m.Groups[1].Value.ToUpper()) isn't in Segoe Fluent Icons."
                }
            }
        }
    } else {
        Warn 'Segoe Fluent Icons is not installed here, so glyphs were not checked.'
    }
} catch {
    Warn "Glyphs were not checked: $($_.Exception.Message)"
}

# ---- 7. Build -----------------------------------------------------------------
if (-not $NoBuild) {
    Write-Host '==> Building Release (this takes a moment)' -ForegroundColor Cyan
    $log = & dotnet build (Join-Path $root 'Hearth.sln') -c Release -nologo -v q 2>&1 | Out-String
    $problems = @($log -split "`n" | Where-Object { $_ -match ': (error|warning) ' } | ForEach-Object { $_.Trim() } | Sort-Object -Unique)
    foreach ($p in $problems) { Fail "Build: $p" }
    if ($LASTEXITCODE -ne 0 -and $problems.Count -eq 0) { Fail "Build failed:`n$log" }
} else {
    Warn 'The build was skipped (-NoBuild); this is not a full check.'
}

# ---- Result -------------------------------------------------------------------
Write-Host ''
foreach ($w in $warnings) { Write-Host "WARNING  $w" -ForegroundColor Yellow }
foreach ($e in $errors) { Write-Host "ERROR    $e" -ForegroundColor Red }
Write-Host ''
Write-Host ('{0} widget(s), {1} error(s), {2} warning(s)' -f $ids.Count, $errors.Count, $warnings.Count)
if ($errors.Count -gt 0) {
    Write-Host 'FAILED' -ForegroundColor Red
    exit 1
}
Write-Host 'PASSED' -ForegroundColor Green
