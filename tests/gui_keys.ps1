# Krupno.exe window hotkeys: preview zoom (Ctrl+1, Ctrl+0, Ctrl+plus), start (Ctrl+Enter),
# copying the result (Ctrl+C) and cancel (Esc). Keys go through SendKeys: don't touch the mouse or keyboard during the run.
# Requires a Release build, real models in models/ and the fixture images (python tests\make_fixtures.py).
#   powershell -File tests\gui_keys.ps1 [-Image <file>] [-Model <name>] [-SlowModel <name>]
param(
    [string]$Exe = "$PSScriptRoot\..\src\Krupno.App\bin\Release\net10.0-windows\win-x64\Krupno.exe",
    [string]$Image = "$PSScriptRoot\fixtures\images\big_1500x1000.png",
    # The fast model is for processing to completion; the slow one leaves time to cancel it.
    [string]$Model = "4xBHI_realplksr_dysample_real",
    [string]$SlowModel = "4xBHI_dat2_real_fp16",
    [string]$Out = "$PSScriptRoot\out\gui-keys"
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gui_common.ps1"

if (-not (Test-Path -LiteralPath $Exe)) { throw "$Exe not found — build it first: dotnet build Krupno.slnx -c Release" }
if (-not (Test-Path -LiteralPath $Image)) { throw "$Image is missing — run first: python tests\make_fixtures.py" }
Assert-Model $Model
Assert-Model $SlowModel

# The input is copied to the output folder: by default the window saves the result next to the original.
New-Item -ItemType Directory -Force $Out | Out-Null
$sourceFile = Join-Path $Out (Split-Path $Image -Leaf)
Copy-Item -LiteralPath $Image $sourceFile -Force
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile($sourceFile); $srcSize = "$($src.Width * 4)×$($src.Height * 4)"; $src.Dispose()

$failed = 0
function Expect([string]$Step, $Got, [scriptblock]$Ok) {
    $pass = [bool](& $Ok $Got)
    if (-not $pass) { $script:failed++ }
    "{0} {1} → {2}" -f $(if ($pass) { "OK  " } else { "FAIL" }), $Step, $(if ($null -eq $Got) { "<timed out>" } else { $Got })
}
function Zoom($w) { $z = WaitFor $w '^\d+%$' 5 -Soft; if ($z) { [int]$z.TrimEnd('%') } else { $null } }
# Waits until the zoom differs from the previous value (the change is not applied instantly).
function ZoomAfter($w, $before) {
    $deadline = (Get-Date).AddSeconds(5)
    do { $z = Zoom $w; if ($z -ne $before) { return $z }; Start-Sleep -Milliseconds 200 } while ((Get-Date) -lt $deadline)
    return $z
}

Backup-Settings
try {
    Settings $Model
    $p = Launch @($sourceFile)
    try {
        $w = $AE::FromHandle($p.MainWindowHandle)
        $fit = Zoom $w
        Expect "zoom on open" $(if ($null -ne $fit) { "$fit%" }) { param($g) $null -ne $g }
        Keys $p "^1"; $z = ZoomAfter $w $fit; Expect "Ctrl+1" "$z%" { param($g) $g -eq "100%" }
        Keys $p "^0"; $z = ZoomAfter $w 100; Expect "Ctrl+0" "$z%" { param($g) $g -eq "$fit%" }
        Keys $p "^{ADD}"; $z = ZoomAfter $w $fit; Expect "Ctrl+Num+" "$z%" { param($g) [int]$g.TrimEnd('%') -gt $fit }
        Keys $p "^{ENTER}"; Expect "Ctrl+Enter" (WaitFor $w '^Done' 60 -Soft) { param($g) $null -ne $g }
        [System.Windows.Forms.Clipboard]::Clear()
        Keys $p "^c"; Expect "Ctrl+C" (WaitFor $w 'copied' 5 -Soft) { param($g) $null -ne $g }
        $clip = [System.Windows.Forms.Clipboard]::GetImage()
        Expect "in the clipboard ($srcSize)" $(if ($clip) { "$($clip.Width)×$($clip.Height)" } else { "empty" }) { param($g) $g -eq $srcSize }
    } finally { Close $p }

    Settings $SlowModel
    $p = Launch @($sourceFile)
    try {
        $w = $AE::FromHandle($p.MainWindowHandle)
        Keys $p "^{ENTER}"; Expect "Ctrl+Enter ($SlowModel)" (WaitFor $w '^Processing… [1-9]' 60 -Soft) { param($g) $null -ne $g }
        Keys $p "{ESC}"; Expect "Esc" (WaitFor $w 'canceled' 30 -Soft) { param($g) $null -ne $g }
    } finally { Close $p }
} finally {
    Restore-Settings
}
exit $(if ($failed -eq 0) { 0 } else { 1 })
