# Krupno.exe window hotkeys: preview zoom (Ctrl+1, Ctrl+0, Ctrl+plus), start (Ctrl+Enter),
# copying the result (Ctrl+C), pasting an image (Ctrl+V) and cancel (Esc). Keys go through SendKeys: don't touch the mouse or keyboard during the run.
# Also handing files over to an open window, the way Explorer's "Upscale with Krupno" does with a selection.
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
# Ctrl+V with an image on the clipboard, as a bitmap (the way a screenshot arrives) or as PNG data (the way browsers put it).
# The window saves it to Pictures\Krupno and opens it. Returns the saved file's size; the file is deleted afterwards.
function Paste($p, $w, [string]$Kind, [int]$Width, [int]$Height) {
    $bmp = New-Object System.Drawing.Bitmap $Width, $Height
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::SteelBlue); $g.Dispose()
    if ($Kind -eq "PNG") {
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = New-Object System.Windows.Forms.DataObject
        $data.SetData("PNG", $false, $ms)
        [System.Windows.Forms.Clipboard]::SetDataObject($data, $true)
    } else {
        [System.Windows.Forms.Clipboard]::SetImage($bmp)
    }
    $bmp.Dispose()
    Keys $p "^v"
    $prefix = "Pasted image saved to "
    $toast = WaitFor $w "^$prefix" 10 -Soft
    if (-not $toast) { return $null }
    $file = $toast.Substring($prefix.Length)
    if (-not (Test-Path -LiteralPath $file)) { return "no file $file" }
    $img = [System.Drawing.Image]::FromFile($file); $size = "$($img.Width)×$($img.Height)"; $img.Dispose()
    # The preview may still be reading the file.
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue
    return $size
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
        Expect "Ctrl+V, bitmap" (Paste $p $w "bitmap" 320 200) { param($g) $g -eq "320×200" }
        Expect "Ctrl+V, PNG" (Paste $p $w "PNG" 240 160) { param($g) $g -eq "240×160" }
    } finally { Close $p }

    Settings $SlowModel
    $p = Launch @($sourceFile)
    try {
        $w = $AE::FromHandle($p.MainWindowHandle)
        Keys $p "^{ENTER}"; Expect "Ctrl+Enter ($SlowModel)" (WaitFor $w '^Processing… [1-9]' 60 -Soft) { param($g) $null -ne $g }
        Keys $p "{ESC}"; Expect "Esc" (WaitFor $w 'canceled' 30 -Soft) { param($g) $null -ne $g }
    } finally { Close $p }

    # Explorer starts a Krupno.exe per selected file: the ones after the first hand their file to the open window and exit,
    # files a moment apart share one queue, and a later one replaces it. Skipped if a Krupno window is already open: it would take the files.
    if (Get-Process Krupno -ErrorAction SilentlyContinue) {
        "SKIP hand-over: close the open Krupno window first"
    } else {
        $second = Join-Path $Out "second.png"
        Copy-Item -LiteralPath "$PSScriptRoot\fixtures\images\noise_300x200.png" $second -Force
        Settings $Model
        $p = Launch -SingleInstance
        try {
            $w = $AE::FromHandle($p.MainWindowHandle)
            # Two at once, like Explorer with a selection of two files.
            $others = @($sourceFile, $second) | ForEach-Object { Start-Process $Exe -ArgumentList ('"' + $_ + '"') -PassThru }
            $exited = @($others | Where-Object { $_.WaitForExit(15000) }).Count
            Expect "Krupno.exe for each file hands over and exits" "$exited of 2" { param($g) $g -eq "2 of 2" }
            Expect "one queue for the selection" (WaitFor $w '^Upscale 2 files' 10 -Soft) { param($g) $null -ne $g }
            Start-Sleep -Seconds 3
            $other = Start-Process $Exe -ArgumentList ('"' + $second + '"') -PassThru
            $other.WaitForExit(15000) | Out-Null
            Expect "a later file replaces the queue" (WaitFor $w '^Upscale ×4$' 10 -Soft) { param($g) $null -ne $g }
        } finally { Close $p }
    }
} finally {
    Restore-Settings
}
exit $(if ($failed -eq 0) { 0 } else { 1 })
