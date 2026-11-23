[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AivisPackage,
    [Parameter(Mandatory)][string]$BuildPython
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$local = Join-Path $repository '.local'
$vendor = Join-Path $local 'vendor/aivis'
$package = (Resolve-Path -LiteralPath $AivisPackage).Path
$developerPython = (Resolve-Path -LiteralPath $BuildPython).Path
if (-not $package.StartsWith($vendor + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not $developerPython.StartsWith($local + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'JVNV conversion requires a repository-owned Aivis package and development Python.'
}
$id = [Guid]::NewGuid().ToString('N')
$temporary = Join-Path $local "tmp/jvnv-$id"
$output = Join-Path $vendor "jvnv/converting-$id"
foreach ($path in @($repository, $local, (Join-Path $local 'tmp'), (Join-Path $local 'vendor'), $vendor, (Join-Path $vendor 'jvnv'), $package)) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "JVNV path traverses a link: $path" }
}
[void][IO.Directory]::CreateDirectory($temporary)
[void][IO.Directory]::CreateDirectory($output)
$environment = @{
    TEMP = $temporary; TMP = $temporary; TMPDIR = $temporary; HF_HOME = $temporary
    HF_HUB_OFFLINE = '1'; TRANSFORMERS_OFFLINE = '1'; HF_HUB_DISABLE_TELEMETRY = '1'; NUMBA_CACHE_DIR = $temporary
}
$previous = @{}
$mode = $null
try {
    foreach ($name in $environment.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    . (Join-Path $PSScriptRoot '../process-error-mode.ps1')
    $mode = Enter-NativeProcessErrorMode
    $converter = Join-Path $PSScriptRoot 'convert_jvnv.py'
    & $developerPython -B $converter stage $vendor $temporary
    if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare the JVNV conversion dependencies.' }
    $python = Join-Path $package 'python/python.exe'
    Push-Location -LiteralPath $temporary
    try {
        foreach ($voice in @('jvnv-F1-jp', 'jvnv-F2-jp', 'jvnv-M1-jp', 'jvnv-M2-jp')) {
            & $python -B $converter export $package $vendor $temporary $voice $output
            if ($LASTEXITCODE -ne 0) { throw "JVNV conversion failed for $voice." }
        }
    } finally { Pop-Location }
    $destination = Join-Path $vendor "jvnv/converted-$id"
    Move-Item -LiteralPath $output -Destination $destination
    Write-Output $destination
}
finally {
    if ($null -ne $mode) { $mode.Dispose() }
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    foreach ($entry in @(@($temporary, (Join-Path $local 'tmp')), @($output, (Join-Path $vendor 'jvnv')))) {
        $path = $entry[0]
        $parent = $entry[1]
        if (Test-Path -LiteralPath $path) {
            $resolved = (Resolve-Path -LiteralPath $path).Path
            if ($resolved -ne $path -or -not $resolved.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "JVNV cleanup refused an unexpected path: $path"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
