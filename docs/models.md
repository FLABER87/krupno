# Models

The models Krupno downloads and their licenses are listed in the [README](../README.md#models). This page is about converting other models and how the models compare.

The app works with ONNX models of the shape `[1, 3, H, W] → [1, 3, H·s, W·s]` (RGB, 0..1, fp32 or fp16).
Krupno looks for them in the `models` folder next to `Krupno.exe` and in `%LOCALAPPDATA%\Krupno\models`.

## Converting PyTorch weights

To get such a model from PyTorch weights, use the conversion script:

```bash
pip install -r tools/requirements.txt
python tools/convert_to_onnx.py RealESRGAN_x4plus.pth
python tools/convert_to_onnx.py 4x-model.safetensors --fp16 --license "CC-BY-NC-SA-4.0"
```

The script detects the architecture with [spandrel](https://github.com/chaiNNer-org/spandrel): ESRGAN/Real-ESRGAN, Compact, SPAN, RealPLKSR, DAT, HAT, SwinIR, OmniSR and others are supported.
It writes the scale, the architecture, the input size requirements and the license into the ONNX metadata under `krupno.*` keys.
Models converted before the project was renamed store the same keys as `upscalr.*`; Krupno reads both.

`.safetensors` is the safer choice: `.pth` files are pickles, so load them only from trusted sources.

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

## Where to get weights

- [Phhofm/models](https://github.com/Phhofm/models/releases): BHI/Nomos models for photos, CC-BY-4.0 license (can be bundled with attribution).
- [Kim2091/UltraSharpV2](https://huggingface.co/Kim2091/UltraSharpV2): CC BY-NC-SA 4.0 license, non-commercial use only.
- [Remacri](https://openmodeldb.info/models/4x-Remacri) by foolhardy: CC BY-NC-SA 4.0 license, non-commercial use only.
- [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN/releases): BSD-3-Clause license.
- [SCUNet](https://github.com/cszn/SCUNet) by Kai Zhang: denoisers, Apache-2.0 license; the weights are in the [KAIR v1.0](https://github.com/cszn/KAIR/releases/tag/v1.0) release.
- [OpenModelDB](https://openmodeldb.info/): hundreds of community models. **Each has its own license**, many are for non-commercial use only.

## Which model to use

Tested on a 1920×1200 photo, downscaled 4 times and recompressed as JPEG (q70), then upscaled back.
Speed: on an RTX 3080, 1920×1200 → 7680×4800 frame, second run, including reading PNG and writing BMP.

| Model | Architecture | License | Speed | Verdict |
|---|---|---|---|---|
| `4xBHI_realplksr_dysample_real` | RealPLKSR | CC-BY-4.0 | 6.7 s | **default**: almost as good as DAT2 at Real-ESRGAN speed |
| `4xBHI_dat2_real` | DAT2 | CC-BY-4.0 | 33 s (fp16), 41 s (fp32) | the most detailed, oversharpens in places |
| `4x-UltraSharpV2` | DAT2 | CC BY-NC-SA 4.0 | 42 s, fp32 only | best PSNR/SSIM, softer than DAT2; personal use only |
| `RealESRGAN_x4plus` | ESRGAN | BSD-3 | 8.6 s (fp32), 4.6 s (fp16) | outdated: "oil painting" look, invents textures |
| `realesr-general-x4v3` | Compact | BSD-3 | 1.6 s | fast but blurry |
| `4x_foolhardy_Remacri_fp16` | ESRGAN | CC BY-NC-SA 4.0 | 5.8 s | for [text](#text); on photos it smears textures and draws streaks on rocks |

All three modern models reproduce the structure of the rocks like the original, while Real-ESRGAN paints a forest in their place.
The speeds in the table are for the models from the `models-v2` release (`models-v3` has the same files and Remacri, `models-v4` SCUNet as well): RealPLKSR, DAT2 fp16 and Remacri have a dynamic input (`torch.export` for the first two), DAT2 fp32 and UltraSharpV2 a static 256×256 one.
In `models-v1` all three modern models had a static input. The difference between the two is shown below.

### Text

Models make small text sharp, but they may also change it, digits included.
Tested on a document rendered at 4 times its size, downscaled 4 times and recompressed as JPEG (q70), with English and Russian text, and on a sign in a photo, downscaled the same way:

- Text 12 px high or larger (a sign, a menu, a clean screenshot): every model keeps it right.
- Capitals about 9 px high: RealPLKSR, DAT2 and UltraSharpV2 already change some digits (17/245 becomes 17/246).
- Capitals about 7 px high: every model changes some dates and digits (12/03/2026 becomes 12/08/2026 or 12/00/2026), Remacri the least: it keeps those dates and the 9 px text right. It also has the highest PSNR on the document: 23.0 dB, against 21.7 for RealPLKSR.

Remacri keeps what the image has, JPEG artifacts and noise included, while the other models restore it and redraw letters on the way. That is why it is the "Text and documents" model, and also why it is worse than RealPLKSR on photos.
Phhofm's siblings of the default model trained without restoration (`4xBHI_realplksr_dysample_multi` and `_multiblur`) keep text no better and break JPEG input into blocks; `_otf_nn` changes digits like the default model.
No model makes unreadable text reliable: check important numbers against the original.

### Static or dynamic input

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

## Noise

"Remove noise" (`--denoise`) runs `scunet_color_real_psnr_fp16` on the image at its original size, before the upscaler. The photo models already deal with light noise: they are trained on degraded photos. Strong noise they partly keep, or turn into texture of their own (hairs on skin, cracks on a wall), and Remacri keeps noise as it is, as it keeps everything else. So the option helps most with noisy photos and with Remacri.

Tested on three sets:
- real camera noise: 100 crops of 512×512 from the [PolyU dataset](https://github.com/csjunxu/PolyU-Real-World-Noisy-Images-Dataset) (Canon, Nikon and Sony cameras, ISO 1600–6400), compared with the average of hundreds of shots of the same scene. The ×4 results are reduced back to 512×512 for that, which shows whether the noise survived but not how sharp the result is;
- added noise: 13 photos downscaled 4 times, with camera-like noise (stronger in light areas, separate in each channel, 6 or 12 levels) and JPEG q90 added, upscaled back and compared with the photo;
- a noisy phone photo, compared by eye.

LPIPS (lower is closer) and PSNR:

| | Real noise, ×1 | Real noise, ×4 | Noise 6, ×4 | Noise 12, ×4 | No noise, ×4 |
|---|---|---|---|---|---|
| Source | 0.197 · 36.0 dB | | | | |
| SCUNet | 0.063 · 38.0 dB | | | | |
| Default model | | 0.094 · 32.9 dB | 0.184 · 27.4 dB | 0.264 · 25.2 dB | 0.139 · 29.3 dB |
| SCUNet, then the default model | | 0.106 · 34.6 dB | 0.261 · 28.1 dB | 0.271 · 27.8 dB | 0.253 · 28.2 dB |
| Remacri | | 0.166 · 34.4 dB | 0.361 · 26.3 dB | 0.509 · 23.8 dB | 0.163 · 28.3 dB |
| SCUNet, then Remacri | | 0.070 · 35.3 dB | 0.305 · 28.4 dB | 0.319 · 28.2 dB | 0.290 · 28.4 dB |

- On its own, SCUNet removes camera noise almost to the average of hundreds of shots, without inventing anything.
- Before Remacri, it removes what Remacri would keep and enlarge: with 12 levels of noise, Remacri alone gives three times the original's detail, most of it noise.
- Before the default model, it removes the noise and the texture the model would invent from it (on the phone photo, lines on the skin and cracks on the wall), and PSNR rises by 0.7–2.6 dB. But it smooths fine texture too: knitted fabric comes out flat, and the detail is 0.76 of the original's. So LPIPS gets worse with moderate noise (0.184 → 0.261), stays about the same with strong noise (0.264 → 0.271), and on a clean image the option only loses texture (0.139 → 0.253). That is why it is off by default.
- `1xDeNoise_realplksr_otf` by Phhofm (CC BY 4.0), the obvious candidate with the default model's architecture, was rejected: it restores rather than denoises. It sharpens with halos, darkens by about 4 levels, draws cracks and eyelashes and turns small text into blocks; on real noise PSNR fell from 36.0 to 29.9 dB.

SCUNet converts with a dynamic input (a multiple of 8, through `torch.export`). Its tiles leave no visible seams: with the default 32 px overlap the result differs from an untiled run by 0.07 levels on average, at most 7. The fp16 file differs from fp32 by 0.13 levels on average (at most 2) and is 24% faster. On a 1944×1300 photo on an RTX 3080 it takes 5.7 s, against 8.5 s for the default model's ×4.

## Publishing a new set of models

See [installer.md](installer.md#model-releases).
