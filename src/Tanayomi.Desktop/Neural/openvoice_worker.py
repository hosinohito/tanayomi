"""Bounded CPU OpenVoice converter worker; JSON lines in/out, no audio playback.

This Tanayomi wrapper uses the MIT OpenVoice inference model at commit
74a1d147b17a8c3092dd5430504bd83ef6c7eb23. It deliberately does not import
upstream api.py/text/se_extractor, so their text, VAD, ASR and codec dependency
trees are absent. STFT matches upstream mel_processing.spectrogram_torch.
The parent owns cancellation, process-tree termination and the wall deadline.
"""
from __future__ import annotations

import argparse
import array
import contextlib
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import stat
import sys
import time
import wave

CHECKPOINT_SHA256 = "9652c27e92b6b2a91632590ac9962ef7ae2b712e5c5b7f4c34ec55ee2b37ab9e"
CONFIG_SHA256 = "9dfff60350b8c63f2c664efd92a61b2516efb22671466960f0e5dfebd881fa47"
MAX_REQUEST_BYTES = 131072
MAX_REFERENCE_COUNT = 149
MODEL_RATE = 22050


def peak_working_set_bytes() -> int | None:
    if os.name != "nt":
        return None
    class Counters(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_ulong), ("faults", ctypes.c_ulong),
                    *[(name, ctypes.c_size_t) for name in ("peak", "current", "pagedPeak", "paged", "nonpagedPeak", "nonpaged", "pagefile", "pagefilePeak")]]
    counters = Counters()
    counters.cb = ctypes.sizeof(counters)
    get_info = ctypes.windll.psapi.GetProcessMemoryInfo
    get_info.argtypes = (ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_ulong)
    get_info.restype = ctypes.c_int
    if get_info(ctypes.c_void_p(-1), ctypes.byref(counters), counters.cb):
        return counters.peak
    return None


class WorkerError(Exception):
    def __init__(self, error_id: str):
        self.error_id = error_id


def reject_links(path: Path) -> None:
    for part in (path, *path.parents):
        try:
            attributes = part.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(attributes.st_mode) or getattr(attributes, "st_file_attributes", 0) & 0x400:
            raise WorkerError("NEURAL_PATH_LINK")


def contained(root: Path, value: object, *, existing: bool = True) -> Path:
    if not isinstance(value, str) or not value or "\0" in value or len(value) > 32768:
        raise WorkerError("NEURAL_PATH_INVALID")
    path = Path(value)
    if not path.is_absolute():
        path = root / path
    # Reject alternate data streams and relative traversal before normalization.
    if any(part in ("..", ".") or ":" in part for part in path.parts[1:]):
        raise WorkerError("NEURAL_PATH_INVALID")
    path = Path(os.path.abspath(path))
    try:
        path.relative_to(root)
    except ValueError:
        raise WorkerError("NEURAL_PATH_OUTSIDE") from None
    if path == root:
        raise WorkerError("NEURAL_PATH_INVALID")
    reject_links(path)
    if existing and (not path.is_file() or path.stat().st_size == 0):
        raise WorkerError("NEURAL_FILE_MISSING")
    return path


def verify_file(path: Path, expected: str, maximum: int) -> None:
    if not 0 < path.stat().st_size <= maximum:
        raise WorkerError("NEURAL_MODEL_SIZE")
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest() != expected:
        raise WorkerError("NEURAL_MODEL_HASH")


def read_pcm16(path: Path, maximum_seconds: float, torch):
    if path.stat().st_size > 40 * 1024 * 1024:
        raise WorkerError("NEURAL_WAV_SIZE")
    with wave.open(str(path), "rb") as source:
        rate, channels, width, count = source.getframerate(), source.getnchannels(), source.getsampwidth(), source.getnframes()
        if not 8000 <= rate <= 192000 or channels not in (1, 2) or width != 2 or not 0 < count <= rate * maximum_seconds:
            raise WorkerError("NEURAL_WAV_FORMAT")
        raw = source.readframes(count)
    if len(raw) != count * channels * 2:
        raise WorkerError("NEURAL_WAV_TRUNCATED")
    pcm = array.array("h")
    pcm.frombytes(raw)
    if sys.byteorder != "little":
        pcm.byteswap()
    audio = torch.tensor(pcm, dtype=torch.float32).reshape(-1, channels).mean(dim=1) / 32768.0
    if not bool(torch.any(audio)):
        raise WorkerError("NEURAL_WAV_SILENT")
    return audio, rate


def resample(audio, source_rate: int, torch):
    """Bandlimited FFT resampling, including the even-length Nyquist bin.

    Input is a bounded complete utterance, rather than a real-time stream.
    Its periodic boundary assumption is appropriate only with quiet endpoints.
    """
    if source_rate == MODEL_RATE:
        return audio
    old = len(audio)
    new = max(1, round(old * MODEL_RATE / source_rate))
    spectrum = torch.fft.rfft(audio)
    output = torch.zeros(new // 2 + 1, dtype=spectrum.dtype)
    common = min(old, new)
    output[:common // 2 + 1] = spectrum[:common // 2 + 1]
    if common % 2 == 0:
        if new < old:
            output[common // 2] *= 2
        elif old < new:
            output[common // 2] *= 0.5
    return (torch.fft.irfft(output, n=new) * (new / old)).to(torch.float32)


class Converter:
    def __init__(self, code_directory: Path, threads: int):
        # Only the trusted launcher chooses code_directory, never a JSON request.
        reject_links(code_directory)
        for name in ("models.py", "modules.py", "commons.py", "attentions.py", "transforms.py", "__init__.py"):
            file = code_directory / "openvoice" / name
            reject_links(file)
            if not file.is_file() or file.stat().st_size > 128 * 1024:
                raise WorkerError("NEURAL_CODE_MISSING")
        sys.path.insert(0, str(code_directory))
        import torch
        from openvoice.models import SynthesizerTrn
        torch.set_num_threads(threads)
        torch.set_num_interop_threads(1)
        self.torch, self.model_type = torch, SynthesizerTrn
        self.model = None
        self.target_cache = None

    def spectrogram(self, audio):
        torch = self.torch
        if len(audio) < 2048:
            raise WorkerError("NEURAL_REFERENCE_SHORT")
        data = audio.unsqueeze(0)
        data = torch.nn.functional.pad(data.unsqueeze(1), (384, 384), mode="reflect").squeeze(1)
        spectrum = torch.stft(data, 1024, hop_length=256, win_length=1024,
                              window=torch.hann_window(1024), center=False,
                              pad_mode="reflect", normalized=False, onesided=True,
                              return_complex=True)
        return (spectrum.abs().square() + 1e-6).sqrt()

    def load(self, checkpoint: Path, config: Path):
        if self.model is not None:
            return
        verify_file(checkpoint, CHECKPOINT_SHA256, 140 * 1024 * 1024)
        verify_file(config, CONFIG_SHA256, 32768)
        settings = json.loads(config.read_text(encoding="utf-8"))
        self.model = self.model_type(0, 513, n_speakers=0, **settings["model"])
        state = self.torch.load(checkpoint, map_location="cpu", weights_only=True)
        if not isinstance(state, dict) or not isinstance(state.get("model"), dict):
            raise WorkerError("NEURAL_MODEL_FORMAT")
        self.model.load_state_dict(state["model"], strict=True)
        self.model.eval()

    def convert(self, root: Path, request: dict):
        if request.get("op") != "convert":
            raise WorkerError("NEURAL_OPERATION")
        maximum = request.get("maxSeconds", 45)
        if not isinstance(maximum, (int, float)) or isinstance(maximum, bool) or not math.isfinite(maximum) or not 0 < maximum <= 45:
            raise WorkerError("NEURAL_LIMIT")
        references = request.get("referenceWavs")
        if not isinstance(references, list) or not 1 <= len(references) <= MAX_REFERENCE_COUNT:
            raise WorkerError("NEURAL_REFERENCE_COUNT")
        paths = [contained(root, item) for item in references]
        if len(set(paths)) != len(paths):
            raise WorkerError("NEURAL_REFERENCE_DUPLICATE")
        if any(path.stat().st_size > 40 * 1024 * 1024 for path in paths) or sum(path.stat().st_size for path in paths) > 140 * 1024 * 1024:
            raise WorkerError("NEURAL_REFERENCE_LIMIT")
        source = contained(root, request.get("sourceWav"))
        checkpoint = contained(root, request.get("checkpoint"))
        config = contained(root, request.get("config"))
        output = contained(root, request.get("outputWav"), existing=False)
        if output.exists() or not output.parent.is_dir():
            raise WorkerError("NEURAL_OUTPUT_EXISTS")
        self.load(checkpoint, config)
        torch = self.torch
        started = time.monotonic()
        key = tuple((str(path), hashlib.sha256(path.read_bytes()).digest()) for path in paths)
        with torch.inference_mode():
            prepared = 0
            if self.target_cache is None or self.target_cache[0] != key:
                embeddings = []
                seconds = 0.0
                for path in paths:
                    audio, rate = read_pcm16(path, 30, torch)
                    seconds += len(audio) / rate
                    if seconds > 180:
                        raise WorkerError("NEURAL_REFERENCE_LIMIT")
                    audio = resample(audio, rate, torch)
                    embeddings.append(self.model.ref_enc(self.spectrogram(audio).transpose(1, 2)).unsqueeze(-1))
                target = torch.stack(embeddings).mean(0)
                self.target_cache = (key, target)
                prepared = len(paths)
            target = self.target_cache[1]
            audio, rate = read_pcm16(source, maximum, torch)
            audio = resample(audio, rate, torch)
            spectrum = self.spectrogram(audio)
            source_embedding = self.model.ref_enc(spectrum.transpose(1, 2)).unsqueeze(-1)
            # Fixed seed makes repeated utterances reproducible within this build.
            torch.manual_seed(0)
            converted = self.model.voice_conversion(spectrum, torch.tensor([spectrum.size(-1)]),
                                                    source_embedding, target, tau=0.3)[0][0, 0]
        if not 1 <= len(converted) <= MODEL_RATE * maximum or not bool(torch.isfinite(converted).all()):
            raise WorkerError("NEURAL_OUTPUT_INVALID")
        peak = float(torch.max(torch.abs(converted)))
        if not 0.00001 < peak <= 4.0:
            raise WorkerError("NEURAL_OUTPUT_LEVEL")
        if peak > 0.92:
            converted = converted * (0.92 / peak)
        pcm = array.array("h", torch.round(converted * 32767).to(torch.int16).tolist())
        if sys.byteorder != "little":
            pcm.byteswap()
        created = False
        try:
            # Exclusive creation protects old output and returns only complete WAVs.
            with output.open("xb") as destination:
                created = True
                with wave.open(destination, "wb") as wav:
                    wav.setnchannels(1)
                    wav.setsampwidth(2)
                    wav.setframerate(MODEL_RATE)
                    wav.writeframes(pcm.tobytes())
            created = False
        finally:
            if created:
                output.unlink(missing_ok=True)
        return {"ok": True, "sampleRate": MODEL_RATE, "samples": len(pcm),
                "elapsedMs": round((time.monotonic() - started) * 1000, 3),
                "preparedReferences": prepared, "peakWorkingSetBytes": peak_working_set_bytes()}


def main() -> int:
    if os.name == "nt":
        ctypes.windll.kernel32.SetErrorMode(0x0001 | 0x0002)
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", required=True)
    parser.add_argument("--code-directory", required=True)
    parser.add_argument("--threads", type=int, default=2, choices=range(1, 5))
    args = parser.parse_args()
    root = Path(os.path.abspath(args.data_root))
    reject_links(root)
    if not root.is_dir():
        raise WorkerError("NEURAL_ROOT_MISSING")
    # No global cache/temp fallback is allowed, including lazy native imports.
    temporary = root / "tmp"
    temporary.mkdir(exist_ok=True)
    reject_links(temporary)
    for name in ("TEMP", "TMP", "TMPDIR", "TORCH_HOME", "HF_HOME", "XDG_CACHE_HOME"):
        os.environ[name] = str(temporary)
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["PYTHONDONTWRITEBYTECODE"] = "1"
    sys.dont_write_bytecode = True
    converter = None
    while True:
        line = sys.stdin.buffer.readline(MAX_REQUEST_BYTES + 1)
        if not line:
            return 0
        if len(line) > MAX_REQUEST_BYTES:
            raise WorkerError("NEURAL_REQUEST_LIMIT")
        try:
            request = json.loads(line)
            if not isinstance(request, dict):
                raise WorkerError("NEURAL_REQUEST_FORMAT")
            with contextlib.redirect_stdout(sys.stderr):
                converter = converter or Converter(Path(os.path.abspath(args.code_directory)), args.threads)
                response = converter.convert(root, request)
        except Exception as error:
            response = {"ok": False, "errorId": getattr(error, "error_id", "NEURAL_INFERENCE_FAILED"),
                        "exceptionType": type(error).__name__}
            print(json.dumps(response, separators=(",", ":")), flush=True)
            return 1
        print(json.dumps(response, separators=(",", ":")), flush=True)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(json.dumps({"ok": False, "errorId": getattr(error, "error_id", "NEURAL_WORKER_FAILED"),
                          "exceptionType": type(error).__name__}, separators=(",", ":")), flush=True)
        sys.exit(1)
