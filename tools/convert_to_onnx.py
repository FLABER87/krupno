#!/usr/bin/env python3
"""Converts upscaling models from PyTorch (.pth / .safetensors / .ckpt) to ONNX for Krupno.

spandrel detects the architecture from the weights: ESRGAN / Real-ESRGAN, Compact, SPAN, RealPLKSR, DAT,
HAT, SwinIR, OmniSR and many others. Everything the app needs goes into the ONNX metadata:
scale, architecture, input size requirements, precision.

The model is first exported with a dynamic input size and checked against PyTorch at a different size.
If the tracer "baked" the sizes into the graph (this happens with DAT/HAT and with DySample), the model is
exported again via torch.export (dynamo) with symbolic sizes. If that fails as well, it is exported with a
static input (--static-fallback), and Krupno tiles the image to fit it.

Examples:
    python tools/convert_to_onnx.py RealESRGAN_x4plus.pth
    python tools/convert_to_onnx.py 4xNomos2.safetensors --fp16 --license "CC-BY-4.0"
    python tools/convert_to_onnx.py models_pth/*.pth -o models/

This is a development tool: the app needs neither torch nor spandrel.
"""

from __future__ import annotations

import argparse
import copy
import inspect
import math
import re
import sys
import time
from pathlib import Path

REPO_MODELS_DIR = Path(__file__).resolve().parent.parent / "models"
META_PREFIX = "krupno."  # models converted before the rename use "upscalr." — Krupno reads both
DEFAULT_OPSET = 17  # The DirectML EP supports opset up to 20; 17 leaves a compatibility margin.
DYNAMO_OPSET = 18  # the torch.export exporter's native opset: with any other it converts the graph and may break it
DYNAMO_MAX_SIZE = 4096  # upper bound of the axes for torch.export's reasoning; it does not end up in the ONNX
DML_GRID_SAMPLE_PIXELS = 2**23  # beyond this, GridSample in DirectML mixes up points (see dynamo_translations)
DEFAULT_STATIC_FALLBACK = 256
# A difference of 2/255 is already visible to the eye; fp16 scatters noticeably more than fp32 but stays within it.
TOLERANCE = 2 / 255


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="PyTorch → ONNX conversion of upscaling models for Krupno.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("models", nargs="+", type=Path, help="weight files (.pth, .safetensors, .ckpt)")
    parser.add_argument("-o", "--output", type=Path,
                        help=f".onnx file or folder (default: {REPO_MODELS_DIR})")
    parser.add_argument("--fp16", action="store_true",
                        help="weights and compute in fp16 (input and output stay fp32); half the GPU memory")
    parser.add_argument("--static", type=int, metavar="N",
                        help="export right away with a fixed N×N input instead of a dynamic one")
    parser.add_argument("--static-fallback", type=int, metavar="N", default=DEFAULT_STATIC_FALLBACK,
                        help="static input size if the dynamic one could not be preserved "
                             f"(default: {DEFAULT_STATIC_FALLBACK})")
    parser.add_argument("--no-dynamo", action="store_true",
                        help="don't try torch.export if the tracer \"baked\" the sizes in: go straight to a static input")
    parser.add_argument("--opset", type=int, default=DEFAULT_OPSET,
                        help=f"opset version (default: {DEFAULT_OPSET}; the torch.export path always uses {DYNAMO_OPSET})")
    parser.add_argument("--name", help="model display name (default: the file name)")
    parser.add_argument("--license", help="model license; goes into the metadata and the UI")
    parser.add_argument("--device", choices=["auto", "cpu", "cuda"], default="auto",
                        help="device to trace the model on (fp16 is more reliable on CUDA)")
    parser.add_argument("--no-verify", action="store_true", help="don't check the ONNX result against PyTorch")
    return parser.parse_args()


def fail(message: str) -> None:
    print(f"Error: {message}", file=sys.stderr)
    sys.exit(1)


def align_up(value: int, multiple: int) -> int:
    return (value + multiple - 1) // multiple * multiple


def output_path_for(source: Path, args: argparse.Namespace) -> Path:
    suffix = "_fp16" if args.fp16 else ""
    name = f"{source.stem}{suffix}.onnx"
    if args.output is None:
        return REPO_MODELS_DIR / name
    if len(args.models) > 1 or args.output.is_dir() or args.output.suffix.lower() != ".onnx":
        return args.output / name
    return args.output


def convert(source: Path, args: argparse.Namespace) -> None:
    import torch
    from spandrel import ImageModelDescriptor, ModelLoader

    if not source.is_file():
        fail(f"file not found: {source}")

    device = torch.device("cuda" if args.device == "cuda" or (args.device == "auto" and torch.cuda.is_available()) else "cpu")
    descriptor = ModelLoader(device="cpu").load_from_file(str(source))
    if not isinstance(descriptor, ImageModelDescriptor):
        fail(f"{source.name}: not an image processing model")
    if descriptor.input_channels != 3 or descriptor.output_channels != 3:
        fail(f"{source.name}: only RGB models are supported "
             f"(this one has {descriptor.input_channels} → {descriptor.output_channels} channels)")
    if descriptor.purpose not in ("SR", "Restoration"):
        fail(f"{source.name}: purpose is \"{descriptor.purpose}\", but upscaling (SR) or restoration is required")

    arch = descriptor.architecture.name
    req = descriptor.size_requirements
    print(f"{source.name}: {arch}, ×{descriptor.scale}, "
          f"multiple of {req.multiple_of}, minimum {req.minimum}{', square' if req.square else ''}")

    if args.fp16 and not descriptor.supports_half:
        print("  Warning: spandrel considers this architecture unreliable in fp16 — check the result.")
    if args.fp16 and device.type == "cpu":
        print("  Warning: fp16 is traced on the CPU; if it fails, you need PyTorch with CUDA (--device cuda).")

    for name, value in (("--static", args.static), ("--static-fallback", args.static_fallback)):
        if value and (value % req.multiple_of or value < req.minimum):
            fail(f"{name} {value}: the size must be a multiple of {req.multiple_of} and at least {req.minimum}")

    model = descriptor.model.eval().to(device)
    if args.fp16:
        full = copy.deepcopy(model)
        model = model.half()
        check_half_stability(full, model, device, args.static or args.static_fallback, source.name)
        del full
    wrapper = _IOWrapper(model, half=args.fp16).eval()

    target = output_path_for(source, args)
    target.parent.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()

    static_size = args.static
    size_multiple, size_minimum, size_maximum = req.multiple_of, req.minimum, None
    if static_size is None:
        trace_size = align_up(max(64, req.minimum), req.multiple_of)
        export_onnx(wrapper, target, trace_size, device, args.opset, dynamic=True)
        if args.no_verify:
            print("  Warning: without verification there is no telling whether the dynamic input size survived. "
                  "For DAT/HAT/DySample, pass --static N.")
        else:
            # Check at a size different from the trace size: this shows whether the dynamic shape survived.
            height = trace_size + req.multiple_of * 2
            width = height if req.square else trace_size + req.multiple_of * 3
            deviation = max_deviation(target, wrapper, device, height, width)
            if deviation is not None:
                report(height, width, deviation)
                if deviation >= TOLERANCE:
                    # The tracer "baked" the sizes (attention windows, coordinate grids) into the graph.
                    dynamic = None
                    if not args.no_dynamo:
                        print("  The dynamic size did not survive tracing — trying torch.export.")
                        dynamic = export_dynamo(wrapper, target, req, descriptor.scale, device)
                    if dynamic is not None:
                        size_multiple, size_minimum, size_maximum = dynamic
                    else:
                        static_size = args.static_fallback
                        reason = "did not survive tracing" if args.no_dynamo else "could not be preserved"
                        print(f"  The dynamic size {reason} — exporting with a static "
                              f"{static_size}×{static_size} input (Krupno will cut the image into tiles of this size).")

    if static_size is not None:
        export_onnx(wrapper, target, static_size, device, args.opset, dynamic=False)
        if not args.no_verify:
            deviation = max_deviation(target, wrapper, device, static_size, static_size)
            if deviation is not None:
                report(static_size, static_size, deviation)
                if deviation >= TOLERANCE:
                    target.unlink(missing_ok=True)
                    hint = " (this architecture is numerically unstable in fp16 — use fp32)" if args.fp16 else ""
                    fail(f"{source.name}: ONNX diverges from PyTorch even with a static input{hint}")

    import onnx

    onnx_model = onnx.load(str(target))
    metadata = {
        "scale": descriptor.scale,
        "architecture": arch,
        "name": args.name or source.stem,
        "size_multiple": size_multiple,
        "size_minimum": size_minimum,
        "square": int(req.square),
        "precision": "fp16" if args.fp16 else "fp32",
        "source": source.name,
        "tags": ", ".join(descriptor.tags),
    }
    if size_maximum:
        metadata["size_maximum"] = size_maximum
    if args.license:
        metadata["license"] = args.license
    onnx.helper.set_model_props(onnx_model, {META_PREFIX + k: str(v) for k, v in metadata.items()})
    onnx.checker.check_model(onnx_model)
    onnx.save(onnx_model, str(target))

    size_mb = target.stat().st_size / 1024 / 1024
    if static_size:
        shape = f"{static_size}×{static_size} input"
    else:
        shape = "dynamic input" + (f", multiple of {size_multiple}" if size_multiple > 1 else "")
        if size_maximum:
            shape += f", at most {size_maximum}"
    print(f"  → {target} ({size_mb:.1f} MB, {shape}, {time.perf_counter() - started:.1f} s)")


def check_half_stability(full, half, device, size: int, name: str) -> None:
    """Compares fp16 and fp32 in PyTorch itself: some architectures (for example, the DAT in UltraSharpV2)
    overflow in fp16 and output garbage. Fast on the GPU; skipped on the CPU."""
    import torch

    if device.type != "cuda":
        print("  fp16 stability check skipped: CUDA is required.")
        return

    torch.backends.cudnn.allow_tf32 = False
    torch.backends.cuda.matmul.allow_tf32 = False
    torch.manual_seed(0)
    sample = torch.rand(1, 3, size, size, device=device)
    with torch.no_grad():
        expected = full(sample).clamp(0, 1)
        actual = half(sample.half()).float().nan_to_num(0).clamp(0, 1)
    diff = (actual - expected).abs()
    mean, peak = diff.mean().item(), diff.max().item()
    print(f"  fp16 vs fp32 ({size}×{size}): mean deviation {mean * 255:.2f}, max {peak * 255:.0f} levels")
    if mean >= 1 / 255:
        fail(f"{name}: the model is numerically unstable in fp16 — use fp32")


def export_onnx(wrapper, target: Path, size: int, device, opset: int, dynamic: bool) -> None:
    import torch

    kwargs = dict(input_names=["input"], output_names=["output"], opset_version=opset, do_constant_folding=True)
    if dynamic:
        # Axis names matter: Krupno uses them to pin the shape for DirectML (AddFreeDimensionOverrideByName).
        kwargs["dynamic_axes"] = {
            "input": {2: "height", 3: "width"},
            "output": {2: "out_height", 3: "out_width"},
        }
    if "dynamo" in inspect.signature(torch.onnx.export).parameters:
        # The classic exporter: for convolutional and windowed SR networks it is more stable and produces a simple graph.
        kwargs["dynamo"] = False

    dummy = torch.rand(1, 3, size, size, device=device)
    with torch.no_grad():
        torch.onnx.export(wrapper, (dummy,), str(target), **kwargs)


def export_dynamo(wrapper, target: Path, req, scale: int, device) -> tuple[int, int, int | None] | None:
    """Export via torch.export: height and width in the graph are symbolic rather than numbers from the trace.

    torch.export reasons about sizes symbolically and refuses if it cannot prove that the graph
    is valid for any size. DAT, for example, pads the input to 8×32 windows, and with an arbitrary size
    the proof does not go through, while with a multiple of 8 it does. So the multiple is searched for.
    Returns (multiple, minimum size, maximum size or None), or None if it did not work out.
    """
    import logging

    import torch

    try:
        import onnxscript  # noqa: F401
        from torch.export import Dim
    except ImportError:
        print("  torch.export skipped: onnxscript is required (pip install onnxscript).")
        return None
    # Failed torch.export attempts log dozens of lines about guards, and the onnxscript optimizer logs
    # hundreds of warnings; the gist of the error is printed as a single line anyway.
    for name in ("torch._guards", "torch._dynamo", "torch.fx.experimental.symbolic_shapes", "torch.onnx", "onnxscript"):
        logging.getLogger(name).setLevel(logging.CRITICAL)

    program = sample = None
    for multiple in dynamo_multiples(req.multiple_of):
        minimum = align_up(max(req.minimum, multiple), multiple)
        if multiple == 1:
            height = Dim("height", min=minimum, max=DYNAMO_MAX_SIZE)
            width = Dim("width", min=minimum, max=DYNAMO_MAX_SIZE)
        else:
            height = multiple * Dim("height_blocks", min=minimum // multiple, max=DYNAMO_MAX_SIZE // multiple)
            width = multiple * Dim("width_blocks", min=minimum // multiple, max=DYNAMO_MAX_SIZE // multiple)
        # The sample is not square, so that a special case (height = width) is not mistaken for the general one.
        base = align_up(max(64, minimum), multiple)
        sample = torch.rand(1, 3, base + multiple, base + 2 * multiple, device=device)
        reason = ""
        # Non-strict mode runs forward as is; strict mode (via Dynamo) sometimes infers sizes
        # where non-strict specializes them (DySample in PyTorch 2.6).
        for strict in (False, True):
            try:
                with torch.no_grad():
                    program = torch.export.export(wrapper, (sample,), dynamic_shapes=({2: height, 3: width},),
                                                  strict=strict)
            except Exception as e:  # noqa: BLE001 — torch.export errors vary; only the failure itself matters
                program, reason = None, reason or short_error(e)
                continue
            size_minimum = dynamo_minimum(program)
            if size_minimum is not None:
                break
            program, reason = None, reason or "sizes became constants"
        if program is not None:
            break
        print(f"  torch.export with multiple {multiple}: {reason}")
    else:
        return None

    print(f"  torch.export: size is a multiple of {multiple}, from {size_minimum} px; converting to ONNX (takes minutes for large networks).")
    try:
        with torch.no_grad():
            onnx_program = torch.onnx.export(
                program, (sample,), dynamo=True, input_names=["input"], output_names=["output"],
                opset_version=DYNAMO_OPSET, optimize=True, verbose=False,
                custom_translation_table=dynamo_translations(),
            )
        onnx_program.save(str(target))
        grid_sample = finish_dynamo_graph(target)
    except Exception as e:  # noqa: BLE001
        print(f"  Conversion to ONNX failed: {short_error(e)}")
        return None
    # Beyond this window DirectML corrupts GridSample without any error, so the app must know the limit.
    size_maximum = None
    if grid_sample:
        size_maximum = math.isqrt(DML_GRID_SAMPLE_PIXELS) // scale // multiple * multiple

    # Check at sizes different from the sample: if something did get "baked" in after all, it will show.
    base = align_up(max(64, size_minimum), multiple)
    for height, width in ((base + 3 * multiple, base), (base, base + 5 * multiple)):
        deviation = max_deviation(target, wrapper, device, height, width)
        if deviation is None:
            break
        report(height, width, deviation)
        if deviation >= TOLERANCE:
            return None
    return multiple, size_minimum, size_maximum


def dynamo_multiples(base: int) -> list[int]:
    """Size multiples to try in turn, starting with the model's native multiple."""
    multiples = [base]
    for step in (8, 16, 32, 64):
        multiple = math.lcm(base, step)
        if multiple > multiples[-1]:
            multiples.append(multiple)
    return multiples


def dynamo_minimum(program) -> int | None:
    """Minimum input size for which the graph is valid (torch.export narrows the given range);
    None if the sizes became constants."""
    import torch

    inputs = [node for node in program.graph.nodes if node.op == "placeholder"]
    shape = inputs[-1].meta["val"].shape  # parameters and buffers come before the user input
    minimum = 0
    for size in shape[2:]:
        if not isinstance(size, torch.SymInt):
            return None
        expr = size.node.expr
        lower = expr.subs({s: program.range_constraints[s].lower for s in expr.free_symbols})
        minimum = max(minimum, int(lower))
    return minimum


def dynamo_translations() -> dict:
    """Custom ONNX implementations of operators for the torch.export exporter."""
    import torch
    from onnxscript import opset18 as op

    int64_max = 2**63 - 1

    def slice_scatter(self, src, dim: int = 0, start: int | None = None, end: int | None = None, step: int = 1):
        """x[:, a:b] = y. The stock implementation is ScatterND with transposes; on DirectML it makes
        RealPLKSR four times slower. Concatenating slices does the same without extra copies."""
        if step != 1:
            raise NotImplementedError("slice_scatter with a step other than 1")
        dim %= len(self.shape)
        size = self.shape[dim]
        if isinstance(size, int):
            start = 0 if start is None else max(0, start + size if start < 0 else start)
            end = size if end is None or end >= int64_max else min(size, end + size if end < 0 else end)
        parts = []
        if start:
            parts.append(op.Slice(self, [0], [start], [dim]))
        parts.append(src)
        if end is not None and end < int64_max and not (isinstance(size, int) and end >= size):
            parts.append(op.Slice(self, [end], [int64_max], [dim]))
        return parts[0] if len(parts) == 1 else op.Concat(*parts, axis=dim)

    def grid_sampler(input, grid, interpolation_mode: int, padding_mode: int, align_corners: bool):
        """DirectML computes the flat output pixel index (n·H + y)·W + x in float32 and past 2²³ picks
        the wrong points. DySample samples 4 groups in a single batch, and even a 384 window corrupts the
        bottom of the image. One batch image per call gives a limit of 2²³ pixels per image."""
        mode = ("bilinear", "nearest", "bicubic")[interpolation_mode]
        padding = ("zeros", "border", "reflection")[padding_mode]
        batch = input.shape[0]
        if not isinstance(batch, int) or batch == 1:
            return op.GridSample(input, grid, align_corners=int(align_corners), mode=mode, padding_mode=padding)
        parts = [
            op.GridSample(op.Slice(input, [n], [n + 1], [0]), op.Slice(grid, [n], [n + 1], [0]),
                          align_corners=int(align_corners), mode=mode, padding_mode=padding)
            for n in range(batch)
        ]
        return op.Concat(*parts, axis=0)

    return {
        torch.ops.aten.slice_scatter.default: slice_scatter,
        torch.ops.aten.grid_sampler.default: grid_sampler,
        torch.ops.aten.grid_sampler_2d.default: grid_sampler,
    }


def finish_dynamo_graph(target: Path) -> bool:
    """Brings the torch.export graph into a form that Krupno and DirectML understand. Returns whether it contains GridSample."""
    import onnx

    model = onnx.load(str(target))
    graph = model.graph
    # Axis names matter: Krupno uses them to pin the shape for DirectML (AddFreeDimensionOverrideByName).
    # torch.export names the axes s0, s1 or with expressions like 32*s0.
    names = {}
    for value, axis_names in ((graph.input[0], ("height", "width")), (graph.output[0], ("out_height", "out_width"))):
        dims = value.type.tensor_type.shape.dim
        for dim, name in zip((dims[2], dims[3]), axis_names):
            if dim.dim_param:
                names.setdefault(dim.dim_param, name)
    for value in (*graph.input, *graph.output, *graph.value_info):
        for dim in value.type.tensor_type.shape.dim:
            if dim.dim_param in names:
                dim.dim_param = names[dim.dim_param]
    # DirectML does not accept Reshape with allowzero=1 ("The parameter is incorrect"), and torch.export sets it
    # for views with a computed shape. Upscaler tensors never have zero-sized dimensions, so the meaning is unchanged.
    for node in graph.node:
        if node.op_type == "Reshape":
            for attr in node.attribute:
                if attr.name == "allowzero":
                    attr.i = 0
    # torch.export writes debug info (call stack, module names) into every node: 10 MB for DAT2.
    for node in graph.node:
        del node.metadata_props[:]
    # The exporter keeps a copy of identical buffers of different blocks (relative_position_index in DAT) per block:
    # 17 MB for DAT2. Keep a single instance of each.
    first, renamed = {}, {}
    for tensor in list(graph.initializer):
        if not tensor.raw_data:
            continue
        key = (tensor.data_type, tuple(tensor.dims), tensor.raw_data)
        if key in first:
            renamed[tensor.name] = first[key]
            graph.initializer.remove(tensor)
        else:
            first[key] = tensor.name
    for node in graph.node:
        for i, name in enumerate(node.input):
            if name in renamed:
                node.input[i] = renamed[name]
    onnx.save(model, str(target))
    return any(node.op_type == "GridSample" for node in graph.node)


def short_error(error: BaseException | None) -> str:
    """The first meaningful line of an exception: torch.export messages run to dozens of lines."""
    if error is None:
        return "unknown error"
    plain = re.sub(r"\x1b\[[0-9;]*m", "", str(error))  # the exporter colors its messages with ANSI codes
    lines = [line.strip() for line in plain.splitlines() if line.strip()]
    text = lines[0] if lines else ""
    text = f"{type(error).__name__}: {text}" if text else type(error).__name__
    return text if len(text) <= 160 else text[:157] + "..."


def max_deviation(target: Path, wrapper, device, height: int, width: int) -> float | None:
    """Maximum deviation of ONNX Runtime (CPU) from PyTorch; inf if ONNX could not compute it; None if onnxruntime is missing."""
    try:
        import numpy as np
        import onnxruntime as ort
    except ImportError:
        print("  Verification skipped: onnxruntime is missing (pip install onnxruntime).")
        return None

    import torch

    # On Ampere and newer, convolutions run in TF32 by default; the reference must be true fp32.
    torch.backends.cudnn.allow_tf32 = False
    torch.backends.cuda.matmul.allow_tf32 = False

    sample = torch.rand(1, 3, height, width)
    with torch.no_grad():
        expected = wrapper(sample.to(device)).float().cpu().numpy()

    options = ort.SessionOptions()
    options.log_severity_level = 4  # errors will arrive as exceptions
    session = ort.InferenceSession(str(target), options, providers=["CPUExecutionProvider"])
    try:
        actual = session.run(None, {"input": sample.numpy()})[0]
    except Exception:  # noqa: BLE001 — any runtime error means the shape is wrong
        return float("inf")
    if actual.shape != expected.shape:
        return float("inf")
    return float(np.abs(actual - expected).max())


def report(height: int, width: int, deviation: float) -> None:
    verdict = "ok" if deviation < TOLERANCE else "mismatch"
    shown = "runtime error" if deviation == float("inf") else f"{deviation:.2e}"
    print(f"  Check {height}×{width}: max deviation from PyTorch {shown} — {verdict}")


try:
    import torch.nn as _nn

    class _IOWrapper(_nn.Module):
        """Keeps the input and output in fp32 even if the weights are fp16, so the app needs no knowledge of half."""

        def __init__(self, model, half: bool) -> None:
            super().__init__()
            self.model = model
            self.half = half

        def forward(self, x):
            if self.half:
                return self.model(x.half()).float()
            return self.model(x)

except ImportError:  # main() will report that torch is missing
    _IOWrapper = None  # type: ignore[assignment,misc]


def main() -> None:
    # When output is redirected, Windows defaults to cp1252, and characters outside it (such as →) crash print.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    args = parse_args()
    if _IOWrapper is None:
        fail("PyTorch is required: pip install -r tools/requirements.txt")
    try:
        import spandrel  # noqa: F401
    except ImportError:
        fail("spandrel is required: pip install -r tools/requirements.txt")

    for source in args.models:
        convert(source, args)


if __name__ == "__main__":
    main()
