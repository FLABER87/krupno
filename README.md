# Krupno

[![Tests](https://github.com/FLABER87/krupno/actions/workflows/tests.yml/badge.svg?branch=main)](https://github.com/FLABER87/krupno/actions/workflows/tests.yml)

**Krupno** is an AI image upscaler for Windows built on .NET 10, WPF and ONNX Runtime with DirectML.
It runs on any DirectX 12 GPU (NVIDIA, AMD or Intel, integrated ones included) and falls back to the CPU.
It comes as a desktop app (`Krupno.exe`) and a command-line tool (`krupno-cli.exe`).
Get it from [Releases](https://github.com/FLABER87/krupno/releases): the installer downloads the models you pick during setup,
and the portable ZIP comes without models (take them from [models-v2](https://github.com/FLABER87/krupno/releases/tag/models-v2)).
See [Models](#models) for how to convert other PyTorch weights to ONNX.
The code is MIT-licensed, and each model has its own license.

The app's only third-party dependency is `Microsoft.ML.OnnxRuntime.DirectML`.
Images are read and written with WIC (built into Windows), the UI is WPF, and GPUs are enumerated directly through DXGI.

## Layout

```
src/Krupno.Core    library: model, tiling, inference, resampling, codecs
src/Krupno.Cli     command-line tool krupno-cli.exe
src/Krupno.App     WPF window (Krupno.exe)
tests/             synthetic tests through the CLI and UI scripts
installer/         Inno Setup installer and release build
tools/             PyTorch → ONNX model conversion (Python, development only)
models/            put .onnx files here (not committed to git)
```

## Building

Requires the .NET SDK 10.

```bash
dotnet build Krupno.slnx -c Release
```

The built programs end up here:
- `src/Krupno.Cli/bin/Release/net10.0-windows/win-x64/krupno-cli.exe`
- `src/Krupno.App/bin/Release/net10.0-windows/win-x64/Krupno.exe`

## Installer

The installer is built with Inno Setup. It installs the app together with .NET (self-contained publish), so .NET does not need to be installed separately.
The models are not bundled with the installer. You can tick them in the component list, and the installer downloads them from the [models-v2](https://github.com/FLABER87/krupno/releases/tag/models-v2) release and verifies their SHA-256.
When the app is updated, a model with the same SHA-256 is not downloaded again.
By default the app is installed for the current user, to `%LOCALAPPDATA%\Programs\Krupno`, without administrator rights. The "for all users" option installs it to Program Files.
Optional checkboxes:
- a desktop shortcut;
- the install folder in PATH, so `krupno-cli` can be run from any console;
- an "Upscale with Krupno" item in the Explorer context menu for images and folders. It is shown when a single item is selected: with a command string, Explorer would open a window per file.

Uninstalling removes all of the above and leaves `%LOCALAPPDATA%\Krupno` (settings, logs and your own models) alone. The installer UI is English only.

To build the installer and the portable ZIP, you also need Inno Setup 6.5+ (`winget install JRSoftware.InnoSetup`):

```bash
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

The output goes to `artifacts/`: `Krupno-<version>-setup-x64.exe`, `Krupno-<version>-win-x64.zip` and `SHA256SUMS.txt`. The version comes from `<Version>` in `Directory.Build.props`.
The script publishes `Krupno.exe` and `krupno-cli.exe` into one folder. Models from `models/` are not copied there, the DirectML debug files and `*.pdb` are removed, and the .NET, ONNX Runtime and DirectML licenses go to `licenses/`.
The installer lists the models in `installer/Krupno.iss` (file name, size, SHA-256). A new set of models is published as a separate release (`models-v3` and so on), and the link in `Krupno.iss` is updated. Old model releases are never overwritten: already built installers point to them.

Releases are built by the `.github/workflows/release.yml` workflow. To release a version:
1. Bump `<Version>` in `Directory.Build.props`.
2. Push a `v<version>` tag.
3. The workflow builds the installer and the ZIP and creates a draft release. Publish it manually on GitHub.

Code signing through SignPath Foundation is already built into the workflow. It is turned on by the setup described in [installer/signpath/README.md](installer/signpath/README.md).

## Models

The app works with ONNX models of the shape `[1, 3, H, W] → [1, 3, H·s, W·s]` (RGB, 0..1, fp32 or fp16).
To get such a model from PyTorch weights, use the conversion script:

```bash
pip install -r tools/requirements.txt
python tools/convert_to_onnx.py RealESRGAN_x4plus.pth
python tools/convert_to_onnx.py 4x-model.safetensors --fp16 --license "CC-BY-NC-SA-4.0"
```

The script detects the architecture with [spandrel](https://github.com/chaiNNer-org/spandrel): ESRGAN/Real-ESRGAN, Compact, SPAN, RealPLKSR, DAT, HAT, SwinIR, OmniSR and others are supported.
It writes the scale, the architecture, the input size requirements and the license into the ONNX metadata under `krupno.*` keys.
Models converted before the project was renamed store the same keys as `upscalr.*`; Krupno reads both.

Export order:
1. The model is exported with the classic tracer with a dynamic input size and checked against PyTorch on an input of a different size.
2. If the tracer baked the sizes into the graph, the model is exported again with `torch.export`: height and width are symbolic in the graph, and the exporter refuses if it cannot prove the graph is correct for any size. The result is checked against PyTorch on two more sizes. This happens with DAT/DAT2 (attention windows) and RealPLKSR with DySample (coordinate grid). The script finds the size multiple on its own: for DAT2 it is 8.
3. If that fails too, the model is exported with a static 256×256 input (`--static-fallback`), and Krupno cuts the image into tiles of exactly that size. The `--no-dynamo` flag skips the second step.

`torch.export` needs the `onnxscript` package. Converting DAT2 to ONNX this way takes several minutes. With PyTorch 2.14 both architectures get a dynamic input. With PyTorch 2.6 only RealPLKSR does, and DAT2 falls back to a static input: the 2.6 exporter is incompatible with the current onnxscript on its operators.

The script adjusts the `torch.export` graph for DirectML:
- DirectML does not accept `Reshape` with `allowzero=1`, so the script sets `allowzero=0` (upscaler tensors never have zero-sized dimensions);
- a slice assignment (`x[:, :16] = …`) normally becomes `ScatterND` with transposes and makes RealPLKSR four times slower, so the script builds it from `Slice` and `Concat`;
- DirectML computes `GridSample` (DySample) incorrectly when a single call outputs more than 2²³ pixels: it fails without any message and simply corrupts the bottom part of the image. The script samples each image of the batch separately. The limit then becomes a 724 px window for ×4, and the script stores it in the metadata (`krupno.size_maximum`). Krupno keeps the window within this limit for any tile: 768 and 1024 px tiles shrink to 724, and "No tiling" (`-t 0`) cuts an image larger than 724 px into windows of that size. In the app window the limit shows up in the model description: "Window up to 724 px".

The script also strips dead weight from the `torch.export` graph: the exporter's debug info in every node (call stacks, module names) and copies of identical buffers (`relative_position_index` in every DAT block). Without this, DAT2 fp16 weighs 51 MB instead of 24.

The `--fp16` flag halves the file size and VRAM usage; the model's input and output stay fp32.
Before exporting, the script compares the model in fp16 and fp32 right in PyTorch and refuses to export if fp16 produces garbage.
Example: UltraSharpV2 has an average error of 124 levels, so this model can only be used in fp32.
Verifying an fp16 model through ONNX Runtime on the CPU takes tens of minutes for heavy models (DAT2); it can be skipped with `--no-verify`.
But with `--no-verify` the dynamic size is not checked, so for DAT/HAT/DySample specify `--static 256` in that case.

Where to get weights:
- [Phhofm/models](https://github.com/Phhofm/models/releases): BHI/Nomos models for photos, CC-BY-4.0 license (can be bundled with attribution).
- [Kim2091/UltraSharpV2](https://huggingface.co/Kim2091/UltraSharpV2): CC BY-NC-SA 4.0 license, non-commercial use only.
- [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN/releases): BSD-3-Clause license.
- [OpenModelDB](https://openmodeldb.info/): hundreds of community models. **Each has its own license**, many are for non-commercial use only.

### Which model to use

Tested on a 1920×1200 photo, downscaled 4 times and recompressed as JPEG (q70), then upscaled back.
Speed: on an RTX 3080, 1920×1200 → 7680×4800 frame, second run, including reading PNG and writing BMP.

| Model | Architecture | License | Speed | Verdict |
|---|---|---|---|---|
| `4xBHI_realplksr_dysample_real` | RealPLKSR | CC-BY-4.0 | 6.7 s | **default**: almost as good as DAT2 at Real-ESRGAN speed |
| `4xBHI_dat2_real` | DAT2 | CC-BY-4.0 | 33 s (fp16), 41 s (fp32) | the most detailed, oversharpens in places |
| `4x-UltraSharpV2` | DAT2 | CC BY-NC-SA 4.0 | 42 s, fp32 only | best PSNR/SSIM, softer than DAT2; personal use only |
| `RealESRGAN_x4plus` | ESRGAN | BSD-3 | 8.6 s (fp32), 4.6 s (fp16) | outdated: "oil painting" look, invents textures |
| `realesr-general-x4v3` | Compact | BSD-3 | 1.6 s | fast but blurry |

All three modern models reproduce the structure of the rocks like the original, while Real-ESRGAN paints a forest in their place.
The speeds in the table are for the models from the `models-v2` release: RealPLKSR and DAT2 fp16 have a dynamic input (`torch.export`), DAT2 fp32 and UltraSharpV2 a static 256×256 one.
In `models-v1` all three modern models had a static input. The difference between the two is shown below.

#### Static or dynamic input

Measured under the same conditions: 1920×1200 → 7680×4800 frame, RTX 3080 (10 GB), 32 px overlap.
"Difference from reference" is the mean difference from PyTorch processing the whole frame at once (480×300 → 1920×1200 test, levels 0–255).
The lower it is, the less visible the tile seams are.

| Model | Input, tile | Speed | Difference from reference |
|---|---|---|---|
| RealPLKSR | static 256 | 7.4 s | 1.37 |
| RealPLKSR | dynamic, 384 (default) | 6.7 s | 0.89 |
| DAT2 fp16 | static 256 | 34.3 s | 2.48 |
| DAT2 fp16 | dynamic, 384 (default) | 32.9 s | 1.19 |
| DAT2 fp16 | dynamic, 448 | 28.3 s | 0.98 |
| DAT2 fp32 | static 256 | 43.5 s | 2.47 |
| DAT2 fp32 | dynamic, 256 | 47.3 s | 2.47 |
| DAT2 fp32 | dynamic, 320 | 49.4 s | 1.34 |
| DAT2 fp32 | dynamic, 384 (default), falls back to 320 | 60.7 s; later frames of a batch as with 320 | 1.34 |

- **RealPLKSR**: the dynamic input is faster and leaves smaller seams. With the same tile, the `torch.export` graph runs as fast as the static one.
- **DAT2 fp16**: the dynamic input with a 448 tile (`-t 448`) pays off: 18% faster, with a 2.5 times smaller difference from the reference. A 512 window no longer fits in VRAM.
- **DAT2 fp32 and UltraSharpV2** are better exported with a static input (`--no-dynamo`) on a 10 GB card. The same tile in the `torch.export` graph is 7–10% slower: DirectML optimizes its structure worse. A 384 window does not fit in memory, and Krupno reduces the tile to 320. The result is the same as with `-t 320`. About 10 s go to the failed attempt with 384, but only on the first frame of a batch: the following ones start with 320 right away. Previously the tile was halved, down to 192, and a frame took 71.5 s. A 352 window fits only by spilling into shared memory, without any error, and a frame takes 466 s. If you already have a dynamic model, use `-t 256` or `-t 320`.

`.safetensors` is the safer choice: `.pth` files are pickles, so load them only from trusted sources.

## Command-line tool

```bash
krupno-cli photo.jpg                          # → photo_x4.png next to the source
krupno-cli photo.jpg -s 2 -f jpg -q 92        # ×2 result as JPEG
krupno-cli C:\photos -o C:\photos_x4          # a whole folder
krupno-cli photo.png -m realesr-general-x4v3 -d gpu1 -t 256
krupno-cli photo.jpg --strength 70              # 70% network, 30% plain Lanczos resize
krupno-cli --list-models --list-devices
```

See `krupno-cli --help` for details. Ctrl+C cancels processing. While it runs, Windows does not go to sleep on idle.

A result never replaces a source image or another result of the same run: if `-o` points at the source's own folder,
or a folder has both `photo.jpg` and `photo.png`, the second name gets ` (2)` (`photo (2).png`). An existing file from an earlier run is overwritten.

In a batch, a file's line (size, time, output path) is printed as soon as it is upscaled, while its result is still being written in the background. If that save fails, `[i/n] Not saved: …` follows once the next file is done, the file counts as failed, and the exit code is 1.

Without `-m`, `4xBHI_realplksr_dysample_real` is used, or the first model in alphabetical order if it is missing. `--list-models` marks the default model with an asterisk.

## Window

The preview is on the left, the settings panel on the right.

- **Opening files:** drag photos or a folder into the window, click "Select files", or pass a path on the command line. With Ctrl held, dropped files are added to the queue; without it, they replace it.
- **Pasting:** Ctrl+V opens files copied in Explorer, or an image from the clipboard (a screenshot, "Copy image" in a browser). The queue works with files, so a pasted image is first saved as `Pictures\Krupno\Pasted <date> <time>.png`, and the result goes next to it like for any other file. A PNG on the clipboard (browsers and Office put one there) is saved as is, with transparency; a plain bitmap whose fourth byte is 0 everywhere, as most apps leave it, is taken as opaque.
- **Models** are shown as cards with descriptive names ("Photo, fast", "Photo, maximum detail"…). The recommended and non-commercial models are marked, and technical details are in the tooltip. Known models are described in `ViewModels/ModelItem.cs`; the others are shown by file name.
- **Strength** (10–100%, under the models) mixes the network's result with a plain Lanczos resize of the original. Below 100% it tones down the textures a model invents and its oversharpening (see [How it works](#how-it-works)).
- **Scale and format** are picked with toggle buttons. The output size is shown right away, and JPEG has a quality slider.
- **Device and tile size** are tucked away under "Advanced"; by default both are chosen automatically.
- **Comparison:** a slider wipe or two frames side by side. Zoom is measured in output pixels (100% is pixel for pixel), the mouse wheel zooms toward the point under the cursor, the right or middle button pans the image, and a double click toggles "Fit to window" ↔ 1:1.
- **During processing** finished tiles are highlighted on the preview, with the percentage, the remaining time and the tile number next to it. Progress also shows on the Windows taskbar icon. While processing runs, Windows does not go to sleep on idle (the display may still turn off; closing the lid or choosing Sleep works as usual).
- **A batch** is shown as a strip of thumbnails with each file's status: processing, done, error. A file keeps the "processing" mark while its result is written, which in a batch overlaps with the next file (see [How it works](#how-it-works)). Clicking a thumbnail shows the comparison for that file.
- **Errors and the log.** Each session of the window writes a log to `%LOCALAPPDATA%\Krupno\logs` (the 20 newest are kept). It starts with the versions of Krupno, Windows, .NET, ONNX Runtime and DirectML and the GPUs with their driver versions, then records each run (model, device, tile, strength, scale, format), every file and its result, warnings (a reduced tile, the fallback to the CPU) and every error with its stack trace. After a failed run, "Copy error details" puts the same facts for that run on the clipboard, ready for a bug report, with the path to the log. The log stays on the computer, and it holds file paths, so look it over before sharing it. The command-line tool writes no log: everything goes to the console.
- **The theme** is light or dark to match the system, with the system accent color. On Windows 10 the window title bar turns dark too. The `KRUPNO_THEME=light|dark` variable forces a theme for testing.

Keyboard shortcuts:

| Keys | Action |
|---|---|
| Ctrl+O | open files |
| Ctrl+Enter | upscale |
| Esc | cancel processing |
| Ctrl+0 / Ctrl+1 | fit to window / pixel for pixel |
| Ctrl+Plus / Ctrl+Minus | zoom in / zoom out |
| Ctrl+C | copy the result |
| Ctrl+V | paste files or an image |

The first launch starts with the default model, ×4, PNG and the "Auto" device and tile. Every choice is saved in `%LOCALAPPDATA%\Krupno\settings.json` as soon as it is made: the model, strength, scale, format, JPEG quality, output folder, device and tile size. The window's size and position (and whether it was maximized) are saved when it closes; a window that would land on a monitor that is no longer connected is moved onto a visible one.
The GPU is saved by name, since its DXGI index may change. If the saved model or GPU is gone, a value is not one the window offers, or the settings file is corrupt or missing, the window falls back to the defaults. A saved output folder is checked before processing starts: if its drive is gone, the window says so right away instead of after the first file.

The app icon is drawn by `tools/make_icon.py` (requires Pillow and numpy).

## Tests

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
Covered: tiling (including a model with a static 64×64 input and a run without tiling), a probe run of a model without metadata, reading `krupno.*` and `upscalr.*` metadata,
fractional scale, ×8 in two passes, alpha channel (with the color fill under transparent pixels), EXIF rotation, ICC profiles (a Display P3 image keeps its pixels and profile; CMYK and grayscale images are converted to sRGB, also with a CMYK profile far from SWOP), folder processing,
strength (the blend is checked against the same Lanczos filter computed in numpy; with transparency the alpha must stay as at full strength),
a save that fails in the background during a batch,
output names that would overwrite a source or another result, and broken files.
`python tests/make_fixtures.py --models` regenerates the models (it also needs the `onnx` package).
On GitHub, the `.github/workflows/tests.yml` workflow (`windows-latest`) builds and runs these tests on every push and pull request. The runner has no GPU, and Krupno does not count "Microsoft Basic Render Driver" as one. So the workflow runs the script with the `-CheckFallback` switch: it sets `KRUPNO_BASIC_RENDER_DRIVER=1`, "Auto" picks that adapter, DirectML fails to start on it, and every "GPU" run must fall back to the CPU. This is how the fallback is tested. If the tests fail, `tests/out/cli` is uploaded as the `cli-tests-out` artifact.
Dependabot (`.github/dependabot.yml`) opens weekly update PRs for ONNX Runtime and the GitHub Actions, so these tests check every update before it is merged. The Python requirements are left out: `tools/requirements.txt` sets deliberate lower bounds, which Dependabot would raise.

The UI scripts launch `Krupno.exe` and drive the window through UI Automation.
They need real models in `models/`: `4xBHI_realplksr_dysample_real` and `4xBHI_dat2_real_fp16` by default; others are set with the `-Model` and `-SlowModel` parameters.
- `tests\gui_states.ps1` walks through the window states (empty, tile-by-tile processing, result, side-by-side comparison, batch, error, light theme) and saves screenshots to `tests/out/gui`. The script itself only checks that the states are reached; a human looks at the screenshots. The `color` scenario also checks pixels. A Display P3 image must look the way Windows' color-managed decoding shows it, before and after processing and in the filmstrip. An untagged copy must be shown unchanged, and the saved result must keep the stored pixels. About 2 min.
- `tests\gui_keys.ps1` checks the keyboard shortcuts: preview zoom, Ctrl+Enter, Ctrl+C (image size on the clipboard), Ctrl+V (a bitmap and a PNG pasted from the clipboard are saved to `Pictures\Krupno` with the right size; the script deletes them afterwards) and Esc. The keys are sent through SendKeys, so it is best not to touch the keyboard and mouse during the run (about 30 s). If the Krupno window is not in the foreground, the script stops and sends no keys.

Both scripts swap `%LOCALAPPDATA%\Krupno\settings.json` for the duration of the run and restore the previous one afterwards.

## How it works

- **Seamless tiling.** The image is cut into fixed-size windows that never go beyond its edges. Each tile keeps at least `--pad` pixels of real context around it, and only the central part of the tile goes into the result. Deep models have a receptive field larger than the overlap, so small deviations at the seams are possible. For RealESRGAN_x4plus compared with a whole-image run: with a 32 px overlap (the default), up to 9 levels on 0.01% of pixels; with 64 px, up to 1 level.
- **Input shape for DirectML.** All tiles of an image are fed to the network in a window of the same size. The size is fixed through `AddFreeDimensionOverrideByName`, and DirectML compiles the graph once per image size.
- **Checked against a reference.** Without tiling, the result matches the PyTorch implementation of Real-ESRGAN within ±1 level, both on DirectML and on the CPU.
- **Out of memory.** If the window does not fit on the device, the tile shrinks and the pass starts over. Up to 512 px, the tile steps down a 64 px grid, respecting the model's size multiple (512 → 448 → 384 → 320 → 256 → …, no smaller than 128). A larger window is halved, but not below 512: every failed attempt costs a graph compilation, about 10 s for DAT2. Previously the tile was always halved and skipped the size that would fit: DAT2 fp32 on 10 GB dropped from 384 straight to 192. The chosen tile is remembered for the model on that device: later frames of a batch and the second ×8 pass start with it right away, and the reduction message is printed once. With DAT2 fp32 and `-t 384`, a batch of two 1920×1200 frames takes 62.1 + 51.7 s: only the first frame spends the extra ~10 s. In the window, the limit lasts until the model or device changes, and the next processing run shows "Tile 320 px (chosen after running out of memory)" once. To try a larger tile again, for example after freeing VRAM, switch the model or restart the app. DirectML does not report spilling into shared memory as an exception, so it cannot be caught this way.
- **CPU fallback.** In "Auto" mode the GPU with the most memory is used; if DirectML fails to start, the work runs on the CPU, and the window says so during processing. Running out of video memory is not a reason to fall back: once DirectML has created a session for the model, or when the error reads as out of memory, the tile shrinks instead. The software "Microsoft Basic Render Driver" does not count as a GPU: on a machine without a GPU it is not in the device list, and "Auto" goes straight to the CPU.
- **Any scale.** The network runs as many times as needed (×8 with a ×4 model is two passes), then the result is resized precisely with a Lanczos-3 filter. If the last pass would not fit in memory at full size (×8 of a 1920×1200 photo makes a 30720×19200 intermediate), its input is first reduced so that the pass lands just above the target size, and a message says so.
- **Strength.** Below 100%, the result is `plain + strength × (network − plain)`, where `plain` is a Lanczos resize of the pixels the network saw (with transparency, the colors filled in under alpha 0) to the output size, clipped to 0..255 like a saved image. The alpha stays as computed for the full-strength result. The mix is done band by band, so the plain resize never exists whole: for a 7680×4800 result it adds under a second on a 4-core CPU.
- **Batches.** A result is written while the next file is loaded and upscaled, so the GPU doesn't wait for the encoder (compressing a 7680×4800 PNG can take seconds, most of a frame for a fast model). One save runs at a time, so files finish in order. The last file of a batch, and a result larger than 512 MB, are written right away instead: one more result in memory, plus the encoder's copy of it, could make a large batch run out of memory. On cancellation or an error, a result already being written is finished and counts as done.
- **The alpha channel** is scaled separately (Lanczos); the network processes RGB. The color under fully transparent pixels is first filled from the nearest visible pixel, so the edges don't pick up the color stored there (usually black), and the final resize weights colors by alpha.
- **EXIF rotation** is applied to the pixels. An RGB ICC profile is carried over to the result (see below), and the metadata too if the output format matches the source.
- **Color profiles.** The pixels are upscaled as stored: an Adobe RGB or Display P3 photo stays in its color space, and the result carries the same profile (BMP, which can't hold one, is converted to sRGB). The result is always RGB, which a CMYK or grayscale profile can't describe. So CMYK and grayscale images are converted with their profile to sRGB on load, the way color-managed viewers show them, and the result has no profile (untagged means sRGB). A profile that doesn't fit the pixels, such as a grayscale profile on a color image, is dropped. The copy of the profile in the source's metadata (a PNG iCCP chunk, JPEG APP2 segments, a TIFF tag) stays behind, so only a profile that fits goes into the result. WPF draws everything as sRGB, so the window converts the comparison and the filmstrip thumbnails from the embedded profile to sRGB, for display only. WIC does the conversion, so the colors match Windows' own color-managed decoding. Krupno converts to sRGB, not to the monitor's profile. Profiles that match sRGB within a couple of levels are left alone. A large result is converted in place, a few megabytes at a time, without another full-size copy. Ctrl+C copies the result as it is shown, in sRGB.

## What's next

- A 16-bit-per-channel pipeline (currently 8-bit).
- Streaming output for images that do not fit in memory.
- WebP/AVIF output (WIC has no encoders for them; this needs a custom implementation or a separate library).
- Overlapping the preparation of the next tile with inference of the current one.
- A model catalog with downloads and license checks right in the window (currently only the installer downloads models).
- Unit tests for `Krupno.Core`. The current tests are end-to-end: synthetic models through the CLI (also run in CI) and UI scripts (see [Tests](#tests)).

## License

The code is under the MIT license, see [LICENSE](LICENSE). It does not cover the models: each has its own license (see [Where to get weights](#models)).
