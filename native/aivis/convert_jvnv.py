"""Convert four original JVNV JP-Extra weights using the pinned SBV2 ONNX exporter."""

from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import runpy
import shutil
import sys
from types import ModuleType
import uuid
import zipfile


REVISION = "205830ca1d49e666ddfbf2a755f0108e9cade4dd"
ORIGINAL = f"https://huggingface.co/litagin/style_bert_vits2_jvnv/tree/{REVISION}"
CORPUS = "https://sites.google.com/site/shinnosuketakamichi/research-topics/jvnv_corpus"
ATTRIBUTION = "litagin / JVNV (Detai Xin, Junfeng Jiang, Shinnosuke Takamichi, Yuki Saito, Akiko Aizawa, Hiroshi Saruwatari)"
MODELS = ("jvnv-F1-jp", "jvnv-F2-jp", "jvnv-M1-jp", "jvnv-M2-jp")


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def stage_export(root, directory):
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from package import copy_data, fixed, ARCHIVES

    lock = json.loads((Path(__file__).resolve().parent / "jvnv-conversion-wheels.json").read_text(encoding="utf-8"))
    for entry in lock:
        path = fixed(root / "jvnv/wheels" / entry["file"], entry["sha256"])
        with zipfile.ZipFile(path) as archive:
            for file in archive.infolist():
                if not file.is_dir() and ".data/" not in file.filename:
                    copy_data(directory / "tools", file.filename, archive.read(file))
    with zipfile.ZipFile(fixed(root / "sbv2.zip", ARCHIVES["sbv2.zip"])) as archive:
        exporter = next(name for name in archive.namelist() if name.count("/") == 1 and name.endswith("/convert_onnx.py"))
        original = archive.read(exporter).decode()
    feature_import = "from style_bert_vits2.models.infer import get_text"
    if original.count(feature_import) != 1:
        raise ValueError("Pinned exporter changed")
    # The upstream exporter receives the original six input tensors. Get
    # them from the bundled fixed Japanese frontend and ONNX BERT so the
    # tracing tool need not download a second, PyTorch BERT model.
    patched = original.replace(feature_import, "from tanayomi_jvnv_trace import get_text")
    # Keep the upstream exported graph directly. The optional onnxsim graph
    # rewrite is not needed for ORT and can spend minutes folding each model.
    optimization = patched.index("            # ONNX モデルを最適化\n")
    metadata = patched.index("        # AIVM/AIVMX ファイルを生成\n", optimization)
    patched = (patched[:optimization] + "            onnx.checker.check_model(str(onnx_temp_model_path))\n"
               + "            onnx_temp_model_path.replace(onnx_optimized_model_path)\n\n" + patched[metadata:])
    patched = patched.replace("from onnxsim import model_info, simplify\n", "")
    (directory / "exporter.py").write_text(patched, encoding="utf-8")


@contextmanager
def command_args(arguments):
    previous = sys.argv
    sys.argv = arguments
    try:
        yield
    finally:
        sys.argv = previous


def export_one(package, root, directory, model, destination):
    import numpy as np
    sys.path.insert(0, str(directory / "tools"))
    import aivmlib
    from aivmlib.schemas.aivm_manifest import ModelArchitecture
    from style_bert_vits2.constants import Languages
    from style_bert_vits2.models.infer_onnx import get_text_onnx
    from style_bert_vits2.nlp import onnx_bert_models
    import torch

    torch.manual_seed(0)
    torch.set_num_threads(4)
    np.random.seed(0)
    providers = ["CPUExecutionProvider"]
    os.environ["OPEN_JTALK_DICT_DIR"] = str(package / "python/Lib/site-packages/pyopenjtalk/dictionary")
    bert = str(package / "bert")
    onnx_bert_models.load_model(Languages.JP, bert, providers)
    onnx_bert_models.load_tokenizer(Languages.JP, bert)

    def trace_features(text, language, config, device, **kwargs):
        result = get_text_onnx(text, language, config, providers, **kwargs)
        return tuple(torch.from_numpy(value).to(dtype=torch.float32) if index < 3 else torch.from_numpy(value)
                     for index, value in enumerate(result))

    trace_module = ModuleType("tanayomi_jvnv_trace")
    trace_module.get_text = trace_features
    sys.modules[trace_module.__name__] = trace_module

    lock = json.loads((Path(__file__).resolve().parent / "jvnv-models.json").read_text(encoding="utf-8"))
    inputs = lock[model]
    local = directory / model
    local.mkdir()
    for name, digest in inputs.items():
        source = root / "jvnv" / model / name
        if source.is_symlink() or sha256(source) != digest:
            raise ValueError(f"Unexpected JVNV input: {model}/{name}")
        shutil.copyfile(source, local / name)
    model_path, = local.glob("*.safetensors")
    with (local / "config.json").open("rb") as config, (local / "style_vectors.npy").open("rb") as styles:
        metadata = aivmlib.generate_aivm_metadata(ModelArchitecture.StyleBertVITS2JPExtra, config, styles)
    manifest = metadata.manifest
    if manifest.model_architecture != ModelArchitecture.StyleBertVITS2JPExtra or len(manifest.speakers) != 1:
        raise ValueError("Expected single-speaker JVNV JP-Extra model")
    speaker = manifest.speakers[0]
    if metadata.hyper_parameters.data.style2id.get("Neutral") != 0 or not any(style.local_id == 0 for style in speaker.styles):
        raise ValueError("Unexpected JVNV default style")
    model_uuid = uuid.uuid5(uuid.NAMESPACE_URL, f"{ORIGINAL}/{model}")
    speaker_uuid = uuid.uuid5(model_uuid, "speaker-0")
    manifest.uuid = model_uuid
    manifest.version = "1.0.0"
    manifest.name = model
    manifest.description = f"{model}, JP-Extra weights by litagin from the JVNV corpus. Converted to ONNX by Tanayomi."
    manifest.creators = ["litagin"]
    manifest.license = (package / "licenses/CC-BY-SA-4.0.txt").read_text(encoding="utf-8")
    speaker.uuid = speaker_uuid
    speaker.name = model
    type(manifest).model_validate_json(manifest.model_dump_json())
    with command_args([str(directory / "exporter.py"), "--model", str(model_path)]):
        runpy.run_path(str(directory / "exporter.py"), run_name="__main__")
    with model_path.with_suffix(".onnx").open("rb") as onnx:
        content = aivmlib.write_aivmx_metadata(onnx, metadata)
    output = destination / f"{model_uuid}.aivmx"
    with output.open("xb") as stream:
        stream.write(content)
    voice = {"id": f"aivis-{model.lower()}", "name": f"JVNV {model[5:7]} (Style-Bert-VITS2)",
             "model": str(model_uuid), "speaker": str(speaker_uuid), "version": "1.0.0",
             "style": 0, "sha256": sha256(output), "sourceFiles": inputs, "credit": f"{model} — {ATTRIBUTION}",
             "usage": "CC BY-SA 4.0。合成音声です。元モデルをTanayomiがONNXに変換しました。",
             "termsUrl": "https://creativecommons.org/licenses/by-sa/4.0/"}
    (destination / f"{model}.json").write_text(json.dumps(voice, ensure_ascii=False, indent=2), encoding="utf-8")


def merge_converted(package, converted):
    helper = Path(__file__).resolve().parent
    sys.path.insert(0, str(helper))
    from model_metadata import read_manifest

    path = package / "voices.json"
    bundle = json.loads(path.read_text(encoding="utf-8"))
    lock = json.loads((helper / "jvnv-models.json").read_text(encoding="utf-8"))
    if len(bundle["voices"]) != 4:
        raise ValueError("Expected the four original Aivis voices")
    for model in MODELS:
        entry = json.loads((converted / f"{model}.json").read_text(encoding="utf-8"))
        expected_uuid = str(uuid.uuid5(uuid.NAMESPACE_URL, f"{ORIGINAL}/{model}"))
        if entry["model"] != expected_uuid or entry["sourceFiles"] != lock[model] or entry["id"] != f"aivis-{model.lower()}":
            raise ValueError("Unexpected converted JVNV identity")
        file = converted / f"{expected_uuid}.aivmx"
        if file.is_symlink() or sha256(file) != entry["sha256"]:
            raise ValueError("Unexpected converted JVNV file hash")
        manifest, _, _ = read_manifest(file)
        if (manifest["model_architecture"] != "Style-Bert-VITS2 (JP-Extra)" or manifest["model_format"] != "ONNX"
                or manifest["uuid"] != expected_uuid or len(manifest["speakers"]) != 1
                or manifest["speakers"][0]["uuid"] != entry["speaker"]):
            raise ValueError("Unexpected converted JVNV manifest")
        shutil.copyfile(file, package / "models" / file.name)
        bundle["voices"].append(entry)
    path.write_text(json.dumps(bundle, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    try:
        if sys.argv[1] == "stage":
            stage_export(*(Path(item).resolve(strict=True) for item in sys.argv[2:]))
        elif sys.argv[1] == "export":
            _, _, package, root, stage, model, destination = sys.argv
            if model not in MODELS:
                raise ValueError("Unknown JVNV voice")
            export_one(*(Path(item).resolve(strict=True) for item in (package, root, stage)),
                       model, Path(destination).resolve(strict=True))
        elif sys.argv[1] == "merge":
            merge_converted(*(Path(item).resolve(strict=True) for item in sys.argv[2:]))
        else:
            raise ValueError("Unknown JVNV command")
    except Exception as error:
        print(f"JVNV-CONVERT: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)
