# Screenshots of the Krupno.exe window in its main states: empty, ready to start, processing, result, batch, error,
# light theme. Checks that the states are reached; the screenshots (tests/out/gui) are reviewed by a human.
# Requires a Release build, real models in models/ and the fixture images (python tests\make_fixtures.py).
#   powershell -File tests\gui_states.ps1 [-Scenario single,batch,error,light] [-Image <file>] [-Batch <folder>]
param(
    [string[]]$Scenario = @("single", "batch", "error", "light"),
    [string]$Exe = "$PSScriptRoot\..\src\Krupno.App\bin\Release\net10.0-windows\win-x64\Krupno.exe",
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
foreach ($path in $Image, $Batch, "$PSScriptRoot\fixtures\broken\broken.png") {
    if (-not (Test-Path -LiteralPath $path)) { throw "$path is missing — run first: python tests\make_fixtures.py" }
}
Assert-Model $Model
Assert-Model $SlowModel

# The inputs are copied to the output folder: by default the window saves the result next to the original.
$data = "$Out\data"
if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
New-Item -ItemType Directory -Force "$data\single", "$data\batch", "$data\broken" | Out-Null
Copy-Item -LiteralPath $Image "$data\single\"
Get-ChildItem -LiteralPath $Batch -File | Where-Object { $_.Extension -match '^\.(png|jpe?g|bmp|tiff?)$' } | Copy-Item -Destination "$data\batch\"
Copy-Item -LiteralPath "$PSScriptRoot\fixtures\broken\broken.png" "$data\broken\"
$single = Join-Path "$data\single" (Split-Path $Image -Leaf)

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
                default { throw "unknown scenario (available: single, batch, error, light)" }
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
