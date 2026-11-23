"""Inspect and assemble the fixed Windows CPU runtime without installing on the host."""

import hashlib
import json
from pathlib import Path, PurePosixPath
import sys
import tarfile
import zipfile
from email.parser import BytesParser


ARCHIVES = {
    "python.zip": "009d6bf7e3b2ddca3d784fa09f90fe54336d5b60f0e0f305c37f400bf83cfd3b",
    "engine.zip": "471f0a3f8aaa1f776d5cd4558466ce7eef702b34383806bf46d1c2593c46ba1f",
    "sbv2.zip": "b11048860e91ccfcd4f67bedd15f666d3037d78de790ab2c9346ed9642c358ba",
    "e2k.zip": "c8967f626702eed2645dcc27e68aa65e5181bcbde9df05ceda9f16adc2c35779",
}
SDIST_CONTENT = {"distance": ("distance/",), "docopt": ("docopt.py",), "jieba": ("jieba/",)}
NOTICE = ("license", "licence", "copying", "notice", "copyright", "authors")
DATA = {".bin", ".dll", ".dic", ".exe", ".htsvoice", ".onnx", ".pyd", ".safetensors", ".npz"}
EXCLUDED_WHEEL_PATHS = ("pyopenjtalk/htsvoice/", "onnx/backend/test/", "onnxruntime/datasets/", "numpy/lib/tests/data/")
CRT_HASH = "4aaf54db0bfc9435f7c3660e1a00237a4b556042bfeea64bde44c2e0194e6ee5"
CRT_PREFIX = "Contents/VC/Redist/MSVC/14.44.35112/x64/Microsoft.VC143.CRT/"


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def fixed(path, expected):
    if path.is_symlink() or sha256(path) != expected:
        raise ValueError(f"Archive hash mismatch: {path.name}")
    return path


def portable_path(name):
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name:
        raise ValueError("Unsafe archive path")
    return Path(*path.parts)


def copy_data(destination, name, data):
    path = destination / portable_path(name)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(data)


def notices(files):
    return [name for name in files if any(term in PurePosixPath(name).name.lower() for term in NOTICE)]


def inspect(root, locks):
    output = []
    for name, digest in ARCHIVES.items():
        with zipfile.ZipFile(fixed(root / name, digest)) as archive:
            files = [file.filename for file in archive.infolist() if not file.is_dir()]
            output.append({"archive": name, "sha256": digest, "notices": notices(files),
                           "assets": [name for name in files if PurePosixPath(name).suffix.lower() in DATA]})
    for lock in locks:
        name = lock["file"]
        asset = fixed(root / "wheels" / name, lock["sha256"])
        if name.endswith(".whl"):
            with zipfile.ZipFile(asset) as archive:
                files = [file.filename for file in archive.infolist() if not file.is_dir()]
                metadata = next((item for item in files if item.endswith(".dist-info/METADATA")), None)
                if metadata is None:
                    raise ValueError(f"Missing wheel metadata: {name}")
                info = BytesParser().parsebytes(archive.read(metadata))
        else:
            with tarfile.open(asset, "r:gz") as archive:
                files = [file.name for file in archive.getmembers() if file.isfile()]
                metadata = next((item for item in files if item.rsplit("/", 1)[-1] == "PKG-INFO"), None)
                info = BytesParser().parsebytes(archive.extractfile(metadata).read() if metadata else b"")
        output.append({"package": lock["name"], "archive": name, "sha256": lock["sha256"],
                       "license": info.get("License-Expression") or info.get("License"),
                       "classifiers": [v for v in info.get_all("Classifier", []) if v.startswith("License")],
                       "notices": notices(files),
                       "assets": [name for name in files if PurePosixPath(name).suffix.lower() in DATA]})
    return output


def extract_source(root, stage, archive_name, package, destination):
    with zipfile.ZipFile(fixed(root / archive_name, ARCHIVES[archive_name])) as archive:
        files = [file.filename for file in archive.infolist() if not file.is_dir()]
        prefix = files[0].split("/", 1)[0] + "/"
        for name in files:
            relative = name.removeprefix(prefix)
            if relative.startswith(package + "/") and not relative.endswith(("/models/model-p2k.npz", "/models/accent.npz")):
                copy_data(destination, relative.removeprefix("src/"), archive.read(name))
            if "/" not in relative and relative in notices([relative]):
                copy_data(stage / "licenses" / archive_name.removesuffix(".zip"), relative, archive.read(name))


def assemble(root, stage, locks):
    python = stage / "python"
    packages = python / "Lib" / "site-packages"
    with zipfile.ZipFile(fixed(root / "python.zip", ARCHIVES["python.zip"])) as archive:
        for file in archive.infolist():
            if not file.is_dir() and not file.filename.startswith("vcruntime140") and file.filename != "pythonw.exe":
                copy_data(python, file.filename, archive.read(file))
    (python / "python311._pth").write_text("python311.zip\n.\nLib/site-packages\n", encoding="ascii")
    extract_source(root, stage, "engine.zip", "voicevox_engine", stage / "engine")
    engine = stage / "engine/voicevox_engine"
    # The private worker never calls the HTTP application or writes its logs.
    (engine / "logging.py").write_text(
        '"""Tanayomi private-worker logger, replacing the HTTP logger (LGPL-3.0)."""\n'
        'import logging\n\nlogger = logging.getLogger("aivis-worker")\n', encoding="utf-8")
    paths = engine / "utility/path_utility.py"
    paths_text = paths.read_text(encoding="utf-8")
    save_dir = '    return Path(user_data_dir(app_name, appauthor=False, roaming=True))'
    if paths_text.count(save_dir) != 1:
        raise ValueError("Unexpected Aivis save-directory source")
    paths.write_text(paths_text.replace("import logging\n", "import logging\nimport os\n").replace(
        save_dir, '    return Path(os.environ["TANAYOMI_AIVIS_TEMP"]).resolve(strict=True)'), encoding="utf-8")
    inference = engine / "tts_pipeline/style_bert_vits2_tts_engine.py"
    inference_text = inference.read_text(encoding="utf-8")
    hub_import = "from ..aivm_manager import AivmManager\n"
    trim_original = (
        "    # 絶対値をとる\n"
        "    abs_audio = np.abs(audio)\n\n"
        "    # 閾値以上のサンプルのインデックスを取得\n"
        "    above_threshold_indices = np.where(abs_audio > threshold)[0]\n\n"
        "    # 全部が閾値以下の場合は空配列を返す\n"
        "    if len(above_threshold_indices) == 0:\n"
        "        return np.array([], dtype=np.float32)\n\n"
        "    # 前側のカット開始位置と後ろ側のカット終了位置(+1)\n"
        "    start_idx = above_threshold_indices[0]\n"
        "    end_idx = above_threshold_indices[-1] + 1\n\n"
        "    # 前後の無音をカット\n"
        "    trimmed = audio[start_idx:end_idx]\n\n"
        "    return trimmed\n"
    )
    if any(inference_text.count(marker) != 1 for marker in (hub_import, trim_original)):
        raise ValueError("Unexpected Aivis inference source")
    inference_text = inference_text.replace(trim_original, (
        "    # Tanayomi: only the first and last samples above threshold are needed.\n"
        "    above_threshold = np.abs(audio) > threshold\n"
        "    if not above_threshold.size:\n"
        "        return np.array([], dtype=np.float32)\n"
        "    start_idx = above_threshold.argmax()\n"
        "    if not above_threshold[start_idx]:\n"
        "        return np.array([], dtype=np.float32)\n"
        "    end_idx = above_threshold.size - above_threshold[::-1].argmax()\n"
        "    return audio[start_idx:end_idx]\n"
    ))
    inference.write_text(inference_text.replace("\nimport copy\n", "\nfrom __future__ import annotations\n\nimport copy\n").replace(
        hub_import, ""), encoding="utf-8")
    extract_source(root, stage, "sbv2.zip", "style_bert_vits2", packages)
    extract_source(root, stage, "e2k.zip", "src/e2k", packages)
    # pyopenjtalk-plus 0.4.1.post8 publishes Nani weights without an explicit
    # grant from their upstream creator. Use the supported Open JTalk reading
    # mode without that optional predictor, preserving the rest of the frontend.
    frontend = packages / "style_bert_vits2/nlp/japanese/pyopenjtalk_worker/__init__.py"
    frontend_text = frontend.read_text(encoding="utf-8")
    option = "            predict_nani=predict_nani,"
    if frontend_text.count(option) != 1:
        raise ValueError("Unexpected pyopenjtalk frontend source")
    frontend.write_text(frontend_text.replace(option, "            predict_nani=False,"), encoding="utf-8")
    for lock in locks:
        name = lock["file"]
        package = lock["name"]
        asset = fixed(root / "wheels" / name, lock["sha256"])
        if name.endswith(".whl"):
            with zipfile.ZipFile(asset) as archive:
                for file in archive.infolist():
                    relative = file.filename
                    if (file.is_dir() or relative.startswith(EXCLUDED_WHEEL_PATHS)
                            or relative in {"pyopenjtalk/yomi_model/nani_enc.onnx", "pyopenjtalk/yomi_model/nani_model.onnx"}
                            or (relative.startswith("setuptools/") and relative.endswith(".exe"))):
                        continue
                    part, separator, rest = relative.partition(".data/")
                    if separator:
                        folder, _, subpath = rest.partition("/")
                        if folder not in {"purelib", "platlib"}:
                            continue  # Wheel CLI scripts, headers, and system installation data.
                        relative = subpath
                    copy_data(packages, relative, archive.read(file))
        else:
            with tarfile.open(asset, "r:gz") as archive:
                for file in archive.getmembers():
                    if not file.isfile():
                        continue
                    relative = file.name.split("/", 1)[1]
                    if relative.startswith(SDIST_CONTENT[package]):
                        copy_data(packages, relative, archive.extractfile(file).read())
                    elif "/" not in relative and relative in notices([relative]):
                        copy_data(stage / "licenses" / package, relative, archive.extractfile(file).read())
    utils = packages / "pyopenjtalk/utils.py"
    utils_text = utils.read_text(encoding="utf-8")
    eager_import = "from .yomi_model.nani_predict import predict\n"
    use = "        is_read_nan = predict([next_feature])"
    if utils_text.count(eager_import) != 1 or utils_text.count(use) != 1:
        raise ValueError("Unexpected pyopenjtalk Nani frontend source")
    utils.write_text(utils_text.replace(eager_import, "").replace(
        use, "        from .yomi_model.nani_predict import predict\n" + use), encoding="utf-8")
    crt = root / "Microsoft.VC.14.44.17.14.CRT.Redist.X64.base.vsix"
    with zipfile.ZipFile(fixed(crt, CRT_HASH)) as archive:
        for name in ("msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll"):
            copy_data(python, name, archive.read(CRT_PREFIX + name))


if __name__ == "__main__":
    try:
        action, directory, output = sys.argv[1:]
        root = Path(directory).resolve(strict=True)
        path = Path(output)
        locks = json.loads((root / "wheels.json").read_text(encoding="utf-8"))
        if action == "inspect":
            path.write_text(json.dumps(inspect(root, locks), ensure_ascii=False, indent=2), encoding="utf-8")
        elif action == "assemble":
            assemble(root, path.resolve(strict=True), locks)
        else:
            raise ValueError("Unknown package action")
    except Exception as error:
        print(f"AIVIS-PACKAGE: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)
