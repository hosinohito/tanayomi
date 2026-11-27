[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildPython,
    [switch]$AuditOnly,
    [string]$JvnvDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'Aivis packaging requires Windows x64.' }
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$local = Join-Path $repository '.local'
$vendor = Join-Path $local 'vendor/aivis'
$inputPython = (Resolve-Path -LiteralPath $BuildPython).Path
if (-not $inputPython.StartsWith($local + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BuildPython must be the repository development Python under .local.'
}
foreach ($path in @($repository, $local, (Join-Path $local 'vendor'), $vendor, (Join-Path $local 'tmp'))) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Aivis staging path traverses a link: $path"
    }
}

$id = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $vendor "stage-$id"
$temporary = Join-Path $local "tmp/aivis-$id"
[void][IO.Directory]::CreateDirectory($stage)
[void][IO.Directory]::CreateDirectory($temporary)
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousTmpDir = $env:TMPDIR
$mode = $null

function Invoke-AivisBuildPython([string[]]$Arguments) {
    & $inputPython -B @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Aivis packaging failed with exit code $LASTEXITCODE." }
}

try {
    $env:TEMP = $temporary
    $env:TMP = $temporary
    $env:TMPDIR = $temporary
    . (Join-Path $PSScriptRoot 'process-error-mode.ps1')
    $mode = Enter-NativeProcessErrorMode

    $helper = Join-Path $PSScriptRoot 'aivis/package.py'
    $lockedWheels = Join-Path $PSScriptRoot 'aivis/wheels.json'
    $stagedWheels = Join-Path $vendor 'wheels.json'
    if ((Get-FileHash -LiteralPath $lockedWheels).Hash -ne (Get-FileHash -LiteralPath $stagedWheels).Hash) {
        throw 'The prepared Aivis wheel inventory differs from the source lock.'
    }
    $inventory = Join-Path $stage 'archive-audit.json'
    Invoke-AivisBuildPython @($helper, 'inspect', $vendor, $inventory)
    if ($AuditOnly) {
        $destination = Join-Path $vendor "archive-audit-$id.json"
        Move-Item -LiteralPath $inventory -Destination $destination
        Write-Output $destination
        return
    }

    $crtName = 'Microsoft.VC.14.44.17.14.CRT.Redist.X64.base.vsix'
    $crt = Join-Path $vendor $crtName
    if (-not (Test-Path -LiteralPath $crt -PathType Leaf)) {
        $shared = Join-Path $local "vendor/voicevox/$crtName"
        if ((Get-FileHash -LiteralPath $shared).Hash -ne '4aaf54db0bfc9435f7c3660e1a00237a4b556042bfeea64bde44c2e0194e6ee5') {
            throw 'Unexpected Microsoft VC redistributable input.'
        }
        Copy-Item -LiteralPath $shared -Destination $crt
    }

    Invoke-AivisBuildPython @($helper, 'assemble', $vendor, $stage)
    $licenses = [ordered]@{
        'ACML-1.0.md' = '72ac22ef56c20f9d9ca8467ca9bdcc5e06e5fba5270812a9ff1494b00ae6d02a'
        'ACML-source-MIT.txt' = '48bddf7ed3702db2b1fedb26eadefc10c329c393d9d8b7477597669244716ff0'
        'CC-BY-SA-4.0.txt' = '28a9529c7d0bb4dc51f4bf5c116a3d16ef247a052f7591466768ddf563fd1cf5'
        'CC-BY-SA-3.0.txt' = '3f941b3b89cf7b8370ceB83cc76d2120d471b58735d8ca60238a751a48d7f72f'
        'FlatBuffers-Apache-2.0.txt' = 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'
        'Loguru-MIT.txt' = 'b35d026cc7aca9d5859a02eb87ddf7a386a24c986838651bd1f283f94e003327'
        'SudachiPy-Apache-2.0.txt' = 'c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4'
        'Tokenizers-Apache-2.0.txt' = 'c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4'
        'CMUdict-BSD-2-Clause.txt' = 'bd4ce8e44170a5f9f481310ca85c51de3c4f851a65e679b40e603b143bd3542a'
        'GPL-3.0.txt' = '3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986'
    }
    foreach ($name in $licenses.Keys) {
        $source = Join-Path $vendor "licenses/$name"
        if ((Get-FileHash -LiteralPath $source).Hash -ne $licenses[$name]) { throw "Unexpected Aivis license $name." }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage 'licenses')
    }
    $sourceDirectory = Join-Path $stage 'licenses/sources'
    $projectSource = Join-Path $sourceDirectory 'Tanayomi/native'
    [void][IO.Directory]::CreateDirectory((Join-Path $projectSource 'aivis'))
    $correspondingSources = [ordered]@{
        'soxr-0.5.0.post1.tar.gz' = '7092b9f3e8a416044e1fa138c8172520757179763b85dc53aa9504f4813cff73'
        'num2words-0.5.14.tar.gz' = 'b066ec18e56b6616a3b38086b5747daafbaa8868b226a36127e0451c0cf379c6'
    }
    foreach ($name in $correspondingSources.Keys) {
        $source = Join-Path $vendor "sources/$name"
        if ((Get-FileHash -LiteralPath $source).Hash -ne $correspondingSources[$name]) { throw "Unexpected Aivis corresponding source $name." }
        Copy-Item -LiteralPath $source -Destination $sourceDirectory
    }
    $sharedNotices = Join-Path $vendor 'licenses'
    $sharedLicenses = [ordered]@{
        'Eigen-source.zip' = '6a60d76351f97132669daeeB721d6bf14b008101883ad2d687a3201c5c461eb0'
        'Eigen-MPL-2.0.txt' = '66a3107d5ad6a058aab753eaac2047ccb2ed0e39465dd0fe5844da3e300d5172'
        'Microsoft-VC-Runtime-LICENSE.txt' = '38c113f791fdd03fa9316f0f743201cb54a1c8c9b683b615a906ea2fea7c8b6e'
    }
    foreach ($name in $sharedLicenses.Keys) {
        $source = Join-Path $sharedNotices $name
        if ((Get-FileHash -LiteralPath $source).Hash -ne $sharedLicenses[$name]) { throw "Unexpected Aivis dependency notice $name." }
        $target = if ($name.EndsWith('.zip')) { $sourceDirectory } else { Join-Path $stage 'licenses' }
        Copy-Item -LiteralPath $source -Destination $target
    }
    foreach ($name in @('prepare-aivis.ps1', 'process-error-mode.ps1', 'aivis_worker.py', 'acquire-aivis-inputs.py')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $projectSource
    }
    foreach ($name in @('package.py', 'locked_wheels.py', 'model_metadata.py', 'verify_worker.py', 'voices.json', 'wheels.json', 'inputs.json', 'NOTICE.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "aivis/$name") -Destination (Join-Path $projectSource 'aivis')
    }
    $template = Join-Path $PSScriptRoot 'aivis/voices.json'
    $metadata = Join-Path $PSScriptRoot 'aivis/model_metadata.py'
    Invoke-AivisBuildPython @($metadata, (Join-Path $vendor 'models'), $template, $stage, (Join-Path $stage 'licenses/ACML-1.0.md'))
    if ($JvnvDirectory) {
        $converted = (Resolve-Path -LiteralPath $JvnvDirectory).Path
        $jvnvRoot = Join-Path $vendor 'jvnv'
        if ([IO.Path]::GetDirectoryName($converted) -ne $jvnvRoot -or
            ((Get-Item -LiteralPath $jvnvRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            ((Get-Item -LiteralPath $converted).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'JVNV conversion directory must be an ordinary directory under .local/vendor/aivis/jvnv.'
        }
        Invoke-AivisBuildPython @((Join-Path $PSScriptRoot 'aivis/convert_jvnv.py'), 'merge', $stage, $converted)
        foreach ($name in @('convert_jvnv.py', 'prepare-jvnv.ps1', 'jvnv-models.json', 'jvnv-conversion-wheels.json')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot "aivis/$name") -Destination (Join-Path $projectSource 'aivis')
        }
    }
    $manifest = Get-Content -LiteralPath $template -Raw -Encoding utf8 | ConvertFrom-Json
    $bert = Join-Path $stage 'bert'
    [void][IO.Directory]::CreateDirectory($bert)
    foreach ($asset in $manifest.bert.PSObject.Properties) {
        $source = Join-Path $vendor "bert/$($asset.Name)"
        if ((Get-FileHash -LiteralPath $source).Hash -ne $asset.Value) { throw "Unexpected BERT asset $($asset.Name)." }
        Copy-Item -LiteralPath $source -Destination $bert
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'aivis_worker.py') -Destination (Join-Path $stage 'worker.py')
    Copy-Item -LiteralPath $lockedWheels -Destination (Join-Path $stage 'wheels.json')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'aivis/NOTICE.md') -Destination (Join-Path $stage 'NOTICE.md')
    $destination = Join-Path $vendor "package-$id"
    Move-Item -LiteralPath $stage -Destination $destination
    Write-Output $destination
}
finally {
    if ($null -ne $mode) { $mode.Dispose() }
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    $env:TMPDIR = $previousTmpDir
    foreach ($entry in @(@($stage, $vendor), @($temporary, (Join-Path $local 'tmp')))) {
        $path = $entry[0]
        $parent = $entry[1]
        if (Test-Path -LiteralPath $path) {
            $resolved = (Resolve-Path -LiteralPath $path).Path
            if ($resolved -ne $path -or -not $resolved.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Aivis staging cleanup refused an unexpected path: $path"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
