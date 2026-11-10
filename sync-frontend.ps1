param(
    [switch]$Init,
    [string]$Root = 'D:\ai\deskbar',
    [switch]$Json
)

# Cross-session sync detector for the DeskBar SETTINGS UI.
#
# Purpose: another session works on the backend (Services/Interop/App), this
# session owns the UI (Views/). When the backend changes in a way that affects
# what the settings window shows, the UI session must follow up.
#
# This script only DETECTS. It never edits source.
#
#   -Init    record the current state as the sync baseline
#   -Report  (default) list everything that changed since the baseline
#
# No byte in this file may be non-ASCII: Windows PowerShell 5.1 decodes a
# BOM-less .ps1 using the OEM/ANSI code page, which corrupts non-ASCII text
# and raises ParserError. Keep every literal here ASCII.

$ErrorActionPreference = 'Stop'
$stateDir = Join-Path $Root '.sync'
$baselinePath = Join-Path $stateDir 'baseline.json'

# Files whose changes can change what the settings UI must display.
$backendGlobs = @(
    'src/DeskBar/Services/*.cs',
    'src/DeskBar/Interop/*.cs',
    'src/DeskBar/App.xaml',
    'src/DeskBar/App.xaml.cs',
    'src/DeskBar/DeskBar.csproj'
)

# Files this UI session owns. A change here is an overlap warning, not a task.
# Views/*.cs matters too: TrayIconHost.cs is a frontend file but matches neither
# *.xaml nor *.xaml.cs, and leaving it out silently hid real UI-side edits.
$frontendGlobs = @(
    'src/DeskBar/Views/*.xaml',
    'src/DeskBar/Views/*.xaml.cs',
    'src/DeskBar/Views/*.cs'
)

function Get-RelativePath([string]$full) {
    return $full.Substring($Root.Length + 1).Replace('\', '/')
}

function Get-FileHashes([string[]]$globs) {
    $map = @{}
    foreach ($g in $globs) {
        $pattern = Join-Path $Root $g
        Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Name -match '\.bak') { return }
            $map[(Get-RelativePath $_.FullName)] = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash
        }
    }
    return $map
}

# Extract the field/derived-property surface of AppSettings. Every settings
# control in the XAML is bound to one of these, so a change here is the single
# strongest signal that the UI needs a matching edit.
function Get-SettingsContract {
    $path = Join-Path $Root 'src/DeskBar/Services/AppSettings.cs'
    if (-not (Test-Path $path)) { return @() }
    $result = New-Object System.Collections.ArrayList
    foreach ($line in (Get-Content -Path $path -Encoding UTF8)) {
        $t = $line.Trim()
        if ($t -match '^public\s+[^(]*\{\s*get;') { [void]$result.Add($t) }
        elseif ($t -match '^public\s+[^(]*=>') { [void]$result.Add($t) }
    }
    return $result.ToArray()
}

# Enumerate the acrylic tone keys the backend actually understands.
function Get-ToneContract {
    $path = Join-Path $Root 'src/DeskBar/Services/AppSettings.cs'
    if (-not (Test-Path $path)) { return @() }
    $text = Get-Content -Path $path -Raw -Encoding UTF8
    # Anchor on the DEFINITION, not the first textual mention. The old pattern
    # ('IsKnownTone[^{]*\{...') started at the call site (AcrylicTone = IsKnownTone(tone) ? ...)
    # and ran forward to the next '{', which is the ShapeStyle switch - so it reported shape
    # keys as tones and every real tone as removed. Locate the declaration first, then
    # bracket-match its body.
    $defIdx = $text.IndexOf('public static bool IsKnownTone')
    if ($defIdx -lt 0) { return @() }
    $openIdx = $text.IndexOf('{', $defIdx)
    if ($openIdx -lt 0) { return @() }
    $depth = 0
    $endIdx = -1
    for ($i = $openIdx; $i -lt $text.Length; $i++) {
        $ch = $text[$i]
        if ($ch -eq '{') { $depth++ }
        elseif ($ch -eq '}') { $depth--; if ($depth -eq 0) { $endIdx = $i; break } }
    }
    if ($endIdx -lt 0) { return @() }
    $body = $text.Substring($openIdx + 1, $endIdx - $openIdx - 1)
    $keys = New-Object System.Collections.ArrayList
    foreach ($k in [regex]::Matches($body, '"([a-z]+)"')) {
        $v = $k.Groups[1].Value
        if (-not $keys.Contains($v)) { [void]$keys.Add($v) }
    }
    return $keys.ToArray()
}

function Compare-FileMaps($before, $after) {
    $rows = New-Object System.Collections.ArrayList
    foreach ($k in $after.Keys) {
        if (-not $before.ContainsKey($k)) {
            [void]$rows.Add([pscustomobject]@{ kind = 'added'; path = $k })
        } elseif ($before[$k] -ne $after[$k]) {
            [void]$rows.Add([pscustomobject]@{ kind = 'modified'; path = $k })
        }
    }
    foreach ($k in $before.Keys) {
        if (-not $after.ContainsKey($k)) {
            [void]$rows.Add([pscustomobject]@{ kind = 'deleted'; path = $k })
        }
    }
    return @($rows | Sort-Object path)
}

function Compare-Contract($before, $after) {
    $added = @($after | Where-Object { $before -notcontains $_ })
    $removed = @($before | Where-Object { $after -notcontains $_ })
    return [pscustomobject]@{ added = $added; removed = $removed }
}

if (-not (Test-Path $stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null }

$backendNow = Get-FileHashes $backendGlobs
$frontendNow = Get-FileHashes $frontendGlobs
$settingsNow = Get-SettingsContract
$tonesNow = Get-ToneContract

if ($Init) {
    $baseline = [pscustomobject]@{
        createdAt    = (Get-Date).ToString('o')
        gitHead      = (& git -C $Root rev-parse --short HEAD 2>$null)
        backend      = $backendNow
        frontend     = $frontendNow
        settings     = $settingsNow
        tones        = $tonesNow
    }
    $baseline | ConvertTo-Json -Depth 8 | Set-Content -Path $baselinePath -Encoding UTF8
    if (-not $Json) {
        Write-Output ("BASELINE_WRITTEN at " + $baseline.createdAt)
        Write-Output ("  backend files  : " + $backendNow.Count)
        Write-Output ("  frontend files : " + $frontendNow.Count)
        Write-Output ("  settings fields: " + $settingsNow.Count)
    } else {
        $baseline | ConvertTo-Json -Depth 8
    }
    exit 0
}

if (-not (Test-Path $baselinePath)) {
    Write-Output 'NO_BASELINE: run with -Init first.'
    exit 2
}

$baseline = Get-Content -Path $baselinePath -Raw -Encoding UTF8 | ConvertFrom-Json

$beforeBackend = @{}
foreach ($p in $baseline.backend.PSObject.Properties) { $beforeBackend[$p.Name] = $p.Value }
$beforeFrontend = @{}
foreach ($p in $baseline.frontend.PSObject.Properties) { $beforeFrontend[$p.Name] = $p.Value }

# Force arrays here: a single-row pipeline result collapses to a bare object in
# Windows PowerShell 5.1, and a bare object has no .Count, which would make
# every count below render empty and flip needsAttention to false.
$backendDelta = @(Compare-FileMaps $beforeBackend $backendNow)
$frontendDelta = @(Compare-FileMaps $beforeFrontend $frontendNow)
$settingsDelta = Compare-Contract @($baseline.settings) $settingsNow
$toneDelta = Compare-Contract @($baseline.tones) $tonesNow

$report = [pscustomobject]@{
    baselineCreatedAt = $baseline.createdAt
    baselineGitHead   = $baseline.gitHead
    currentGitHead    = (& git -C $Root rev-parse --short HEAD 2>$null)
    backendChanged    = $backendDelta
    frontendChanged   = $frontendDelta
    settingsAdded     = $settingsDelta.added
    settingsRemoved   = $settingsDelta.removed
    tonesAdded        = $toneDelta.added
    tonesRemoved      = $toneDelta.removed
    needsAttention    = (($backendDelta.Count + $settingsDelta.added.Count + $settingsDelta.removed.Count + $toneDelta.added.Count + $toneDelta.removed.Count) -gt 0)
    overlapWarning    = ($frontendDelta.Count -gt 0)
}

if ($Json) {
    $report | ConvertTo-Json -Depth 6
    exit 0
}

Write-Output ("baseline: " + $report.baselineCreatedAt + "  git " + $report.baselineGitHead + " -> " + $report.currentGitHead)
Write-Output ''

if ($backendDelta.Count -eq 0) {
    Write-Output 'BACKEND: no change since baseline.'
} else {
    Write-Output ('BACKEND: ' + $backendDelta.Count + ' file(s) changed')
    foreach ($r in $backendDelta) { Write-Output ('  [' + $r.kind + '] ' + $r.path) }
}

if ($settingsDelta.added.Count -gt 0 -or $settingsDelta.removed.Count -gt 0) {
    Write-Output ''
    Write-Output 'SETTINGS CONTRACT:'
    foreach ($s in $settingsDelta.added) { Write-Output ('  [+] ' + $s) }
    foreach ($s in $settingsDelta.removed) { Write-Output ('  [-] ' + $s) }
}

if ($toneDelta.added.Count -gt 0 -or $toneDelta.removed.Count -gt 0) {
    Write-Output ''
    Write-Output 'TONE CONTRACT:'
    foreach ($s in $toneDelta.added) { Write-Output ('  [+] ' + $s) }
    foreach ($s in $toneDelta.removed) { Write-Output ('  [-] ' + $s) }
}

if ($frontendDelta.Count -gt 0) {
    Write-Output ''
    Write-Output ('OVERLAP WARNING: ' + $frontendDelta.Count + ' UI file(s) changed outside this session')
    foreach ($r in $frontendDelta) { Write-Output ('  [' + $r.kind + '] ' + $r.path) }
}

if (-not $report.needsAttention -and -not $report.overlapWarning) {
    Write-Output ''
    Write-Output 'RESULT: up to date, nothing to follow up.'
} elseif ($report.needsAttention) {
    Write-Output ''
    Write-Output 'RESULT: follow-up required.'
}

exit 0
