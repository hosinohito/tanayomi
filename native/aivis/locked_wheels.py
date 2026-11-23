"""Resolve the pinned engine lock for the bundled CPython 3.11 Windows CPU worker."""

import json
from pathlib import Path
import sys
import tomllib
from urllib.parse import unquote, urlsplit
import zipfile

from packaging.markers import Marker
from packaging.tags import Tag, compatible_tags, cpython_tags
from packaging.utils import parse_wheel_filename


def resolve(source, cpu_wheel):
    with zipfile.ZipFile(source) as archive:
        lock = tomllib.loads(archive.read("AivisSpeech-Engine-1.2.0/uv.lock").decode())
    packages = {entry["name"]: entry for entry in lock["package"]}
    tags = list(cpython_tags((3, 11), platforms=["win_amd64"]))
    tags.extend(compatible_tags((3, 11), "cp311", ["win_amd64"]))
    tags.append(Tag("py3", "none", "any"))
    rank = {tag: index for index, tag in enumerate(tags)}
    env = {
        "os_name": "nt", "sys_platform": "win32", "platform_machine": "AMD64",
        "platform_system": "Windows", "platform_python_implementation": "CPython",
        "implementation_name": "cpython", "implementation_version": "3.11.9",
        "python_version": "3.11", "python_full_version": "3.11.9", "extra": "",
    }
    # The worker imports the engine's query model and inference code, without
    # the HTTP runner's telemetry, settings, or WMI environment discovery.
    pending = ["aivmlib", "e2k", "fastapi", "httpx", "jaconv", "kanalizer",
               "numpy", "onnxruntime", "platformdirs", "psutil", "pydantic",
               "pyopenjtalk-plus", "pyworld-prebuilt", "pyyaml", "semver",
               "setuptools", "soxr", "style-bert-vits2"]
    other_language_packages = {"cmudict", "cn2an", "g2p-en", "jieba", "nltk", "pypinyin"}
    seen = set()
    result = []
    while pending:
        name = pending.pop()
        if name in seen:
            continue
        seen.add(name)
        entry = packages[name]
        for dependency in entry.get("dependencies", []):
            if name == "style-bert-vits2" and dependency["name"] in other_language_packages:
                continue
            if name != "onnxruntime" and "marker" in dependency and not Marker(dependency["marker"]).evaluate(env):
                continue
            pending.append(dependency["name"])
        if "git" in entry["source"]:
            continue
        if name == "onnxruntime":
            result.append(cpu_wheel)
            continue
        matches = []
        for wheel in entry.get("wheels", []):
            filename = unquote(Path(urlsplit(wheel["url"]).path).name)
            supported = parse_wheel_filename(filename)[3] & rank.keys()
            if supported:
                matches.append((min(rank[tag] for tag in supported), filename, wheel))
        if not matches:
            wheel = entry.get("sdist")
            if wheel is None:
                raise ValueError(f"Missing Windows distribution for {name} {entry['version']}")
            filename = unquote(Path(urlsplit(wheel["url"]).path).name)
        else:
            _, filename, wheel = min(matches, key=lambda item: item[0])
        result.append({"name": name, "version": entry["version"], "file": filename,
                       "url": wheel["url"], "sha256": wheel["hash"].removeprefix("sha256:")})
    return sorted(result, key=lambda item: item["name"])


if __name__ == "__main__":
    try:
        source, cpu = map(Path, sys.argv[1:])
        entries = resolve(source, json.loads(cpu.read_text(encoding="utf-8")))
        print(json.dumps(entries, indent=2))
    except Exception as error:
        print(f"AIVIS-LOCK: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)
