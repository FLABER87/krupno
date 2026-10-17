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
# Strength: the network's result mixed with a plain Lanczos resize; with transparency the alpha must stay as at full strength.
Run "noise_red_s50"   @("$img\noise_300x200.png", "-m", "$mod\nearest_red_x4.onnx", "--strength", "50", "-o", "$out\noise_red_s50.png")
Run "alpha_red_s30"   @("$img\alpha_257x129.png", "-m", "$mod\nearest_red_x4.onnx", "--strength", "30", "-o", "$out\alpha_red_s30.png")
Run "strength_0"      @("$img\noise_300x200.png", "-m", "$mod\nearest_red_x4.onnx", "--strength", "0", "-o", "$out\strength_0.png") -Expect 2
# Target sizes: --width gives that width (×2.5 here) and a name saying so next to the source; --long-side counts the longer
# side after the EXIF rotation (90×160 → 540×960); -s together with a target size is refused.
New-Item -ItemType Directory -Force "$out\target" | Out-Null
Copy-Item -LiteralPath "$img\noise_300x200.png" "$out\target\noise.png"
Run "target_width"    @("$out\target\noise.png", "-m", "$mod\nearest_red_x4.onnx", "--width", "750")
Run "target_long"     @("$img\orient6.jpg", "-m", "$mod\nearest_red_x4.onnx", "--long-side", "960", "-o", "$out\target_long.png")
Run "target_conflict" @("$img\noise_300x200.png", "-m", "$mod\nearest_red_x4.onnx", "-s", "2", "--width", "750", "-o", "$out\target_conflict.png") -Expect 2
Run "orient6_red"     @("$img\orient6.jpg", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\orient6_red.png")
if (Test-Path -LiteralPath "$img\icc_60x40.png") {
    Run "icc_red"     @("$img\icc_60x40.png", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\icc_red.png")
}
# Display P3: the pixels are processed as stored, not converted to sRGB, and the profile goes along with them.
Run "p3_red"          @("$fx\color\wide_p3.png", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\p3_red.png")
# CMYK and grayscale profiles: the pixels are converted to sRGB, and the result carries no profile, also in the same container.
if (Test-Path -LiteralPath "$fx\profiles\cmyk_swop.jpg") {
    Run "cmyk_swop"     @("$fx\profiles\cmyk_swop.jpg", "-m", "$mod\legacy_x2.onnx", "-o", "$out\cmyk_swop.png")
    Run "cmyk_swop_jpg" @("$fx\profiles\cmyk_swop.jpg", "-m", "$mod\legacy_x2.onnx", "-o", "$out\cmyk_swop_jpg.jpg")
}
Run "cmyk_toy_tif"    @("$fx\profiles\cmyk_toy.tif", "-m", "$mod\legacy_x2.onnx", "-o", "$out\cmyk_toy_tif.tif")
Run "gray_icc"        @("$fx\profiles\gray.png", "-m", "$mod\legacy_x2.onnx", "-o", "$out\gray_icc.png")
Run "gray_alpha_icc"  @("$fx\profiles\gray_alpha.png", "-m", "$mod\legacy_x2.onnx", "-o", "$out\gray_alpha_icc.png")
Run "noise_legacy"    @("$img\noise_300x200.png", "-m", "$mod\legacy_x2.onnx", "-o", "$out\noise_legacy.png")
# fp16 input and output: the conversion on the way in and on the way out (a row of the tile core at a time).
Run "noise_half"      @("$img\noise_300x200.png", "-m", "$mod\half_x2.onnx", "-o", "$out\noise_half.png")
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
# The first file of a batch is written while the second one is processed. A folder in the way of its output file makes that
# save fail in the background: it must be reported with the file's number, and the rest of the batch still saved.
New-Item -ItemType Directory -Force "$out\bgsave", "$out\bgsave_out\a.png" | Out-Null
Copy-Item -LiteralPath "$img\noise_300x200.png" "$out\bgsave\a.png"
Copy-Item -LiteralPath "$img\noise_300x200.png" "$out\bgsave\b.png"
Run "bg_save_fail"    @("$out\bgsave", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\bgsave_out") -Expect 1
# -r repeats the subfolders in the output folder. --skip-existing then resumes: with every result there it ends before
# loading the model, and with one result deleted it does only that one.
New-Item -ItemType Directory -Force "$out\tree\sub" | Out-Null
Copy-Item -LiteralPath "$img\noise_300x200.png" "$out\tree\a.png"
Copy-Item -LiteralPath "$img\orient6.jpg" "$out\tree\sub\b.jpg"
Run "recursive"       @("$out\tree", "-r", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\tree_out")
Run "skip_all"        @("$out\tree", "-r", "--skip-existing", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\tree_out")
Remove-Item -LiteralPath "$out\tree_out\sub\b.png"
Run "skip_some"       @("$out\tree", "-r", "--skip-existing", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\tree_out")
# Several inputs, one of them a pattern that krupno-cli expands itself (PowerShell passes it on as it is).
New-Item -ItemType Directory -Force "$out\multi" | Out-Null
Copy-Item -LiteralPath "$img\noise_300x200.png" "$out\multi\a.png"
Copy-Item -LiteralPath "$img\noise_300x200.png" "$out\multi\b.png"
Copy-Item -LiteralPath "$img\orient6.jpg" "$out\multi\c.jpg"
Run "multi_pattern"   @("$out\multi\*.png", "$img\orient6.jpg", "-m", "$mod\nearest_red_x4.onnx", "-o", "$out\multi_out")

& $Python "$PSScriptRoot\check_outputs.py" $fx $out
$checkCode = $LASTEXITCODE
if ($script:failed -gt 0) { "Failed runs: $($script:failed)" }
exit $(if ($script:failed -eq 0 -and $checkCode -eq 0) { 0 } else { 1 })
