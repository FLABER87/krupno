"""Inputs for the synthetic tests: images and tiny ONNX models with an exactly known result.

    python tests/make_fixtures.py            # images (requires numpy and Pillow)
    python tests/make_fixtures.py --models   # also regenerate the models (additionally requires onnx)

The models are a few hundred bytes each and live in git (tests/fixtures/models); the images are regenerated on every run.
"""
import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image

parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
parser.add_argument("root", nargs="?", type=Path, default=Path(__file__).resolve().parent / "fixtures")
parser.add_argument("--models", action="store_true", help="also regenerate the models (requires the onnx package)")
args = parser.parse_args()
sys.stdout.reconfigure(encoding="utf-8")
root = args.root


def resize_model(path, scale, dims, keep, meta=None):
    """out = nearest_upscale(x, scale) * keep (per channel), so that a swapped channel order is visible."""
    import onnx
    from onnx import TensorProto, helper, numpy_helper

    x = helper.make_tensor_value_info("input", TensorProto.FLOAT, dims)
    out_dims = [d * scale if isinstance(d, int) and i >= 2 else (None if i >= 2 else d) for i, d in enumerate(dims)]
    y = helper.make_tensor_value_info("output", TensorProto.FLOAT, out_dims)
    inits = [
        numpy_helper.from_array(np.array([1, 1, scale, scale], np.float32), "scales"),
        numpy_helper.from_array(np.array(keep, np.float32).reshape(1, 3, 1, 1), "keep"),
    ]
    nodes = [
        helper.make_node("Resize", ["input", "", "scales"], ["up"], mode="nearest",
                         coordinate_transformation_mode="asymmetric", nearest_mode="floor"),
        helper.make_node("Mul", ["up", "keep"], ["output"]),
    ]
    graph = helper.make_graph(nodes, "g", [x], [y], inits)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 9
    if meta:
        helper.set_model_props(model, meta)
    onnx.checker.check_model(model)
    onnx.save(model, str(path))


if args.models:
    m = root / "models"
    m.mkdir(parents=True, exist_ok=True)
    # Named dynamic axes and krupno.* metadata: the scale is taken from the metadata.
    resize_model(m / "nearest_red_x4.onnx", 4, [1, 3, "height", "width"], [1, 0, 0],
                 {"krupno.scale": "4", "krupno.name": "Nearest red x4", "krupno.architecture": "Test"})
    # Static 64×64 input: the scale comes from the output shape, and the image is cut into tiles of exactly this size.
    resize_model(m / "fixed64_x2.onnx", 2, [1, 3, 64, 64], [0, 1, 0])
    # Unnamed axes and no metadata: the scale is determined by a probe run.
    resize_model(m / "probe_x3.onnx", 3, [1, 3, None, None], [0, 0, 1])
    # Metadata with the legacy upscalr.* prefix (models converted before the project was renamed).
    resize_model(m / "legacy_x2.onnx", 2, [1, 3, "height", "width"], [1, 1, 1],
                 {"upscalr.scale": "2", "upscalr.name": "Legacy x2", "upscalr.architecture": "Test"})

img = root / "images"
img.mkdir(parents=True, exist_ok=True)
rng = np.random.default_rng(1)
Image.fromarray(rng.integers(0, 256, (200, 300, 3), np.uint8)).save(img / "noise_300x200.png")
Image.fromarray(rng.integers(0, 256, (1000, 1500, 3), np.uint8)).save(img / "big_1500x1000.png")
rgba = rng.integers(0, 256, (129, 257, 4), np.uint8)
rgba[..., 3] = np.tile(np.linspace(0, 255, 257).astype(np.uint8), (129, 1))
Image.fromarray(rgba).save(img / "alpha_257x129.png")

# JPEG with EXIF orientation 6 (rotate 90° clockwise for display): stored as 160×90, displayed as 90×160.
stored = np.zeros((90, 160, 3), np.uint8)
stored[:10, :10] = [255, 0, 0]  # red mark in the top-left corner of the stored frame
exif = Image.Exif()
exif[0x0112] = 6
Image.fromarray(stored).save(img / "orient6.jpg", quality=98, exif=exif)

# PNG with an embedded ICC profile (the Windows system sRGB profile).
icc_path = Path(r"C:\Windows\System32\spool\drivers\color\sRGB Color Space Profile.icm")
if icc_path.exists():
    Image.fromarray(rng.integers(0, 256, (40, 60, 3), np.uint8)).save(img / "icc_60x40.png", icc_profile=icc_path.read_bytes())

# A file with a .png extension that isn't an image: a read error, not a program crash.
broken = root / "broken"
broken.mkdir(parents=True, exist_ok=True)
(broken / "broken.png").write_bytes(b"this is not a png\n" * 4)

print(f"fixtures ready: {root}")
