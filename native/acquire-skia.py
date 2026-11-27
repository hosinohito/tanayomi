"""Fixed Skia source acquisition only. No compiler, git clone, SDK or app copy.
Usage: python native/acquire-skia.py --work-root .local/skia-rebuild --acquire.
The default prints the plan only. --verify is local and never downloads.
"""
import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import tarfile
import urllib.parse
import urllib.request
import uuid
import zipfile

WORKSPACE = Path(__file__).resolve().parent.parent
ROOT = WORKSPACE / ".local" / "skia-rebuild"
ALLOW_DOWNLOAD = False
ARCHIVE_SHA256 = {"skia-7dbfc07dd33181f84e0958afb7ee805c6c769f0b.tar.gz":"5265180696746618c344acda2916c9ad1022a0313289018197876cf2612743a9","d3d12allocator-169895d529dfce00390a20e69c2f516066fe7a3b.tar.gz":"145fd395dc97bd57dab7763f68a97de7ce745eb8d862c1b875b6f9093cff4e19","expat-f31adfd584b7f6c50bbf4d22eb928538ffc9145a.tar.gz":"595322e2a8f96a03b1cc985b34dc644f9716a7365889a1828929f782f8ea4d6e","libjpeg-turbo-927aabfcd26897abb9776ecf2a6c38ea5bb52ab6.tar.gz":"519702d4a32d8b95f7227c3b3c2011dc7a3526ea489f1a9f96b215a78e8d27c1","libpng-3061454d980de7d53608f594194cfac722721d2a.tar.gz":"a2569b7553971e36e86c8f75b9d1b5abbe92c436e05169afe3f0072e937f3536","libwebp-4fa21912338357f89e4fd51cf2368325b59e9bd9.tar.gz":"923f3382a47a2af185c3240c954cf004428b237bd7317413a95146d01eb4b94b","vulkanmemoryallocator-c788c52156f3ef7bc7ab769cb03c110a53ac8fcb.tar.gz":"7dacd2f5010f6e9c6a860d2767518b524c433912fcd38f8ff5681e6f40596c7a","spirv-cross-6e1fb9b09efadee38748e0fd0e6210d329087e89.tar.gz":"e8f175054803997c98df5270fde711b7d6d9d5434194718df5a2b8527f654e7c","vulkan-headers-7e691380166fb1cd9b193ac9db896bc23a4ea9ad.tar.gz":"a579b9449c6a8c67075ee0f4cde28b24c9b6bac5b5f41b8e350b7fee084d17a0","wuffs-e3f919ccfe3ef542cfc983a82146070258fb57f8.tar.gz":"e849dab1f372f16b782ba7528e7f70281e35425bd2d644c92a36fb95909f98db","zlib-e432200a7931de1b0b1b50201aac8a46d9380cb2.tar.gz":"f500cf5aa314c21699c692f570c7d141d0272f12703e7acb9d72cd74847a255d","gn-fe330c0ae1ec29db30b6f830e50771a335e071fb.zip":"661e1771fb3e219718df27b69bba169a32ddf134c413e19348e592c528815a60"}
LIMIT_TRANSFER = 600 * 1024 * 1024
LIMIT_EXPANDED = 2 * 1024 * 1024 * 1024
# Gitiles regenerates tar metadata/compression for the same commit. These
# manifests were derived from the SHA-256-pinned original archives above.
# They cover every entry, including excluded tests/docs, and ignore only tar
# timestamps, ownership and compression. Per-file Git blob checks still apply.
ARCHIVE_CONTENT_SHA256 = {
    "libjpeg-turbo-927aabfcd26897abb9776ecf2a6c38ea5bb52ab6.tar.gz": "b2ad361579b368872531e6b6ebe64d944d8e2766cc1481c99234b5ab0d7072b7",
    "zlib-e432200a7931de1b0b1b50201aac8a46d9380cb2.tar.gz": "4294c9cc032cf9b0afeb6eb356f9a167aa28a391edf477e1e053cccc14903c7d",
}
SKIA = "7dbfc07dd33181f84e0958afb7ee805c6c769f0b"
GN = "fe330c0ae1ec29db30b6f830e50771a335e071fb"
# Only fixed dependencies used by the preserved Windows graphics/codecs path.
# DNG and Piex are deliberately absent. No source-history clones are used.
DEPS = [
    ("skia", "mono/skia", SKIA, None),
    ("d3d12allocator", "GPUOpen-LibrariesAndSDKs/D3D12MemoryAllocator", "169895d529dfce00390a20e69c2f516066fe7a3b", None),
    ("expat", "libexpat/libexpat", "f31adfd584b7f6c50bbf4d22eb928538ffc9145a", None),
    ("libjpeg-turbo", None, "927aabfcd26897abb9776ecf2a6c38ea5bb52ab6", "https://chromium.googlesource.com/chromium/deps/libjpeg_turbo"),
    ("libpng", "pnggroup/libpng", "3061454d980de7d53608f594194cfac722721d2a", None),
    ("libwebp", "webmproject/libwebp", "4fa21912338357f89e4fd51cf2368325b59e9bd9", None),
    ("vulkanmemoryallocator", "GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator", "c788c52156f3ef7bc7ab769cb03c110a53ac8fcb", None),
    ("spirv-cross", "KhronosGroup/SPIRV-Cross", "6e1fb9b09efadee38748e0fd0e6210d329087e89", None),
    ("vulkan-headers", "KhronosGroup/Vulkan-Headers", "7e691380166fb1cd9b193ac9db896bc23a4ea9ad", None),
    ("wuffs", "google/wuffs-mirror-release-c", "e3f919ccfe3ef542cfc983a82146070258fb57f8", None),
    ("zlib", None, "e432200a7931de1b0b1b50201aac8a46d9380cb2", "https://chromium.googlesource.com/chromium/src/third_party/zlib"),
]


def owned(path):
    path = Path(path)
    resolved = path.resolve()
    if not resolved.is_relative_to(ROOT) or resolved == ROOT:
        raise ValueError("path outside dedicated source workspace")
    for parent in [path, *path.parents]:
        if parent == ROOT.parent:
            break
        if parent.is_symlink() or getattr(parent, "is_junction", lambda: False)():
            raise ValueError("linked source workspace")
    return path


def atomic_json(path, value):
    path = owned(path)
    temporary = owned(path.with_suffix(path.suffix + ".new"))
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    temporary.replace(path)


state_path = ROOT / "acquisition.json"
state = {}


def configure_root(value, acquire_requested):
    global ROOT, state_path, state, ALLOW_DOWNLOAD
    requested = Path(value).absolute()
    boundary = (WORKSPACE / ".local").resolve()
    if requested.resolve().parent != boundary or not __import__("re").fullmatch(r"skia-rebuild(?:-[a-z0-9-]+)?", requested.name):
        raise ValueError("source workspace must be a dedicated directory below repository .local")
    for parent in [requested, *requested.parents]:
        if parent.is_symlink() or getattr(parent, "is_junction", lambda: False)():
            raise ValueError("linked source workspace")
    ROOT = requested.resolve()
    state_path = ROOT / "acquisition.json"
    ALLOW_DOWNLOAD = acquire_requested
    if acquire_requested:
        ROOT.mkdir(parents=True, exist_ok=True)
    if state_path.exists():
        if state_path.stat().st_size > 4 * 1024 * 1024:
            raise ValueError("acquisition metadata too large")
        state = json.loads(state_path.read_text(encoding="utf-8"))
    else:
        state = {"transferLimit": LIMIT_TRANSFER, "expandedLimit": LIMIT_EXPANDED,
                 "transferredBytes": 0, "expandedBytes": 0, "archives": {}, "sources": {}}
    if not 0 <= state["transferredBytes"] <= LIMIT_TRANSFER or not 0 <= state["expandedBytes"] <= LIMIT_EXPANDED:
        raise ValueError("invalid acquisition budget counters")


def archive_content_sha256(path):
    entries = {}
    expanded = 0
    with tarfile.open(path, "r|gz") as archive:
        for entry in archive:
            if entry.name in entries:
                raise ValueError("duplicate archive entry")
            expanded += entry.size
            if entry.size < 0 or expanded > LIMIT_EXPANDED:
                raise ValueError("archive content verification exceeds expanded budget")
            digest = hashlib.sha256()
            if entry.isfile():
                with archive.extractfile(entry) as source:
                    while block := source.read(1024 * 1024):
                        digest.update(block)
            elif not (entry.isdir() or entry.issym()):
                raise ValueError("unexpected archive content entry")
            entries[entry.name] = (entry.type.decode("ascii"), entry.mode, entry.linkname, digest.hexdigest())
    canonical = json.dumps(entries, sort_keys=True, separators=(",", ":")).encode()
    return hashlib.sha256(canonical).hexdigest()


def archive_matches(path, name, digest, expected=None):
    wanted = ARCHIVE_SHA256.get(name, expected)
    if wanted and digest == wanted:
        return True
    content_pin = ARCHIVE_CONTENT_SHA256.get(name)
    return bool(content_pin and archive_content_sha256(path) == content_pin)


def download(url, path, metadata=False):
    path = owned(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        expected = state["archives"].get(path.name)
        if archive_matches(path, path.name, hashlib.sha256(path.read_bytes()).hexdigest(),
                           expected["sha256"] if expected else None):
            return path
        raise ValueError("existing unverified download is preserved: " + path.name)
    if not ALLOW_DOWNLOAD:
        raise ValueError("local verification requires existing acquisition metadata: " + path.name)
    temporary = owned(path.with_name(path.name + ".part"))
    if temporary.exists():
        raise ValueError("existing incomplete download is preserved: " + path.name)
    digest = hashlib.sha256()
    size = 0
    try:
        request = urllib.request.Request(url, headers={"User-Agent": "Tanayomi-fixed-source-audit"})
        with urllib.request.urlopen(request, timeout=60) as response, temporary.open("xb") as target:
            declared = response.headers.get("Content-Length")
            if declared and int(declared) > LIMIT_TRANSFER - state["transferredBytes"]:
                raise ValueError("compressed transfer budget would be exceeded")
            while True:
                remaining = LIMIT_TRANSFER - state["transferredBytes"]
                block = response.read(min(1024 * 1024, remaining + 1))
                if not block:
                    break
                state["transferredBytes"] += len(block)
                if state["transferredBytes"] > LIMIT_TRANSFER:
                    raise ValueError("compressed transfer budget exceeded; acquisition stopped")
                size += len(block)
                if metadata and size > 16 * 1024 * 1024:
                    raise ValueError("metadata size limit")
                target.write(block)
                digest.update(block)
        if path.name in ARCHIVE_SHA256 and not archive_matches(temporary, path.name, digest.hexdigest()):
            raise ValueError("fixed archive hash mismatch")
        temporary.replace(path)
        state["archives"][path.name] = {"url": url, "bytes": size, "sha256": digest.hexdigest()}
        atomic_json(state_path, state)
        return path
    finally:
        atomic_json(state_path, state)
        if temporary.exists():
            owned(temporary).unlink()


def excluded(name, path):
    parts = PurePosixPath(path).parts
    if not parts:
        return True
    if name == "skia":
        return parts[0] in {".git", "bazel", "bench", "docs", "demos.skia.org", "gm", "infra", "resources", "site", "tests"}
    # No large reference corpora, performance fixtures, documentation builds,
    # example executables or test projects enter the compiler source checkout.
    return any(part.lower() in {"test", "tests", "testdata", "testing", "fuzz", "fuzzing", "corpus", "doc", "docs", "examples", "samples", "reference", "references", "shaders", "shaders-no-opt", "shaders-opt", "shaders-msl", "shaders-hlsl", "shaders-other"} for part in parts)


class Expected:
    def __init__(self, name, github, revision, gitiles):
        self.name, self.revision, self.gitiles = name, revision, gitiles
        self.directories = {}
        self.blobs = None
        if github:
            metadata = ROOT / (name + "-tree.json")
            if not metadata.exists():
                download(f"https://api.github.com/repos/{github}/git/trees/{revision}?recursive=1", metadata, True)
            tree = json.loads(metadata.read_text(encoding="utf-8-sig"))
            if tree.get("truncated"):
                raise ValueError("incomplete upstream source tree")
            self.blobs = {item["path"]: item for item in tree["tree"] if item["type"] == "blob"}

    def blob(self, path):
        if self.blobs is not None:
            return self.blobs[path]["sha"]
        directory, _, filename = path.rpartition("/")
        if directory not in self.directories:
            key = hashlib.sha256(directory.encode()).hexdigest()[:16]
            target = ROOT / "metadata" / (self.name + "-" + key + ".json")
            download(self.gitiles + "/+/" + self.revision + "/" + urllib.parse.quote(directory) + "?format=JSON", target, True)
            raw = target.read_text(encoding="utf-8")
            if raw.startswith(")]}'"):
                raw = raw.split("\n", 1)[1]
            self.directories[directory] = {entry["name"]: entry for entry in json.loads(raw)["entries"]}
        entry = self.directories[directory][filename]
        if entry["type"] != "blob":
            raise ValueError("expected a regular source blob")
        return entry["id"]


def extract(name, archive, destination, strip, expected):
    destination = owned(destination)
    if destination.exists():
        previous = state["sources"].get(name)
        if previous and previous["revision"] == expected.revision:
            return
        raise ValueError("existing source tree is preserved: " + name)
    staging = owned(ROOT / (".extract-" + name + "-" + uuid.uuid4().hex))
    staging.mkdir()
    kept = 0
    written = 0
    links = []
    normalized_newlines = []
    try:
        with tarfile.open(archive, "r|gz") as tar:
            for entry in tar:
                parts = PurePosixPath(entry.name).parts
                if strip:
                    parts = parts[1:]
                if not parts:
                    continue
                if any(part in {"..", ""} or ":" in part or "\\" in part for part in parts) or entry.name.startswith("/"):
                    raise ValueError("unsafe archive path")
                relative = "/".join(parts)
                state["expandedBytes"] += entry.size
                if state["expandedBytes"] > LIMIT_EXPANDED:
                    raise ValueError("expanded source budget exceeded; acquisition stopped")
                if entry.isdir() or excluded(name, relative):
                    continue
                if entry.issym():
                    target_bytes = entry.linkname.encode("utf-8")
                    actual = hashlib.sha1(b"blob " + str(len(target_bytes)).encode() + b"\0" + target_bytes).hexdigest()
                    if actual != expected.blob(relative):
                        raise ValueError("fixed-commit symbolic metadata mismatch")
                    target = staging.joinpath(*parts)
                    referent = (target.parent / entry.linkname).resolve()
                    if not referent.is_relative_to(staging.resolve()):
                        raise ValueError("upstream link escapes its source root")
                    links.append((relative, str(referent.relative_to(staging))))
                    continue
                if not entry.isfile():
                    raise ValueError("unexpected source link or special entry: " + relative)
                blob = expected.blob(relative)
                digest = hashlib.sha1(b"blob " + str(entry.size).encode() + b"\0")
                target = owned(staging.joinpath(*parts))
                target.parent.mkdir(parents=True, exist_ok=True)
                with tar.extractfile(entry) as source, target.open("xb") as output:
                    while block := source.read(1024 * 1024):
                        digest.update(block)
                        output.write(block)
                        written += len(block)
                if digest.hexdigest() != blob:
                    # Git archive honors eol=crlf attributes, while the Git blob
                    # itself is LF. Accept only a proven exact LF normalization.
                    if target.stat().st_size > 16 * 1024 * 1024:
                        raise ValueError("fixed-commit source blob mismatch: " + relative)
                    raw = target.read_bytes()
                    canonical = raw.replace(b"\r\n", b"\n")
                    canonical_hash = hashlib.sha1(b"blob " + str(len(canonical)).encode() + b"\0" + canonical).hexdigest()
                    if canonical_hash != blob:
                        raise ValueError("fixed-commit source blob mismatch: " + relative)
                    target.write_bytes(canonical)
                    written += len(canonical) - len(raw)
                    normalized_newlines.append(relative)
                kept += 1
        # Preserve required header/document aliases as regular copies, never as
        # workspace links. Each link's Git metadata and its target were checked.
        pending = list(links)
        while pending:
            progress = False
            for relative, referent in list(pending):
                source = owned(staging / referent)
                if not source.is_file():
                    continue
                target = owned(staging / relative)
                target.parent.mkdir(parents=True, exist_ok=True)
                state["expandedBytes"] += source.stat().st_size
                if state["expandedBytes"] > LIMIT_EXPANDED:
                    raise ValueError("normalized link exceeds expanded source budget")
                shutil.copyfile(source, target)
                written += source.stat().st_size
                kept += 1
                pending.remove((relative, referent))
                progress = True
            if not progress:
                raise ValueError("unresolved source link after safe extraction")
        destination.parent.mkdir(parents=True, exist_ok=True)
        staging.replace(destination)
        state["sources"][name] = {"revision": expected.revision, "files": kept, "writtenBytes": written, "destination": str(destination), "materializedLinks": links, "normalizedNewlines": normalized_newlines}
        atomic_json(state_path, state)
        print(f"source {name}: {kept} files, {written} bytes", flush=True)
    finally:
        atomic_json(state_path, state)
        if staging.exists():
            owned(staging)
            shutil.rmtree(staging)


def acquire():
    (ROOT / "archives").mkdir(exist_ok=True)
    skia = ROOT / "source" / "skia"
    for name, github, revision, gitiles in DEPS:
        destination = skia if name == "skia" else skia / "third_party" / "externals" / name
        if destination.exists() and name in state["sources"]:
            if state["sources"][name]["revision"] != revision:
                raise ValueError("existing source revision is preserved: " + name)
            continue
        archive_url = f"https://codeload.github.com/{github}/tar.gz/{revision}" if github else gitiles + "/+archive/" + revision + ".tar.gz"
        archive = download(archive_url, ROOT / "archives" / (name + "-" + revision + ".tar.gz"))
        extract(name, archive, destination, github is not None, Expected(name, github, revision, gitiles))
    # Keep the original DEPS, and a separate restricted input for any future
    # sync. Do not run upstream git-sync-deps, which clones complete histories.
    deps = skia / "DEPS"
    text = deps.read_text(encoding="utf-8")
    selected = []
    allowed = {item[0] for item in DEPS if item[0] != "skia"}
    for line in text.splitlines(keepends=True):
        if line.lstrip().startswith('"third_party/externals/'):
            name = line.split('"')[1].rsplit("/", 1)[-1]
            if name not in allowed:
                continue
            selected.append(line)
    restricted = "deps = {\n" + "".join(selected) + "}\n"
    (ROOT / "DEPS.selected").write_text(restricted, encoding="utf-8")
    # Upstream DEPS remains recoverable from the verified original archive.
    # Restrict the isolated checkout too, so it cannot fetch DNG/Piex by mistake.
    deps.write_text(restricted, encoding="utf-8")
    tool = download(f"https://chrome-infra-packages.appspot.com/dl/gn/gn/windows-amd64/+/git_revision:{GN}", ROOT / "archives" / ("gn-" + GN + ".zip"))
    tool_root = owned(ROOT / "tools")
    tool_root.mkdir(exist_ok=True)
    with zipfile.ZipFile(tool) as archive:
        if archive.testzip() is not None:
            raise ValueError("GN archive CRC mismatch")
        entry = archive.getinfo("gn.exe")
        state["expandedBytes"] += entry.file_size
        if state["expandedBytes"] > LIMIT_EXPANDED:
            raise ValueError("GN exceeds expanded budget")
        binary = archive.read(entry)
        target = owned(tool_root / "gn.exe")
        digest = hashlib.sha256(binary).hexdigest()
        if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() != digest:
            raise ValueError("existing GN binary is preserved")
        if not target.exists():
            target.write_bytes(binary)
        state["gn"] = {"revision": GN, "bytes": len(binary), "sha256": digest}
    atomic_json(state_path, state)
    retain_license("tools/GN-LICENSE.txt", "845022e0c1db1abb41a6ba4cd3c4b674ec290f3359d9d3c78ae558d4c0ed9308",
                   f"https://gn.googlesource.com/gn/+/{GN}/LICENSE?format=TEXT", "gn-license-" + GN + ".b64", True)
    retain_license("audit/Chromium-BSD-LICENSE.txt", "368cca1106be99d39ecd32a38d8305585d802a475effb66380b91ffc9bcf709b",
                   "https://raw.githubusercontent.com/chromium/chromium/120.0.6099.109/LICENSE", "chromium-bsd-120.0.6099.109.txt")
    print(json.dumps({"transferredBytes": state["transferredBytes"], "expandedBytes": state["expandedBytes"], "writtenSourceBytes": sum(item["writtenBytes"] for item in state["sources"].values()), "gn": state["gn"]}), flush=True)


def retain_license(relative, digest, url, archive_name, encoded=False):
    target = owned(ROOT / relative)
    if target.exists():
        if hashlib.sha256(target.read_bytes()).hexdigest() != digest:
            raise ValueError("existing license text is preserved: " + relative)
        return
    downloaded = download(url, ROOT / "archives" / archive_name, True).read_bytes()
    data = base64.b64decode(downloaded, validate=False) if encoded else downloaded
    if len(data) > 16384 or hashlib.sha256(data).hexdigest() != digest:
        raise ValueError("fixed primary license mismatch")
    state["expandedBytes"] += len(data)
    if state["expandedBytes"] > LIMIT_EXPANDED:
        raise ValueError("license exceeds expanded budget")
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    atomic_json(state_path, state)


def verify():
    files = total = 0
    for name, github, revision, gitiles in DEPS:
        recorded = state["sources"].get(name)
        if not recorded or recorded["revision"] != revision:
            raise ValueError("fixed source manifest missing: " + name)
        root = ROOT / "source" / "skia"
        if name != "skia":
            root /= "third_party/externals/" + name
        root = owned(root)
        if not root.is_dir():
            raise ValueError("fixed source directory missing")
        expected = Expected(name, github, revision, gitiles)
        aliases = {a: b.replace("\\", "/") for a, b in recorded.get("materializedLinks", [])}
        count = 0
        for path in root.rglob("*"):
            if path.is_dir():
                continue
            relative = path.relative_to(root).as_posix()
            if name == "skia" and relative.startswith("third_party/externals/"):
                continue
            owned(path)
            data = path.read_bytes()
            digest = hashlib.sha256(data).hexdigest()
            if name == "skia" and relative == "DEPS":
                if digest != "6d6b183051f1e5e1c40d021bbe02b5bd0d9da81a74d7cb34a1fd12698eb0b122":
                    raise ValueError("restricted DEPS changed")
            elif name == "skia" and relative == "gn/toolchain/BUILD.gn":
                if digest not in {"6cd433dc1e0c7f72a070e6f9a10e7596ba4b7c0725153bffb94305d4f1128d78",
                                  "fe3d4723543289327b563001b2078fcfd77009d9ff5e9f1e58b9a99b10745a38"}:
                    raise ValueError("isolated MSVC toolchain changed")
            else:
                blob = hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
                if blob != expected.blob(aliases.get(relative, relative)):
                    raise ValueError("fixed source blob changed: " + name + "/" + relative)
            files += 1
            count += 1
            total += len(data)
        if count != recorded["files"]:
            raise ValueError("fixed source file count changed: " + name)
    for relative, digest in {
        "tools/gn.exe": "d9144c267332eeb220f23ffe41696a826437854216531476d46bee807c452511",
        "tools/GN-LICENSE.txt": "845022e0c1db1abb41a6ba4cd3c4b674ec290f3359d9d3c78ae558d4c0ed9308",
        "audit/Chromium-BSD-LICENSE.txt": "368cca1106be99d39ecd32a38d8305585d802a475effb66380b91ffc9bcf709b",
    }.items():
        if hashlib.sha256(owned(ROOT / relative).read_bytes()).hexdigest() != digest:
            raise ValueError("fixed tool/license changed: " + relative)
    pins = [{"name": name, "revision": revision,
             "archiveSha256": ARCHIVE_SHA256[name + "-" + revision + ".tar.gz"],
             "archiveContentSha256": ARCHIVE_CONTENT_SHA256.get(name + "-" + revision + ".tar.gz")}
            for name, _, revision, _ in DEPS]
    print(json.dumps({"verifiedFiles": files, "verifiedBytes": total, "sources": len(DEPS), "networkUsed": False,
                      "sourcePins": pins, "gnRevision": GN,
                      "gnZipSha256": ARCHIVE_SHA256["gn-" + GN + ".zip"]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--acquire", action="store_true")
    parser.add_argument("--verify", action="store_true")
    parser.add_argument("--work-root", default=str(ROOT))
    options = parser.parse_args()
    try:
        if options.acquire and options.verify:
            raise ValueError("choose acquisition or local verification")
        configure_root(options.work_root, options.acquire)
        if options.acquire:
            acquire()
        elif options.verify:
            verify()
        else:
            print(json.dumps({"transferLimit": LIMIT_TRANSFER, "expandedLimit": LIMIT_EXPANDED,
                              "sources": DEPS, "gnRevision": GN, "archiveSha256": ARCHIVE_SHA256,
                              "archiveContentSha256": ARCHIVE_CONTENT_SHA256}))
    except Exception as error:
        print(type(error).__name__ + ": " + str(error), file=__import__("sys").stderr)
        raise SystemExit(1)
