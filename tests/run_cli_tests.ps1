# Synthetic tests for krupno-cli.exe: the models in tests/fixtures/models produce an exactly known result,
# which tests/check_outputs.py verifies. Requires a Release build and Python with numpy and Pillow.
#   powershell -File tests\run_cli_tests.ps1 [-Exe <krupno-cli.exe>] [-Python <python.exe>] [-CheckFallback]
# -CheckFallback is for a machine without a GPU (CI): with KRUPNO_BASIC_RENDER_DRIVER=1, "-d auto" picks "Microsoft Basic Render Driver",
# DirectML does not start on it, and every "gpu" run must fall back to the CPU. With a real GPU the switch makes the runs FAIL.
param(
    [string]$Exe = "$PSScriptRoot\..\src\Krupno.Cli\bin\Release\net10.0-windows\win-x64\krupno-cli.exe",
    [string]$Python = "python",
    [switch]$CheckFallback
)
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$fx = "$PSScriptRoot\fixtures"; $img = "$fx\images"; $mod = "$fx\models"; $out = "$PSScriptRoot\out\cli"

if (-not (Test-Path -LiteralPath $Exe)) { Write-Error "$Exe not found; build it first: dotnet build Krupno.slnx -c Release"; exit 2 }
& $Python "$PSScriptRoot\make_fixtures.py" $fx
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to prepare the test images (numpy and Pillow are required: pip install -r tests\requirements.txt)"; exit 2 }
# Remove old results, otherwise a failed run would "pass" on a file left over from the previous run.
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
if ($CheckFallback) { $env:KRUPNO_BASIC_RENDER_DRIVER = "1" }

$script:failed = 0
function Run([string]$Label, [string[]]$CliArgs, [int]$Expect = 0) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    # PowerShell 5.1 wraps stderr lines in an ErrorRecord and prints an empty one as the exception type name.
    $text = @(& $Exe @CliArgs 2>&1 | ForEach-Object { if ($_ -is [Management.Automation.ErrorRecord]) { $_.Exception.Message } else { "$_" } })
    $code = $LASTEXITCODE
    [IO.File]::WriteAllLines("$out\$Label.log", [string[]]$text, (New-Object Text.UTF8Encoding $false))
    $ok = $code -eq $Expect
    if ($CheckFallback -and $Label.EndsWith("_gpu") -and -not ($text -match "DirectML is unavailable")) {
        $ok = $false
        $text += "Test error: no DirectML → CPU fallback"
    }
    if (-not $ok) { $script:failed++ }
    "{0} {1,-18} exit={2} {3,5:0.0}s  {4}" -f $(if ($ok) { "OK  " } else { "FAIL" }), $Label, $code, $sw.Elapsed.TotalSeconds,
        (($text | Where-Object { $_ -match "Device|→|Error|DirectML|memory|Done" }) -join " | ")
}

foreach ($dev in "auto", "cpu") {
    $tag = if ($dev -eq "auto") { "gpu" } else { "cpu" }
    Run "noise_red_$tag"   @("$img\noise_300x200.png", "-m", "$mod\nearest_red_x4.onnx", "-d", $dev, "-o", "$out\noise_red_$tag.png")
    Run "big_red_$tag"     @("$img\big_1500x1000.png", "-m", "$mod\nearest_red_x4.onnx", "-d", $dev, "-o", "$out\big_red_$tag.png")
    Run "big_fixed_$tag"   @("$img\big_1500x1000.png", "-m", "$mod\fixed64_x2.onnx", "-d", $dev, "-o", "$out\big_fixed_$tag.png")
    Run "noise_probe_$tag" @("$img\noise_300x200.png", "-m", "$mod\probe_x3.onnx", "-d", $dev, "-o", "$out\noise_probe_$tag.png")
}
Run "big_red_notile"  @("$img\big_1500x1000.png", "-m", "$mod\nearest_red_x4.onnx", "-t", "0", "-o", "$out\big_red_notile.png")
Run "alpha_red"       @("$img\alpha_257x129.png", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\alpha_red.png")
Run "noise_red_x2_5"  @("$img\noise_300x200.png", "-m", "$mod\nearest_red_x4.onnx", "-s", "2.5", "-o", "$out\noise_red_x2_5.png")
Run "noise_red_x8"    @("$img\noise_300x200.png", "-m", "$mod\nearest_red_x4.onnx", "-s", "8", "-o", "$out\noise_red_x8.png")
Run "orient6_red"     @("$img\orient6.jpg", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\orient6_red.png")
if (Test-Path -LiteralPath "$img\icc_60x40.png") {
    Run "icc_red"     @("$img\icc_60x40.png", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\icc_red.png")
}
Run "noise_legacy"    @("$img\noise_300x200.png", "-m", "$mod\legacy_x2.onnx", "-o", "$out\noise_legacy.png")
Run "batch"           @($img, "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\batch")
Run "broken"          @("$fx\broken\broken.png", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\broken.png") -Expect 1
Run "broken_gif"      @("$fx\broken\broken.gif", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\broken_gif.png") -Expect 1

# "-o" pointing at the source's own folder must not replace the source: the result gets " (2)".
New-Item -ItemType Directory -Force "$out\same" | Out-Null
Copy-Item -LiteralPath "$img\noise_300x200.png" "$out\same\noise.png"
Run "same_folder"     @("$out\same\noise.png", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\same")
# "x.png" and "x.jpg" in one folder both become "x.png": the second one gets " (2)" instead of overwriting the first.
New-Item -ItemType Directory -Force "$out\dup" | Out-Null
Copy-Item -LiteralPath "$img\noise_300x200.png" "$out\dup\x.png"
Copy-Item -LiteralPath "$img\orient6.jpg" "$out\dup\x.jpg"
Run "dup_names"       @("$out\dup", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\dup_out")

& $Python "$PSScriptRoot\check_outputs.py" $fx $out
$checkCode = $LASTEXITCODE
if ($script:failed -gt 0) { "Failed runs: $($script:failed)" }
exit $(if ($script:failed -eq 0 -and $checkCode -eq 0) { 0 } else { 1 })
