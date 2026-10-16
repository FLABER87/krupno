"""Checks the results of tests/run_cli_tests.ps1 against exactly computed expectations.

    python tests/check_outputs.py [<fixtures folder>] [<results folder>]
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image

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
expect_model("noise_red_cpu", "Nearest red x4 (×4, Test)")
expect_model("big_fixed_cpu", "fixed64_x2 (×2)")
expect_model("noise_probe_cpu", "probe_x3 (×3)")
expect_model("noise_legacy", "Legacy x2 (×2, Test)")

# Alpha: RGB is exact, alpha is Lanczos (compared approximately with Pillow's Lanczos).
src = np.asarray(Image.open(img / "alpha_257x129.png")).astype(np.int32)
got = np.asarray(Image.open(out / "alpha_red.png")).astype(np.int32)
exp_rgb = np.repeat(np.repeat(src[..., :3], 4, 0), 4, 1) * np.array([1, 0, 0])
rgb_ok = got.shape[:2] == exp_rgb.shape[:2] and got.shape[2] == 4 and int(np.abs(got[..., :3] - exp_rgb).max()) == 0
pil_alpha = np.asarray(Image.fromarray(src[..., 3].astype(np.uint8)).resize((257 * 4, 129 * 4), Image.LANCZOS)).astype(np.int32)
alpha_err = float(np.abs(got[..., 3] - pil_alpha).mean()) if got.shape[2] == 4 else 999
check("alpha_red.png", rgb_ok and alpha_err < 1.5, f"rgb exact={rgb_ok}, alpha mean diff vs Pillow Lanczos {alpha_err:.2f}")

# Fractional scale: ×2.5 with a ×4 model means downscaling the ×4 result with a Lanczos filter.
got = load(out / "noise_red_x2_5.png")
check("noise_red_x2_5.png", got.shape == (500, 750, 3) and int(got[..., 1:].max()) == 0,
      f"shape {got.shape}, G/B max {int(got[..., 1:].max())}")

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

# Folder → folder: all images, name without a suffix, .png extension.
sources = sorted(p for p in img.iterdir() if p.suffix.lower() in (".png", ".jpg"))
missing = [p.name for p in sources if not (out / "batch" / (p.stem + ".png")).exists()]
check("batch", not missing and f"Done: {len(sources)} of {len(sources)}" in log("batch"),
      f"{len(sources) - len(missing)} of {len(sources)} files" + (f", missing: {', '.join(missing)}" if missing else ""))
expect_nearest("noise_300x200.png", "batch/noise_300x200.png", 4, [1, 0, 0])
got = load(out / "batch" / "orient6.png") if (out / "batch" / "orient6.png").exists() else np.zeros((0, 0, 3))
check("batch/orient6.png", got.shape == (160 * 4, 90 * 4, 3), f"shape {got.shape}")

# Broken file: a clear error and exit code 1, not a crash and not an empty result.
check("broken.log", "Error:" in log("broken") and not (out / "broken.png").exists(),
      " | ".join(line for line in log("broken").splitlines() if line.strip()) or "empty output")

print("ALL OK" if failures == 0 else f"{failures} FAILED")
sys.exit(1 if failures else 0)
