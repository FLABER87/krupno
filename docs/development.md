# Development

## Layout

```
src/Krupno.Core    library: model, tiling, inference, resampling, codecs
src/Krupno.Cli     command-line tool krupno-cli.exe
src/Krupno.App     WPF window (Krupno.exe)
tests/             unit tests (Krupno.Core.Tests), synthetic tests through the CLI and UI scripts
installer/         Inno Setup installer and release build (see installer.md)
tools/             PyTorch → ONNX model conversion (Python, development only; see models.md)
models/            put .onnx files here (not committed to git)
docs/              these notes
```

Krupno is built on .NET 10, WPF and Windows ML (ONNX Runtime with DirectML).
The app's only third-party dependency is `Microsoft.Windows.AI.MachineLearning` (Windows ML: ONNX Runtime 1.27 with its .NET API, DirectML and the catalog of vendor execution providers). It replaced `Microsoft.ML.OnnxRuntime.DirectML`, which stopped at 1.24.4. Windows ML needs a Windows SDK version in the target framework (`net10.0-windows10.0.19041.0`), which brings the C#/WinRT projection (`Microsoft.Windows.SDK.NET.dll`, about 25 MB).
Images are read and written with WIC (built into Windows), the UI is WPF, and GPUs are enumerated directly through DXGI.

## Building

Requires the .NET SDK 10.

```bash
dotnet build Krupno.slnx -c Release
```

The built programs end up here:
- `src/Krupno.Cli/bin/Release/net10.0-windows10.0.19041.0/win-x64/krupno-cli.exe`
- `src/Krupno.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/Krupno.exe`

The installer and the portable ZIP are built by `installer\build.ps1`, see [installer.md](installer.md).
The app icon is drawn by `tools/make_icon.py` (requires Pillow and numpy).

## Tests

The unit tests (`tests/Krupno.Core.Tests`, xUnit) run in a couple of seconds and need nothing besides the .NET SDK:

```bash
dotnet test tests/Krupno.Core.Tests -c Release
```

They cover the tile plan (cores that cover the image without gaps, real context around each, mirroring at the edges), output names that would clash,
the Lanczos resizes (the blend for strength clips the plain resize to 0..255 first and leaves alpha alone; colors under transparency don't leak),
target sizes and the number of passes, the SIMD packing of the network's output against the scalar conversion (NaN, infinities and values outside 0..1 included),
the command-line options, and the whole pipeline on the CPU with the synthetic models below: exact results for every model, strength 50% against the formula,
and `--pad 64` giving the same result in 35 tiles instead of 24 on a 1920×1200 image. They also hand paths from 12 clients at once to a window over the named pipe
(`SingleInstance` from the app, compiled into the tests), and every path must arrive.

### Synthetic tests through the command-line tool

The synthetic tests run the Release build through the command-line tool. The models in `tests/fixtures/models` are a few hundred bytes each.
Each one upscales the image with nearest neighbor and keeps some of the color channels. So the correct result is known down to the pixel, and a mixed-up channel order shows immediately.
Requires Python with numpy and Pillow:

```bash
pip install -r tests/requirements.txt
dotnet build Krupno.slnx -c Release
powershell -ExecutionPolicy Bypass -File tests\run_cli_tests.ps1
```

The script creates the input images (`tests/make_fixtures.py`), runs `krupno-cli.exe` on the GPU and on the CPU, and puts the results and logs into `tests/out/cli`.
Then `tests/check_outputs.py` compares them with the expectations, and the script exits with code 0 only if everything matches. A run takes about 15 s.
Covered: tiling (including a model with a static 64×64 input and a run without tiling), a probe run of a model without metadata, a model with fp16 input and output, reading `krupno.*` and `upscalr.*` metadata,
fractional scale, ×8 in two passes, alpha channel (with the color fill under transparent pixels), EXIF rotation, ICC profiles (a Display P3 image keeps its pixels and profile; CMYK and grayscale images are converted to sRGB, also with a CMYK profile far from SWOP), folder processing,
strength (the blend is checked against the same Lanczos filter computed in numpy; with transparency the alpha must stay as at full strength),
a save that fails in the background during a batch, target sizes (`--width` with the name it gives, `--long-side` after the EXIF rotation, `-s` together with a target size refused),
output names that would overwrite a source or another result, and broken files.
`python tests/make_fixtures.py --models` regenerates the models (it also needs the `onnx` package).

### CI

On GitHub, the `.github/workflows/tests.yml` workflow (`windows-latest`) builds and runs the unit tests and these tests on every push and pull request; it also compiles the installer script (see [installer.md](installer.md#languages)). The runner has no GPU, and Krupno does not count "Microsoft Basic Render Driver" as one. So the workflow runs the script with the `-CheckFallback` switch: it sets `KRUPNO_BASIC_RENDER_DRIVER=1`, "Auto" picks that adapter, DirectML fails to start on it, and every "GPU" run must fall back to the CPU. This is how the fallback is tested. If the tests fail, `tests/out/cli` is uploaded as the `cli-tests-out` artifact.
Dependabot (`.github/dependabot.yml`) opens weekly update PRs for ONNX Runtime and the GitHub Actions, so these tests check every update before it is merged. The Python requirements are left out: `tools/requirements.txt` sets deliberate lower bounds (see the PyTorch notes in [models.md](models.md#converting-pytorch-weights)), which Dependabot would raise.

### UI scripts

The UI scripts launch `Krupno.exe` and drive the window through UI Automation.
They need real models in `models/`: `4xBHI_realplksr_dysample_real` and `4xBHI_dat2_real_fp16` by default; others are set with the `-Model` and `-SlowModel` parameters.
- `tests\gui_states.ps1` walks through the window states (empty, tile-by-tile processing, result, side-by-side comparison, batch, error, light theme, the settings window) and saves screenshots to `tests/out/gui`. The script itself only checks that the states are reached; a human looks at the screenshots. The `color` scenario also checks pixels. A Display P3 image must look the way Windows' color-managed decoding shows it, before and after processing and in the filmstrip. An untagged copy must be shown unchanged, and the saved result must keep the stored pixels. The `cpunodes` scenario puts `cubic_x2.onnx` from the fixtures next to the app: DirectML leaves its bicubic Resize to the CPU, and the window's log must say so, but not again for a run in the same window where "Remove noise" at ×1 runs the denoiser alone. About 2 min.
- `tests\gui_keys.ps1` checks the keyboard shortcuts: preview zoom, Ctrl+Enter, Ctrl+C (image size on the clipboard), Ctrl+V (a bitmap and a PNG pasted from the clipboard are saved to `Pictures\Krupno` with the right size; the script deletes them afterwards) and Esc. It also checks handing files over to an open window: two Krupno.exe started together give one queue of two files and exit, and one started 3 seconds later replaces the queue (skipped if a Krupno window is already open). The UI scripts start their windows with `--new-window`, so a window you have open doesn't take their files. The keys are sent through SendKeys, so it is best not to touch the keyboard and mouse during the run (about 30 s). If the Krupno window is not in the foreground, the script stops and sends no keys.
- `tests\readme_screenshot.ps1` takes the README screenshots in `docs/images` again after the window changes: it upscales `tests/fixtures/readme/fox.jpg` with "Photo, maximum detail" (`4xBHI_dat2_real_fp16`), sets the slider, the side-by-side mode and the zoom with the mouse and saves both themes. About 40 s; don't touch the mouse meanwhile.

The scripts swap `%LOCALAPPDATA%\Krupno\settings.json` for the duration of the run and restore the previous one afterwards; the settings they write set the window to English, since they find buttons by their English names, and turn the update check off, so the scripts stay offline and a newer release doesn't change the screenshots.

## Translations

The window's text is in `src/Krupno.App/Properties/Strings.resx` (English) and `Strings.<culture>.resx` next to it, one file per language; the progress messages and errors of the library are in `src/Krupno.Core/Properties` the same way. The build turns each translation into a satellite assembly (`uk\Krupno.resources.dll` and so on), and .NET picks the one for the UI language. XAML reaches the text through `{x:Static p:Strings.Key}`, C# through `Strings.Key`; `Strings.Designer.cs` is generated by Visual Studio or Rider when `Strings.resx` is saved.

- A text with plural forms holds one form per plural category of its language, separated by `|`: two in English (`{0} file|{0} files`), three in Ukrainian, Russian and Polish, one in Chinese, Japanese, Korean, Vietnamese and Indonesian (`Krupno.Core/Localization/Plurals.cs` has the rules). Its comment in `Strings.resx` says so.
- To add a language: copy `Strings.resx` in both projects to `Strings.<culture>.resx`, translate the values, add the language to `Infrastructure/UiLanguage.cs` and, if its plural rules differ from English, to `Plurals.cs`. The installer has its own texts for each language (see [installer.md](installer.md#languages)).
- `LocalizationTests` checks that every translation has the same keys as `Strings.resx`, the same placeholders, valid `string.Format` text and the right number of plural forms, that `Strings.Designer.cs` matches `Strings.resx`, and that every text is used somewhere.

## What's next

- A 16-bit-per-channel pipeline (currently 8-bit).
- Streaming output for images that do not fit in memory.
- WebP/AVIF output (WIC has no encoders for them; this needs a custom implementation or a separate library).
- A model catalog with downloads and license checks right in the window (currently only the installer downloads models).
