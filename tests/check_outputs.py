"""Checks the results of tests/run_cli_tests.ps1 against exactly computed expectations.

    python tests/check_outputs.py [<fixtures folder>] [<results folder>]
"""
import io
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageCms

sys.stdout.reconfigure(encoding="utf-8")
here = Path(__file__).resolve().parent
fixtures = Path(sys.argv[1]) if len(sys.argv) > 1 else here / "fixtures"
out = Path(sys.argv[2]) if len(sys.argv) > 2 else here / "out" / "cli"
img = fixtures / "images"
failures = 0


def check(name, ok, detail):
    global failures
    print(f"{'OK  ' if ok else 'FAIL'} {name}: {detail}")
    failures += not ok


def load(path, mode="RGB"):
    return np.asarray(Image.open(path).convert(mode)).astype(np.int32)


def log(label):
    path = out / f"{label}.log"
    return path.read_text(encoding="utf-8-sig") if path.exists() else ""


def expect_nearest(src_name, out_name, scale, keep):
    if not (out / out_name).exists():
        return check(out_name, False, "file missing")
    src = load(img / src_name)
    exp = np.repeat(np.repeat(src, scale, 0), scale, 1) * np.array(keep, np.int32)
    got = load(out / out_name)
    ok = got.shape == exp.shape and int(np.abs(got - exp).max()) == 0
    check(out_name, ok, f"max diff {int(np.abs(got - exp).max())}" if got.shape == exp.shape else f"shape {got.shape} != {exp.shape}")


def lanczos_weights(src, dst):
    """The weights of Resampler.Kernel.Create: Lanczos-3, stretched when downscaling, cut off at the edges and normalized."""
    scale = dst / src
    filter_scale = max(1.0, 1.0 / scale)
    support = 3.0 * filter_scale
    max_count = int(np.ceil(support * 2)) + 1
    weights = np.zeros((dst, src))
    for i in range(dst):
        center = (i + 0.5) / scale
        left = max(0, int(np.floor(center - support)))
        right = min(src, int(np.ceil(center + support)))
        n = min(right - left, max_count)
        x = (np.arange(left, left + n) + 0.5 - center) / filter_scale
        k = np.where(np.abs(x) < 3, np.sinc(x) * np.sinc(x / 3), 0.0)
        weights[i, left:left + n] = k / k.sum() if k.sum() != 0 else 1.0 / n
    return weights


def lanczos(a, height, width):
    """Resampler.Resize before rounding, per channel: the horizontal pass, then the vertical one."""
    rows = np.einsum("xw,hwc->hxc", lanczos_weights(a.shape[1], width), a.astype(np.float64))
    return np.einsum("yh,hxc->yxc", lanczos_weights(a.shape[0], height), rows)


def expect_blend(out_name, network, src, strength, alpha=None):
    """--strength: network + (1 - strength) × (plain - network), the plain resize clipped to 0..255 like a saved image.
    Computed in float64 here, so a pixel may be off by 1 at a rounding boundary. alpha: the alpha it must keep exactly."""
    if not (out / out_name).exists():
        return check(out_name, False, "file missing")
    plain = np.clip(lanczos(src, network.shape[0], network.shape[1]), 0, 255)
    exp = np.clip(np.floor(plain + strength * (network - plain) + 0.5), 0, 255)
    got = np.asarray(Image.open(out / out_name)).astype(np.int32)
    if got.shape[:2] != exp.shape[:2]:
        return check(out_name, False, f"shape {got.shape} != {exp.shape}")
    diff = int(np.abs(got[..., :3] - exp).max())
    alpha_ok = alpha is None or (got.shape[2] == 4 and bool((got[..., 3] == alpha).all()))
    check(out_name, diff <= 1 and alpha_ok, f"max diff {diff} (≤1) from the blend with Lanczos"
          + (f", alpha {'kept' if alpha_ok else 'changed'}" if alpha is not None else ""))


def expect_model(label, text):
    """Model name and scale in the CLI output: shows that the metadata was read."""
    check(f"{label}.log", text in log(label), f'"Model: {text}"')


for device in ("gpu", "cpu"):
    expect_nearest("noise_300x200.png", f"noise_red_{device}.png", 4, [1, 0, 0])
    expect_nearest("big_1500x1000.png", f"big_red_{device}.png", 4, [1, 0, 0])
    expect_nearest("big_1500x1000.png", f"big_fixed_{device}.png", 2, [0, 1, 0])
    expect_nearest("noise_300x200.png", f"noise_probe_{device}.png", 3, [0, 0, 1])
expect_nearest("big_1500x1000.png", "big_red_notile.png", 4, [1, 0, 0])
expect_nearest("noise_300x200.png", "noise_legacy.png", 2, [1, 1, 1])
expect_nearest("noise_300x200.png", "noise_half.png", 2, [1, 1, 0])
expect_model("noise_red_cpu", "Nearest red x4 (×4, Test)")
expect_model("big_fixed_cpu", "fixed64_x2 (×2)")
expect_model("noise_probe_cpu", "probe_x3 (×3)")
expect_model("noise_legacy", "Legacy x2 (×2, Test)")
expect_model("noise_half", "half_x2 (×2), fp16")

# Alpha: RGB is exact, alpha is Lanczos (compared approximately with Pillow's Lanczos).
# Under fully transparent pixels (columns 0 and 1 of the fixture) the color is filled from the nearest visible pixel
# (column 2 of the same row) before the network sees it, so the edge doesn't pick up the color stored under alpha 0.
src = np.asarray(Image.open(img / "alpha_257x129.png")).astype(np.int32)
got = np.asarray(Image.open(out / "alpha_red.png")).astype(np.int32)
bled = src[..., :3].copy()
transparent_columns = np.where((src[..., 3] == 0).all(0))[0].tolist()
assert transparent_columns == [0, 1] and not (src[:, 2:, 3] == 0).any(), f"unexpected alpha fixture: {transparent_columns}"
bled[:, :2] = bled[:, 2:3]
exp_rgb = np.repeat(np.repeat(bled, 4, 0), 4, 1) * np.array([1, 0, 0])
rgb_ok = got.shape[:2] == exp_rgb.shape[:2] and got.shape[2] == 4 and int(np.abs(got[..., :3] - exp_rgb).max()) == 0
pil_alpha = np.asarray(Image.fromarray(src[..., 3].astype(np.uint8)).resize((257 * 4, 129 * 4), Image.LANCZOS)).astype(np.int32)
alpha_err = float(np.abs(got[..., 3] - pil_alpha).mean()) if got.shape[2] == 4 else 999
check("alpha_red.png", rgb_ok and alpha_err < 1.5, f"rgb exact={rgb_ok}, alpha mean diff vs Pillow Lanczos {alpha_err:.2f}")

# Strength: the network's result mixed with a plain Lanczos resize of the pixels the network saw (with transparency, the
# colors filled in under alpha 0); the alpha is the one of the full-strength result.
noise = load(img / "noise_300x200.png")
expect_blend("noise_red_s50.png", np.repeat(np.repeat(noise, 4, 0), 4, 1) * np.array([1, 0, 0]), noise, 0.5)
expect_blend("alpha_red_s30.png", exp_rgb, bled, 0.3, alpha=got[..., 3] if got.shape[2] == 4 else None)
check("strength_0.log", "--strength" in log("strength_0") and not (out / "strength_0.png").exists(),
      " | ".join(line for line in log("strength_0").splitlines() if line.strip()) or "empty output")

# Fractional scale: ×2.5 with a ×4 model means downscaling the ×4 result with a Lanczos filter.
got = load(out / "noise_red_x2_5.png")
check("noise_red_x2_5.png", got.shape == (500, 750, 3) and int(got[..., 1:].max()) == 0,
      f"shape {got.shape}, G/B max {int(got[..., 1:].max())}")

# Target sizes. --width 750 on 300×200 is ×2.5, and the name next to the source says the target; --long-side 960 on the
# rotated 90×160 image is ×6 (two passes); -s together with a target size is refused before any work.
path = out / "target" / "noise_750w.png"
got = load(path) if path.exists() else np.zeros((0, 0, 3), np.int32)
check("target/noise_750w.png", got.shape == (500, 750, 3) and int(got[..., 1:].max(initial=0)) == 0,
      f"shape {got.shape}" + ("" if path.exists() else ", file missing"))
path = out / "target_long.png"
got = load(path) if path.exists() else np.zeros((0, 0, 3), np.int32)
check("target_long.png", got.shape == (960, 540, 3) and got.size > 0 and got[:40, -40:, 0].mean() > 200,
      f"shape {got.shape}, top-right R {got[:40, -40:, 0].mean() if got.size else 0:.0f}")
check("target_conflict.log", "Use only one of" in log("target_conflict") and not (out / "target_conflict.png").exists(),
      " | ".join(line for line in log("target_conflict").splitlines() if line.strip()) or "empty output")

# Multiple passes: ×8 with a ×4 model means two passes (×16), then downscaling to ×8.
got = load(out / "noise_red_x8.png")
check("noise_red_x8.png", got.shape == (1600, 2400, 3), f"shape {got.shape}")

# EXIF orientation 6: the displayed size is 90×160, and the red mark must end up in the top-right corner.
got = load(out / "orient6_red.png")
check("orient6_red.png", got.shape == (160 * 4, 90 * 4, 3) and got[:20, -20:, 0].mean() > 200 and got[:20, :20, 0].mean() < 50,
      f"shape {got.shape}, top-right R {got[:20, -20:, 0].mean():.0f}, top-left R {got[:20, :20, 0].mean():.0f}")

# The ICC profile must survive PNG → PNG.
if (img / "icc_60x40.png").exists():
    icc_in = Image.open(img / "icc_60x40.png").info.get("icc_profile")
    icc_out = Image.open(out / "icc_red.png").info.get("icc_profile")
    check("icc_red.png", icc_out is not None and len(icc_out) == len(icc_in),
          f"icc in {len(icc_in or b'')} bytes, out {len(icc_out or b'')} bytes")

# Display P3: the pixels are processed as stored (only the window converts them to sRGB, for display), and the profile goes along.
if (out / "p3_red.png").exists():
    got = load(out / "p3_red.png")
    icc_in = Image.open(fixtures / "color" / "wide_p3.png").info.get("icc_profile")
    icc_out = Image.open(out / "p3_red.png").info.get("icc_profile")
    check("p3_red.png", got.shape == (1600, 2400, 3) and bool((got == [60, 0, 0]).all()) and icc_out == icc_in,
          f"shape {got.shape}, colors {np.unique(got.reshape(-1, 3), axis=0)[:3].tolist()}, profile {'kept' if icc_out == icc_in else 'lost'}")
else:
    check("p3_red.png", False, "file missing")


def expect_srgb(src_name, out_name, tolerance, mode="RGB", alpha=None, centers=False, intent=ImageCms.Intent.PERCEPTUAL):
    """A CMYK or grayscale source upscaled ×2 (legacy_x2): its pixels converted with the embedded profile to sRGB, compared with
    LittleCMS (Windows' color engine differs by a few levels), and no profile in the result. centers: only the middle of each
    CMYK patch, for a lossy result."""
    path = out / out_name
    if not path.exists():
        return check(out_name, False, "file missing")
    src = Image.open(fixtures / "profiles" / src_name)
    profile = ImageCms.ImageCmsProfile(io.BytesIO(src.info["icc_profile"]))
    exp = np.asarray(ImageCms.profileToProfile(src.convert("L") if src.mode == "LA" else src, profile,
                                               ImageCms.createProfile("sRGB"), renderingIntent=intent,
                                               outputMode="RGB")).astype(np.int32)
    exp = np.repeat(np.repeat(exp, 2, 0), 2, 1)
    result = Image.open(path)
    icc = result.info.get("icc_profile")
    got = load(path)
    if got.shape != exp.shape:
        return check(out_name, False, f"shape {got.shape} != {exp.shape}")
    if centers:
        got, exp = got[32::64, 24::48], exp[32::64, 24::48]
    diff = int(np.abs(got - exp).max())
    alpha_ok = alpha is None or bool((np.asarray(result.getchannel("A")) == alpha).all())
    check(out_name, result.mode == mode and icc is None and diff <= tolerance and alpha_ok,
          f"mode {result.mode}, profile {icc[16:20].decode() if icc else 'none'}, max diff {diff} (≤{tolerance})"
          + (f", alpha {'kept' if alpha_ok else 'changed'}" if alpha is not None else ""))


# CMYK and grayscale profiles: the result is RGB, which a CMYK or grayscale profile can't describe, so the pixels are
# converted to sRGB on load, the way color-managed viewers show them. The same container (JPEG → JPEG, TIFF → TIFF) carries
# the metadata over, and the profile must not come along with it. Windows converts CMYK without a profile as SWOP, so only
# the toy press profile shows that the embedded profile is applied: a naive conversion misses it by over 100 levels.
# The toy profile has the same table for every intent, but LittleCMS also scales the black point of a v2 profile for the
# perceptual intent, which Windows doesn't; the relative colorimetric intent gives the table as it is.
if (fixtures / "profiles" / "cmyk_swop.jpg").exists():
    expect_srgb("cmyk_swop.jpg", "cmyk_swop.png", 6)
    expect_srgb("cmyk_swop.jpg", "cmyk_swop_jpg.jpg", 8, centers=True)
expect_srgb("cmyk_toy.tif", "cmyk_toy_tif.tif", 4, intent=ImageCms.Intent.RELATIVE_COLORIMETRIC)
expect_srgb("gray.png", "gray_icc.png", 3)
expect_srgb("gray_alpha.png", "gray_alpha_icc.png", 3, mode="RGBA", alpha=160)

# Folder → folder: all images, name without a suffix, .png extension.
sources = sorted(p for p in img.iterdir() if p.suffix.lower() in (".png", ".jpg"))
missing = [p.name for p in sources if not (out / "batch" / (p.stem + ".png")).exists()]
check("batch", not missing and f"Done: {len(sources)} of {len(sources)}" in log("batch"),
      f"{len(sources) - len(missing)} of {len(sources)} files" + (f", missing: {', '.join(missing)}" if missing else ""))
expect_nearest("noise_300x200.png", "batch/noise_300x200.png", 4, [1, 0, 0])
expect_nearest("noise_300x200.png", "noise_red_tta.png", 4, [1, 0, 0])
got = load(out / "batch" / "orient6.png") if (out / "batch" / "orient6.png").exists() else np.zeros((0, 0, 3))
check("batch/orient6.png", got.shape == (160 * 4, 90 * 4, 3), f"shape {got.shape}")

# Broken file: a clear error and exit code 1, not a crash and not an empty result.
for label in ("broken", "broken_gif"):
    check(f"{label}.log", "Error:" in log(label) and "Unhandled" not in log(label) and not (out / f"{label}.png").exists(),
          " | ".join(line for line in log(label).splitlines() if line.strip()) or "empty output")

# The source's own folder as the output: the source stays as it was, the result is named as next to the source.
same_src = out / "same" / "noise.png"
same_out = out / "same" / "noise_x4.png"
check("same_folder", same_src.exists() and Image.open(same_src).size == (300, 200)
      and same_out.exists() and Image.open(same_out).size == (1200, 800),
      f"source {Image.open(same_src).size if same_src.exists() else 'missing'}, "
      f"result {Image.open(same_out).size if same_out.exists() else 'missing'}")

# "x.jpg" and "x.png" from one folder: both results are kept, each named with its source's extension.
dup = out / "dup_out"
check("dup_names", (dup / "x (jpg).png").exists() and (dup / "x (png).png").exists() and not (dup / "x.png").exists()
      and Image.open(dup / "x (jpg).png").size == (90 * 4, 160 * 4) and Image.open(dup / "x (png).png").size == (1200, 800),
      ", ".join(sorted(p.name for p in dup.iterdir())) if dup.exists() else "folder missing")

# A save that fails in the background (the first file of a batch is written while the second one is processed): reported with
# the file's number, counted as failed, no temporary file left behind, and the second file saved as usual.
text = log("bg_save_fail")
bgsave = out / "bgsave_out"
check("bg_save_fail", "[1/2] Not saved:" in text and "Done: 1 of 2" in text
      and not (list(bgsave.glob("krupno-*.tmp")) if bgsave.exists() else []),
      " | ".join(line for line in text.splitlines() if "Not saved" in line or "Done" in line) or "empty output")
expect_nearest("noise_300x200.png", "bgsave_out/b.png", 4, [1, 0, 0])

# -r: the subfolder is repeated in the output folder. --skip-existing: with every result there nothing is loaded or written;
# with one result deleted, only that one is made again.
expect_nearest("noise_300x200.png", "tree_out/a.png", 4, [1, 0, 0])
got = load(out / "tree_out" / "sub" / "b.png") if (out / "tree_out" / "sub" / "b.png").exists() else np.zeros((0, 0, 3))
check("tree_out/sub/b.png", got.shape == (160 * 4, 90 * 4, 3), f"shape {got.shape}")
text = log("skip_all")
check("skip_all", "Skipping 2 of 2" in text and "Model:" not in text, " | ".join(text.splitlines()) or "empty output")
text = log("skip_some")
check("skip_some", "Skipping 1 of 2" in text and "sub" in text and "a.png" not in text.replace("Skipping", ""),
      " | ".join(line for line in text.splitlines() if line.strip()) or "empty output")

# A pattern and a file together: the pattern takes a.png and b.png but not c.jpg, and all go into one folder.
multi = out / "multi_out"
check("multi_pattern", sorted(p.name for p in multi.iterdir()) == ["a.png", "b.png", "orient6.png"] if multi.exists() else False,
      ", ".join(sorted(p.name for p in multi.iterdir())) if multi.exists() else "folder missing")

print("ALL OK" if failures == 0 else f"{failures} FAILED")
sys.exit(1 if failures else 0)
