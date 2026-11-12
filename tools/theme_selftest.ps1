# Checks the PC app's themes (windows/app/Theme/<Theme>-<Light|Dark>.xaml, see docs/DESIGN_PATTERNS.md "Themes"):
#  1. every theme defines exactly the same keys as Studio-Light (a missing key silently falls back to the wrong look);
#  2. text colours are readable: WCAG contrast >= 4.5 for every pair listed below, in every theme;
#  3. every font family a theme names has its TTF in windows/app/Fonts.
# Prints PASS/FAIL lines and "THEME SELF-TEST PASS" (exit 0) or FAIL (exit 1). No build or phone needed.
$root = Split-Path $PSScriptRoot -Parent
$themeDir = Join-Path $root 'windows\app\Theme'
$fontDir = Join-Path $root 'windows\app\Fonts'
$failed = 0
function Fail($msg) { "FAIL: $msg"; $script:failed++ }

function Keys($path) { ([regex]::Matches([IO.File]::ReadAllText($path), 'x:Key="(\w+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) }
function Brush($path, $key) {
    $m = [regex]::Match([IO.File]::ReadAllText($path), 'x:Key="' + $key + '" Color="#([0-9A-Fa-f]{6,8})"')
    if ($m.Success) { $m.Groups[1].Value.Substring($m.Groups[1].Value.Length - 6) } else { $null }
}
function Lum([string]$hex) {
    $v = @(0, 2, 4) | ForEach-Object { [Convert]::ToInt32($hex.Substring($_, 2), 16) / 255.0 }
    $l = $v | ForEach-Object { if ($_ -le 0.03928) { $_ / 12.92 } else { [math]::Pow(($_ + 0.055) / 1.055, 2.4) } }
    0.2126 * $l[0] + 0.7152 * $l[1] + 0.0722 * $l[2]
}
function Ratio([string]$a, [string]$b) { $x = Lum $a; $y = Lum $b; if ($x -lt $y) { $t = $x; $x = $y; $y = $t }; ($x + 0.05) / ($y + 0.05) }

$files = Get-ChildItem $themeDir -Filter '*-*.xaml' | Where-Object { $_.Name -match '^\w+-(Light|Dark)\.xaml$' }
$reference = Keys (Join-Path $themeDir 'Studio-Light.xaml')
if ($reference.Count -lt 80) { Fail "Studio-Light defines only $($reference.Count) keys (expected 80+): generator output looks truncated" }
"themes: " + (($files | ForEach-Object { $_.BaseName }) -join ', ') + "  ($($reference.Count) keys each)"

# text pairs: foreground, background (Paper is skipped when it is a pattern, as in Gamer)
$pairs = @(
    @('Ink', 'Panel'), @('Ink2', 'Panel'), @('Ink', 'Paper'), @('Ink2', 'Paper'), @('Link', 'Panel'), @('Link', 'Paper'),
    @('OnAccent', 'Accent'), @('OnPrimary', 'Primary'), @('OnHeader', 'HeaderBlock'),
    @('OnStatus', 'Good'), @('OnStatus', 'Bad'), @('OnStatus', 'Wait'), @('OnStatus', 'Idle'),
    @('SectionInk', 'Section1'), @('SectionInk', 'Section2'), @('SectionInk', 'Section3'), @('SectionInk', 'Section4'), @('SectionInk', 'Section5'),
    @('SectionInk', 'RowLens'), @('SectionInk', 'RowResolution'), @('SectionInk', 'RowFps'), @('SectionInk', 'RowQuality'), @('SectionInk', 'RowFocus'), @('SectionInk', 'RowWb'), @('SectionInk', 'RowFlicker'), @('SectionInk', 'RowStab'), @('SectionInk', 'RowTheme'), @('SectionInk', 'RowAppearance'),
    @('Ink', 'Panel1'), @('Ink', 'Panel2'), @('Ink', 'Panel3'), @('Ink', 'Panel4'), @('Ink', 'Panel5'), @('Ink2', 'Panel1'), @('Ink2', 'Panel2'), @('Ink2', 'Panel3'), @('Ink2', 'Panel4'), @('Ink2', 'Panel5'), @('Link', 'Panel1'), @('Link', 'Panel2'), @('Link', 'Panel3'), @('Link', 'Panel4'), @('Link', 'Panel5')
)
foreach ($file in $files) {
    $name = $file.BaseName
    $keys = Keys $file.FullName
    $missing = $reference | Where-Object { $_ -notin $keys }
    $extra = $keys | Where-Object { $_ -notin $reference }
    if ($missing -or $extra) { Fail "$name keys differ (missing: $($missing -join ',') extra: $($extra -join ','))" } else { "PASS: $name defines all keys" }
    $bad = @()
    foreach ($p in $pairs) {
        $fg = Brush $file.FullName ($p[0] + 'Brush'); $bg = Brush $file.FullName ($p[1] + 'Brush')
        if (-not $fg -or -not $bg) { continue }
        $r = Ratio $fg $bg
        if ($r -lt 4.5) { $bad += ('{0} on {1} = {2:N2}' -f $p[0], $p[1], $r) }
    }
    if ($bad) { Fail "$name low contrast: $($bad -join '; ')" } else { "PASS: $name text contrast >= 4.5" }
    foreach ($m in [regex]::Matches([IO.File]::ReadAllText($file.FullName), '#([^<"]+)</FontFamily>')) {
        $family = $m.Groups[1].Value
        $found = Get-ChildItem $fontDir -Filter '*.ttf' | Where-Object { $_.BaseName -replace '[-\[].*', '' -replace '(?<=[a-z])(?=[A-Z])', ' ' -like ($family -replace ' ', '*') -or ($_.BaseName -replace '[-\[].*', '') -eq ($family -replace ' ', '') }
        if (-not $found) { Fail "$name uses font '$family' but no matching TTF in Fonts" }
    }
}
if ($failed) { "THEME SELF-TEST FAIL ($failed)"; exit 1 } else { 'THEME SELF-TEST PASS' }
