"""Acquire audited Aivis/JVNV inputs; never install packages or replace existing bytes.

Run with repository-owned Python 3.11+: --verify checks existing inputs without writes;
--plan lists paths and origins without reading model payloads; --without-jvnv omits
conversion-only inputs. Default acquisition stages only missing inputs under .local/tmp.
Hashes and versions remain in the existing package/voice/wheel locks plus inputs.json.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import sys
import tempfile
import urllib.request

REPOSITORY = Path(__file__).resolve().parent.parent
LOCKS = REPOSITORY / "native/aivis"
VENDOR = REPOSITORY / ".local/vendor/aivis"


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def no_links(path):
    for current in (path, *path.parents):
        if current.is_symlink() or (hasattr(current, "is_junction") and current.is_junction()):
            raise ValueError(f"Input path traverses a link: {current}")
        # Python 3.11 has no is_junction(). Reject all Windows reparse points.
        if current.exists() and getattr(current.stat(follow_symlinks=False), "st_file_attributes", 0) & 1024:
            raise ValueError(f"Input path traverses a reparse point: {current}")


def safe_child(root, relative):
    path = PurePosixPath(relative)
    if not relative or path.is_absolute() or ".." in path.parts or "\\" in relative or ":" in relative:
        raise ValueError("Invalid input path")
    result = root.joinpath(*path.parts)
    no_links(result)
    return result


def inventory(include_jvnv):
    manifest = read_json(LOCKS / "inputs.json")
    entries = list(manifest["inputs"])
    entries.append({"path": "wheels.json", "bundled": "native/aivis/wheels.json",
                    "sha256": digest(LOCKS / "wheels.json")})
    for lock_name, prefix in [("wheels.json", "wheels/"), *(
            [("jvnv-conversion-wheels.json", "jvnv/wheels/")] if include_jvnv else [])]:
        entries.extend({"path": prefix + item["file"], "url": item["url"], "sha256": item["sha256"]}
                       for item in read_json(LOCKS / lock_name))
    voices = read_json(LOCKS / "voices.json")
    entries.extend({"path": "bert/" + name, "url": manifest["bertBase"] + name, "sha256": sha}
                   for name, sha in voices["bert"].items())
    for voice in voices["voices"]:
        base = "https://api.aivis-project.com/v1/aivm-models/" + voice["model"]
        entries.append({"path": "models/" + voice["model"] + ".aivmx",
                        "url": base + "/download?model_type=AIVMX", "metadata": base,
                        "version": voice["version"], "sha256": voice["sourceSha256"]})
    if include_jvnv:
        entries.extend({"path": "jvnv/" + model + "/" + name,
                        "url": manifest["jvnvBase"] + model + "/" + name, "sha256": sha}
                       for model, files in read_json(LOCKS / "jvnv-models.json").items()
                       for name, sha in files.items())
    paths = [entry["path"] for entry in entries]
    if len(paths) != len(set(paths)):
        raise ValueError("Duplicate input path")
    for entry in entries:
        if len(entry["sha256"]) != 64 or any(c not in "0123456789abcdef" for c in entry["sha256"]):
            raise ValueError("Invalid locked SHA-256")
        if "url" in entry and not entry["url"].startswith("https://"):
            raise ValueError("Input origin must use HTTPS")
        safe_child(VENDOR, entry["path"])
    return entries


def acquire(entry, verify):
    target = safe_child(VENDOR, entry["path"])
    if target.exists():
        if not target.is_file() or digest(target) != entry["sha256"]:
            raise ValueError(f"Existing input differs from lock (preserved): {entry['path']}")
        return "verified"
    if verify:
        raise FileNotFoundError(f"Missing input: {entry['path']}")
    bundled = safe_child(REPOSITORY, entry["bundled"]) if "bundled" in entry else None
    if bundled is not None and digest(bundled) != entry["sha256"]:
        raise ValueError(f"Bundled input differs from lock: {entry['bundled']}")
    if "metadata" in entry:
        # Hub's public download API only provides the newest version. Refuse a changed
        # release before fetching a large model; the final payload hash is still required.
        with urllib.request.urlopen(entry["metadata"], timeout=60) as response:
            metadata = json.load(response)
        models = [item for item in metadata["model_files"] if item["model_type"] == "AIVMX"]
        if not models or any(item["version"] != entry["version"] or
                             item["checksum"] != entry["sha256"] for item in models):
            raise ValueError(f"Hub no longer exposes the locked release: {entry['path']}")
    temporary = REPOSITORY / ".local/tmp"
    no_links(temporary)
    temporary.mkdir(parents=True, exist_ok=True)
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="aivis-input-", dir=temporary) as scratch:
        staged = Path(scratch) / "payload"
        if bundled is not None:
            shutil.copyfile(bundled, staged)
        else:
            with urllib.request.urlopen(entry["url"], timeout=240) as response, staged.open("xb") as output:
                if not response.url.startswith("https://"):
                    raise ValueError("Insecure download redirect")
                shutil.copyfileobj(response, output, 1024 * 1024)
        if digest(staged) != entry["sha256"]:
            raise ValueError(f"Downloaded input differs from lock: {entry['path']}")
        no_links(target)
        # Both paths are repository-local on the same volume. Publish the verified file
        # atomically without replacing an original or exposing an incomplete download.
        os.link(staged, target)
    return "acquired"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--verify", action="store_true")
    action.add_argument("--plan", action="store_true")
    parser.add_argument("--without-jvnv", action="store_true")
    args = parser.parse_args()
    entries = inventory(not args.without_jvnv)
    failures = 0
    for entry in entries:
        if args.plan:
            print(f"{entry['path']}\t{entry.get('url', entry.get('bundled'))}\t{entry['sha256']}")
            continue
        try:
            result = acquire(entry, args.verify)
            print(f"{result}: {entry['path']}")
        except Exception as error:
            failures += 1
            # Avoid printing URLs from network exceptions (Hub redirects contain signatures).
            message = str(error) if isinstance(error, (ValueError, FileNotFoundError)) else type(error).__name__
            print(f"AIVIS-INPUT: {entry['path']}: {message}", file=sys.stderr)
    print(f"Aivis inputs: {len(entries)} entries, {failures} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"AIVIS-INPUT: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)
