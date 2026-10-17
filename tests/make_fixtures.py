"""Inputs for the synthetic tests: images and tiny ONNX models with an exactly known result.

    python tests/make_fixtures.py            # images (requires numpy and Pillow)
    python tests/make_fixtures.py --models   # also regenerate the models (additionally requires onnx)

The models are a few hundred bytes each and live in git (tests/fixtures/models); the images are regenerated on every run.
"""
import argparse
import itertools
import struct
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


def resize_model(path, scale, dims, keep, meta=None, half=False):
    """out = nearest_upscale(x, scale) * keep (per channel), so that a swapped channel order is visible.
    half: fp16 input and output, as some third-party models have."""
    import onnx
    from onnx import TensorProto, helper, numpy_helper

    element = TensorProto.FLOAT16 if half else TensorProto.FLOAT
    x = helper.make_tensor_value_info("input", element, dims)
    out_dims = [d * scale if isinstance(d, int) and i >= 2 else (None if i >= 2 else d) for i, d in enumerate(dims)]
    y = helper.make_tensor_value_info("output", element, out_dims)
    inits = [
        numpy_helper.from_array(np.array([1, 1, scale, scale], np.float32), "scales"),
        numpy_helper.from_array(np.array(keep, np.float16 if half else np.float32).reshape(1, 3, 1, 1), "keep"),
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


D50 = (0.9642, 1.0, 0.8249)


def s15(*values):
    return b"".join(struct.pack(">i", round(v * 65536)) for v in values)


def xyz(*values):
    return b"XYZ \0\0\0\0" + s15(*values)


def icc_profile(device_class, color_space, pcs, name, tags):
    """A minimal ICC v2 profile: the header, a description, a copyright and the D50 white point, then the given tags."""
    name = name.encode() + b"\0"
    tags = [
        (b"desc", b"desc\0\0\0\0" + struct.pack(">I", len(name)) + name + bytes(4 + 4 + 2 + 1 + 67)),
        (b"cprt", b"text\0\0\0\0Public domain\0"),
        (b"wtpt", xyz(*D50)),
    ] + tags
    data, table = b"", []
    start = 128 + 4 + 12 * len(tags)
    for sig, body in tags:
        data += bytes(-len(data) % 4)
        table.append(struct.pack(">4sII", sig, start + len(data), len(body)))
        data += body
    size = start + len(data)
    header = (struct.pack(">I", size) + bytes(4) + bytes([2, 0x10, 0, 0]) + device_class + color_space + pcs + bytes(12) + b"acsp"
              + bytes(4 + 4 + 4 + 4 + 8 + 4) + s15(*D50)).ljust(128, b"\0")
    return header + struct.pack(">I", len(tags)) + b"".join(table) + data


def display_p3_profile():
    """Display P3: P3 primaries adapted to D50, the sRGB tone curve. Windows ships no wide-gamut profile."""
    x = np.linspace(0, 1, 1024)
    curve = np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)
    trc = b"curv\0\0\0\0" + struct.pack(">I", 1024) + np.round(curve * 65535).astype(">u2").tobytes()
    return icc_profile(b"mntr", b"RGB ", b"XYZ ", "Display P3 (Krupno test)", [
        (b"rXYZ", xyz(0.51512, 0.24119, -0.00105)),
        (b"gXYZ", xyz(0.29198, 0.69224, 0.04188)),
        (b"bXYZ", xyz(0.15710, 0.06657, 0.78407)),
        (b"rTRC", trc), (b"gTRC", trc), (b"bTRC", trc),
    ])


def gray_profile(gamma):
    """A grayscale profile with a plain gamma curve, like Photoshop's "Gray Gamma 1.8"."""
    trc = b"curv\0\0\0\0" + struct.pack(">IH", 1, round(gamma * 256))
    return icc_profile(b"mntr", b"GRAY", b"XYZ ", f"Gray Gamma {gamma} (Krupno test)", [(b"kTRC", trc)])


def lut16(inputs, outputs, grid, table):
    """An ICC v2 lut16Type with identity curves: table holds grid ** inputs rows of outputs values in 0..1, the first input slowest."""
    identity = struct.pack(">2H", 0, 65535)
    return (b"mft2\0\0\0\0" + bytes([inputs, outputs, grid, 0]) + s15(1, 0, 0, 0, 1, 0, 0, 0, 1) + struct.pack(">2H", 2, 2)
            + identity * inputs + np.round(np.clip(table, 0, 1) * 65535).astype(">u2").tobytes() + identity * outputs)


def toy_cmyk_profile():
    """A CMYK press with inks far from SWOP. WIC converts CMYK without a profile as SWOP, so only this profile shows whether the
    embedded one was applied. Each grid node is white paper times the transmittance of every ink, taken as linear sRGB."""
    levels = np.linspace(0, 1, 5)
    inks = np.array([[0.2, 0.7, 0.95], [0.95, 0.25, 0.55], [0.98, 0.9, 0.1], [0.1, 0.1, 0.1]])  # C, M, Y, K
    nodes = np.array(list(itertools.product(levels, repeat=4)))
    rgb = np.prod(1 - nodes[:, :, None] * (1 - inks), axis=1)
    srgb_to_xyz_d50 = np.array([[0.43607, 0.38515, 0.14308], [0.22250, 0.71688, 0.06062], [0.01393, 0.09710, 0.71417]])
    t = rgb @ srgb_to_xyz_d50.T / D50
    f = np.where(t > (6 / 29) ** 3, np.cbrt(t), t / (3 * (6 / 29) ** 2) + 4 / 29)
    lab = np.stack([116 * f[:, 1] - 16, 500 * (f[:, 0] - f[:, 1]), 200 * (f[:, 1] - f[:, 2])], 1)
    # The ICC v2 16-bit Lab encoding: L* 0..100 is 0..0xFF00, a* and b* -128..127.996 are 0..0xFFFF.
    a2b = lut16(4, 3, len(levels), np.stack([lab[:, 0] * 652.8, (lab[:, 1] + 128) * 256, (lab[:, 2] + 128) * 256], 1) / 65535)
    # Nothing converts to CMYK here, but Windows rejects a CMYK profile without all three intents both ways and a gamut tag.
    b2a, gamut = lut16(3, 4, 2, np.zeros((8, 4))), lut16(3, 1, 2, np.zeros((8, 1)))
    return icc_profile(b"prtr", b"CMYK", b"Lab ", "Toy press CMYK (Krupno test)", [
        (b"A2B0", a2b), (b"A2B1", a2b), (b"A2B2", a2b), (b"B2A0", b2a), (b"B2A1", b2a), (b"B2A2", b2a), (b"gamt", gamut),
    ])


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
    # fp16 input and output: each byte level survives the trip through fp16 (b/255 has 11 significant bits), so the result is exact.
    resize_model(m / "half_x2.onnx", 2, [1, 3, "height", "width"], [1, 1, 0], {"krupno.scale": "2"}, half=True)

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

# Color-managed preview (tests/gui_states.ps1 -Scenario color): one flat color tagged Display P3, where the window must show it
# converted to sRGB, and the same pixels untagged, shown as they are. A separate folder, so the other tests don't pick them up.
color = root / "color"
color.mkdir(parents=True, exist_ok=True)
green = np.full((400, 600, 3), [60, 160, 70], np.uint8)
Image.fromarray(green).save(color / "wide_p3.png", icc_profile=display_p3_profile())
Image.fromarray(green).save(color / "srgb_untagged.png")

# CMYK and grayscale profiles: an RGB result can't carry them, so the pixels are converted to sRGB on load.
# Flat CMYK patches (96×64): paper, the four inks, a mix, a bright mix, half black. A separate folder again.
profiles = root / "profiles"
profiles.mkdir(parents=True, exist_ok=True)
patches = [[0, 0, 0, 0], [255, 0, 0, 0], [0, 255, 0, 0], [0, 0, 255, 0], [0, 0, 0, 255], [128, 64, 32, 16], [40, 200, 120, 0], [0, 0, 0, 128]]
cmyk = Image.frombytes("CMYK", (96, 64), np.array(patches, np.uint8).reshape(2, 4, 4).repeat(32, 0).repeat(24, 1).tobytes())
swop_path = Path(r"C:\Windows\System32\spool\drivers\color\RSWOP.icm")
if swop_path.exists():
    cmyk.save(profiles / "cmyk_swop.jpg", quality=100, icc_profile=swop_path.read_bytes())
cmyk.save(profiles / "cmyk_toy.tif", icc_profile=toy_cmyk_profile())
gray = np.tile(np.arange(256, dtype=np.uint8), (32, 1))
Image.fromarray(gray).save(profiles / "gray.png", icc_profile=gray_profile(1.8))
Image.fromarray(np.dstack([gray, np.full_like(gray, 160)])).save(profiles / "gray_alpha.png", icc_profile=gray_profile(1.8))

# A file with a .png extension that isn't an image: a read error, not a program crash.
broken = root / "broken"
broken.mkdir(parents=True, exist_ok=True)
(broken / "broken.png").write_bytes(b"this is not a png\n" * 4)
# A GIF header followed by garbage: WIC reports it with ArgumentException ("The image is missing a frame"), not NotSupportedException.
(broken / "broken.gif").write_bytes(b"GIF89a\x01\x00\x01\x00garbage")

print(f"fixtures ready: {root}")
