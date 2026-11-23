"""Private PCM worker for AivisSpeech Engine 1.2.0's Japanese ONNX pipeline.

The query defaults below follow voicevox_engine/app/routers/tts_pipeline.py
from AivisSpeech Engine (LGPL-3.0). The upstream inference source is bundled
separately with its licenses. This program does not start an HTTP server.
"""

import hashlib
import json
import os
from pathlib import Path
import re
import struct
import sys
from types import SimpleNamespace


REQUEST = struct.Struct("<4sIII")
RESPONSE = struct.Struct("<4sIII")
MAX_TEXT_BYTES = 4096
MAX_TEXT_CHARACTERS = 500
SAMPLE_RATE = 44100
MAX_SAMPLES = 45 * SAMPLE_RATE


def read_exact(stream, count):
    data = stream.read(count)
    if len(data) != count:
        raise EOFError()
    return data


def verify_file(root, relative, expected):
    path = root / relative
    resolved = path.resolve(strict=True)
    if resolved != path or not path.is_relative_to(root):
        raise ValueError("Unexpected asset path")
    with path.open("rb") as file:
        if hashlib.file_digest(file, "sha256").hexdigest() != expected:
            raise ValueError("Asset checksum mismatch")
    return path


def encode_pcm(wave):
    import numpy as np

    if wave.ndim != 1 or not 0 < wave.size <= MAX_SAMPLES or not np.isfinite(wave).all():
        raise ValueError("Invalid PCM")
    scaled = wave * 32768
    np.floor(scaled, out=scaled)
    np.clip(scaled, -32768, 32767, out=scaled)
    # The view keeps the little-endian sample array alive through the pipe write.
    return memoryview(scaled.astype("<i2")).cast("B")


def build_engine(package, bundle):
    # Native or third-party logs may include text. Suppress them before loading
    # any dependency, while retaining a separate binary protocol handle.
    from logging import disable, CRITICAL
    disable(CRITICAL)
    import aivmlib
    from style_bert_vits2.constants import Languages
    from style_bert_vits2.nlp import onnx_bert_models
    from voicevox_engine.model import AudioQuery
    from voicevox_engine.tts_pipeline.style_bert_vits2_tts_engine import (
        StyleBertVITS2TTSEngine,
    )

    class LocalModels:
        def __init__(self):
            self.model = None
            self.speaker = None

        def select(self, entry):
            if self.model and str(self.model.manifest.uuid) == entry["model"]:
                return
            filename = f'{entry["model"]}.aivmx'
            path = verify_file(package, f"models/{filename}", entry["sha256"])
            with path.open("rb") as file:
                manifest = aivmlib.read_aivmx_metadata(file).manifest
            if str(manifest.uuid) != entry["model"] or manifest.version != entry["version"]:
                raise ValueError("Model identity mismatch")
            speaker = next(speaker for speaker in manifest.speakers if str(speaker.uuid) == entry["speaker"])
            self.model = SimpleNamespace(file_path=path, manifest=manifest)
            self.speaker = speaker

        def get_aivm_info(self, model_uuid):
            if not self.model or str(self.model.manifest.uuid) != model_uuid:
                raise ValueError("Unknown model")
            return self.model

        def get_aivm_manifest_from_style_id(self, style_id):
            style = next(style for style in self.speaker.styles if style.local_id == style_id)
            return self.model.manifest, self.speaker, style

        def update_model_load_state(self, model_uuid, is_loaded):
            self.get_aivm_info(model_uuid)

    class LocalEngine(StyleBertVITS2TTSEngine):
        def _load_bert_model_and_tokenizer(self):
            directory = str(package / "bert")
            onnx_bert_models.load_model(Languages.JP, directory, self.onnx_providers)
            onnx_bert_models.load_tokenizer(Languages.JP, directory)

        def _reset_bert_model_cache(self):
            raise OSError("Bundled BERT could not be loaded")

    for filename, checksum in bundle["bert"].items():
        verify_file(package, f"bert/{filename}", checksum)
    models = LocalModels()
    # Upstream checks a historical cache filename even when BERT is local.
    engine = LocalEngine(models, bert_model_cache_dir=Path(os.environ["TANAYOMI_AIVIS_TEMP"]) / "bert-cache")

    def process(entry, style, text):
        for model_uuid in list(engine.tts_models):
            if model_uuid != entry["model"]:
                engine.unload_model(model_uuid)
        models.select(entry)
        models.get_aivm_manifest_from_style_id(style)
        if text is None:
            engine.initialize_synthesis(style, True)
            return b""
        # Same defaults as the pinned upstream /audio_query endpoint.
        query = AudioQuery(
            accent_phrases=engine.create_accent_phrases(text, style, True),
            speedScale=1.0, intonationScale=1.0, tempoDynamicsScale=1.0,
            pitchScale=0.0, volumeScale=1.0, prePhonemeLength=0.1,
            postPhonemeLength=0.1, pauseLength=None, pauseLengthScale=1,
            outputSamplingRate=SAMPLE_RATE, outputStereo=False, kana=text,
        )
        wave = engine.synthesize_wave(query, style, enable_interrogative_upspeak=True)
        return encode_pcm(wave)

    return process


def main():
    package = Path(__file__).resolve().parent
    os.environ["OPEN_JTALK_DICT_DIR"] = str(package / "python/Lib/site-packages/pyopenjtalk/dictionary")
    source = package / "engine"
    # Use the bundled, frozen dependency and source paths even if Python is
    # also installed on the user's machine.
    sys.path.insert(0, str(source))
    bundle_path = package / "voices.json"
    if bundle_path.stat().st_size > 65536:
        raise ValueError("Invalid voice manifest")
    bundle = json.loads(bundle_path.read_text(encoding="utf-8"))
    entries = bundle["voices"]
    if bundle["version"] != 1 or not 1 <= len(entries) <= 8:
        raise ValueError("Invalid voice manifest")
    def reject_network(event, _):
        if event in {"socket.connect", "socket.bind", "socket.getaddrinfo"}:
            raise OSError("The bundled Aivis worker is offline")

    sys.addaudithook(reject_network)
    incoming = sys.stdin.buffer
    with os.fdopen(os.dup(sys.stdout.fileno()), "wb") as outgoing, open(os.devnull, "wb") as sink:
        os.dup2(sink.fileno(), sys.stdout.fileno())
        os.dup2(sink.fileno(), sys.stderr.fileno())
        process = None
        while header := incoming.read(REQUEST.size):
            if len(header) != REQUEST.size:
                return 2
            magic, command, voice, length = REQUEST.unpack(header)
            if magic != b"AVQ1" or voice >= len(entries) or command not in (0, 1) or length > MAX_TEXT_BYTES or (command == 0) != (length == 0):
                return 2
            try:
                text = read_exact(incoming, length).decode("utf-8") if command else None
            except (EOFError, UnicodeError):
                return 2
            if text is not None and (not text.strip() or len(text) > MAX_TEXT_CHARACTERS or "\0" in text):
                return 2
            try:
                if process is None:
                    process = build_engine(package, bundle)
                entry = entries[voice]
                audio = process(entry, entry["style"], text)
            except Exception as error:
                # Report a bounded exception category, never its message or input.
                category = type(error).__name__
                if not re.fullmatch(r"[A-Za-z_][A-Za-z_0-9]{0,63}", category):
                    category = "Exception"
                diagnostic = category.encode("ascii")
                outgoing.write(RESPONSE.pack(b"AVR1", 1, 0, len(diagnostic)))
                outgoing.write(diagnostic)
                outgoing.flush()
                return 1
            outgoing.write(RESPONSE.pack(b"AVR1", 0, SAMPLE_RATE if command else 0, len(audio)))
            outgoing.write(audio)
            outgoing.flush()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"AIVIS-WORKER-START: {type(error).__name__}", file=sys.stderr)
        sys.exit(1)
