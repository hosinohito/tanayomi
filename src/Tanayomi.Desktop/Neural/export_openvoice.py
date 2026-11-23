"""Local, development-only conversion of pinned OpenVoice V2 into ONNX.

PyTorch/NumPy/export tools are not product runtime dependencies. The two
graphs accept a magnitude STFT; the parent supplies bounded PCM/STFT and noise.
No user audio or weights are uploaded. Existing output is never overwritten.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import urllib.request

from openvoice_worker import Converter, WorkerError, reject_links


def prepare_inputs(root: Path):
    """Explicit development GETs only; preserve existing verified inputs."""
    code_revision = "74a1d147b17a8c3092dd5430504bd83ef6c7eb23"
    model_revision = "f36e7edfe1684461a8343844af60babc2efbb727"
    code_url = f"https://raw.githubusercontent.com/myshell-ai/OpenVoice/{code_revision}"
    model_url = f"https://huggingface.co/myshell-ai/OpenVoiceV2/resolve/{model_revision}"
    source = [
        ("models.py", 16801, "54F9E8284A0E9A89C75CB3163D2694CF34D90A5D05CFBCBF12CCD4F0B1BA2DC8"),
        ("modules.py", 19010, "B2376049141934EB63BB4D905920C486A286BEC65F0735B78BA52B2FB54564D0"),
        ("commons.py", 4956, "046943D265635C3D9C99195562E703564A26300981A7DB5B87D4CD3FD180AE0D"),
        ("attentions.py", 16360, "6A392057838E6051AFC8BEED43C2D1279D75DE8C8B600552E7232AD930B356C3"),
        ("__init__.py", 0, "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"),
    ]
    entries = [(root / "upstream/openvoice" / name, f"{code_url}/openvoice/{name}", size, sha, None)
               for name, size, sha in source]
    entries.extend([
        (root / "upstream/LICENSE", f"{code_url}/LICENSE", 1057,
         "BB3541F421E6273D3A3E3DD5BDBA2BD3B79A3C2EADCA65CDE9C62C2467903AC0", None),
        (root / "models/config.json", f"{model_url}/converter/config.json", 838,
         "9DFFF60350B8C63F2C664EFD92A61B2516EFB22671466960F0E5DFEBD881FA47", None),
        (root / "models/checkpoint.pth", f"{model_url}/converter/checkpoint.pth", 131320490,
         "9652C27E92B6B2A91632590AC9962EF7AE2B712E5C5B7F4C34EC55EE2B37AB9E", None),
        (root / "models/README.md", f"{model_url}/README.md", 5371,
         "9F7D4211260D6105D0106705AE250C947BF3FB28E8E40A196DE36A0E70D49394", None),
    ])
    def scalar_math(data):
        prefix = ("# Modified for Tanayomi: replace optional NumPy scalar math with stdlib math; "
                  f"upstream {code_revision}, MIT.\n")
        return (prefix + data.decode().replace("import numpy as np", "import math")
                .replace("np.log", "math.log").replace("np.exp", "math.exp")).encode()
    entries.append((root / "upstream/openvoice/transforms.py", f"{code_url}/openvoice/transforms.py", 7384,
                    "CAA95F7A0A3004BEBCCA8370BBAEE2A65667EDF43A1014C7CAB6450C5645D253", scalar_math))
    downloaded = 0
    for destination, url, expected_size, expected_sha, modify in entries:
        reject_links(destination)
        if destination.exists():
            if not destination.is_file() or destination.stat().st_size != expected_size or \
                    hashlib.sha256(destination.read_bytes()).hexdigest().upper() != expected_sha:
                raise WorkerError("NEURAL_PREPARE_EXISTING_MISMATCH")
            continue
        destination.parent.mkdir(parents=True, exist_ok=True)
        reject_links(destination.parent)
        # This opt-in helper never opens a user-supplied URL or sends audio.
        original_size = 7253 if modify else expected_size
        request = urllib.request.Request(url, headers={"User-Agent": "Tanayomi-pinned-model-preparation"})
        started = time.monotonic()
        payload = bytearray()
        with urllib.request.urlopen(request, timeout=30) as response:
            while block := response.read(65536):
                payload.extend(block)
                if len(payload) > original_size or time.monotonic() - started > 180:
                    raise WorkerError("NEURAL_PREPARE_DOWNLOAD_LIMIT")
        if len(payload) != original_size:
            raise WorkerError("NEURAL_PREPARE_DOWNLOAD_SIZE")
        if modify:
            if hashlib.sha256(payload).hexdigest().upper() != "E6798989A0D75CBEC45E7E24E3697E1C4886B3664C6A80A452645EF1D2A93F9C":
                raise WorkerError("NEURAL_PREPARE_ORIGINAL_HASH")
            payload = modify(payload)
        if len(payload) != expected_size or hashlib.sha256(payload).hexdigest().upper() != expected_sha:
            raise WorkerError("NEURAL_PREPARE_DOWNLOAD_HASH")
        created = False
        try:
            with destination.open("xb") as output:
                created = True
                output.write(payload)
            created = False
        finally:
            if created:
                destination.unlink(missing_ok=True)
        downloaded += 1
    print(json.dumps({"ok": True, "verifiedInputs": len(entries), "downloadedInputs": downloaded}))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True)
    parser.add_argument("--prepare-inputs", action="store_true",
                        help="Verify pinned development inputs; download only missing inputs, then exit")
    args = parser.parse_args()
    root = Path(os.path.abspath(args.root))
    reject_links(root)
    repository = Path(__file__).resolve().parents[3]
    if not root.is_relative_to(repository / ".local"):
        raise WorkerError("NEURAL_DEVELOPMENT_ROOT")
    if args.prepare_inputs:
        root.mkdir(parents=True, exist_ok=True)
    temporary = root / "tmp"
    temporary.mkdir(exist_ok=True)
    for name in ("TEMP", "TMP", "TMPDIR", "TORCH_HOME", "HF_HOME", "XDG_CACHE_HOME"):
        os.environ[name] = str(temporary)
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["PYTHONDONTWRITEBYTECODE"] = "1"
    sys.dont_write_bytecode = True
    if args.prepare_inputs:
        return prepare_inputs(root)
    import numpy as np
    import onnx
    import onnxruntime as ort
    import torch

    publish = root / "onnx"
    if publish.exists():
        raise WorkerError("NEURAL_EXPORT_EXISTS")
    with tempfile.TemporaryDirectory(prefix="onnx-export-", dir=temporary) as scratch:
        return export_models(root, Path(scratch), publish, torch, np, onnx, ort)


def export_models(root, output, publish, torch, np, onnx, ort):
    converter = Converter(root / "upstream", 2)
    converter.load(root / "models/checkpoint.pth", root / "models/config.json")
    model = converter.model

    class Reference(torch.nn.Module):
        def __init__(self):
            super().__init__()
            self.encoder = model.ref_enc

        def forward(self, spectrum):
            return self.encoder(spectrum.transpose(1, 2)).unsqueeze(-1)

    class Conversion(torch.nn.Module):
        def __init__(self):
            super().__init__()
            self.encoder, self.flow, self.decoder = model.enc_q, model.flow, model.dec

        def forward(self, spectrum, source_embedding, target_embedding, noise):
            mask = torch.ones_like(spectrum[:, :1, :])
            hidden = self.encoder.pre(spectrum) * mask
            hidden = self.encoder.enc(hidden, mask, g=torch.zeros_like(source_embedding))
            mean, logs = torch.split(self.encoder.proj(hidden) * mask, 192, dim=1)
            latent = (mean + noise * 0.3 * torch.exp(logs)) * mask
            latent = self.flow(latent, mask, g=source_embedding)
            latent = self.flow(latent, mask, g=target_embedding, reverse=True)
            return self.decoder(latent * mask, g=torch.zeros_like(target_embedding))

    reference = Reference().eval()
    conversion = Conversion().eval()
    torch.manual_seed(0)
    spectrum = torch.rand(1, 513, 128)
    source_embedding, target_embedding = torch.rand(1, 256, 1), torch.rand(1, 256, 1)
    noise = torch.randn(1, 192, 128)
    started = time.monotonic()
    with torch.inference_mode():
        torch.onnx.export(reference, (spectrum,), output / "reference.onnx", input_names=["spectrum"],
                          output_names=["embedding"], dynamic_axes={"spectrum": {2: "frames"}},
                          opset_version=17, dynamo=False)
        torch.onnx.export(conversion, (spectrum, source_embedding, target_embedding, noise), output / "converter.onnx",
                          input_names=["spectrum", "source_embedding", "target_embedding", "noise"],
                          output_names=["pcm"], dynamic_axes={"spectrum": {2: "frames"}, "noise": {2: "frames"}, "pcm": {2: "samples"}},
                          opset_version=17, dynamo=False)
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 1
    sessions = [ort.InferenceSession(str(output / name), sess_options=options, providers=["CPUExecutionProvider"])
                for name in ("reference.onnx", "converter.onnx")]
    checks = []
    with torch.inference_mode():
        for frames in (128, 249):
            spectrum = torch.rand(1, 513, frames)
            noise = torch.randn(1, 192, frames)
            expected_reference = reference(spectrum).numpy()
            actual_reference = sessions[0].run(None, {"spectrum": spectrum.numpy()})[0]
            expected_pcm = conversion(spectrum, source_embedding, target_embedding, noise).numpy()
            actual_pcm = sessions[1].run(None, {"spectrum": spectrum.numpy(), "source_embedding": source_embedding.numpy(),
                                               "target_embedding": target_embedding.numpy(), "noise": noise.numpy()})[0]
            reference_difference = float(np.max(np.abs(expected_reference - actual_reference)))
            pcm_difference = float(np.max(np.abs(expected_pcm - actual_pcm)))
            if reference_difference > 0.0001 or pcm_difference > 0.0001:
                raise WorkerError("NEURAL_EXPORT_MISMATCH")
            checks.append(dict(frames=frames, referenceMaxAbsoluteError=reference_difference,
                               pcmMaxAbsoluteError=pcm_difference, samples=actual_pcm.shape[-1]))
    files = []
    for name in ("reference.onnx", "converter.onnx"):
        path = output / name
        onnx.checker.check_model(str(path))
        files.append(dict(name=name, bytes=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    manifest = dict(sourceCommit="74a1d147b17a8c3092dd5430504bd83ef6c7eb23",
                    modelCommit="f36e7edfe1684461a8343844af60babc2efbb727", opset=17,
                    modifications="Separate reference encoder and converter. Supply normal noise explicitly; magnitude STFT is external.",
                    files=files, checks=checks, seconds=round(time.monotonic() - started, 3),
                    torchVersion=torch.__version__, onnxVersion=onnx.__version__, onnxRuntimeVersion=ort.__version__)
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    (output / "OpenVoice-LICENSE.txt").write_bytes((root / "upstream/LICENSE").read_bytes())
    (output / "OpenVoice-NOTICE.txt").write_text(
        "OpenVoice V2, Copyright 2024 MyShell.ai, MIT License.\n"
        "Code: https://github.com/myshell-ai/OpenVoice/tree/74a1d147b17a8c3092dd5430504bd83ef6c7eb23\n"
        "Model: https://huggingface.co/myshell-ai/OpenVoiceV2/tree/f36e7edfe1684461a8343844af60babc2efbb727\n"
        "Modified by Tanayomi: exported reference encoder and converter separately to ONNX opset 17; "
        "magnitude STFT is external and latent normal noise is an explicit input. "
        "No speaker reference recording is embedded into these generic converter graphs.\n",
        encoding="utf-8")
    # Publish only after graph validation and both parity checks completed.
    if publish.exists():
        raise WorkerError("NEURAL_EXPORT_EXISTS")
    output.rename(publish)
    print(json.dumps(manifest, separators=(",", ":")), flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(json.dumps(dict(ok=False, errorId=getattr(error, "error_id", "NEURAL_EXPORT_FAILED"),
                              exceptionType=type(error).__name__)), flush=True)
        sys.exit(1)
