# The README screenshots in docs/images: the window comparing a small photo with its ×4 result, in both themes. split-*.jpg
# is the slider on the face, side-*.jpg the two frames zoomed in on the eye. The photo (fixtures/readme/fox.jpg, 300×200)
# is AI-generated and downscaled, small enough for its pixels to show at the README's size.
# Requires a Release build and "Photo, maximum detail" in models/. The script moves the mouse to set the divider and zoom,
# so don't touch the mouse during the run (about 40 s).
#   powershell -File tests\readme_screenshot.ps1
param(
    [string]$Exe = "$PSScriptRoot\..\src\Krupno.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\Krupno.exe",
    [string]$Image = "$PSScriptRoot\fixtures\readme\fox.jpg",
    [string]$Model = "4xBHI_dat2_real_fp16",
    [string]$Out = "$PSScriptRoot\..\docs\images",
    # The window in DIPs, and the width of the saved images in pixels.
    [int]$Width = 1240,
    [int]$Height = 700,
    [int]$ImageWidth = 1800
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gui_common.ps1"
if (-not ("KrupnoUi.Shot" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
namespace KrupnoUi {
    public static class Shot {
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out RECT r, int size);
        public static void Click(int x, int y) {
            SetCursorPos(x, y); Thread.Sleep(150);
            mouse_event(0x2, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(80); mouse_event(0x4, 0, 0, 0, UIntPtr.Zero);
        }
        public static void Wheel(int x, int y, int notches) {
            SetCursorPos(x, y); Thread.Sleep(150);
            for (int i = 0; i < notches; i++) { mouse_event(0x800, 0, 0, 120, UIntPtr.Zero); Thread.Sleep(120); }
        }
        // The window as it is drawn, without the invisible resize borders that GetWindowRect includes.
        public static RECT Frame(IntPtr h) {
            RECT r; DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))); return r;  // 9 = DWMWA_EXTENDED_FRAME_BOUNDS
        }
    }
}
"@
}
if (-not (Test-Path -LiteralPath $Exe)) { throw "$Exe not found — build it first: dotnet build Krupno.slnx -c Release" }
Assert-Model $Model
New-Item -ItemType Directory -Force $Out | Out-Null

# The input is copied to a folder of its own: the window saves the result next to the original.
$data = Join-Path $env:TEMP "krupno-readme"
New-Item -ItemType Directory -Force $data | Out-Null
Copy-Item -LiteralPath $Image $data -Force
$sample = Join-Path $data (Split-Path $Image -Leaf)

# The comparison's picture as displayed (screen pixels): the largest image element, and of two the same size (side by
# side) the left one, the original.
function Image-Rect($Root) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Image)
    $best = $null
    foreach ($e in $Root.FindAll($Tree::Descendants, $condition)) {
        $r = $e.Current.BoundingRectangle
        if ($r.IsEmpty) { continue }
        $area = $r.Width * $r.Height
        $bestArea = if ($best) { $best.Width * $best.Height } else { 0 }
        if ($area -gt $bestArea * 1.02 -or ($area -gt $bestArea * 0.98 -and $r.Left -lt $best.Left)) { $best = $r }
    }
    if (-not $best) { throw "the comparison's image was not found" }
    return $best
}

# The window as it is drawn (without the invisible resize borders), scaled down and saved.
function Save-Window([IntPtr]$h, [string]$Name) {
    # The cursor off the window, so nothing shows a hover state.
    [KrupnoUi.Shot]::SetCursorPos(0, 0) | Out-Null
    Start-Sleep -Seconds 1
    $shot = Capture $h
    $frame = [KrupnoUi.Shot]::Frame($h)
    $visible = New-Object System.Drawing.Rectangle ($frame.L - $shot.Left), ($frame.T - $shot.Top), ($frame.R - $frame.L), ($frame.B - $frame.T)
    $cropped = $shot.Bitmap.Clone($visible, $shot.Bitmap.PixelFormat); $shot.Bitmap.Dispose()
    Save-Jpeg $cropped "$Out\$Name.jpg"; $cropped.Dispose()
    "  $Name.jpg"
}

function Save-Jpeg([System.Drawing.Bitmap]$Bitmap, [string]$Path) {
    $width = [Math]::Min($ImageWidth, $Bitmap.Width)
    $scaled = New-Object System.Drawing.Bitmap $width, ([int][Math]::Round($Bitmap.Height * $width / $Bitmap.Width))
    $g = [System.Drawing.Graphics]::FromImage($scaled)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($Bitmap, 0, 0, $scaled.Width, $scaled.Height); $g.Dispose()
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq "image/jpeg" }
    $parameters = New-Object System.Drawing.Imaging.EncoderParameters 1
    $parameters.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 92L
    $scaled.Save($Path, $codec, $parameters); $scaled.Dispose()
}

Backup-Settings
try {
    Settings $Model
    foreach ($theme in "light", "dark") {
        $p = Launch @($sample) -Theme $theme
        try {
            $h = $p.MainWindowHandle
            $scale = [KrupnoUi.Shot]::GetDpiForWindow($h) / 96.0
            [KrupnoUi.Shot]::SetWindowPos($h, [IntPtr]::Zero, [int](40 * $scale), [int](20 * $scale), [int]($Width * $scale), [int]($Height * $scale), 0x4) | Out-Null  # 0x4 = SWP_NOZORDER
            Start-Sleep -Seconds 1
            $w = $AE::FromHandle($h)
            Keys $p "^{ENTER}"
            WaitFor $w "^Done" | Out-Null
            Start-Sleep -Seconds 1
            # The slider: the divider between the eyes (a click moves it while the picture fits the window), then two wheel
            # notches around the face.
            $r = Image-Rect $w
            [KrupnoUi.Shot]::Click([int]($r.Left + 0.49 * $r.Width), [int]($r.Top + 0.5 * $r.Height))
            Start-Sleep -Milliseconds 400
            [KrupnoUi.Shot]::Wheel([int]($r.Left + 0.5 * $r.Width), [int]($r.Top + 0.42 * $r.Height), 2)
            Save-Window $h "split-$theme"
            # Side by side, from "fit to window": seven notches around the eye on the left, which both frames follow.
            Keys $p "^0"
            Invoke-Named $w "^Side by side$"
            Start-Sleep -Seconds 1
            $r = Image-Rect $w
            [KrupnoUi.Shot]::Wheel([int]($r.Left + 0.44 * $r.Width), [int]($r.Top + 0.36 * $r.Height), 7)
            Save-Window $h "side-$theme"
        }
        finally { Close $p }
    }
}
finally {
    Restore-Settings
    Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
}
