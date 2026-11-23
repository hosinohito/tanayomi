"""Read or replace only the manifest metadata field of a fixed AIVMX ModelProto."""

import hashlib
import io
import json
from pathlib import Path
import shutil
import sys


MANIFEST_FIELD = 14
MANIFEST_KEY = b"aivm_manifest"
MAX_METADATA = 8 * 1024 * 1024
# Owned transparent PNG used in place of optional uploader artwork.
PLACEHOLDER = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNgYAAAAAMAASsJTYQAAAAASUVORK5CYII="


def varint(stream):
    result = 0
    for shift in range(0, 70, 7):
        byte = stream.read(1)
        if not byte:
            raise ValueError("Truncated ONNX varint")
        result |= (byte[0] & 127) << shift
        if byte[0] < 128:
            return result
    raise ValueError("Invalid ONNX varint")


def encode(value):
    result = bytearray()
    while value >= 128:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return result


def fields(stream, end):
    while stream.tell() < end:
        start = stream.tell()
        key = varint(stream)
        kind = key & 7
        if kind == 0:
            varint(stream)
            offset = length = 0
        else:
            length = varint(stream) if kind == 2 else {1: 8, 5: 4}[kind]
            offset = stream.tell()
            stream.seek(length, 1)
        next_field = stream.tell()
        if next_field > end:
            raise ValueError("Truncated ONNX field")
        yield key >> 3, start, offset, length, next_field
        stream.seek(next_field)


def read_manifest(source):
    with source.open("rb") as stream:
        for field, start, offset, length, end in fields(stream, source.stat().st_size):
            if field != MANIFEST_FIELD or not 0 < length <= MAX_METADATA:
                continue
            stream.seek(offset)
            entry = io.BytesIO(stream.read(length))
            values = {}
            for field_number, _, value_offset, value_length, _ in fields(entry, length):
                entry.seek(value_offset)
                values[field_number] = entry.read(value_length)
            if values.get(1) == MANIFEST_KEY:
                return json.loads(values[2]), start, end
    raise ValueError("Missing AIVMX manifest")


def sized_field(number, value):
    return encode((number << 3) | 2) + encode(len(value)) + value


def digest(file):
    with file.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def remove_unlicensed_media(source, destination, voice, acml):
    if digest(source) != voice["sourceSha256"]:
        raise ValueError("Unexpected AIVMX source checksum")
    manifest, start, end = read_manifest(source)
    if manifest["uuid"] != voice["model"] or manifest["version"] != voice["version"]:
        raise ValueError("Unexpected AIVMX source identity")
    license_text = manifest["license"]
    if "ACML 1.0" in voice["usage"]:
        if [line.rstrip() for line in license_text.splitlines()] != [line.rstrip() for line in acml.splitlines()]:
            raise ValueError("Unexpected embedded ACML terms")
    elif "CC0" not in license_text and "WTFPL" not in license_text:
        raise ValueError("Unexpected embedded public-domain terms")
    speakers = manifest["speakers"]
    speaker = next(speaker for speaker in speakers if speaker["uuid"] == voice["speaker"])
    if "ja" not in speaker["supported_languages"] or voice["style"] not in [style["local_id"] for style in speaker["styles"]]:
        raise ValueError("Unexpected speaker language or style")
    for speaker in speakers:
        speaker["icon"] = PLACEHOLDER
        for style in speaker["styles"]:
            style["icon"] = None
            style["voice_samples"] = []
    raw = json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    metadata = sized_field(MANIFEST_FIELD, sized_field(1, MANIFEST_KEY) + sized_field(2, raw))
    with source.open("rb") as incoming, destination.open("xb") as outgoing:
        remaining = start
        while remaining:
            chunk = incoming.read(min(remaining, 1024 * 1024))
            if not chunk:
                raise ValueError("Truncated model")
            outgoing.write(chunk)
            remaining -= len(chunk)
        outgoing.write(metadata)
        incoming.seek(end)
        shutil.copyfileobj(incoming, outgoing, 1024 * 1024)
    voice["sha256"] = digest(destination)
    return license_text


if __name__ == "__main__":
    try:
        root, bundle, staged, acml_path = map(Path, sys.argv[1:])
        template = json.loads(bundle.read_text(encoding="utf-8"))
        acml = acml_path.read_text(encoding="utf-8")
        model_folder = staged / "models"
        model_folder.mkdir()
        licenses = staged / "licenses" / "voices"
        licenses.mkdir(parents=True)
        for entry in template["voices"]:
            name = entry["model"] + ".aivmx"
            license_text = remove_unlicensed_media(root / name, model_folder / name, entry, acml)
            (licenses / (entry["id"] + ".txt")).write_text(license_text, encoding="utf-8")
        (staged / "voices.json").write_text(json.dumps(template, ensure_ascii=False, indent=2), encoding="utf-8")
    except Exception as error:
        print(f"AIVIS-MODEL: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)
