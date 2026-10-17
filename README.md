# Krupno

[![Tests](https://github.com/FLABER87/krupno/actions/workflows/tests.yml/badge.svg?branch=main)](https://github.com/FLABER87/krupno/actions/workflows/tests.yml)

**Krupno** is an AI image upscaler for Windows built on .NET 10, WPF and Windows ML (ONNX Runtime with DirectML).
It runs on any DirectX 12 GPU (NVIDIA, AMD or Intel, integrated ones included) and falls back to the CPU. On Windows 11 24H2 and later it can also use the GPU and NPU vendors' own execution providers (NVIDIA TensorRT-RTX, Intel OpenVINO and others), which Windows downloads on request.
It needs Windows 10 1903 or later.
It comes as a desktop app (`Krupno.exe`) and a command-line tool (`krupno-cli.exe`).
Get it from [Releases](https://github.com/FLABER87/krupno/releases): the installer downloads the models you pick during setup,
and the portable ZIP comes without models (take them from [models-v2](https://github.com/FLABER87/krupno/releases/tag/models-v2)).
See [Models](#models) for how to convert other PyTorch weights to ONNX.
The code is MIT-licensed, and each model has its own license.

The app's only third-party dependency is `Microsoft.Windows.AI.MachineLearning` (Windows ML: ONNX Runtime 1.27 with its .NET API, DirectML and the catalog of vendor execution providers). It replaced `Microsoft.ML.OnnxRuntime.DirectML`, which stopped at 1.24.4. Windows ML needs a Windows SDK version in the target framework (`net10.0-windows10.0.19041.0`), which brings the C#/WinRT projection (`Microsoft.Windows.SDK.NET.dll`, about 25 MB).
Images are read and written with WIC (built into Windows), the UI is WPF, and GPUs are enumerated directly through DXGI.

## Layout

```
src/Krupno.Core    library: model, tiling, inference, resampling, codecs
src/Krupno.Cli     command-line tool krupno-cli.exe
src/Krupno.App     WPF window (Krupno.exe)
tests/             unit tests (Krupno.Core.Tests), synthetic tests through the CLI and UI scripts
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
- `src/Krupno.Cli/bin/Release/net10.0-windows10.0.19041.0/win-x64/krupno-cli.exe`
- `src/Krupno.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/Krupno.exe`

## Installer

The installer is built with Inno Setup. It installs the app together with .NET (self-contained publish), so .NET does not need to be installed separately.
The models are not bundled with the installer. You can tick them in the component list, and the installer downloads them from the [models-v2](https://github.com/FLABER87/krupno/releases/tag/models-v2) release and verifies their SHA-256.
When the app is updated, a model with the same SHA-256 is not downloaded again.
By default the app is installed for the current user, to `%LOCALAPPDATA%\Programs\Krupno`, without administrator rights. The "for all users" option installs it to Program Files.
Optional checkboxes:
- a desktop shortcut;
- the install folder in PATH, so `krupno-cli` can be run from any console;
- an "Upscale with Krupno" item in the Explorer context menu for images and folders, for a selection of up to 15 items (Explorer's limit for such items). Explorer starts Krupno.exe for each selected item; every one after the first hands its file to the window already open, so the whole selection lands in one queue;
- checking for new versions at startup (ticked by default; see [Updates](#window)). The choice goes to `HKCU\Software\Krupno` (`HKLM` for all users) as the app's default. Clearing it in a later installation turns the check off even if it was turned on in the window.

Uninstalling removes all of the above and the accelerators' compiled models (`%LOCALAPPDATA%\Krupno\cache`), and leaves the rest of `%LOCALAPPDATA%\Krupno` (settings, logs and your own models) alone. That folder belongs to the account that runs the uninstaller: after a "for all users" installation, the other accounts' compiled models stay in their own `%LOCALAPPDATA%\Krupno\cache` (up to 2 GB each) until they delete it.
The installer speaks the same languages as the window and picks the one Windows uses; with none of them, it asks. Hong Kong and Macau get Traditional Chinese (Setup matches them by their own language IDs; otherwise it would take the first Chinese one, Simplified). If the wizard runs in a language the app wouldn't pick by itself (one chosen in that dialog, or Brazilian Portuguese on a Portuguese Windows), a first installation writes it to the app's `settings.json`, so the app starts in it too; "Language" under "Advanced" changes it later. The Explorer item is named in the language chosen during installation.
The wizard's own text comes from Inno Setup's messages files: twelve come with Inno Setup, and the Indonesian, Vietnamese and both Chinese ones, which it doesn't install, are kept in `installer/languages` (taken from the `is-6_7_1` tag of [jrsoftware/issrc](https://github.com/jrsoftware/issrc), to match the Inno Setup version the release uses). Two of those lacked the download and verification messages of Inno Setup 6.5+ that the model download shows; Krupno adds them at the end of the file's `[Messages]`. A missing message would fall back to English with a compiler warning, and CI fails on any warning of the installer script.
Krupno's own text (installation types, components, tasks, the Explorer item) is in `installer/messages.iss`; the model names there are the ones the window shows.
CI compiles the script without output on every push (`ISCC /O-`), so a broken language or message shows up before a release, and `LocalizationTests` checks that the installer has the window's languages and every message in each of them.

To build the installer and the portable ZIP, you also need Inno Setup 6.5+ (`winget install JRSoftware.InnoSetup`):

```bash
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

The output goes to `artifacts/`: `Krupno-<version>-setup-x64.exe`, `Krupno-<version>-win-x64.zip` and `SHA256SUMS.txt`. The version comes from `<Version>` in `Directory.Build.props`.
The script publishes `Krupno.exe` and `krupno-cli.exe` into one folder. Models from `models/` are not copied there, the DirectML debug files and `*.pdb` are removed, and the .NET and Windows ML licenses go to `licenses/`.
The installer lists the models in `installer/Krupno.iss` (file name, size, SHA-256). A new set of models is published as a separate release (`models-v3` and so on), and the link in `Krupno.iss` is updated. Old model releases are never overwritten: already built installers point to them. A model release is published without "Set as the latest release" (`gh release create models-v3 --latest=false`): the window finds a new version of Krupno through the latest release, and a model release in its place would hide one until the next release of the app.

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
krupno-cli photo.jpg --tta                      # all 8 rotations and mirrorings, averaged (8 times slower)
krupno-cli C:\photos --long-side 3840           # every result 3840 px on its longer side
krupno-cli C:\photos -r -o D:\x4                # subfolders too, repeated under D:\x4
krupno-cli C:\photos -r -o D:\x4 --skip-existing   # resume: only the images without a result
krupno-cli *.jpg scan.png -o C:\x4             # several inputs; krupno-cli expands the pattern itself
krupno-cli --list-models --list-devices
krupno-cli --install-accelerators              # download the vendors' execution providers Windows ML offers
krupno-cli photo.jpg -d acc0                    # run on the first of them (see --list-devices)
```

See `krupno-cli --help` for details. Ctrl+C cancels processing. While it runs, Windows does not go to sleep on idle.

A result never replaces a source image or another result of the same run. If `-o` points at the source's own folder, the result is named as next to the source (`photo_x4.png`). When a folder has both `photo.jpg` and `photo.png`, neither gets `photo.png` (or `photo_x4.png`): each takes its source's extension, `photo (jpg).png` and `photo (png).png`. So the name depends on the image, not on which others are in the folder, and an image added later can't take the name of another one's earlier result. Only images that even the extension doesn't tell apart (two `photo.jpg` from different folders into one `-o`) get ` (2)`. An existing file from an earlier run is overwritten, unless `--skip-existing` is given: then that image is left out, and if nothing is left the model isn't even loaded.

Several files, folders and patterns can be given at once. cmd and PowerShell don't expand `*.jpg` for a program like krupno-cli, so it expands patterns itself, in the file name only (`photos\*.jpg`, not `pho*\a.jpg`); `*.jpg` doesn't match `.jpeg`. A file or pattern without `-o` keeps its result next to the source, a folder fills `<folder>_upscaled`; with `-o` everything goes into that folder. With `-r` the images in subfolders are taken too, and their folders are repeated in the output folder. An image found in a folder or by a pattern that is the result of another one in the same command is left out, and the tool says how many were: `*.png` next to the sources or `-o` inside the folder with `-r` doesn't upscale the results of an earlier run again. A file named on the command line is always taken, even if its name looks like another image's result, and then that result is named so that it doesn't replace it. A long path with the `\\?\` prefix is a path, not a pattern.

In a batch, a file's line (size, time, output path) is printed as soon as it is upscaled, while its result is still being written in the background. If that save fails, `[i/n] Not saved: …` follows once the next file is done, the file counts as failed, and the exit code is 1.

Without `-m`, `4xBHI_realplksr_dysample_real` is used, or the first model in alphabetical order if it is missing. `--list-models` marks the default model with an asterisk.

## Window

The preview is on the left, the settings panel on the right.

- **Opening files:** drag photos or a folder into the window, click "Select files", or pass a path on the command line. With Ctrl held, dropped files are added to the queue; without it, they replace it.
- **One window.** Krupno.exe started while a window is open hands its files to that window over a named pipe (for the same user and session only) and exits; started with no files, it brings the window forward. Files that arrive within 2 seconds of each other are one selection (Explorer starts a Krupno.exe per selected file) and share one queue; a later one replaces the queue, like opening files. While processing runs, handed-over files are not opened, and a message says so. If the open window doesn't answer within 5 seconds, or it is closing, the new Krupno.exe opens a window of its own instead of handing its files to a window that would drop them. `--new-window` always opens a separate window, for example to run two GPUs at once.
- **Pasting:** Ctrl+V opens files copied in Explorer, or an image from the clipboard (a screenshot, "Copy image" in a browser). The queue works with files, so a pasted image is first saved as `Pictures\Krupno\Pasted <date> <time>.png`, and the result goes next to it like for any other file. A PNG on the clipboard (browsers and Office put one there) is saved as is, with transparency; a plain bitmap whose fourth byte is 0 everywhere, as most apps leave it, is taken as opaque.
- **Models** are shown as cards with descriptive names ("Photo, fast", "Photo, maximum detail"…). The recommended and non-commercial models are marked, and technical details are in the tooltip. Known models are described in `ViewModels/ModelItem.cs`; the others are shown by file name.
- **Strength** (10–100%, under the models) mixes the network's result with a plain Lanczos resize of the original. Below 100% it tones down the textures a model invents and its oversharpening (see [How it works](#how-it-works)).
- **Scale and format** are picked with toggle buttons. The output size is shown right away, and JPEG has a quality slider. "Other" takes a scale of your own (2.5, or 2,5) or a length for one side of the result: width, height or long side in pixels. With a size, every image of a batch gets its own scale, shown next to the output size, and the name says what was asked for (`photo_3840px.png`).
- **Device and tile size** are tucked away under "Advanced"; by default both are chosen automatically. Besides "Auto", every GPU (through DirectML) and the CPU, the device list has the GPU and NPU vendors' accelerators that Windows has installed, such as "RTX 3080 · TensorRT-RTX". When Windows ML offers more for this computer, a line under the list names them, and "Download" has Windows download them (some are hundreds of megabytes); Krupno downloads nothing by itself. "Seamless tiles" there doubles the tile overlap to 64 px (`--pad 64` in the command-line tool): no visible seams even with deep models, at the cost of more tiles (35 instead of 24 for a 1920×1200 photo with auto tiles, about 1.5 times the time).
- **Comparison:** a slider wipe or two frames side by side. Zoom is measured in output pixels (100% is pixel for pixel), the mouse wheel zooms toward the point under the cursor, the right or middle button pans the image, and a double click toggles "Fit to window" ↔ 1:1.
- **During processing** finished tiles are highlighted on the preview, with the percentage, the remaining time and the tile number next to it. Progress also shows on the Windows taskbar icon, and when a run ends (done or failed) while you are in another window, the icon flashes until you come back. While processing runs, Windows does not go to sleep on idle (the display may still turn off; closing the lid or choosing Sleep works as usual).
- **A batch** is shown as a strip of thumbnails with each file's status: processing, done, error. A file keeps the "processing" mark while its result is written, which in a batch overlaps with the next file (see [How it works](#how-it-works)). Clicking a thumbnail shows the comparison for that file. Closing the window during a run cancels it and closes once it has stopped, so a result that is being written is finished first; closing it once more doesn't wait.
- **Errors and the log.** Each session of the window writes a log to `%LOCALAPPDATA%\Krupno\logs` (the 20 newest are kept). It starts with the versions of Krupno, Windows, .NET, ONNX Runtime and DirectML and the GPUs with their driver versions, then records each run (model, device, tile, strength, scale, format), every file and its result, warnings (a reduced tile, the fallback to the CPU) and every error with its stack trace. After a failed run, "Copy error details" puts the same facts for that run on the clipboard, ready for a bug report, with the path to the log. The log stays on the computer, and it holds file paths, so look it over before sharing it. The command-line tool writes no log: everything goes to the console.
- **Language.** The window speaks the Windows display language if it is one of these: English, Ukrainian, Russian, Polish, German, French, Italian, Spanish, Brazilian Portuguese, Turkish, Indonesian, Vietnamese, Japanese, Korean, Simplified and Traditional Chinese; otherwise English. "Language" under "Advanced" picks another one, from the next start. Only the text changes: numbers keep the regional format of Windows. The log and "Copy error details" stay in English, for whoever reads the bug report, and so does the command-line tool, whose output scripts read.
- **Updates.** At startup the window asks github.com which release of Krupno is the latest, and if it is newer, a line above the settings says so, with a link to its page. This is the only time Krupno goes online, and it is one HEAD request to `github.com/FLABER87/krupno/releases/latest`, not the GitHub API with its limit of 60 requests an hour. GitHub answers with a redirect to the latest release's tag, which isn't followed. Nothing else is sent (no settings, files or identifiers, only the version in the User-Agent), and nothing is downloaded or installed: the release page has the installer. Only tags like `v0.3.0` count, so a model release can't pass for one of the app. "Check for updates" under "Advanced" turns it off; until it is changed there, the installer's choice applies, and without an installer (the ZIP) it is on. The answer is in the log. `KRUPNO_UPDATE_FROM=0.0.9` makes the window compare as another version, to see the notice.
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

The first launch starts with the default model, ×4, PNG and the "Auto" device and tile. Every choice is saved in `%LOCALAPPDATA%\Krupno\settings.json` as soon as it is made: the model, strength, scale, format, JPEG quality, output folder, device, tile size, "Seamless tiles", "Test-time augmentation", the language and, once changed there, "Check for updates". The window's size and position (and whether it was maximized) are saved when it closes; a window that would land on a monitor that is no longer connected is moved onto a visible one.
The GPU is saved by name, since its DXGI index may change. If the saved model or GPU is gone, a value is not one the window offers, or the settings file is corrupt or missing, the window falls back to the defaults. A saved output folder is checked before processing starts: if its drive is gone, the window says so right away instead of after the first file.

The app icon is drawn by `tools/make_icon.py` (requires Pillow and numpy).

### Translations

The window's text is in `src/Krupno.App/Properties/Strings.resx` (English) and `Strings.<culture>.resx` next to it, one file per language; the progress messages and errors of the library are in `src/Krupno.Core/Properties` the same way. The build turns each translation into a satellite assembly (`uk\Krupno.resources.dll` and so on), and .NET picks the one for the UI language. XAML reaches the text through `{x:Static p:Strings.Key}`, C# through `Strings.Key`; `Strings.Designer.cs` is generated by Visual Studio or Rider when `Strings.resx` is saved.

- A text with plural forms holds one form per plural category of its language, separated by `|`: two in English (`{0} file|{0} files`), three in Ukrainian, Russian and Polish, one in Chinese, Japanese, Korean, Vietnamese and Indonesian (`Krupno.Core/Localization/Plurals.cs` has the rules). Its comment in `Strings.resx` says so.
- To add a language: copy `Strings.resx` in both projects to `Strings.<culture>.resx`, translate the values, add the language to `Infrastructure/UiLanguage.cs` and, if its plural rules differ from English, to `Plurals.cs`.
- `LocalizationTests` checks that every translation has the same keys as `Strings.resx`, the same placeholders, valid `string.Format` text and the right number of plural forms, that `Strings.Designer.cs` matches `Strings.resx`, and that every text is used somewhere.

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
On GitHub, the `.github/workflows/tests.yml` workflow (`windows-latest`) builds and runs the unit tests and these tests on every push and pull request. The runner has no GPU, and Krupno does not count "Microsoft Basic Render Driver" as one. So the workflow runs the script with the `-CheckFallback` switch: it sets `KRUPNO_BASIC_RENDER_DRIVER=1`, "Auto" picks that adapter, DirectML fails to start on it, and every "GPU" run must fall back to the CPU. This is how the fallback is tested. If the tests fail, `tests/out/cli` is uploaded as the `cli-tests-out` artifact.
Dependabot (`.github/dependabot.yml`) opens weekly update PRs for ONNX Runtime and the GitHub Actions, so these tests check every update before it is merged. The Python requirements are left out: `tools/requirements.txt` sets deliberate lower bounds, which Dependabot would raise.

The UI scripts launch `Krupno.exe` and drive the window through UI Automation.
They need real models in `models/`: `4xBHI_realplksr_dysample_real` and `4xBHI_dat2_real_fp16` by default; others are set with the `-Model` and `-SlowModel` parameters.
- `tests\gui_states.ps1` walks through the window states (empty, tile-by-tile processing, result, side-by-side comparison, batch, error, light theme) and saves screenshots to `tests/out/gui`. The script itself only checks that the states are reached; a human looks at the screenshots. The `color` scenario also checks pixels. A Display P3 image must look the way Windows' color-managed decoding shows it, before and after processing and in the filmstrip. An untagged copy must be shown unchanged, and the saved result must keep the stored pixels. About 2 min.
- `tests\gui_keys.ps1` checks the keyboard shortcuts: preview zoom, Ctrl+Enter, Ctrl+C (image size on the clipboard), Ctrl+V (a bitmap and a PNG pasted from the clipboard are saved to `Pictures\Krupno` with the right size; the script deletes them afterwards) and Esc. It also checks handing files over to an open window: two Krupno.exe started together give one queue of two files and exit, and one started 3 seconds later replaces the queue (skipped if a Krupno window is already open). The UI scripts start their windows with `--new-window`, so a window you have open doesn't take their files. The keys are sent through SendKeys, so it is best not to touch the keyboard and mouse during the run (about 30 s). If the Krupno window is not in the foreground, the script stops and sends no keys.

Both scripts swap `%LOCALAPPDATA%\Krupno\settings.json` for the duration of the run and restore the previous one afterwards; the settings they write set the window to English, since they find buttons by their English names, and turn the update check off, so the scripts stay offline and a newer release doesn't change the screenshots.

## How it works

- **Seamless tiling.** The image is cut into fixed-size windows that never go beyond its edges. Each tile keeps at least `--pad` pixels of real context around it, and only the central part of the tile goes into the result. Deep models have a receptive field larger than the overlap, so small deviations at the seams are possible. For RealESRGAN_x4plus compared with a whole-image run: with a 32 px overlap (the default), up to 9 levels on 0.01% of pixels; with 64 px, up to 1 level.
- **Input shape for DirectML.** All tiles of an image are fed to the network in a window of the same size. The size is fixed through `AddFreeDimensionOverrideByName`, and DirectML compiles the graph once per image size.
- **Tiles overlap with the CPU work around them.** While the network runs on one tile, another thread fills the input of the next one and copies the previous one's result into the image, so the GPU doesn't wait for that work between tiles. This keeps one more tile's output in memory.
- **Checked against a reference.** Without tiling, the result matches the PyTorch implementation of Real-ESRGAN within ±1 level, both on DirectML and on the CPU.
- **Out of memory.** If the window does not fit on the device, the tile shrinks and the pass starts over. Up to 512 px, the tile steps down a 64 px grid, respecting the model's size multiple (512 → 448 → 384 → 320 → 256 → …, no smaller than 128). A larger window is halved, but not below 512: every failed attempt costs a graph compilation, about 10 s for DAT2. Previously the tile was always halved and skipped the size that would fit: DAT2 fp32 on 10 GB dropped from 384 straight to 192. The chosen tile is remembered for the model on that device: later frames of a batch and the second ×8 pass start with it right away, and the reduction message is printed once. With DAT2 fp32 and `-t 384`, a batch of two 1920×1200 frames takes 62.1 + 51.7 s: only the first frame spends the extra ~10 s. In the window, the limit lasts until the model or device changes, and the next processing run shows "Tile 320 px (chosen after running out of memory)" once. To try a larger tile again, for example after freeing VRAM, switch the model or restart the app. DirectML does not report spilling into shared memory as an exception, so it cannot be caught this way. If the model also keeps sessions for other windows (a second pass, or TTA's transposed window), they are released and the same window gets one more try before the tile shrinks. With a vendor's accelerator only an error that reads as out of memory shrinks the tile: every attempt there is an engine build, so any other error is reported at once.
- **Accelerators (Windows ML).** On Windows 11 24H2 and later, Windows ML has a catalog of the GPU and NPU vendors' execution providers: NVIDIA TensorRT-RTX (GeForce RTX 30 and later), Intel OpenVINO, AMD MIGraphX and Vitis AI, Qualcomm QNN. Windows downloads and updates them as packages from Microsoft. At startup Krupno registers the ones Windows has with ONNX Runtime (in the background in the window, without downloading anything), and each of their devices becomes a device of its own (`accN` in the command-line tool). A provider that Windows has but that isn't ready for this process yet (another app installed it, or it was just updated) is made ready then too, which downloads nothing; if that fails, it is offered under the device list like one Windows doesn't have. Two identical GPUs are told apart by their DXGI index ("RTX 4090 · TensorRT-RTX #1"). If Windows ML takes more than 30 s to register them, the window lists the devices once it is done, and a saved accelerator is kept meanwhile. They get the same fixed window size per image as DirectML, so TensorRT-RTX builds one engine per window size, and the first image of a size takes longer. The compiled model is kept in `%LOCALAPPDATA%\Krupno\cache\compiled` (ONNX Runtime's compile API, with the engine embedded), so the next start loads it instead of building it again. A file is about the size of the model; above 2 GB in all the least recently used go, and uninstalling removes the folder. One that no longer suits the provider or the driver, by ONNX Runtime's compatibility check or because it fails to load, is compiled again. A provider that can't compile a model for a window runs it as before, building the engine each time, and the log (the console in the command-line tool) says why; another window of the same model is still tried. A model whose input stays dynamic is compiled as it is. The weights of a model stored next to it (`model.onnx.data`) count for the cache too. Building an engine can't be stopped, so canceling doesn't wait for it: the build finishes in the background and its result is kept. "Auto" stays with DirectML, and a chosen accelerator never falls back to the CPU on its own: an error says what is wrong instead of a much slower run. That includes a model the provider doesn't take whole, which ONNX Runtime would quietly finish on the CPU: an accelerator's sessions and compiles may not put any operation on the CPU (`session.disable_cpu_ep_fallback`), so such a model gets an error that the accelerator can't run it. A compile that exports nothing (exporting what it compiled is optional for a provider) is followed by a plain session, which works if the provider takes the whole model. If the cache folder can't be written or has no room, the model is still compiled, into a temporary file for that session. On Windows 10, or where the catalog has nothing for the hardware, the list is the same as before.
- **CPU fallback.** In "Auto" mode the GPU with the most memory is used; if DirectML fails to start, the work runs on the CPU, and the window says so during processing. Running out of video memory is not a reason to fall back: once DirectML has created a session for the model, or when the error reads as out of memory, the tile shrinks instead. The software "Microsoft Basic Render Driver" does not count as a GPU: on a machine without a GPU it is not in the device list, and "Auto" goes straight to the CPU.
- **Any scale or size.** `--width`, `--height` and `--long-side` ("Other" in the window) set one side of the result in pixels instead of a scale: the scale is worked out for each image, the other side follows its proportions, and next to the source the name gets `_3840w`, `_2160h` or `_3840px`. The network runs as many times as needed (×8 with a ×4 model is two passes), then the result is resized precisely with a Lanczos-3 filter. If the last pass would not fit in memory at full size (×8 of a 1920×1200 photo makes a 30720×19200 intermediate), its input is first reduced so that the pass lands just above the target size, and a message says so. A size just above the model's own scale takes the second pass too: 3840 px wide from a 900 px photo is ×4.27, so a ×4 model runs twice (×16) and the result is reduced. The window shows the scale next to the output size for this reason.
- **Strength.** Below 100%, the result is `plain + strength × (network − plain)`, where `plain` is a Lanczos resize of the pixels the network saw (with transparency, the colors filled in under alpha 0) to the output size, clipped to 0..255 like a saved image. The alpha stays as computed for the full-strength result. The mix is done band by band, so the plain resize never exists whole: for a 7680×4800 result it adds under a second on a 4-core CPU.
- **Test-time augmentation** (`--tta`, "Test-time augmentation" in "Advanced"). Every network pass runs on the image in all 8 rotations and mirrorings, each result is turned back, and the 8 are averaged, rounded to the nearest level. Networks are not quite the same in every direction, so this evens out noise and artifacts that lean one way, at 8 times the time. Each orientation is tiled on its own, as an image of its own; the preview shows the tiles where they are on the image as it is. If the image is smaller than a tile on one side, a quarter turn also turns the window, and on the GPU that is a second compiled graph; with TTA the model keeps up to four of them (a window and its transpose in each of two passes) instead of two, so a batch doesn't compile them again for every image. Each round's result is added to the sum through the orientation's pixel mapping rather than turned back as a copy, so TTA needs about 10 bytes per output pixel (the result and the sum) instead of 4: 4.1 GB for the ×16 intermediate of ×8 on a 1600×1000 photo. A quarter turn transposes the intermediate, so its size is checked both ways before the first round.
- **Batches.** A result is written while the next file is loaded and upscaled, so the GPU doesn't wait for the encoder (compressing a 7680×4800 PNG can take seconds, most of a frame for a fast model). One save runs at a time, so files finish in order. The last file of a batch, and a result larger than 512 MB, are written right away instead: one more result in memory, plus the encoder's copy of it, could make a large batch run out of memory. On cancellation or an error, a result already being written is finished and counts as done, and closing the window waits for it too.
- **The alpha channel** is scaled separately (Lanczos); the network processes RGB. The color under fully transparent pixels is first filled from the nearest visible pixel, so the edges don't pick up the color stored there (usually black), and the final resize weights colors by alpha.
- **EXIF rotation** is applied to the pixels. An RGB ICC profile is carried over to the result (see below), and the metadata too if the output format matches the source.
- **Color profiles.** The pixels are upscaled as stored: an Adobe RGB or Display P3 photo stays in its color space, and the result carries the same profile (BMP, which can't hold one, is converted to sRGB). The result is always RGB, which a CMYK or grayscale profile can't describe. So CMYK and grayscale images are converted with their profile to sRGB on load, the way color-managed viewers show them, and the result has no profile (untagged means sRGB). A profile that doesn't fit the pixels, such as a grayscale profile on a color image, is dropped. The copy of the profile in the source's metadata (a PNG iCCP chunk, JPEG APP2 segments, a TIFF tag) stays behind, so only a profile that fits goes into the result. WPF draws everything as sRGB, so the window converts the comparison and the filmstrip thumbnails from the embedded profile to sRGB, for display only. WIC does the conversion, so the colors match Windows' own color-managed decoding. Krupno converts to sRGB, not to the monitor's profile. Profiles that match sRGB within a couple of levels are left alone. A large result is converted in place, a few megabytes at a time, without another full-size copy. Ctrl+C copies the result as it is shown, in sRGB.

## What's next

- A 16-bit-per-channel pipeline (currently 8-bit).
- Streaming output for images that do not fit in memory.
- WebP/AVIF output (WIC has no encoders for them; this needs a custom implementation or a separate library).
- A model catalog with downloads and license checks right in the window (currently only the installer downloads models).

## License

The code is under the MIT license, see [LICENSE](LICENSE). It does not cover the models: each has its own license (see [Where to get weights](#models)).
