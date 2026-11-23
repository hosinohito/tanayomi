"""Run fixed, non-playing PCM and failure checks against an assembled package."""

from io import BytesIO
import json
import os
from pathlib import Path
import runpy
import struct
import subprocess
import sys
import tempfile


HEADER = struct.Struct("<4sIII")


def call(package, temp, input_bytes, timeout, script=None, *args):
    env = dict(os.environ)
    env.update(dict.fromkeys(("TEMP", "TMP", "TMPDIR", "TANAYOMI_AIVIS_TEMP", "HF_HOME", "XDG_CACHE_HOME", "NLTK_DATA"), temp))
    env.update(dict.fromkeys(("HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_HUB_DISABLE_TELEMETRY"), "1"))
    return subprocess.run(
        [str(package / "python/python.exe"), "-B", str(script or package / "worker.py"), *args],
        input=input_bytes, capture_output=True, timeout=timeout,
        creationflags=subprocess.CREATE_NO_WINDOW, cwd=package, env=env,
    )


def request(index, text=None):
    payload = text.encode("utf-8") if text else b""
    return HEADER.pack(b"AVQ1", int(text is not None), index, len(payload)) + payload


def verify(package):
    voices = json.loads((package / "voices.json").read_text(encoding="utf-8"))["voices"]
    with tempfile.TemporaryDirectory(prefix="aivis-check-") as temp:
        conversion = call(package, temp, b"", 30, Path(__file__).resolve(), "--pcm", str(package))
        if conversion.returncode != 0:
            raise ValueError(f"PCM conversion check failed: {conversion.stderr[:200]!r}")
        malformed = call(package, temp, HEADER.pack(b"AVQ1", 1, 0, 5000), 12)
        if malformed.returncode != 2 or malformed.stdout:
            raise ValueError("Oversized frame was not rejected")
        frames = b"".join(request(index) + request(index, "今日は何を読みますか。") + request(index, "Hello、日本語の読み上げです。")
                          for index in range(len(voices)))
        result = call(package, temp, frames, 180)
        if result.returncode != 0:
            diagnostic = result.stdout[-64:]
            raise ValueError(f"Worker exit {result.returncode}, response tail {diagnostic!r}, stderr {result.stderr[:120]!r}")
        position = 0
        for voice in voices:
            for preparation in (True, False, False):
                magic, status, rate, size = HEADER.unpack_from(result.stdout, position)
                position += HEADER.size
                data = result.stdout[position:position + size]
                position += size
                if magic != b"AVR1" or status != 0 or len(data) != size:
                    raise ValueError("Invalid worker response")
                if preparation:
                    if rate != 0 or size != 0:
                        raise ValueError("Invalid preparation acknowledgment")
                elif rate != 44100 or not 0 < size <= 2 * 45 * 44100 or size % 2 or not any(data):
                    raise ValueError("Invalid generated PCM")
                else:
                    print(f"{voice['id']} {rate} Hz {size // 2} samples")
        if position != len(result.stdout):
            raise ValueError("Unexpected response bytes")


def verify_pcm(package):
    import numpy as np

    worker = runpy.run_path(str(package / "worker.py"))
    encode_pcm = worker["encode_pcm"]
    boundaries = np.array([-2, -1, -0.9, -1 / 65536, -0.0, 0, 1 / 65536, 1 / 32768,
                           32767 / 32768, 1, 2], dtype=np.float32)
    maximum = np.random.default_rng(7).uniform(-1.25, 1.25, worker["MAX_SAMPLES"]).astype(np.float32)
    for wave in (boundaries, boundaries[::-2], maximum):
        original = wave.copy()
        expected = np.clip(np.floor(wave * 32768), -32768, 32767).astype("<i2").tobytes()
        view = encode_pcm(wave)
        pipe = BytesIO()
        if (len(view) != len(expected) or pipe.write(view) != len(expected)
                or pipe.getvalue() != expected or not np.array_equal(wave, original)):
            raise ValueError("PCM byte encoding mismatch")
    for invalid in (np.array([], dtype=np.float32), np.array([np.nan, np.inf], dtype=np.float32),
                    np.zeros((1, 1), dtype=np.float32)):
        try:
            encode_pcm(invalid)
        except ValueError:
            continue
        raise ValueError("Invalid wave was accepted")

    os.environ["OPEN_JTALK_DICT_DIR"] = str(package / "python/Lib/site-packages/pyopenjtalk/dictionary")
    sys.path.insert(0, str(package / "engine"))
    from voicevox_engine.tts_pipeline.style_bert_vits2_tts_engine import _trim_silence

    threshold = np.float32(0.0004)
    above = np.nextafter(threshold, np.float32(np.inf))
    trim_boundaries = np.array([np.nan, threshold, -above, np.nan, threshold, above, 0], dtype=np.float32)
    sparse = np.zeros_like(maximum)
    sparse[60000], sparse[-50000] = above, -above
    for wave in (trim_boundaries, trim_boundaries[::-2], trim_boundaries[:0], trim_boundaries[:2],
                 trim_boundaries[2:3], maximum, sparse):
        hits = np.where(np.abs(wave) > 0.0004)[0]
        expected = wave[hits[0]:hits[-1] + 1] if hits.size else np.array([], dtype=np.float32)
        actual = _trim_silence(wave)
        if (actual.dtype != expected.dtype or actual.shape != expected.shape or actual.tobytes() != expected.tobytes()
                or (expected.size and (actual.ctypes.data != expected.ctypes.data or actual.strides != expected.strides))):
            raise ValueError("Silence trim differs from the original slice")
    try:
        _trim_silence(np.zeros(1, dtype=np.float64))
    except ValueError:
        return
    raise ValueError("Silence trim accepted an unsupported dtype")


if __name__ == "__main__":
    try:
        if sys.argv[1] == "--pcm":
            verify_pcm(Path(sys.argv[2]).resolve(strict=True))
        else:
            verify(Path(sys.argv[1]).resolve(strict=True))
    except Exception as error:
        print(f"AIVIS-VERIFY: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)
