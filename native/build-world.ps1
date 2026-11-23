[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This native build uses the existing Windows x64 Visual C++ toolchain.' }

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$local = Join-Path $repository '.local'
$vendor = Join-Path $local 'vendor/world'
$output = Join-Path $local 'build/native'
$temporaryRoot = Join-Path $local 'tmp'
$temporary = Join-Path $temporaryRoot ('world-native-' + [Guid]::NewGuid().ToString('N'))
$revision = 'd625e7608ca23a870018f01e7c562ac683d9847f'
$archiveHash = 'a62635555243dfe4fb5a859a882fd92713137ec29dbc7a86f0a01f78362f2e60'
$archive = Join-Path $vendor "world-$revision.zip"
$source = Join-Path $vendor "World-$revision"
$vsRoot = 'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools'
$vsDev = Join-Path $vsRoot 'Common7/Tools/VsDevCmd.bat'

function Assert-Contained([string]$Path, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) { throw 'Empty native output boundary.' }
    $full = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $full.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Native output escaped its workspace boundary.'
    }
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Native build paths cannot traverse a link.'
        }
    }
    return $full
}
function Assert-CmdPath([string]$Path) {
    if ($Path -match '["%!?&|<>^\r\n]') { throw 'Native build path contains unsupported command characters.' }
}

$savedEnvironment = @{}
$lock = $null
$process = $null
$nativeErrorGuard = $null
try {
    foreach ($path in @($vendor, $output, $temporary)) { $null = Assert-Contained $path $local; New-Item -ItemType Directory -Path $path -Force | Out-Null }
    New-Item -ItemType Directory -Path (Join-Path $temporary 'tmp'), (Join-Path $temporary 'obj') | Out-Null
    foreach ($name in @('TEMP', 'TMP', 'TMPDIR', 'CL', '_CL_', 'LINK', '_LINK_')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $(if ($name -in @('TEMP', 'TMP', 'TMPDIR')) { Join-Path $temporary 'tmp' } else { $null }), 'Process')
    }
    . (Join-Path $PSScriptRoot 'process-error-mode.ps1')
    $nativeErrorGuard = Enter-NativeProcessErrorMode
    $lock = [IO.File]::Open((Join-Path $output 'build.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    if (-not (Test-Path -LiteralPath $vsDev)) { throw 'Existing Visual Studio 2019 BuildTools with C++ is required; no compiler is installed automatically.' }
    if (-not (Test-Path -LiteralPath $archive)) {
        $download = Join-Path $temporary 'world.zip'
        Invoke-WebRequest -Uri "https://codeload.github.com/mmorise/World/zip/$revision" -OutFile $download
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $archiveHash) { throw 'WORLD archive hash mismatch.' }
        Move-Item -LiteralPath $download -Destination $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) { throw 'WORLD archive hash mismatch.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            if (-not $entry.FullName.StartsWith("World-$revision/", [StringComparison]::Ordinal) -or
                $entry.FullName.Contains(':') -or $entry.FullName.Contains('\') -or ($entry.FullName.Split('/') -contains '..')) {
                throw 'WORLD archive entry escaped its pinned root.'
            }
        }
        if (-not (Test-Path -LiteralPath $source)) { Expand-Archive -LiteralPath $archive -DestinationPath $vendor }
        # Validate cached sources against the verified archive, including all headers.
        foreach ($entry in $zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') -and ($_.FullName.Contains('/src/') -or $_.FullName.EndsWith('/LICENSE.txt')) }) {
            $path = Assert-Contained (Join-Path $vendor $entry.FullName) $vendor
            $stream = $entry.Open()
            try { $expectedHash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
            finally { $stream.Dispose() }
            if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expectedHash) {
                throw 'Cached WORLD source differs from the verified archive.'
            }
        }
    } finally { $zip.Dispose() }

    $compiled = @('cheaptrick', 'common', 'd4c', 'dio', 'stonemask', 'fft', 'harvest', 'matlabfunctions', 'synthesis')
    $files = @($compiled | ForEach-Object { Join-Path $source "src/$_.cpp" }) + (Join-Path $PSScriptRoot 'world-bridge.cpp')
    foreach ($path in @($vsDev, $source, $temporary) + $files) { Assert-CmdPath $path }
    $commands = [Collections.Generic.List[string]]::new()
    $commands.Add('@echo off')
    $commands.Add(('call "{0}" -no_logo -host_arch=x64 -arch=x64' -f $vsDev))
    $commands.Add('if errorlevel 1 exit /b 1')
    $objects = @()
    foreach ($file in $files) {
        $object = Join-Path $temporary ('obj/' + [IO.Path]::GetFileNameWithoutExtension($file) + '.obj')
        $objects += '"' + $object + '"'
        $commands.Add(('cl.exe /nologo /c /O2 /MT /EHsc /std:c++17 /W3 /fp:precise /guard:cf /D_CRT_SECURE_NO_WARNINGS /I"{0}" /Fo"{1}" "{2}"' -f (Join-Path $source 'src'), $object, $file))
        $commands.Add('if errorlevel 1 exit /b 1')
    }
    $dll = Join-Path $temporary 'tanayomi-world.dll'
    $commands.Add(('link.exe /NOLOGO /DLL /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /GUARD:CF /OPT:REF /OPT:ICF /INCREMENTAL:NO /OUT:"{0}" /IMPLIB:"{1}" {2}' -f $dll, (Join-Path $temporary 'tanayomi-world.lib'), ($objects -join ' ')))
    $commands.Add('if errorlevel 1 exit /b 1')
    $commands.Add(('dumpbin.exe /IMPORTS /EXPORTS "{0}" > "{1}"' -f $dll, (Join-Path $temporary 'pe.txt')))
    $commands.Add('if errorlevel 1 exit /b 1')
    $commands.Add('exit /b 0')
    $batch = Join-Path $temporary 'build.cmd'
    [IO.File]::WriteAllLines($batch, $commands, [Text.Encoding]::ASCII)
    $stdout = Join-Path $temporary 'compiler.stdout.txt'
    $stderr = Join-Path $temporary 'compiler.stderr.txt'
    $cmd = Join-Path $env:SystemRoot 'System32/cmd.exe'
    $process = Start-Process -FilePath $cmd -ArgumentList ('/d /v:off /s /c ""{0}""' -f $batch) -WorkingDirectory $temporary -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit(180000)) { $process.Kill($true); $process.WaitForExit(); throw 'Native compiler exceeded its 180 second deadline.' }
    if ($process.ExitCode -ne 0) { throw ('Native build failed: ' + [IO.File]::ReadAllText($stdout) + [IO.File]::ReadAllText($stderr)) }
    $pe = [IO.File]::ReadAllText((Join-Path $temporary 'pe.txt'))
    $imports = @([regex]::Matches($pe, '(?im)^\s+([a-z0-9_.-]+\.dll)\s*$') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if ($imports.Count -eq 0 -or @($imports | Where-Object { $_ -notmatch '^(KERNEL32\.dll|api-ms-win-core-[a-z0-9_.-]+\.dll)$' }).Count -ne 0) {
        throw ('Unexpected native import; review before use: ' + ($imports -join ', '))
    }
    $exports = @([regex]::Matches($pe, '(?m)^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(tw_world_[a-z_]+)\s*$') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if (($exports -join ',') -ne 'tw_world_abi_version,tw_world_analyze,tw_world_analyze_dio,tw_world_analyze_dio_f0,tw_world_analyze_dio_parallel,tw_world_analyze_f0,tw_world_analyze_parallel,tw_world_layout,tw_world_synthesize') { throw 'Native ABI export set mismatch.' }
    $destination = Assert-Contained (Join-Path $output 'tanayomi-world.dll') $output
    Move-Item -LiteralPath $dll -Destination $destination -Force
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{ revision = $revision; archiveSha256 = $archiveHash; dllSha256 = $hash; compiler = $vsRoot; runtime = 'MT'; architecture = 'x64'; imports = $imports; exports = $exports } |
        ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'world-build.json') -Encoding utf8
    Write-Output "WORLD DLL: $destination"
    Write-Output "SHA256: $hash"
    Write-Output ('PE imports: ' + ($imports -join ', '))
} finally {
    if ($null -ne $nativeErrorGuard) { $nativeErrorGuard.Dispose() }
    if ($process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    if ($lock) { $lock.Dispose() }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if (Test-Path -LiteralPath $temporary) {
        $checkedTemporary = Assert-Contained $temporary $temporaryRoot
        if ([IO.Path]::GetFileName($checkedTemporary) -notmatch '^world-native-[0-9a-f]{32}$') { throw 'Native cleanup refused an unowned directory.' }
        if (@(Get-ChildItem -LiteralPath $checkedTemporary -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -ne 0) { throw 'Native cleanup refused a linked entry.' }
        Remove-Item -LiteralPath $checkedTemporary -Recurse -Force
    }
}
