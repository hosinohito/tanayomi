"""Generate the opt-in OpenVoice numeric fixture from pinned local inputs.

Development-only PyTorch is used as an independent STFT/model oracle. No
speaker recording, network, playback or WAV is involved. Refuse old outputs.
"""
from __future__ import annotations

import argparse
import array
import base64
import hashlib
import json
import math
import os
from pathlib import Path
import struct
import sys


def packed(values, kind):
    result = array.array(kind, values)
    if sys.byteorder != "little":
        result.byteswap()
    return base64.b64encode(result.tobytes()).decode("ascii")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = args.root.resolve(strict=True)
    temporary = root / "tmp"
    for name in ("TEMP", "TMP", "TMPDIR", "TORCH_HOME", "HF_HOME", "XDG_CACHE_HOME"):
        os.environ[name] = str(temporary)
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["PYTHONDONTWRITEBYTECODE"] = "1"
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src/Tanayomi.Desktop/Neural"))
    from openvoice_worker import Converter, reject_links
    reject_links(root)
    reject_links(args.output)
    if args.output.exists() or not args.output.parent.is_dir():
        raise ValueError("Oracle output must be new in an existing directory")
    converter = Converter(root / "upstream", 2)
    converter.load(root / "models/checkpoint.pth", root / "models/config.json")
    torch, model = converter.torch, converter.model
    if torch.__version__ != "2.9.1+cpu":
        raise ValueError("Oracle requires pinned development PyTorch 2.9.1+cpu")
    # These exact signed PCM values are stored; other runtimes need not repeat
    # trigonometry or rely on platform libm rounding to recreate the input.
    source = [round(8000 * math.sin(2 * math.pi * 223 * i / 22050)
                    + 2000 * math.cos(2 * math.pi * 697 * i / 22050)) for i in range(8192)]
    reference = [round(9000 * math.sin(2 * math.pi * 331 * i / 22050)
                       + 1700 * math.cos(2 * math.pi * 911 * i / 22050)) for i in range(8192)]
    with torch.inference_mode():
        spec = converter.spectrogram(torch.tensor(source, dtype=torch.float32) / 32768)
        ref_spec = converter.spectrogram(torch.tensor(reference, dtype=torch.float32) / 32768)
        source_embedding = model.ref_enc(spec.transpose(1, 2)).unsqueeze(-1)
        target_embedding = model.ref_enc(ref_spec.transpose(1, 2)).unsqueeze(-1)
        state = 0x12345678
        def uniform():
            nonlocal state
            state ^= (state << 13) & 0xffffffff
            state ^= state >> 17
            state ^= (state << 5) & 0xffffffff
            state &= 0xffffffff
            return (state + 0.5) / 4294967296.0
        noise_values = []
        for _ in range(192 * spec.shape[-1] // 2):
            radius = math.sqrt(-2 * math.log(uniform()))
            angle = 2 * math.pi * uniform()
            noise_values.extend([radius * math.cos(angle), radius * math.sin(angle)])
        noise = torch.tensor(noise_values, dtype=torch.float32).reshape(1, 192, spec.shape[-1])
        mask = torch.ones_like(spec[:, :1, :])
        hidden = model.enc_q.pre(spec) * mask
        hidden = model.enc_q.enc(hidden, mask, g=torch.zeros_like(source_embedding))
        mean, logs = torch.split(model.enc_q.proj(hidden) * mask, 192, dim=1)
        latent = (mean + noise * 0.3 * torch.exp(logs)) * mask
        latent = model.flow(latent, mask, g=source_embedding)
        latent = model.flow(latent, mask, g=target_embedding, reverse=True)
        audio = model.dec(latent * mask, g=torch.zeros_like(target_embedding)).flatten()
        peak = float(audio.abs().max())
        scale = struct.unpack("<f", struct.pack("<f", 0.92 / peak))[0] if peak > 0.92 else 1.0
        # Match C# float operations followed by Math.Round(ToEven).
        float32 = lambda value: struct.unpack("<f", struct.pack("<f", value))[0]
        pcm = [round(float32(float32(value * scale) * 32767)) for value in audio.tolist()]
    fixture = dict(schema=1, sampleRate=22050, frames=int(spec.shape[-1]),
                   sourcePcm=packed(source, "h"), referencePcm=packed(reference, "h"),
                   sourceSpectrum=packed(spec.flatten().tolist(), "f"),
                   referenceSpectrum=packed(ref_spec.flatten().tolist(), "f"),
                   sourceEmbedding=packed(source_embedding.flatten().tolist(), "f"),
                   targetEmbedding=packed(target_embedding.flatten().tolist(), "f"),
                   noise=packed(noise.flatten().tolist(), "f"),
                   modelPcm=packed(audio.tolist(), "f"), finalPcm=packed(pcm, "h"),
                   oracle="PyTorch 2.9.1+cpu; pinned OpenVoice V2; artificial PCM only")
    payload = (json.dumps(fixture, separators=(",", ":")) + "\n").encode()
    with args.output.open("xb") as output:
        output.write(payload)
    print(json.dumps(dict(bytes=len(payload), sha256=hashlib.sha256(payload).hexdigest(), frames=fixture["frames"])))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(json.dumps(dict(errorType=type(error).__name__, message=str(error))))
        sys.exit(1)
