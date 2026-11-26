[CmdletBinding()]
param(
    [switch]$Download,
    [string]$Python = '',
    [string]$ReadObj = '',
    [string]$CrtDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vendor = Join-Path $root '.local/vendor/supertonic'
if (-not $Python) { $Python = Join-Path $root '.local/neural-openvoice/venv/Scripts/python.exe' }
if (-not $ReadObj) {
    $ReadObj = Join-Path $root '.local/vendor/llvm-mingw/llvm-mingw-20261020-msvcrt-x86_64/bin/llvm-readobj.exe'
}
if (-not $CrtDirectory) { $CrtDirectory = Join-Path $root '.local/vendor/voicevox/package' }

function Confirm-LocalPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Supertonic staging path is outside the repository.'
    }
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and
            ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Supertonic staging path traverses a link.'
        }
    }
}

Confirm-LocalPath $vendor
[IO.Directory]::CreateDirectory($vendor) | Out-Null
$stage = Join-Path $root ('.local/tmp/supertonic-' + [Guid]::NewGuid().ToString('N'))
$prepared = Join-Path $vendor 'prepared-md-vc143'
Confirm-LocalPath $stage
Confirm-LocalPath $prepared
$savedTemp = @($env:TEMP, $env:TMP, $env:TMPDIR)
$guard = $null
$nativeName = 'sherpa-onnx-v2.0.1-win-x64-shared-MD-Release-lib.tar.bz2'
$modelName = 'sherpa-onnx-supertonic-3-tts-int8-2026-05-11.tar.bz2'
$inputs = @(
    @{
        Name = $nativeName
        Url = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/v2.0.1/' + $nativeName
        Hash = 'c3c78220db8bebf97d5f932496165bb3897b163410427d7c021013439c80f183'
    },
    @{
        Name = $modelName
        Url = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/' + $modelName
        Hash = '82fa96f91c4ef8abaae3a14a3f4153facf88bed821d1f7331cec2700f432c427'
    }
)

function Get-FixedInput([string]$Directory, [string]$Name, [string]$Url, [string]$Hash, [string]$BundledInput = '') {
    $inputPath = Join-Path $Directory $Name
    Confirm-LocalPath $inputPath
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
        if ($BundledInput) {
            Confirm-LocalPath $BundledInput
            if ((Get-FileHash -LiteralPath $BundledInput -Algorithm SHA256).Hash -ne $Hash) {
                throw "Bundled Supertonic input digest mismatch: $Name."
            }
            [IO.File]::Copy($BundledInput, $inputPath)
        } else {
            if (-not $Download) { throw "Missing fixed Supertonic input $Name." }
            $pending = Join-Path $stage $Name
            Invoke-WebRequest -Uri $Url -OutFile $pending -TimeoutSec 180 -UseBasicParsing
            if ((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash -ne $Hash) {
                throw "Supertonic input digest mismatch: $Name."
            }
            [IO.File]::Move($pending, $inputPath)
        }
    }
    if ((Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -ne $Hash) {
        throw "Supertonic input digest mismatch: $Name."
    }
    return $inputPath
}

try {
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    $env:TEMP = $env:TMP = $env:TMPDIR = $stage
    . (Join-Path $PSScriptRoot 'process-error-mode.ps1')
    $guard = Enter-NativeProcessErrorMode

    foreach ($item in $inputs) {
        $null = Get-FixedInput $vendor $item.Name $item.Url $item.Hash
    }
    $null = Get-FixedInput $vendor 'sherpa-c-api-v2.0.1.h' `
        'https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/v2.0.1/sherpa-onnx/c-api/c-api.h' `
        '5698F38BA09AD2BDF085C56A6CBBD7741ACF15E25F4CE5C7097B9702CADF437F'
    $clang = Join-Path (Split-Path -Parent $ReadObj) 'clang.exe'
    @'
#include "sherpa-c-api-v2.0.1.h"
#define offset(type, field) __builtin_offsetof(type, field)
_Static_assert(sizeof(SherpaOnnxOfflineTtsConfig) == 456, "TTS config size");
_Static_assert(offset(SherpaOnnxOfflineTtsConfig, model.num_threads) == 56, "threads");
_Static_assert(offset(SherpaOnnxOfflineTtsConfig, model.provider) == 64, "provider");
_Static_assert(offset(SherpaOnnxOfflineTtsConfig, model.supertonic) == 368, "Supertonic config");
_Static_assert(sizeof(SherpaOnnxOfflineTtsSupertonicModelConfig) == 56, "Supertonic paths");
_Static_assert(offset(SherpaOnnxOfflineTtsConfig, max_num_sentences) == 432, "sentence limit");
_Static_assert(offset(SherpaOnnxOfflineTtsConfig, silence_scale) == 448, "silence scale");
_Static_assert(sizeof(SherpaOnnxGenerationConfig) == 104, "generation config size");
_Static_assert(offset(SherpaOnnxGenerationConfig, num_steps) == 40, "steps");
_Static_assert(offset(SherpaOnnxGenerationConfig, extra) == 48, "extra");
_Static_assert(sizeof(SherpaOnnxGeneratedAudio) == 16, "audio result size");
'@ | & $clang --target=x86_64-pc-windows-msvc -I $vendor -x c -fsyntax-only -
    if ($LASTEXITCODE -ne 0) { throw 'Supertonic C ABI verification failed.' }

    $output = Join-Path $stage 'prepared'
    [IO.Directory]::CreateDirectory($output) | Out-Null
    @'
import hashlib
import json
import pathlib
import shutil
import sys
import tarfile

vendor, out = map(pathlib.Path, sys.argv[1:3])
native_name, model_name = sys.argv[3:5]
model_members = {
    "duration_predictor.int8.onnx": 3700147,
    "text_encoder.int8.onnx": 36416150,
    "vector_estimator.int8.onnx": 78400833,
    "vocoder.int8.onnx": 25991073,
    "tts.json": 8253,
    "unicode_indexer.bin": 262144,
    "voice.bin": 517168,
    "LICENSE": 1070,
    "README.md": 19518,
}
native_members = {
    "lib/sherpa-onnx-c-api.dll": 3925504,
    "lib/onnxruntime.dll": 17136128,
    "lib/onnxruntime_providers_shared.dll": 10752,
}
licenses = out / "licenses"
licenses.mkdir()

def extract(archive_name, expected):
    prefix = archive_name.removesuffix(".tar.bz2") + "/"
    seen = set()
    with tarfile.open(vendor / archive_name, "r:bz2") as archive:
        for member in archive:
            if not member.name.startswith(prefix):
                if member.isdir() and member.name == prefix[:-1]:
                    continue
                raise ValueError("archive root")
            relative = member.name[len(prefix):]
            if relative not in expected:
                continue
            if not member.isfile() or member.size != expected[relative] or relative in seen:
                raise ValueError("archive member")
            seen.add(relative)
            destination = out / pathlib.PurePosixPath(relative).name
            if relative == "LICENSE":
                destination = licenses / "sherpa-supertonic-conversion-MIT.txt"
            elif relative == "README.md":
                destination = licenses / "sherpa-supertonic-conversion-README.md"
            with archive.extractfile(member) as source, destination.open("xb") as target:
                shutil.copyfileobj(source, target)
    if seen != expected.keys():
        raise ValueError("missing archive member")

extract(native_name, native_members)
extract(model_name, model_members)
manifest = {}
for path in sorted(p for p in out.iterdir() if p.is_file()):
    with path.open("rb") as stream:
        manifest[path.name] = {"bytes": path.stat().st_size, "sha256": hashlib.file_digest(stream, "sha256").hexdigest()}
(out / "supertonic-assets.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
'@ | & $Python -I -B - $vendor $output $nativeName $modelName
    if ($LASTEXITCODE -ne 0) { throw 'Supertonic archive verification or staging failed.' }

    $pe = @()
    foreach ($file in Get-ChildItem -LiteralPath $output -Filter '*.dll' -File) {
        $inspection = & $ReadObj --file-headers --coff-imports $file.FullName
        if ($LASTEXITCODE -ne 0 -or -not ($inspection -match 'IMAGE_FILE_MACHINE_AMD64')) {
            throw "Supertonic DLL is not a verified x64 PE: $($file.Name)."
        }
        $pe += $inspection -replace [regex]::Escape($file.FullName), $file.Name
    }
    $pe | Set-Content -LiteralPath (Join-Path $output 'supertonic-pe.txt') -Encoding utf8

    $notices = Join-Path $vendor 'license-inputs'
    Confirm-LocalPath $notices
    [IO.Directory]::CreateDirectory($notices) | Out-Null
    $licenseOut = Join-Path $output 'licenses'
    $raw = 'https://raw.githubusercontent.com/'
    $licenseFiles = @(
        @('Supertonic-3-OpenRAIL-M.txt', 'https://huggingface.co/Supertone/supertonic-3/raw/724fb5abbf5502583fb520898d45929e62f02c0b/LICENSE', '0D944A9110FED9A9602D60E0423A272903E7BD21AB060490774EFC77C2275E9F'),
        @('sherpa-onnx-LICENSE.txt', ($raw + 'k2-fsa/sherpa-onnx/v2.0.1/LICENSE'), 'CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30'),
        @('onnxruntime-LICENSE.txt', ($raw + 'microsoft/onnxruntime/v1.28.2/LICENSE'), '2F07C72751AED99790B8A4869CF2311DF85A860B22DED05FA22803587A48922C'),
        @('onnxruntime-ThirdPartyNotices.txt', ($raw + 'microsoft/onnxruntime/v1.28.2/ThirdPartyNotices.txt'), '0E07B95F3A8D6230037707C5C4A2B554D12C4CB67369669AC255635528FFCEE2'),
        @('kaldi-native-fbank.txt', ($raw + 'csukuangfj/kaldi-native-fbank/v1.22.4/LICENSE'), 'CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30'),
        @('kaldi-decoder.txt', ($raw + 'k2-fsa/kaldi-decoder/v0.3.0/LICENSE'), 'CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30'),
        @('kaldifst.txt', ($raw + 'k2-fsa/kaldifst/v1.8.0/LICENSE'), 'A682D6EFD1EE5DEE08A8E405C233C2C198EA70AE0718129DAA83AB58CFE31C5D'),
        @('openfst.txt', ($raw + 'csukuangfj/openfst/v1.8.5-2026-04-10/COPYING'), '4300529197035FD3452350718A0B8CEE984E9412C9932D7F35FCDE849FC97A4B'),
        @('utf8proc.txt', ($raw + 'JuliaStrings/utf8proc/v2.12.0/LICENSE.md'), '3B510150D34F248A221BB88E1D811238D6C6C18B51231822C42974C39BB07256'),
        @('simple-sentencepiece.txt', ($raw + 'pkufool/simple-sentencepiece/v0.7/LICENSE'), 'C71D239DF91726FC519C6EB72D318EC65820627232B2F796219E87DCF35D0AB4'),
        @('darts.h', ($raw + 'pkufool/simple-sentencepiece/v0.7/ssentencepiece/csrc/darts.h'), 'A0FF15ABF69E2651DEDEF5ACACF474B7B8D20ACE8CD5DD79DD201E99CCFEE91D'),
        @('nlohmann-json.txt', ($raw + 'nlohmann/json/v3.12.0/LICENSE.MIT'), '46A65CFFD1EA955132D95A8DD921640714A8D6B537D2E4E482D31145AE95B603'),
        @('kissfft.txt', ($raw + 'mborgerding/kissfft/febd4caeed32e33ad8b2e0bb5ea77542c40f18ec/COPYING'), 'A2840585F8411BE8E6826A31EF15AE65C950BD74A2437A73B013398A934AD0C6'),
        @('kissfft-BSD-3-Clause.txt', ($raw + 'mborgerding/kissfft/febd4caeed32e33ad8b2e0bb5ea77542c40f18ec/LICENSES/BSD-3-Clause'), '4D6BCAB46F27D6D0C878E8305D4BDCFE040D52BF6656B9771F04C9FB9902C0ED'),
        @('eigen-MPL.txt', 'https://gitlab.com/libeigen/eigen/-/raw/3.4.0/COPYING.MPL2', 'FAB3DD6BDAB226F1C08630B1DD917E11FCB4EC5E1E020E2C16F83A0A13863E85'),
        @('hclust-cpp.txt', ($raw + 'csukuangfj/hclust-cpp/2026-02-25/LICENSE'), 'E361D842DA1D290351860AD6DCCE2673A1E42E4F873A3AE1FF4EB92C6ADAA882')
    )
    foreach ($entry in $licenseFiles) {
        $bundledInput = ''
        if ($entry[0] -eq 'onnxruntime-LICENSE.txt') {
            # Preserve the audited bytes even when the upstream fixed tag is unavailable.
            $bundledInput = Join-Path $root 'assets/supertonic-ort-LICENSE.txt'
        } elseif ($entry[0] -eq 'onnxruntime-ThirdPartyNotices.txt') {
            $bundledInput = Join-Path $root 'assets/supertonic-ort-NOTICES.txt'
        }
        $source = Get-FixedInput $notices $entry[0] $entry[1] $entry[2] $bundledInput
        if ($entry[0] -eq 'darts.h') {
            $header = [IO.File]::ReadAllText($source)
            $end = $header.IndexOf('*/', [StringComparison]::Ordinal)
            if ($end -lt 0) { throw 'Missing Darts-clone license block.' }
            [IO.File]::WriteAllText((Join-Path $licenseOut 'darts-clone-BSD-2-Clause.txt'), $header.Substring(0, $end + 2))
        } else { [IO.File]::Copy($source, (Join-Path $licenseOut $entry[0])) }
    }
    $eigen = Get-FixedInput $vendor 'eigen-3.4.0.tar.gz' `
        'https://gitlab.com/libeigen/eigen/-/archive/3.4.0/eigen-3.4.0.tar.gz' `
        '8586084f71f9bde545ee7fa6d00288b264a2b7ac3607b974e54d13e7162c1c72'
    [IO.File]::Copy($eigen, (Join-Path $licenseOut 'eigen-3.4.0.tar.gz'))
    $ortEigen = Get-FixedInput $vendor 'eigen-1d8b82b0740839c0de7f1242a3585e3390ff5f33.zip' `
        'https://github.com/eigen-mirror/eigen/archive/1d8b82b0740839c0de7f1242a3585e3390ff5f33/eigen-1d8b82b0740839c0de7f1242a3585e3390ff5f33.zip' `
        '6A60D76351F97132669DAEEB721D6BF14B008101883AD2D687A3201C5C461EB0'
    [IO.File]::Copy($ortEigen, (Join-Path $licenseOut 'eigen-ort-source.zip'))
    $crtFiles = [ordered]@{
        'msvcp140.dll' = '0F885B509A685D2BBFA652FED26B5FB31D88FBDAB0A978C641D1C7B8AA460AA9'
        'msvcp140_1.dll' = 'BFAD5AEF4C63A669E3C140655CDFDF395B6C979B400A447BD5DCB65ED8826C3D'
        'vcruntime140.dll' = 'D5E4D9A3E835FA679450145D6A7D94E36573A509317111904D9B3712C30D9066'
        'vcruntime140_1.dll' = '1F2D41C4AA5DB0BC33EBF7B66D72943A817D7CE6CBE880502A9403823633093F'
        'licenses/Microsoft-VC-Redist-NOTICE.txt' = '65E4B5CBBEB80EA5DFBD4D5B3542701E07DC4501FDB06E6A5307F0C8C007364A'
        'licenses/Microsoft-VC-Runtime-LICENSE.txt' = '38C113F791FDD03FA9316F0F743201CB54A1C8C9B683B615A906EA2FEA7C8B6E'
    }
    foreach ($relative in $crtFiles.Keys) {
        $source = Join-Path $CrtDirectory $relative
        Confirm-LocalPath $source
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $crtFiles[$relative]) {
            throw "VC143 14.44.35211 file digest mismatch: $relative."
        }
        if ($relative.EndsWith('.dll') -and (Get-AuthenticodeSignature -LiteralPath $source).Status -ne 'Valid') {
            throw "VC143 signature verification failed: $relative."
        }
        [IO.File]::Copy($source, (Join-Path $output $relative))
    }
    @'
Supertonic 3 weights copyright (c) 2026 Supertone Inc.
Original weights and OpenRAIL-M: https://huggingface.co/Supertone/supertonic-3/tree/724fb5abbf5502583fb520898d45929e62f02c0b
The bundled INT8 ONNX graphs and packed voice/Unicode files are the sherpa-onnx conversion.
Conversion information: https://github.com/k2-fsa/sherpa-onnx/pull/3609
The upstream conversion archive also contains an MIT notice. The weights remain subject to OpenRAIL-M.
Disclose that the generated audio is machine generated and follow Attachment A.

sherpa-onnx and next-gen Kaldi copyright Xiaomi Corporation and the respective contributors.
Source and Windows build recipes: https://github.com/k2-fsa/sherpa-onnx/tree/v2.0.1
ONNX Runtime copyright Microsoft Corporation. Source: https://github.com/microsoft/onnxruntime/tree/v1.28.2
Windows archive recipe: https://github.com/csukuangfj/onnxruntime-libs/tree/v1.28.2
Windows MD library uses the separately identified and verified VC143 14.44.35211 x64 app-local runtime.
UTF-8 normalization: utf8proc v2.12.0 (MIT and Unicode data notice).
Kaldi-native-fbank v1.22.4, Kaldi decoder v0.3.0, Kaldifst v1.8.0 and OpenFst v1.8.5-2026-04-10 (Apache-2.0).
KISS FFT and Darts-clone retain their BSD notices; simple-sentencepiece v0.7 is Apache-2.0.
Eigen 3.4.0 source is available under MPL-2.0 at https://gitlab.com/libeigen/eigen/-/archive/3.4.0/eigen-3.4.0.tar.gz
Eigen source archive SHA-256: 8586084f71f9bde545ee7fa6d00288b264a2b7ac3607b974e54d13e7162c1c72
ONNX Runtime's Eigen source is the fixed 1d8b82b0740839c0de7f1242a3585e3390ff5f33 revision, also included here.
'@ | Set-Content -LiteralPath (Join-Path $licenseOut 'supertonic-native-NOTICES.txt') -Encoding utf8

    if (Test-Path -LiteralPath $prepared) {
        $paths = Get-ChildItem -LiteralPath $output -File -Recurse
        $actual = Get-ChildItem -LiteralPath $prepared -File -Recurse
        if ($paths.Count -ne $actual.Count) { throw 'Existing Supertonic staging has a different file count.' }
        foreach ($item in $paths) {
            $relative = [IO.Path]::GetRelativePath($output, $item.FullName)
            $previous = Join-Path $prepared $relative
            Confirm-LocalPath $previous
            if ((Get-FileHash -LiteralPath $previous).Hash -ne (Get-FileHash -LiteralPath $item.FullName).Hash) {
                throw "Existing Supertonic staging differs: $relative."
            }
        }
    } else { [IO.Directory]::Move($output, $prepared) }
    Write-Output $prepared
}
finally {
    try { if ($guard) { $guard.Dispose() } }
    finally {
        $env:TEMP, $env:TMP, $env:TMPDIR = $savedTemp
        if (Test-Path -LiteralPath $stage) {
            Confirm-LocalPath $stage
            Remove-Item -LiteralPath $stage -Recurse
        }
    }
}
