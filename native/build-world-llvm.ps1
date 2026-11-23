[CmdletBinding()]
param([switch]$DownloadToolchain, [switch]$PrepareOnly, [ValidateSet('O2','O3')][string]$Optimization = 'O2')
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The comparison audit currently targets Windows x64.' }

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$local = Join-Path $repository '.local'
$vendor = Join-Path $local 'vendor/llvm-mingw'
$toolchainName = 'llvm-mingw-20261020-msvcrt-x86_64'
$archive = Join-Path $vendor ($toolchainName + '.zip')
$archiveHash = '52a3e3f584a74c3cde8ceb0c6960549683178e03b36818cb02b7ed109a68ff67'
$toolchain = Join-Path $vendor $toolchainName
$revision = 'd625e7608ca23a870018f01e7c562ac683d9847f'
$worldArchive = Join-Path $local "vendor/world/world-$revision.zip"
$worldArchiveHash = 'a62635555243dfe4fb5a859a882fd92713137ec29dbc7a86f0a01f78362f2e60'
$world = Join-Path $local "vendor/world/World-$revision"
$temporaryRoot = Join-Path $local 'tmp'
$temporary = Join-Path $temporaryRoot ('world-llvm-' + [Guid]::NewGuid().ToString('N'))
$outputRoot = Join-Path $local 'build/native-llvm'
$output = Join-Path $outputRoot ('audit-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$staging = $null
$processRunning = $false
$nativeErrorGuard = $null
$savedTemporaryEnvironment = @{}

function Assert-Contained([string]$Path, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) { throw 'Empty audit path boundary.' }
    $full = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $full.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Audit path escaped its boundary.' }
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Audit path contains a link.' }
    }
    return $full
}
function Remove-Owned([string]$Path, [string]$Root, [string]$NamePattern) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = Assert-Contained $Path $Root
    if ([IO.Path]::GetFileName($full) -notmatch $NamePattern) { throw 'Audit cleanup refused an unowned directory.' }
    if (@(Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Audit cleanup refused linked contents.' }
    Remove-Item -LiteralPath $full -Recurse -Force
}
function Invoke-AuditTool([string]$Executable, [string[]]$Arguments, [string]$LogName) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $temporary
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($name in @('INCLUDE','LIB','LIBPATH','CL','_CL_','LINK','_LINK_','CPATH','CPLUS_INCLUDE_PATH','C_INCLUDE_PATH','COMPILER_PATH','GCC_EXEC_PREFIX','LIBRARY_PATH','CCC_OVERRIDE_OPTIONS','CLANG_CONFIG_FILE_SYSTEM_DIR','CLANG_CONFIG_FILE_USER_DIR')) { $null = $info.Environment.Remove($name) }
    $info.Environment['PATH'] = (Join-Path $toolchain 'bin') + ';' + (Join-Path $env:SystemRoot 'System32') + ';' + $env:SystemRoot
    foreach ($name in @('TEMP','TMP','TMPDIR')) { $info.Environment[$name] = Join-Path $temporary 'tmp' }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'Audit compiler could not start.' }
        $script:processRunning = $true
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(180000)) { throw 'Audit tool exceeded its 180 second deadline.' }
        $script:processRunning = $false
        $text = $stdout.WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult()
        $errorText = $stderr.WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $temporary ($LogName + '.stdout.txt')), $text)
        [IO.File]::WriteAllText((Join-Path $temporary ($LogName + '.stderr.txt')), $errorText)
        if ($process.ExitCode -ne 0) { throw ('Audit tool failed: ' + $LogName + "`n" + $errorText.Substring(0, [Math]::Min(8000, $errorText.Length))) }
        return $text
    } finally {
        if ($script:processRunning) {
            try { $process.Kill($true); $script:processRunning = -not $process.WaitForExit(3000) }
            catch { $script:processRunning = $true }
        }
        $process.Dispose()
    }
}

try {
    foreach ($directory in @($vendor, $temporary, $output)) {
        $null = Assert-Contained $directory $local
        if ($directory -eq $output -and (Test-Path -LiteralPath $directory)) { throw 'Audit output already exists.' }
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    New-Item -ItemType Directory -Path (Join-Path $temporary 'tmp') | Out-Null
    foreach ($name in @('TEMP','TMP','TMPDIR')) {
        $savedTemporaryEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, (Join-Path $temporary 'tmp'), 'Process')
    }
    . (Join-Path $PSScriptRoot 'process-error-mode.ps1')
    $nativeErrorGuard = Enter-NativeProcessErrorMode
    if (-not (Test-Path -LiteralPath $archive)) {
        if (-not $DownloadToolchain) { throw 'Pinned toolchain is absent. Use -DownloadToolchain explicitly to fetch the audit toolchain.' }
        $part = Join-Path $vendor ('download-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            Invoke-WebRequest -Uri "https://github.com/mstorsjo/llvm-mingw/releases/download/20261020/$toolchainName.zip" -OutFile $part -TimeoutSec 180
            if ((Get-FileHash -LiteralPath $part -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archiveHash) { throw 'Toolchain archive hash mismatch.' }
            Move-Item -LiteralPath $part -Destination $archive
        } finally { if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part } }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archiveHash) { throw 'Cached toolchain archive hash mismatch.' }
    if (-not (Test-Path -LiteralPath $toolchain)) {
        $staging = Join-Path $vendor ('extract-' + [Guid]::NewGuid().ToString('N'))
        $null = Assert-Contained $staging $vendor
        New-Item -ItemType Directory -Path $staging | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $total = 0L
            foreach ($entry in $zip.Entries) {
                $total += $entry.Length
                if ($total -gt 3GB -or -not $entry.FullName.StartsWith($toolchainName + '/', [StringComparison]::Ordinal) -or
                    $entry.FullName -match '[:\\\x00-\x1f]' -or ($entry.FullName.Split('/') -contains '..') -or
                    -not $names.Add($entry.FullName) -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000 -or
                    ($entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint)) { throw 'Toolchain archive contains unsupported entries.' }
            }
        } finally { $zip.Dispose() }
        Expand-Archive -LiteralPath $archive -DestinationPath $staging
        Move-Item -LiteralPath (Join-Path $staging $toolchainName) -Destination $toolchain
        Remove-Owned $staging $vendor '^extract-[0-9a-f]{32}$'
        $staging = $null
    }
    $null = Assert-Contained $toolchain $vendor
    # The ZIP digest alone does not authenticate an existing expanded cache.
    # Recheck the compiler, headers, libraries and notices used by this target.
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') }) {
            $relative = $entry.FullName.Substring($toolchainName.Length + 1)
            if ($relative -notmatch '^(bin/|include/|lib/|x86_64-w64-mingw32/|LICENSE\.TXT$)') { continue }
            $path = Assert-Contained (Join-Path $vendor $entry.FullName) $toolchain
            $stream = $entry.Open()
            try { $expected = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
            finally { $stream.Dispose() }
            if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw 'Expanded toolchain differs from the pinned archive.' }
        }
    } finally { $zip.Dispose() }
    if ($PrepareOnly) { Write-Output "Verified audit toolchain: $toolchain"; return }

    if (-not (Test-Path -LiteralPath $worldArchive) -or (Get-FileHash -LiteralPath $worldArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $worldArchiveHash) { throw 'Pinned WORLD archive is missing or differs. No implicit WORLD download is performed.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($worldArchive)
    try {
        foreach ($entry in $zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') -and $_.FullName.Contains('/src/') }) {
            $path = Assert-Contained (Join-Path (Split-Path $world) $entry.FullName) (Split-Path $world)
            $stream = $entry.Open()
            try { $expected = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
            finally { $stream.Dispose() }
            if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw 'WORLD source differs from its pinned archive.' }
        }
    } finally { $zip.Dispose() }
    $compiler = Join-Path $toolchain 'bin/clang++.exe'
    $readobj = Join-Path $toolchain 'bin/llvm-readobj.exe'
    $nm = Join-Path $toolchain 'bin/llvm-nm.exe'
    # This release shares C/C++ headers at the root; only target libraries
    # live under x86_64-w64-mingw32. Disable implicit user clang configs.
    $sysroot = $toolchain
    $version = Invoke-AuditTool $compiler @('--version') 'version'
    if ($version -notmatch 'clang version 23\.1\.4\b') { throw 'Unexpected audit compiler version.' }
    $bridge = Join-Path $temporary 'world-bridge.cpp'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'world-bridge.cpp') -Destination $bridge
    $bridgeHash = (Get-FileHash -LiteralPath $bridge -Algorithm SHA256).Hash.ToLowerInvariant()
    $files = @('cheaptrick','common','d4c','dio','stonemask','fft','harvest','matlabfunctions','synthesis') | ForEach-Object { Join-Path $world "src/$_.cpp" }
    $files += $bridge
    $common = @('--no-default-config','--target=x86_64-w64-windows-gnu',"--sysroot=$sysroot",'-stdlib=libc++',('-' + $Optimization),'-std=c++17','-fno-fast-math','-ffp-contract=off','-fno-lto','-mguard=cf')
    $objects = @()
    $headers = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        $name = [IO.Path]::GetFileNameWithoutExtension($file)
        $object = Join-Path $temporary ($name + '.obj')
        $objects += $object
        Write-Output "Compile audit: $name"
        $null = Invoke-AuditTool $compiler ($common + @('-c','-H','-MD','-ffunction-sections','-fdata-sections',('-I' + (Join-Path $world 'src')),'-o',$object,$file)) $name
        foreach ($line in [IO.File]::ReadAllLines((Join-Path $temporary ($name + '.stderr.txt')))) {
            if ($line -match '^\.+ (.+)$') {
                $header = [IO.Path]::GetFullPath($Matches[1])
                if (-not $header.StartsWith($toolchain + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and
                    -not $header.StartsWith($world + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Compiler included a header outside the pinned sources/toolchain.' }
                $null = $headers.Add($header)
            }
        }
    }
    $dll = Join-Path $temporary 'tanayomi-world.dll'
    $map = Join-Path $temporary 'world.map'
    $null = Invoke-AuditTool $compiler ($common + @('--rtlib=compiler-rt','--unwindlib=libunwind','-shared','-static','-fuse-ld=lld','-Wl,--gc-sections','-Wl,--verbose',('-Wl,-Map,' + $map),'-o',$dll) + $objects) 'link'
    $pe = Invoke-AuditTool $readobj @('--coff-imports','--coff-exports',$dll) 'pe'
    $imports = @([regex]::Matches($pe,'(?m)^\s*Name: ([^\r\n]+\.dll)\s*$') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $exports = @([regex]::Matches($pe,'(?m)^\s*Name: ([^\r\n]+)\s*$') | ForEach-Object { $_.Groups[1].Value.Trim() } | Where-Object { $_ -notmatch '\.dll$' } | Sort-Object -Unique)
    if ($imports.Count -eq 0 -or 'msvcrt.dll' -notin $imports -or @($imports | Where-Object { $_ -notmatch '^(KERNEL32|msvcrt|ADVAPI32|USER32|ntdll)\.dll$' }).Count) { throw ('Unexpected native dependency: ' + ($imports -join ', ')) }
    if (($exports -join ',') -ne 'tw_world_abi_version,tw_world_analyze,tw_world_analyze_dio,tw_world_analyze_dio_f0,tw_world_analyze_dio_parallel,tw_world_analyze_f0,tw_world_analyze_parallel,tw_world_layout,tw_world_synthesize') { throw 'Audit ABI export set differs.' }
    $null = Invoke-AuditTool $nm (@('--undefined-only') + $objects) 'undefined'
    $loaded = @([IO.File]::ReadAllLines((Join-Path $temporary 'link.stderr.txt')) |
        Where-Object { $_ -match '^ld\.lld: Loaded ' } |
        ForEach-Object { $_ -replace '^ld\.lld: Loaded ', '' -replace ' for .*$', '' } | Sort-Object -Unique)
    if (-not $loaded.Count -or @($loaded | Where-Object { $_ -notmatch '^(libc\+\+|libunwind|libclang_rt\.builtins-x86_64|libmingw32|libmingwex|libmsvcrt|libkernel32)\.a\(' }).Count) { throw 'Unexpected or absent archive member trace. Review before adoption.' }
    [IO.File]::WriteAllLines((Join-Path $output 'linked-members.txt'), $loaded)
    [IO.File]::WriteAllLines((Join-Path $output 'includes.txt'), @($headers | Sort-Object))
    $notices = Join-Path $output 'licenses'
    New-Item -ItemType Directory -Path $notices | Out-Null
    Copy-Item -LiteralPath (Join-Path $toolchain 'LICENSE.TXT') -Destination (Join-Path $notices 'LLVM-LICENSE.TXT')
    foreach ($notice in @('COPYING','COPYING.MinGW-w64-runtime.txt','COPYING.MinGW-w64.txt')) {
        Copy-Item -LiteralPath (Join-Path $toolchain ('x86_64-w64-mingw32/share/mingw32/' + $notice)) -Destination (Join-Path $notices $notice)
    }
    # This notice belongs to the reviewed member set. A newly generated DLL
    # still requires comparing its members/includes before reusing the notice.
    Copy-Item -LiteralPath (Join-Path $repository 'assets/world-runtime-LICENSE.txt') -Destination (Join-Path $notices 'world-runtime-LICENSE.txt')
    foreach ($name in @('world.map','link.stdout.txt','link.stderr.txt','pe.stdout.txt','undefined.stdout.txt','version.stdout.txt')) { Copy-Item -LiteralPath (Join-Path $temporary $name) -Destination (Join-Path $output $name) }
    Copy-Item -LiteralPath $dll -Destination (Join-Path $output 'tanayomi-world.dll')
    $hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{ purpose='License/link audit only; not approved for product adoption'; toolchain=$toolchainName; toolchainArchiveSha256=$archiveHash; llvm='23.1.4'; optimization=$Optimization; mingwRevision='0d6dfbc348dfad714761c0a7151615767f96bcb5'; worldRevision=$revision; worldArchiveSha256=$worldArchiveHash; bridgeSha256=$bridgeHash; dllSha256=$hash; imports=$imports; exports=$exports; cephesMembershipVerified=$false; compilerFlags=$common; linkFlags=@('--rtlib=compiler-rt','--unwindlib=libunwind','-shared','-static','-fuse-ld=lld','-Wl,--gc-sections','-Wl,--verbose') } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'world-build.json') -Encoding utf8
    Write-Output "Audit output: $output"
    Write-Output ('PE imports: ' + ($imports -join ', '))
    Write-Output 'Review linked archive members and source licenses before any product adoption.'
} finally {
    if ($null -ne $nativeErrorGuard) { $nativeErrorGuard.Dispose() }
    foreach ($name in $savedTemporaryEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedTemporaryEnvironment[$name], 'Process') }
    if ($staging) { Remove-Owned $staging $vendor '^extract-[0-9a-f]{32}$' }
    if (-not $processRunning) { Remove-Owned $temporary $temporaryRoot '^world-llvm-[0-9a-f]{32}$' }
    else { Write-Warning "An audit tool did not confirm exit; retained owned temporary directory: $temporary" }
    if ((Test-Path -LiteralPath $output) -and -not (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) { Remove-Owned $output $outputRoot '^audit-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$' }
}
