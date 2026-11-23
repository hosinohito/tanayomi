#Requires -Version 7.4
[CmdletBinding()]
param([switch] $Offline)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'VOICEVOX preparation requires Windows x64 PowerShell.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vendor = Join-Path $repo '.local/vendor/voicevox'
$downloads = Join-Path $vendor 'downloads'
$package = Join-Path $vendor 'package'
$temporaryRoot = Join-Path $repo '.local/tmp'

$models = [ordered]@{
    '0.vvm' = '917E592CAE92A02FB7668BD9E168DF4E39C8AB2F5302C2BE71906C70EC4E2DF2'
    '1.vvm' = '8DF20815BE9A84A4B4723B9E778D2EF6A4DF277E4872FE9EFE29676A60E02774'
    '2.vvm' = '4C97FF029895A819CEE3AD431A77284D6AF7E097AC42FAA5AF1C87D2BC5A9B5C'
    '3.vvm' = 'BE05BA9A196BC5ACC298DAA2C85DE1188F0795B46422581437E2B07071963E03'
    '4.vvm' = 'DA5AAAD87D5B7EA5B346DEF2E610CC63E64A90088BB0FC2D27FB63A622113953'
    '5.vvm' = '487856083C347E298B0B9BDABFC278DC14240C9365A7A0E4870AEA965CE7B4D1'
    '8.vvm' = 'BDE9CB507F62277A176CC2C7182DB686294028D2ADFF4D3A49C39DD1498F2FED'
    '9.vvm' = '5145551D31ED4FAB14D7921AAFDD2DB5C9E90DA40D5B59F9418EE435C400C2FA'
    '10.vvm' = 'B5F8A97C0B6BE2B8EAA6F2C1AF88B2E06F9072BB82DDDD4E1B3A05E9DE0C04D4'
    '11.vvm' = '46C209914812C13B1717331701D76491708448F90C7F8E87B5B286E5CA91341E'
    '12.vvm' = '5961B7F7EECEC1BE9EB38C0868898483D9582E490E9D9631DCA9EA5018BD0E70'
    '13.vvm' = 'F083DD1C3B67091FBBF1839C97D0CF817DBFAE40DDC4FA8E88236CE6E2ABE55D'
    '14.vvm' = 'BAAB280D80769F1D8FD5AEAD07137C5ED1B5E61ACC5F0E26DFF53439E907D085'
    '15.vvm' = '1CAA2A42417F6B9BBB1A3C8EC1370724D7E153862B4D12F8C317C4E37CEA786F'
    '18.vvm' = 'FBD69EBF1775E5E44E39DB8580584601822686D0ABD0383DEE01DD3A0A5FFC9D'
    '19.vvm' = 'D6C8096606D462A26C7E7B093E0EAB4452E52EFD42AF29E9CDC989E022EA9F0E'
    '21.vvm' = 'DA1172DA295F89E9733CDE36468365F573133E1903A4FE9CD47B2BA4BDADBEAC'
    '22.vvm' = '4B7F2AB58A14F163C95B4896CCF5116AA8BBF4ADAF2B18632ACBB279CCA357C5'
    '23.vvm' = 'A5862259A0A3173A15FE83B56817F6E66386B5059864889D69B0A1A0B43F281F'
    '24.vvm' = '7A80EAC3F27D33E45AC1C23534F9E5C5967A29F4D5EC28B898FC453871F1C341'
    'n0.vvm' = 'E91E2E6ED5CFA6940FF55B61234BA89C631FCE3D0146C23685552E7F5D2FE437'
}
$vvmTerms = [ordered]@{
    'TERMS.txt' = '57FC18AFEEBC8A08C303B2B4E3F05BF6453A37C482D17C04EC3FBC674E5FC5F4'
    'README.txt' = '12A64121DB0815FC5E6EA8CDF13CD3ED052B7C7D7A170A70A2C0ECED2FC63573'
}
$nativeHashes = [ordered]@{
    'voicevox_core.dll' = 'EC9E730AE9E1AE897AECF78BF380B9D9C34120F8B4BE2D3844F52C9EFDE5BC91'
    'voicevox_onnxruntime.dll' = '53BC7F528EC804FE4E80A57ED03BCECB580B1ED6F8EAE4B08613487E9135453E'
    'msvcp140.dll' = '0F885B509A685D2BBFA652FED26B5FB31D88FBDAB0A978C641D1C7B8AA460AA9'
    'msvcp140_1.dll' = 'BFAD5AEF4C63A669E3C140655CDFDF395B6C979B400A447BD5DCB65ED8826C3D'
    'vcruntime140.dll' = 'D5E4D9A3E835FA679450145D6A7D94E36573A509317111904D9B3712C30D9066'
    'vcruntime140_1.dll' = '1F2D41C4AA5DB0BC33EBF7B66D72943A817D7CE6CBE880502A9403823633093F'
}
$core = 'voicevox_core-windows-x64-0.17.1'
$runtime = 'voicevox_onnxruntime-win-x64-1.23.2'
$crt = 'Microsoft.VC.14.44.17.14.CRT.Redist.X64.base.vsix'
$crtHash = '4AAF54DB0BFC9435F7C3660E1A00237A4B556042BFEEA64BDE44C2E0194E6EE5'
$vvmRelease = 'https://github.com/VOICEVOX/voicevox_vvm/releases/download/0.16.4'
$eigenRevision = '1d8b82b0740839c0de7f1242a3585e3390ff5f33'
$eigenName = "eigen-$eigenRevision.zip"
$eigenUrl = "https://github.com/eigen-mirror/eigen/archive/$eigenRevision/$eigenName"
$eigenHash = '6A60D76351F97132669DAEEB721D6BF14B008101883AD2D687A3201C5C461EB0'

function Assert-NoLinks([string] $Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'VOICEVOX requires an absolute path.' }
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "VOICEVOX path traverses a link: $current"
        }
    }
}

function Assert-Hash([string] $Path, [string] $Expected) {
    Assert-NoLinks $Path
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash -ne $Expected) {
        throw "VOICEVOX SHA-256 mismatch: $([IO.Path]::GetFileName($Path))"
    }
}

function Get-Source([string] $Directory, [string] $Name, [string] $Url, [string] $Hash) {
    $destination = Join-Path $Directory $Name
    Assert-NoLinks $destination
    if (-not [IO.File]::Exists($destination)) {
        if ($Offline) { throw "VOICEVOX offline source is missing: $Name" }
        $pending = Join-Path $temporaryRoot ('voicevox-download-' + [guid]::NewGuid().ToString('N'))
        try {
            Invoke-WebRequest $Url -OutFile $pending -TimeoutSec 240 -MaximumRetryCount 2 -RetryIntervalSec 3
            Assert-Hash $pending $Hash
            [IO.File]::Move($pending, $destination)
        }
        finally {
            if ([IO.File]::Exists($pending)) { [IO.File]::Delete($pending) }
        }
    }
    Assert-Hash $destination $Hash
    return $destination
}

function Copy-ZipMembers([string] $Archive, [hashtable] $Members, [string] $Destination) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $Members.GetEnumerator()) {
            $matches = @($zip.Entries | Where-Object { $_.FullName -ceq $entry.Key })
            if ($matches.Count -ne 1 -or $matches[0].Length -lt 1 -or $matches[0].Length -gt 32MB) {
                throw "VOICEVOX archive member missing or invalid: $($entry.Key)"
            }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($matches[0], (Join-Path $Destination $entry.Value))
        }
    }
    finally { $zip.Dispose() }
}

function Copy-RuntimeMembers([string] $Archive, [string] $Destination) {
    $members = @{
        "$runtime/lib/voicevox_onnxruntime.dll" = 'voicevox_onnxruntime.dll'
        "$runtime/TERMS.txt" = 'licenses/VOICEVOX-ONNX-Runtime-TERMS.txt'
        "$runtime/third-party-notices.html" = 'licenses/VOICEVOX-ONNX-Runtime-third-party-notices.html'
        "$runtime/GIT_COMMIT_ID" = 'licenses/VOICEVOX-ONNX-Runtime-GIT_COMMIT_ID.txt'
    }
    $file = [IO.File]::OpenRead($Archive)
    try {
        $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress)
        try {
            $tar = [System.Formats.Tar.TarReader]::new($gzip)
            try {
                while ($entry = $tar.GetNextEntry()) {
                    if (-not $members.ContainsKey($entry.Name)) { continue }
                    if ($entry.EntryType -notin @('RegularFile','V7RegularFile') -or $entry.Length -lt 1 -or $entry.Length -gt 32MB) {
                        throw "VOICEVOX runtime archive member is invalid: $($entry.Name)"
                    }
                    $out = [IO.File]::Open((Join-Path $Destination $members[$entry.Name]), [IO.FileMode]::CreateNew)
                    try { $entry.DataStream.CopyTo($out) } finally { $out.Dispose() }
                }
            }
            finally { $tar.Dispose() }
        }
        finally { $gzip.Dispose() }
    }
    finally { $file.Dispose() }
    foreach ($name in $members.Values) {
        if (-not [IO.File]::Exists((Join-Path $Destination $name))) { throw "VOICEVOX runtime file missing: $name" }
    }
}

function Copy-ComplianceFiles([string] $Destination) {
    $licenseDirectory = Join-Path $Destination 'licenses'
    Assert-NoLinks $licenseDirectory
    $eigenCopy = Join-Path $licenseDirectory 'Eigen-source.zip'
    $eigenLicense = Join-Path $licenseDirectory 'Eigen-MPL-2.0.txt'
    $microsoftCopy = Join-Path $licenseDirectory 'Microsoft-VC-Runtime-LICENSE.txt'
    foreach ($name in @($eigenCopy, $eigenLicense, $microsoftCopy)) { Assert-NoLinks $name }
    if (-not [IO.File]::Exists($eigenCopy)) {
        $existingEigen = Join-Path $repo '.local/build/native-openvoice/eigen-source.zip'
        Assert-NoLinks $existingEigen
        $source = if ([IO.File]::Exists($existingEigen)) { $existingEigen }
            else { Get-Source $vendor $eigenName $eigenUrl $eigenHash }
        Assert-Hash $source $eigenHash
        [IO.File]::Copy($source, $eigenCopy)
    }
    Assert-Hash $eigenCopy $eigenHash
    if (-not [IO.File]::Exists($eigenLicense)) {
        Copy-ZipMembers $eigenCopy @{ "eigen-$eigenRevision/COPYING.MPL2" = 'Eigen-MPL-2.0.txt' } $licenseDirectory
    }
    Assert-Hash $eigenLicense '66A3107D5AD6A058AAB753EAAC2047CCB2ED0E39465DD0FE5844DA3E300D5172'
    $microsoftLicenseHash = '38C113F791FDD03FA9316F0F743201CB54A1C8C9B683B615A906EA2FEA7C8B6E'
    if (-not [IO.File]::Exists($microsoftCopy)) {
        $source = Join-Path $repo 'assets/microsoft-vc-runtime-LICENSE.txt'
        Assert-Hash $source $microsoftLicenseHash
        [IO.File]::Copy($source, $microsoftCopy)
    }
    Assert-Hash $microsoftCopy $microsoftLicenseHash
    $sourceNotice = Join-Path $licenseDirectory 'Eigen-source-NOTICE.txt'
    Assert-NoLinks $sourceNotice
    if (-not [IO.File]::Exists($sourceNotice)) {
        [IO.File]::WriteAllLines($sourceNotice, @(
            'The bundled Eigen-source.zip is the full unmodified Eigen source selected by public ONNX Runtime v1.23.2.',
            'See Eigen-MPL-2.0.txt and the copyrights and license texts in the source archive.',
            "Eigen source: $eigenUrl",
            'ONNX Runtime dependency lock: https://github.com/microsoft/onnxruntime/blob/v1.23.2/cmake/deps.txt',
            'The VOICEVOX production fork revision is recorded in VOICEVOX-ONNX-Runtime-GIT_COMMIT_ID.txt.',
            'The private fork source and its exact build inputs have not been independently verified.'
        ))
    }
}

function Write-PackageManifest([string] $Destination) {
    $manifest = [ordered]@{
        coreVersion = '0.17.1'
        runtimeVersion = '1.23.2'
        modelVersion = '0.16.4'
        crtVersion = '14.44.35211'
        dlls = $nativeHashes
        models = $models
    }
    $path = Join-Path $Destination 'voicevox-assets.json'
    Assert-NoLinks $path
    $json = $manifest | ConvertTo-Json -Depth 4
    if ([IO.File]::Exists($path) -and [IO.File]::ReadAllText($path) -ceq $json) { return }
    $pending = Join-Path $temporaryRoot ('voicevox-manifest-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($pending, $json)
        [IO.File]::Move($pending, $path, $true)
    }
    finally {
        if ([IO.File]::Exists($pending)) { [IO.File]::Delete($pending) }
    }
}

Assert-NoLinks $vendor
Assert-NoLinks $temporaryRoot
Assert-NoLinks $package
[void][IO.Directory]::CreateDirectory($temporaryRoot)
if ([IO.Directory]::Exists($package)) {
    foreach ($item in $nativeHashes.GetEnumerator()) { Assert-Hash (Join-Path $package $item.Key) $item.Value }
    foreach ($item in $vvmTerms.GetEnumerator()) { Assert-Hash (Join-Path $package ('licenses/VOICEVOX-VVM-' + $item.Key)) $item.Value }
    foreach ($name in @('voicevox-assets.json','licenses/VOICEVOX-CORE-LICENSE.txt','licenses/VOICEVOX-ONNX-Runtime-TERMS.txt',
        'licenses/VOICEVOX-ONNX-Runtime-third-party-notices.html','licenses/Microsoft-VC-Redist-NOTICE.txt')) {
        $path = Join-Path $package $name
        Assert-NoLinks $path
        if (-not [IO.File]::Exists($path)) { throw "Existing VOICEVOX package lacks $name" }
    }
    foreach ($item in $models.GetEnumerator()) {
        $destination = Join-Path $package ('models/' + $item.Key)
        Assert-NoLinks $destination
        if (-not [IO.File]::Exists($destination)) {
            Assert-NoLinks $downloads
            [void][IO.Directory]::CreateDirectory($downloads)
            $source = Get-Source $downloads $item.Key "$vvmRelease/$($item.Key)" $item.Value
            [IO.File]::Move($source, $destination)
        }
        Assert-Hash $destination $item.Value
    }
    Copy-ComplianceFiles $package
    Write-PackageManifest $package
    Write-Output $package
    return
}

[void][IO.Directory]::CreateDirectory($vendor)
Assert-NoLinks $downloads
[void][IO.Directory]::CreateDirectory($downloads)
$stage = Join-Path $temporaryRoot ('voicevox-package-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
try {
    [void][IO.Directory]::CreateDirectory((Join-Path $stage 'models'))
    [void][IO.Directory]::CreateDirectory((Join-Path $stage 'licenses'))
    $coreArchive = Get-Source $vendor "$core.zip" "https://github.com/VOICEVOX/voicevox_core/releases/download/0.17.1/$core.zip" '7E6A4F6D39C0A083E694A9191289DF3B09725ABFA676027228A72C01A2AE565B'
    $runtimeArchive = Get-Source $vendor "$runtime.tgz" "https://github.com/VOICEVOX/onnxruntime-builder/releases/download/voicevox_onnxruntime-1.23.2/$runtime.tgz" '1BF9635D1C0FFD4E7345E0F628CECBABDE65782C73C5051D52A52E7EDF7CE11C'
    $crtArchive = Get-Source $vendor $crt "https://download.visualstudio.microsoft.com/download/pr/45d3b8dd-bced-4b37-9974-142f748d710c/$($crtHash.ToLowerInvariant())/$crt" $crtHash
    Copy-ZipMembers $coreArchive @{
        "$core/lib/voicevox_core.dll" = 'voicevox_core.dll'
        "$core/LICENSE" = 'licenses/VOICEVOX-CORE-LICENSE.txt'
    } $stage
    Copy-RuntimeMembers $runtimeArchive $stage
    $crtMembers = @{}
    foreach ($name in $nativeHashes.Keys) {
        if ($name.StartsWith('voicevox_')) { continue }
        $crtMembers["Contents/VC/Redist/MSVC/14.44.35112/x64/Microsoft.VC143.CRT/$name"] = $name
    }
    Copy-ZipMembers $crtArchive $crtMembers $stage
    foreach ($item in $nativeHashes.GetEnumerator()) { Assert-Hash (Join-Path $stage $item.Key) $item.Value }

    foreach ($item in $models.GetEnumerator()) {
        $source = Get-Source $downloads $item.Key "$vvmRelease/$($item.Key)" $item.Value
        [IO.File]::Copy($source, (Join-Path $stage ('models/' + $item.Key)))
    }
    foreach ($item in $vvmTerms.GetEnumerator()) {
        $source = Get-Source $downloads $item.Key "$vvmRelease/$($item.Key)" $item.Value
        [IO.File]::Copy($source, (Join-Path $stage ('licenses/VOICEVOX-VVM-' + $item.Key)))
    }

    $crtNotice = @'
Microsoft Visual C++ 2022 Runtime (VC143 x64), version 14.44.35211.
Copyright Microsoft Corporation. All rights reserved.
App-local files from Microsoft.VC.14.44.17.14.CRT.Redist.X64.base.vsix:
msvcp140.dll, msvcp140_1.dll, vcruntime140.dll, vcruntime140_1.dll.
Visual Studio 2022 redistributable code list:
https://learn.microsoft.com/visualstudio/releases/2022/redistribution
Redistribution conditions:
https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files
'@
    [IO.File]::WriteAllText((Join-Path $stage 'licenses/Microsoft-VC-Redist-NOTICE.txt'), $crtNotice)
    Copy-ComplianceFiles $stage
    Write-PackageManifest $stage

    Assert-NoLinks $package
    [IO.Directory]::Move($stage, $package)
    # Completed package holds the model originals; remove only verified redundant staging files.
    foreach ($list in @($models, $vvmTerms)) {
        foreach ($item in $list.GetEnumerator()) {
            $cached = Join-Path $downloads $item.Key
            Assert-Hash $cached $item.Value
            [IO.File]::Delete($cached)
        }
    }
    Write-Output $package
}
finally {
    if ([IO.Directory]::Exists($stage)) {
        Assert-NoLinks $stage
        $resolved = (Resolve-Path -LiteralPath $stage).ProviderPath
        if ($resolved -ne $stage -or -not $resolved.StartsWith($temporaryRoot + '\voicevox-package-', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Cannot verify the VOICEVOX staging directory for cleanup.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
