# Models

Put `.onnx` models here. The build copies them to the `models` folder next to `krupno-cli.exe` and `Krupno.exe`.
They are not committed to git (see `.gitignore`).

Models are also looked up in `%LOCALAPPDATA%\Krupno\models`; the "Open models folder" button in the app window opens it.

To get ONNX from PyTorch weights: `python tools/convert_to_onnx.py <file.pth>` (see the root README for details).
