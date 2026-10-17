# Screenshots of the Krupno.exe window in its main states: empty, ready to start, processing, result, batch, error,
# light theme. Checks that the states are reached; the screenshots (tests/out/gui) are reviewed by a human.
# The color scenario also checks pixels: a Display P3 image must be shown converted to sRGB, as Windows decodes it.
# Requires a Release build, real models in models/ and the fixture images (python tests\make_fixtures.py).
#   powershell -File tests\gui_states.ps1 [-Scenario single,batch,error,light,color] [-Image <file>] [-Batch <folder>]
param(
    [string[]]$Scenario = @("single", "batch", "error", "light", "color"),
    [string]$Exe = "$PSScriptRoot\..\src\Krupno.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\Krupno.exe",
    [string]$Image = "$PSScriptRoot\fixtures\images\big_1500x1000.png",
    [string]$Batch = "$PSScriptRoot\fixtures\images",
    # The fast model is for the batch, error and theme scenarios; the slow one is to catch tile-by-tile processing.
    [string]$Model = "4xBHI_realplksr_dysample_real",
    [string]$SlowModel = "4xBHI_dat2_real_fp16",
    [string]$Out = "$PSScriptRoot\out\gui"
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gui_common.ps1"

$Scenario = $Scenario -split ","
if (-not (Test-Path -LiteralPath $Exe)) { throw "$Exe not found — build it first: dotnet build Krupno.slnx -c Release" }
foreach ($path in $Image, $Batch, "$PSScriptRoot\fixtures\broken\broken.png", "$PSScriptRoot\fixtures\color\wide_p3.png") {
    if (-not (Test-Path -LiteralPath $path)) { throw "$path is missing — run first: python tests\make_fixtures.py" }
}
Assert-Model $Model
Assert-Model $SlowModel

# The inputs are copied to the output folder: by default the window saves the result next to the original.
$data = "$Out\data"
if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
New-Item -ItemType Directory -Force "$data\single", "$data\batch", "$data\broken", "$data\color" | Out-Null
Copy-Item -LiteralPath $Image "$data\single\"
Get-ChildItem -LiteralPath $Batch -File | Where-Object { $_.Extension -match '^\.(png|jpe?g|bmp|tiff?)$' } | Copy-Item -Destination "$data\batch\"
Copy-Item -LiteralPath "$PSScriptRoot\fixtures\broken\broken.png" "$data\broken\"
Copy-Item -Path "$PSScriptRoot\fixtures\color\*.png" "$data\color\"
$single = Join-Path "$data\single" (Split-Path $Image -Leaf)

# The middle pixel of an image file as Windows' own color-managed decoding gives it (WIC converts the ICC profile to sRGB),
# or with -Raw as stored.
function Decoded-Color([string]$Path, [switch]$Raw) {
    Add-Type -AssemblyName PresentationCore, WindowsBase
    $options = if ($Raw) { [Windows.Media.Imaging.BitmapCreateOptions]::IgnoreColorProfile } else { [Windows.Media.Imaging.BitmapCreateOptions]::None }
    $stream = [IO.File]::OpenRead($Path)
    try { $frame = [Windows.Media.Imaging.BitmapDecoder]::Create($stream, $options, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad).Frames[0] }
    finally { $stream.Dispose() }
    $bgra = New-Object Windows.Media.Imaging.FormatConvertedBitmap $frame, ([Windows.Media.PixelFormats]::Bgra32), $null, 0.0
    $px = New-Object byte[] 4
    $bgra.CopyPixels((New-Object Windows.Int32Rect ([int]($bgra.PixelWidth / 2)), ([int]($bgra.PixelHeight / 2)), 1, 1), $px, 4, 0)
    return @([int]$px[2], [int]$px[1], [int]$px[0])
}
function Color-Distance($A, $B) { return (0..2 | ForEach-Object { [Math]::Abs($A[$_] - $B[$_]) } | Measure-Object -Maximum).Maximum }
# The screen rectangle of the first element of this type whose name matches.
function Find-Rect($Root, [string]$Pattern, $Type) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $Type
    foreach ($e in $Root.FindAll($Tree::Descendants, $condition)) {
        if ($e.Current.Name -match $Pattern) { return $e.Current.BoundingRectangle }
    }
    throw "element '$Pattern' not found"
}
# Waits until the window shows the expected color at a screen point: previews and thumbnails load in the background.
function Expect-Color([IntPtr]$h, [string]$What, $Point, $Expected, [int]$Tolerance) {
    $deadline = (Get-Date).AddSeconds(10)
    do {
        $shot = Capture $h
        $c = $shot.Bitmap.GetPixel([int]$Point[0] - $shot.Left, [int]$Point[1] - $shot.Top); $shot.Bitmap.Dispose()
        $got = @([int]$c.R, [int]$c.G, [int]$c.B)
        if ((Color-Distance $got $Expected) -le $Tolerance) { "  $What`: ($($got -join ',')), expected ($($Expected -join ',')) ±$Tolerance"; return }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw "$What shows ($($got -join ',')) instead of ($($Expected -join ',')) ±$Tolerance"
}
# Sample points: under the "Original" and "Result" chips (the top-left and top-right corners of the image in the split view)
# and in the middle of a filmstrip thumbnail.
function Before-Point($w) { $r = Find-Rect $w "^Original · " ([System.Windows.Automation.ControlType]::Text); return @(($r.Left + 4), ($r.Bottom + 40)) }
function After-Point($w) { $r = Find-Rect $w "^Result · " ([System.Windows.Automation.ControlType]::Text); return @(($r.Right - 4), ($r.Bottom + 40)) }
function Thumb-Point($w, [string]$Name) {
    $r = Find-Rect $w "^$([regex]::Escape($Name))$" ([System.Windows.Automation.ControlType]::ListItem)
    return @(($r.Left + $r.Width / 2), ($r.Top + $r.Height * 0.4))
}

$failed = 0
Backup-Settings
try {
    foreach ($name in $Scenario) {
        "scenario $name"
        try {
            switch ($name) {
                "single" {
                    Settings $Model
                    $p = Launch; try { Shot $p.MainWindowHandle "01-empty" } finally { Close $p }
                    Settings $SlowModel
                    $p = Launch @($single)
                    try {
                        $w = $AE::FromHandle($p.MainWindowHandle); $h = $p.MainWindowHandle
                        Shot $h "02-ready"
                        Invoke-Named $w "^Upscale "
                        Start-Sleep -Seconds 3; Shot $h "03-busy"
                        WaitFor $w "^Processing… [4-7]\d%" | Out-Null; Shot $h "03b-busy-tiles"
                        WaitFor $w "^Done" | Out-Null; Shot $h "04-done"
                        Invoke-Named $w "^Side by side$"; Shot $h "05-side"
                        Invoke-Named $w "^Actual size$"; Shot $h "06-side-1to1"
                    } finally { Close $p }
                }
                "batch" {
                    Settings $Model
                    $p = Launch @("$data\batch")
                    try {
                        $w = $AE::FromHandle($p.MainWindowHandle); $h = $p.MainWindowHandle
                        Shot $h "07-batch-ready"
                        Invoke-Named $w "^Upscale "
                        "  " + (WaitFor $w "^Done"); Shot $h "08-batch-done"
                    } finally { Close $p }
                }
                "error" {
                    Settings $Model
                    $p = Launch @("$data\broken\broken.png")
                    try {
                        $w = $AE::FromHandle($p.MainWindowHandle); $h = $p.MainWindowHandle
                        Shot $h "09-broken-ready"
                        Invoke-Named $w "^Upscale "
                        WaitFor $w "^Upscaling failed" 30 | Out-Null; Shot $h "10-error"
                    } finally { Close $p }
                }
                "light" {
                    Settings $Model
                    $p = Launch @() "light"; try { Shot $p.MainWindowHandle "11-light-empty" } finally { Close $p }
                    $p = Launch @($single) "light"
                    try {
                        $w = $AE::FromHandle($p.MainWindowHandle); $h = $p.MainWindowHandle
                        Invoke-Named $w "^Upscale "
                        WaitFor $w "^Done" | Out-Null; Shot $h "12-light-done"
                    } finally { Close $p }
                }
                "color" {
                    # Display P3 is shown converted to sRGB, the way Windows decodes it, before, during and after processing;
                    # the untagged copy is shown as stored, and the saved result keeps the stored pixels.
                    $p3 = "$data\color\wide_p3.png"
                    $managed = Decoded-Color $p3; $raw = Decoded-Color $p3 -Raw
                    if ((Color-Distance $managed $raw) -lt 20) { throw "the fixture's profile hardly changes the color: ($($raw -join ',')) → ($($managed -join ','))" }
                    Settings $Model
                    $p = Launch @("$data\color")
                    try {
                        $w = $AE::FromHandle($p.MainWindowHandle); $h = $p.MainWindowHandle
                        Invoke-Named $w "^wide_p3\.png$"
                        WaitFor $w "^Original · 600 × 400" | Out-Null
                        Expect-Color $h "P3 preview" (Before-Point $w) $managed 3
                        Expect-Color $h "P3 thumbnail" (Thumb-Point $w "wide_p3.png") $managed 3
                        Expect-Color $h "untagged thumbnail" (Thumb-Point $w "srgb_untagged.png") $raw 3
                        Shot $h "13-color-ready"

                        # The batch ends on wide_p3.png, so its comparison comes straight from processing.
                        Invoke-Named $w "^Upscale "
                        WaitFor $w "^Done" | Out-Null
                        Shot $h "14-color-done"
                        Expect-Color $h "P3 original after processing" (Before-Point $w) $managed 3
                        Expect-Color $h "P3 result" (After-Point $w) $managed 6
                        $saved = Decoded-Color "$data\color\wide_p3_x4.png" -Raw
                        if ((Color-Distance $saved $raw) -gt 6) { throw "the saved result holds ($($saved -join ',')) instead of the stored ($($raw -join ','))" }
                        "  saved result: ($($saved -join ',')), stored ($($raw -join ','))"

                        # Selected again: the comparison is read back from the files.
                        Invoke-Named $w "^srgb_untagged\.png$"
                        Expect-Color $h "untagged result" (After-Point $w) $raw 6
                        Expect-Color $h "untagged original" (Before-Point $w) $raw 3
                        Invoke-Named $w "^wide_p3\.png$"
                        Expect-Color $h "P3 result reloaded" (After-Point $w) $managed 6
                        Expect-Color $h "P3 original reloaded" (Before-Point $w) $managed 3
                    } finally { Close $p }
                }
                default { throw "unknown scenario (available: single, batch, error, light, color)" }
            }
            "OK   $name"
        } catch {
            $failed++
            "FAIL $name`: $($_.Exception.Message)"
        }
    }
} finally {
    Restore-Settings
}
"screenshots: $Out"
exit $(if ($failed -eq 0) { 0 } else { 1 })
