# Shared code for the UI scripts (gui_states.ps1, gui_keys.ps1): launching Krupno.exe, finding elements via UI Automation,
# window screenshots, keys, swapping and restoring %LOCALAPPDATA%\Krupno\settings.json. Included via ". gui_common.ps1".
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
if (-not ("KrupnoUi.Win" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
namespace KrupnoUi {
    public static class Win {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    }
}
"@
}
# Without this, PowerShell gets "virtual" window coordinates at 200% scaling and the screenshot is cropped.
[KrupnoUi.Win]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null

$AE = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
$SettingsPath = "$env:LOCALAPPDATA\Krupno\settings.json"
$SettingsBackup = "$SettingsPath.ui-test-backup"

function Assert-Model([string]$Name) {
    $dirs = @("$env:LOCALAPPDATA\Krupno\models", (Join-Path (Split-Path $Exe) "models"))
    if (-not ($dirs | Where-Object { Test-Path -LiteralPath "$_\$Name.onnx" })) {
        throw "Model `"$Name`" not found ($($dirs -join '; ')). Put it in models/ and rebuild, or pass a different one as a parameter."
    }
}

# The scripts pick the model through settings.json, so the user's settings are backed up and restored afterwards.
# If a backup is left over from an interrupted run, it holds the real settings, so it is not overwritten.
function Backup-Settings {
    if (-not (Test-Path -LiteralPath $SettingsBackup) -and (Test-Path -LiteralPath $SettingsPath)) {
        Copy-Item -LiteralPath $SettingsPath $SettingsBackup
    }
}
function Restore-Settings {
    if (Test-Path -LiteralPath $SettingsBackup) { Move-Item -LiteralPath $SettingsBackup $SettingsPath -Force }
    else { Remove-Item -LiteralPath $SettingsPath -ErrorAction SilentlyContinue }
}
# The window is set to English whatever the Windows language: the scripts find buttons and texts by their English names.
# The update check is off: the scripts stay offline, and a newer release's notice doesn't change the screenshots.
# $Extra adds more settings, as JSON members after a comma: ', "Denoise": true'.
function Settings([string]$Model, [string]$Extra = "") {
    New-Item -ItemType Directory -Force (Split-Path $SettingsPath) | Out-Null
    [IO.File]::WriteAllText($SettingsPath,
        "{ `"Model`": `"$Model`", `"DeviceKind`": `"Auto`", `"Language`": `"en`", `"CheckForUpdates`": false$Extra }",
        (New-Object Text.UTF8Encoding $false))
}

# --new-window: a window of its own even if Krupno is already open (otherwise the files would go to that window).
# -SingleInstance leaves it out, to test handing files over to an open window.
function Launch([string[]]$Arguments = @(), [string]$Theme, [switch]$SingleInstance) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $Exe
    $all = if ($SingleInstance) { @($Arguments) } else { @("--new-window") + @($Arguments) }
    $psi.Arguments = ($all | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $psi.UseShellExecute = $false
    if ($Theme) { $psi.Environment["KRUPNO_THEME"] = $Theme }
    $p = [System.Diagnostics.Process]::Start($psi)
    $deadline = (Get-Date).AddSeconds(30)
    while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300; $p.Refresh() }
    if ($p.MainWindowHandle -eq 0) { Close $p; throw "the Krupno window did not appear within 30 s" }
    Start-Sleep -Seconds 2
    Activate $p | Out-Null
    return $p
}
function Close($p) {
    if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $p.HasExited) { $p.Kill() } }
}
function Activate($p) {
    [KrupnoUi.Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 300
    if ([KrupnoUi.Win]::GetForegroundWindow() -ne $p.MainWindowHandle) {
        (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null
        Start-Sleep -Milliseconds 300
    }
    return [KrupnoUi.Win]::GetForegroundWindow() -eq $p.MainWindowHandle
}
# SendKeys sends keys to whatever window is active: without this check, Esc or Ctrl+Enter could go to another window.
function Keys($p, [string]$Keys) {
    if (-not (Activate $p)) { throw "could not bring the Krupno window to the foreground, keys `"$Keys`" were not sent" }
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Milliseconds 600
}

# The window as drawn, with its top-left corner in screen coordinates.
function Capture([IntPtr]$h) {
    $r = New-Object KrupnoUi.Win+RECT
    [KrupnoUi.Win]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc(); [KrupnoUi.Win]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()  # 2 = PW_RENDERFULLCONTENT
    return @{ Bitmap = $bmp; Left = $r.L; Top = $r.T }
}
function Shot([IntPtr]$h, [string]$Name) {
    Start-Sleep -Milliseconds 700
    $shot = Capture $h
    $shot.Bitmap.Save("$Out\$Name.png", [System.Drawing.Imaging.ImageFormat]::Png); $shot.Bitmap.Dispose()
    "  screenshot: $Name"
}

function FindName($Root, [string]$Pattern) {
    foreach ($e in $Root.FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.Name -match $Pattern) { return $e }
    }
    return $null
}
function Invoke-Named($Root, [string]$Pattern) {
    $e = FindName $Root $Pattern
    if (-not $e) { throw "element '$Pattern' not found" }
    $p = $null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke() }
    elseif ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select() }
    else { throw "element '$Pattern' cannot be clicked" }
}
# Name of the first element matching the pattern; on timeout, an exception (or $null with -Soft).
function WaitFor($Root, [string]$Pattern, [int]$Seconds = 120, [switch]$Soft) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $e = FindName $Root $Pattern
        if ($e) { return $e.Current.Name }
        Start-Sleep -Milliseconds 300
    }
    if ($Soft) { return $null }
    throw "'$Pattern' did not appear within $Seconds s"
}
