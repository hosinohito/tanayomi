#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Rid,
    [switch]$PrepareOnly,
    [string]$CompilerExecutable,
    [string]$NmExecutable,
    [string]$DependencyInspectorExecutable
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$local = Join-Path $repository '.local'
$revision = 'd625e7608ca23a870018f01e7c562ac683d9847f'
$archiveHash = 'a62635555243dfe4fb5a859a882fd92713137ec29dbc7a86f0a01f78362f2e60'
$archive = Join-Path $local "vendor/world/world-$revision.zip"
$temporary = Join-Path $local ('tmp/world-unix-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $local ('build/native-world-unix/audit-' + $Rid + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$completed = $false
$createdTemporary = $false
$createdOutput = $false
$process = $null

function Assert-Contained([string]$Path, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) { throw 'Empty audit path.' }
    $full = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $full.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, $comparison)) { throw 'Audit path escaped its boundary.' }
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Audit path contains a link.' }
    }
    return $full
}
function Remove-Owned([string]$Path, [string]$Root, [string]$Pattern) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = Assert-Contained $Path $Root
    if ([IO.Path]::GetFileName($full) -notmatch $Pattern) { throw 'Audit cleanup refused an unowned path.' }
    if (@(Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Audit cleanup refused linked contents.' }
    Remove-Item -LiteralPath $full -Recurse -Force
}
function Assert-Tool([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Existing tool must be specified by absolute file path.' }
    return [IO.Path]::GetFullPath($Path)
}
function Assert-LibraryHeader([string]$Path, [string]$Target) {
    $header = [byte[]]::new(64)
    $inputFile = [IO.File]::OpenRead($Path)
    try { $inputFile.ReadExactly($header, 0, $header.Length) } finally { $inputFile.Dispose() }
    if ($Target.StartsWith('linux-')) {
        $machine = if ($Target -eq 'linux-x64') { 62 } else { 183 }
        # 64-bit little-endian ELF shared object (ET_DYN).
        if ([Convert]::ToHexString($header, 0, 7) -ne '7F454C46020101' -or
            ($header[16] -ne 3 -or $header[17] -ne 0) -or
            ($header[18] -ne $machine -or $header[19] -ne 0)) { throw "ELF header does not match $Target." }
    } else {
        $cpu = if ($Target -eq 'osx-x64') { '07000001' } else { '0C000001' }
        # Thin 64-bit little-endian Mach-O dylib (MH_DYLIB).
        if ([Convert]::ToHexString($header, 0, 4) -ne 'CFFAEDFE' -or
            [Convert]::ToHexString($header, 4, 4) -ne $cpu -or
            [Convert]::ToHexString($header, 12, 4) -ne '06000000') { throw "Mach-O header does not match $Target." }
    }
}
function Confirm-BridgeExports([string]$SymbolLog, [string]$Target) {
    $prefix = if ($Target.StartsWith('osx-')) { '_' } else { '' }
    $defined = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in [IO.File]::ReadLines($SymbolLog)) {
        if ($line -cmatch ('^\s*[\da-fA-F]+\s+T\s+' + $prefix + '(tw_world_\w+)\s*$')) {
            [void]$defined.Add($Matches[1])
        }
    }
    foreach ($suffix in 'abi_version','layout','analyze','analyze_dio','analyze_parallel','analyze_dio_parallel','analyze_f0','analyze_dio_f0','synthesize') {
        $name = 'tw_world_' + $suffix
        if (!$defined.Contains($name)) { throw "Shared library has no defined public function $name." }
    }
    return $defined | Sort-Object
}
function Invoke-AuditTool([string]$Executable, [string[]]$Arguments, [string]$Name) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $temporary
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($environmentName in @('TEMP','TMP','TMPDIR')) { $start.Environment[$environmentName] = Join-Path $temporary 'tmp' }
    foreach ($environmentName in @('CPATH','CPLUS_INCLUDE_PATH','C_INCLUDE_PATH','LIBRARY_PATH','COMPILER_PATH','GCC_EXEC_PREFIX','CCC_OVERRIDE_OPTIONS','CFLAGS','CXXFLAGS','LDFLAGS')) { $null = $start.Environment.Remove($environmentName) }
    $script:process = [Diagnostics.Process]::new()
    $script:process.StartInfo = $start
    if (-not $script:process.Start()) { throw 'Audit tool could not start.' }
    # Drain both pipes concurrently into fixed-size buffers. Stop on overflow;
    # never accumulate an unbounded ReadToEndAsync result in memory.
    $buffers = @([byte[]]::new(1048577), [byte[]]::new(1048577))
    $streams = @($script:process.StandardOutput.BaseStream, $script:process.StandardError.BaseStream)
    $counts = @(0, 0)
    $reads = @($streams[0].ReadAsync($buffers[0], 0, 8192), $streams[1].ReadAsync($buffers[1], 0, 8192))
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    while ($null -ne $reads[0] -or $null -ne $reads[1]) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Audit tool exceeded its 180 second deadline.' }
        foreach ($index in 0,1) {
            if ($null -eq $reads[$index] -or -not $reads[$index].IsCompleted) { continue }
            $count = $reads[$index].GetAwaiter().GetResult()
            if ($count -eq 0) { $reads[$index] = $null; continue }
            $counts[$index] += $count
            if ($counts[$index] -gt 1048576) { throw 'Audit tool output exceeded its 1 MiB per-stream limit.' }
            $reads[$index] = $streams[$index].ReadAsync($buffers[$index], $counts[$index], [Math]::Min(8192, 1048577 - $counts[$index]))
        }
        [Threading.Thread]::Sleep(10)
    }
    if (-not $script:process.WaitForExit([Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds))) { throw 'Audit tool did not exit.' }
    foreach ($index in 0,1) {
        $suffix = if ($index -eq 0) { 'stdout' } else { 'stderr' }
        $logBytes = [byte[]]::new($counts[$index])
        [Buffer]::BlockCopy($buffers[$index], 0, $logBytes, 0, $counts[$index])
        [IO.File]::WriteAllBytes((Join-Path $output ($Name + '.' + $suffix + '.txt')), $logBytes)
    }
    $exitCode = $script:process.ExitCode
    $script:process.Dispose(); $script:process = $null
    if ($exitCode -ne 0) { throw "Audit tool failed: $Name (exit $exitCode)." }
}

try {
    $null = Assert-Contained $archive $local
    $null = Assert-Contained $temporary (Join-Path $local 'tmp')
    $null = Assert-Contained $output (Join-Path $local 'build/native-world-unix')
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archiveHash) { throw 'Pinned WORLD archive is missing or differs; no download is performed.' }
    $bridge = Join-Path $PSScriptRoot 'world-bridge.cpp'
    $null = Assert-Contained $bridge $repository
    if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) { throw 'WORLD bridge is missing.' }
    $licenseInputs = @('world-LICENSE.txt','ooura-fft-LICENSE.txt')
    foreach ($licenseName in $licenseInputs) {
        $licensePath = Assert-Contained (Join-Path $repository ('assets/' + $licenseName)) $repository
        if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw "Required license input is missing: $licenseName" }
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            if (-not $entry.FullName.StartsWith("World-$revision/", [StringComparison]::Ordinal) -or $entry.FullName.Contains(':') -or $entry.FullName.Contains('\') -or ($entry.FullName.Split('/') -contains '..') -or -not $names.Add($entry.FullName) -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unsafe or duplicate WORLD archive entry.' }
        }
        # Same translation units as build-world.ps1; do not use a broad glob.
        $units = @('cheaptrick','common','d4c','dio','stonemask','fft','harvest','matlabfunctions','synthesis')
        foreach ($relative in @($units | ForEach-Object { "src/$_.cpp" }) + @('LICENSE.txt','src/world/cheaptrick.h','src/world/d4c.h','src/world/harvest.h','src/world/dio.h','src/world/stonemask.h','src/world/synthesis.h')) {
            if (-not $names.Contains("World-$revision/$relative")) { throw "Pinned WORLD input is missing: $relative" }
        }
    } finally { $zip.Dispose() }
    if (Test-Path -LiteralPath $temporary) { throw 'Audit temporary directory already exists.' }
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $createdTemporary = $true
    New-Item -ItemType Directory -Path (Join-Path $temporary 'tmp') | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $temporary)
    $source = Join-Path $temporary "World-$revision"
    Copy-Item -LiteralPath $bridge -Destination (Join-Path $temporary 'world-bridge.cpp')
    if ($PrepareOnly) {
        Write-Output "WORLD source preparation passed for $Rid; no compiler executed, no assets adopted."
    } else {
        $hostOs = if ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { '' }
        $hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
        if ($Rid -ne "$hostOs-$hostArchitecture") { throw 'Actual compilation requires a matching Unix host and process architecture.' }
        $compiler = Assert-Tool $CompilerExecutable
        $nm = Assert-Tool $NmExecutable
        $inspector = Assert-Tool $DependencyInspectorExecutable
        if (Test-Path -LiteralPath $output) { throw 'Audit output already exists.' }
        New-Item -ItemType Directory -Path $output | Out-Null
        $createdOutput = $true
        Invoke-AuditTool $compiler @('--version') 'compiler-version'
        $common = @('-O2','-std=c++17','-fPIC','-fvisibility=hidden','-fno-fast-math','-ffp-contract=off','-fno-lto')
        if ($hostOs -eq 'linux') { $common += '-pthread' }
        $files = @($units | ForEach-Object { Join-Path $source "src/$_.cpp" }) + (Join-Path $temporary 'world-bridge.cpp')
        $objects = @()
        foreach ($file in $files) {
            $name = [IO.Path]::GetFileNameWithoutExtension($file)
            $object = Join-Path $temporary ($name + '.o'); $objects += $object
            Invoke-AuditTool $compiler ($common + @('-I', (Join-Path $source 'src'), '-c', $file, '-o', $object)) ('compile-' + $name)
        }
        $filename = if ($IsLinux) { 'libtanayomi-world.so' } else { 'libtanayomi-world.dylib' }
        $library = Join-Path $output $filename
        $linkFlags = if ($IsLinux) { @('-shared','-Wl,-z,defs') } else { @('-dynamiclib','-Wl,-undefined,error') }
        Invoke-AuditTool $compiler ($common + $linkFlags + $objects + @('-o', $library)) 'link'
        Assert-LibraryHeader $library $Rid
        $symbolArgs = if ($IsLinux) { @('-D', '-g', '--defined-only', $library) } else { @('-gU', $library) }
        Invoke-AuditTool $nm $symbolArgs 'symbols'
        $bridgeExports = @(Confirm-BridgeExports (Join-Path $output 'symbols.stdout.txt') $Rid)
        $inspectArguments = if ($IsLinux) { @('-d', $library) } else { @('-L', $library) }
        Invoke-AuditTool $inspector $inspectArguments 'dependencies'
        # Preserve the project's complete source/FFT notices. Unix toolchain and
        # runtime redistribution conditions are still unknown; do not copy a
        # Windows runtime notice and imply that it covers this candidate.
        foreach ($licenseName in $licenseInputs) {
            Copy-Item -LiteralPath (Join-Path $repository ('assets/' + $licenseName)) -Destination (Join-Path $output $licenseName)
        }
        [ordered]@{
            purpose = 'Unadopted Unix WORLD audit candidate; ABI, dependencies and redistribution conditions remain unverified'
            auditRequired = $true; adoptionApproved = $false; rid = $Rid; architecture = $hostArchitecture
            expectedAbiVersion = 1; worldRevision = $revision; worldArchiveSha256 = $archiveHash
            verifiedHeaderRid = $Rid; definedBridgeExports = $bridgeExports
            bridgeSha256 = (Get-FileHash -LiteralPath (Join-Path $temporary 'world-bridge.cpp') -Algorithm SHA256).Hash.ToLowerInvariant()
            librarySha256 = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant()
            compiler = $compiler; nm = $nm; dependencyInspector = $inspector
            compilerFlags = $common; linkFlags = $linkFlags
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'world-audit.json') -Encoding utf8
        $completed = $true
        Write-Output "Unadopted WORLD audit candidate: $output"
    }
} catch {
    [Console]::Error.WriteLine('WORLD Unix preparation/build failed: ' + $_.Exception.Message)
    exit 1
} finally {
    $childReaped = $true
    if ($null -ne $process) {
        $childReaped = $false
        try {
            if (-not $process.HasExited) { $process.Kill($true) }
            $childReaped = $process.WaitForExit(3000)
        } catch {
            [Console]::Error.WriteLine('Audit child recovery failed: ' + $_.Exception.GetType().Name)
        }
        finally { $process.Dispose() }
    }
    if ($childReaped) {
        if ($createdTemporary) { Remove-Owned $temporary (Join-Path $local 'tmp') '^world-unix-[0-9a-f]{32}$' }
        if ($createdOutput -and -not $completed) { Remove-Owned $output (Join-Path $local 'build/native-world-unix') '^audit-(linux|osx)-(x64|arm64)-[0-9]{8}-[0-9]{6}-[0-9a-f]{32}$' }
    } else {
        [Console]::Error.WriteLine('Audit child was not confirmed reaped; owned paths are retained until its exit is verified.')
        if ($createdTemporary) { [Console]::Error.WriteLine('Retained temporary: ' + $temporary) }
        if ($createdOutput) { [Console]::Error.WriteLine('Retained audit output: ' + $output) }
    }
}
