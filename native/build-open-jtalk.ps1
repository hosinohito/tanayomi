[CmdletBinding()]
param(
    [switch]$VoicedPlanComparison,
    [ValidateSet('win-x64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Rid = 'win-x64',
    [switch]$PrepareOnly,
    [string]$CompilerExecutable,
    [string]$NmExecutable,
    [string]$DependencyInspectorExecutable,
    [string]$MacOsSdkPath
)
$ErrorActionPreference = 'Stop'
$linux = $Rid -in 'linux-x64','linux-arm64'
$mac = $Rid -in 'osx-x64','osx-arm64'
$unix = $linux -or $mac
if ($unix) {
    $arm = $Rid -in 'linux-arm64','osx-arm64'
    $architecture = if ($arm) { [Runtime.InteropServices.Architecture]::Arm64 } else { [Runtime.InteropServices.Architecture]::X64 }
    $target = if ($linux) {
        if ($arm) { 'aarch64-linux-gnu' } else { 'x86_64-linux-gnu' }
    } else {
        if ($arm) { 'arm64' } else { 'x86_64' }
    }
    $elfMachine = if ($arm) { 183 } else { 62 }
    $machCpu = if ($arm) { '0C000001' } else { '07000001' }
    $matchingOs = ($linux -and $IsLinux) -or ($mac -and $IsMacOS)
    if ($VoicedPlanComparison) { throw '-VoicedPlanComparison is supported only by the Windows audit.' }
    if (-not $PrepareOnly -and (-not $matchingOs -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne $architecture -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne $architecture)) {
        throw "Actual compilation requires a matching host and process for $Rid."
    }
} else {
    if ($PrepareOnly) { throw '-PrepareOnly requires a Unix RID.' }
    if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'This audit build requires Windows x64 PowerShell 7.' }
}
if ((-not $unix -or $PrepareOnly) -and ($CompilerExecutable -or $NmExecutable -or $DependencyInspectorExecutable)) {
    throw 'Explicit tool paths are supported only for an actual Unix audit build.'
}
if ((-not $mac -or $PrepareOnly) -and $MacOsSdkPath) {
    throw '-MacOsSdkPath is supported only for an actual macOS audit build.'
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$local = Join-Path $repository '.local'
$vendor = Join-Path $local 'vendor/open-jtalk'
$toolchain = Join-Path $local 'vendor/llvm-mingw/llvm-mingw-20261020-msvcrt-x86_64'
$temporary = Join-Path $local ('tmp/open-jtalk-' + [Guid]::NewGuid().ToString('N'))
$outputRoot = Join-Path $local $(if ($linux) { 'build/open-jtalk-linux' } elseif ($mac) { 'build/open-jtalk-osx' } else { 'build/open-jtalk' })
$output = Join-Path $outputRoot ('audit-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$processRunning = $false
$complete = $false
$savedEnvironment = @{}
$nativeErrorGuard = $null
$createdTemporary = $false
$createdOutput = $false
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

function Assert-Path([string]$Path, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) { throw 'Empty audit path.' }
    $full = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $full.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, $pathComparison) -or
        [IO.Path]::GetRelativePath($boundary,$full).Contains(':')) { throw 'Audit path escaped its boundary.' }
    for ($current=$full; $current; $current=[IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked audit path.' }
    }
    return $full
}
function Assert-Tool([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path) -or -not [IO.File]::Exists($Path)) {
        throw 'Existing audit tools must be specified by absolute file path.'
    }
    return [IO.Path]::GetFullPath($Path)
}
function Remove-Owned([string]$Path, [string]$Root, [string]$Pattern) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $checked = Assert-Path $Path $Root
    if ([IO.Path]::GetFileName($checked) -notmatch $Pattern) { throw 'Unowned audit cleanup target.' }
    foreach ($entry in Get-ChildItem -LiteralPath $checked -Recurse -Force) { $null = Assert-Path $entry.FullName $checked }
    Remove-Item -LiteralPath $checked -Recurse -Force
}
function Extract-Fixed([string]$Name, [string]$Hash, [bool]$CodeOnly) {
    $path = Assert-Path (Join-Path $vendor $Name) $vendor
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Hash) { throw 'Pinned input hash mismatch.' }
    $source = [IO.File]::OpenRead($path)
    $gzip = [IO.Compression.GZipStream]::new($source,[IO.Compression.CompressionMode]::Decompress)
    $tar = [System.Formats.Tar.TarReader]::new($gzip)
    try {
        $expanded = 0
        while ($entry = $tar.GetNextEntry()) {
            $expanded += $entry.Length
            if ($expanded -gt 160MB -or $entry.Name.Contains('\') -or $entry.Name.Split('/') -contains '..') { throw 'Invalid archive boundary.' }
            if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) { continue }
            if ($entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile,[System.Formats.Tar.TarEntryType]::V7RegularFile)) { throw 'Archive links/special entries are unsupported.' }
            if ($CodeOnly -and $entry.Name -notmatch '\.(c|cpp|h)$|/(COPYING|README|AUTHORS)$') { continue }
            $destination = Assert-Path (Join-Path $temporary $entry.Name) $temporary
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            $stream = [IO.File]::Open($destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try { $entry.DataStream.CopyTo($stream) } finally { $stream.Dispose() }
        }
    } finally { $tar.Dispose(); $gzip.Dispose(); $source.Dispose() }
}
function Invoke-Audit([string]$Tool, [string[]]$Arguments, [string]$Name) {
    $start = [Diagnostics.ProcessStartInfo]::new($Tool)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WorkingDirectory=$temporary
    $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($environmentName in @('INCLUDE','LIB','LIBPATH','CL','_CL_','LINK','_LINK_','CPATH','CPLUS_INCLUDE_PATH','C_INCLUDE_PATH','COMPILER_PATH','GCC_EXEC_PREFIX','LIBRARY_PATH','CCC_OVERRIDE_OPTIONS','CLANG_CONFIG_FILE_SYSTEM_DIR','CLANG_CONFIG_FILE_USER_DIR')) { $null=$start.Environment.Remove($environmentName) }
    if ($unix) {
        foreach ($environmentName in @('LD_PRELOAD','LD_AUDIT','LD_LIBRARY_PATH','CFLAGS','CXXFLAGS','LDFLAGS')) { $null=$start.Environment.Remove($environmentName) }
        $start.Environment['PATH']='/usr/bin:/bin'
        $start.Environment['LC_ALL']='C'
        if ($mac) {
            foreach ($environmentName in @('SDKROOT','MACOSX_DEPLOYMENT_TARGET','DYLD_LIBRARY_PATH','DYLD_FALLBACK_LIBRARY_PATH','DYLD_FRAMEWORK_PATH','DYLD_FALLBACK_FRAMEWORK_PATH','DYLD_INSERT_LIBRARIES')) { $null=$start.Environment.Remove($environmentName) }
        }
    } else {
        $start.Environment['PATH']=(Join-Path $toolchain 'bin')+';'+[Environment]::SystemDirectory+';'+[Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    }
    foreach ($environmentName in @('TEMP','TMP','TMPDIR')) { $start.Environment[$environmentName]=$temporary }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo=$start
    $readers=@(); $cancellation=[Threading.CancellationTokenSource]::new()
    try {
        if (-not $process.Start()) { throw 'Audit compiler did not start.' }
        $script:processRunning=$true; $process.StandardInput.Close()
        [IO.File]::WriteAllText((Join-Path $output ($Name+'.arguments.json')),($Arguments | ConvertTo-Json))
        foreach ($stream in @($process.StandardOutput.BaseStream,$process.StandardError.BaseStream)) {
            $buffer=[byte[]]::new(4096)
            $readers += [pscustomobject]@{Stream=$stream;Buffer=$buffer;Bytes=[IO.MemoryStream]::new();Pending=$stream.ReadAsync($buffer,0,$buffer.Length,$cancellation.Token)}
        }
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            foreach ($reader in $readers) {
                if ($null -ne $reader.Pending -and $reader.Pending.IsCompleted) {
                    $count=$reader.Pending.GetAwaiter().GetResult();$reader.Pending=$null
                    if ($count -gt 0) {
                        if ($reader.Bytes.Length+$count -gt 1MB) { throw 'Audit diagnostic cap exceeded.' }
                        $reader.Bytes.Write($reader.Buffer,0,$count)
                        $reader.Pending=$reader.Stream.ReadAsync($reader.Buffer,0,$reader.Buffer.Length,$cancellation.Token)
                    }
                }
            }
            if ($process.HasExited -and $null -eq $readers[0].Pending -and $null -eq $readers[1].Pending) { break }
            if ($watch.Elapsed.TotalSeconds -gt 60) { throw 'Audit compiler timeout.' }
            [Threading.Thread]::Sleep(10)
        }
        $script:processRunning=$false
        $outText=[Text.Encoding]::UTF8.GetString($readers[0].Bytes.ToArray());$errText=[Text.Encoding]::UTF8.GetString($readers[1].Bytes.ToArray())
        [IO.File]::WriteAllText((Join-Path $output ($Name+'.stdout.txt')),$outText)
        [IO.File]::WriteAllText((Join-Path $output ($Name+'.stderr.txt')),$errText)
        if ($process.ExitCode -ne 0) {
            $brief=($errText -split '\n' | Where-Object { $_ -notmatch '^\.+ ' } | Select-Object -Last 25) -join "`n"
            throw ('Audit tool failed: '+$Name+"`n"+$brief)
        }
        return $outText + $errText
    } finally {
        $cancellation.Cancel()
        try {
            if ($script:processRunning) {
                $process.Kill($true)
                if ($process.WaitForExit(3000)) { $script:processRunning=$false }
            }
        } finally {
            foreach ($reader in $readers) { $reader.Bytes.Dispose() }
            $cancellation.Dispose();$process.Dispose()
        }
    }
}

try {
    if ($unix -and -not $PrepareOnly) {
        $compiler=Assert-Tool $CompilerExecutable
        $nm=Assert-Tool $NmExecutable
        $inspector=Assert-Tool $DependencyInspectorExecutable
        if ($mac) {
            if ([string]::IsNullOrWhiteSpace($MacOsSdkPath) -or -not [IO.Path]::IsPathFullyQualified($MacOsSdkPath) -or -not [IO.Directory]::Exists($MacOsSdkPath)) {
                throw 'Specify the absolute path to an existing macOS SDK with -MacOsSdkPath.'
            }
            $sdk=[IO.Path]::GetFullPath($MacOsSdkPath)
            $sdkSettings=Join-Path $sdk 'SDKSettings.json'
            if (-not [IO.File]::Exists($sdkSettings) -or -not [IO.File]::Exists((Join-Path $sdk 'usr/lib/libSystem.tbd')) -or -not [IO.File]::Exists((Join-Path $sdk 'usr/include/sys/types.h'))) {
                throw 'The macOS SDK must contain SDKSettings.json, libSystem.tbd and POSIX headers.'
            }
            $sdkInfo=Get-Content -LiteralPath $sdkSettings -Raw | ConvertFrom-Json
            if ($sdkInfo.CanonicalName -cnotmatch '^macosx(?:[0-9]+(?:\.[0-9]+)*)?$') { throw 'The supplied SDK does not identify itself as macOS.' }
        }
    }
    $null=Assert-Path $temporary (Join-Path $local 'tmp'); $null=Assert-Path $output $outputRoot
    if (-not $unix) { $null=Assert-Path $toolchain (Join-Path $local 'vendor') }
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $createdTemporary=$true
    foreach ($name in @('TEMP','TMP','TMPDIR')) { $savedEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process'); [Environment]::SetEnvironmentVariable($name,$temporary,'Process') }
    if (-not $PrepareOnly) {
        New-Item -ItemType Directory -Path $output | Out-Null
        $createdOutput=$true
    }
    if (-not $unix) {
        . (Join-Path $PSScriptRoot 'process-error-mode.ps1')
        $nativeErrorGuard = Enter-NativeProcessErrorMode
        # Authenticate the expanded compiler/runtime cache against its pinned ZIP,
        # not merely the compiler's self-reported version or an old audit result.
        $toolchainArchive=Assert-Path ($toolchain+'.zip') (Join-Path $local 'vendor/llvm-mingw')
        $toolchainHash='52a3e3f584a74c3cde8ceb0c6960549683178e03b36818cb02b7ed109a68ff67'
        if ((Get-FileHash -LiteralPath $toolchainArchive -Algorithm SHA256).Hash -ne $toolchainHash) { throw 'Pinned toolchain archive hash mismatch.' }
        $zip=[IO.Compression.ZipFile]::OpenRead($toolchainArchive)
        $verifiedToolchain=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        try {
            $prefix=[IO.Path]::GetFileName($toolchain)+'/'
            foreach ($entry in $zip.Entries) {
                if ($entry.FullName.EndsWith('/') -or -not $entry.FullName.StartsWith($prefix)) { continue }
                $relative=$entry.FullName.Substring($prefix.Length)
                if ($relative -notmatch '^(bin/|include/|lib/|x86_64-w64-mingw32/|LICENSE\.TXT$)') { continue }
                $path=Assert-Path (Join-Path $toolchain $relative) $toolchain
                $stream=$entry.Open()
                try { $expected=(Get-FileHash -InputStream $stream -Algorithm SHA256).Hash } finally { $stream.Dispose() }
                if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw 'Expanded compiler/runtime differs from pinned ZIP.' }
                [void]$verifiedToolchain.Add($path)
            }
        } finally { $zip.Dispose() }
    }
    $archiveHashes=[ordered]@{
        'open_jtalk-1.11.tar.gz'='20fdc6aeb6c757866034abc175820573db43e4284707c866fcd02c8ec18de71f'
        'open_jtalk_dic_utf_8-1.11.tar.gz'='33e9cd251bc41aa2bd7ca36f57abbf61eae3543ca25ca892ae345e394cb10549'
        'hts_engine_API-1.10.tar.gz'='e2132be5860d8fb4a460be766454cfd7c3e21cf67b509c48e1804feab14968f7'
        'hts_voice_nitech_jp_atr503_m001-1.05.tar.gz'='2e555c88482267b2931c7dbc7ecc0e3df140d6f68fc913aa4822f336c9e0adfc'
    }
    foreach ($archiveName in $archiveHashes.Keys) {
        Extract-Fixed $archiveName $archiveHashes[$archiveName] ($archiveName -in 'open_jtalk-1.11.tar.gz','hts_engine_API-1.10.tar.gz')
    }
    $jtalk=Join-Path $temporary 'open_jtalk-1.11'; $hts=Join-Path $temporary 'hts_engine_API-1.10'
    $includes=@('text2mecab','mecab/src','mecab2njd','njd','njd_set_pronunciation','njd_set_digit','njd_set_accent_phrase','njd_set_accent_type','njd_set_unvoiced_vowel','njd_set_long_vowel','njd2jpcommon','jpcommon')
    $includeFlags=@(('-I'+$temporary),('-I'+(Join-Path $hts 'include'))) + @($includes | ForEach-Object { '-I'+(Join-Path $jtalk $_) })
    $header=@'
#pragma once
#include <stdio.h>
#ifdef __cplusplus
struct TanayomiHtsFatal {};
extern "C" FILE* toj_fopen_utf8(const char*,const char*);
#ifdef _WIN32
#include <windows.h>
#ifdef ERROR
#undef ERROR
#endif
extern "C" HANDLE toj_create_file_utf8(const char*,DWORD,DWORD,LPSECURITY_ATTRIBUTES,DWORD,DWORD,HANDLE);
#endif
#endif
'@
    $localHeader=Join-Path $temporary 'open-jtalk-local.h'; [IO.File]::WriteAllText($localHeader,$header)
    # These patches affect only this owned extraction, never the pinned archive.
    $misc=Join-Path $hts 'lib/HTS_misc.c'; $miscText=[IO.File]::ReadAllText($misc)
    $pattern='(?s)void HTS_error\(int error, const char \*message, \.\.\.\)\s*\{.*?\n\}'
    if ([regex]::Matches($miscText,$pattern).Count -ne 1 -or ([regex]::Matches($miscText,'\bfopen\(name, opt\)')).Count -ne 1) { throw 'Pinned HTS patch context changed.' }
    $miscText=[regex]::Replace($miscText,$pattern,'void HTS_error(int error, const char *message, ...) { if (error > 0) throw TanayomiHtsFatal{}; }')
    $miscText=$miscText.Replace('fopen(name, opt)','toj_fopen_utf8(name, opt)'); [IO.File]::WriteAllText($misc,$miscText)
    if (-not $unix) {
        $mmap=Join-Path $jtalk 'mecab/src/mmap.h'; $mmapText=[IO.File]::ReadAllText($mmap)
        if ([regex]::Matches($mmapText,'::CreateFileA\(filename,').Count -ne 1) { throw 'Pinned MeCab UTF8 patch context changed.' }
        [IO.File]::WriteAllText($mmap,$mmapText.Replace('::CreateFileA(filename,','::toj_create_file_utf8(filename,'))
    }
    # Omit the two dictionary-writing methods and their private cost/compiler
    # helpers so the inference build does not need optimizer/training objects.
    $dictionary=Join-Path $jtalk 'mecab/src/dictionary.cpp'; $dictionaryText=[IO.File]::ReadAllText($dictionary)
    $helpers=$dictionaryText.IndexOf('int toInt('); $helperEnd=$dictionaryText.IndexOf('}  // namespace',$helpers)
    if ($helpers -lt 0 -or $helperEnd -le $helpers) { throw 'Pinned inference helper patch context changed.' }
    $dictionaryText=$dictionaryText.Remove($helpers,$helperEnd-$helpers)
    $training=$dictionaryText.IndexOf('bool Dictionary::assignUserDictionaryCosts(')
    if ($training -lt 0 -or $dictionaryText.IndexOf('bool Dictionary::compile(',$training) -lt 0) { throw 'Pinned dictionary-writing patch context changed.' }
    [IO.File]::WriteAllText($dictionary,$dictionaryText.Substring(0,$training)+"`n} // namespace MeCab`n")
    $files=@(Join-Path $repository 'native/open-jtalk-bridge.cpp')
    foreach ($part in @('text2mecab/text2mecab.c','mecab2njd/mecab2njd.c','njd/njd.c','njd/njd_node.c','njd2jpcommon/njd2jpcommon.c','jpcommon/jpcommon.c','jpcommon/jpcommon_node.c','jpcommon/jpcommon_label.c')) { $files += Join-Path $jtalk $part }
    foreach ($part in @('pronunciation','digit','accent_phrase','accent_type','unvoiced_vowel','long_vowel')) { $files += Join-Path $jtalk ('njd_set_'+$part+'/njd_set_'+$part+'.c') }
    # Inference only: no learner/lbfgs/dictionary compiler or code generator.
    foreach ($part in @('char_property','connector','context_id','dictionary','iconv_utils','libmecab','mecab','nbest_generator','param','string_buffer','tagger','tokenizer','utils','viterbi','writer')) { $files += Join-Path $jtalk ('mecab/src/'+$part+'.cpp') }
    foreach ($part in @('audio','engine','misc','pstream','sstream','model','vocoder','gstream','label')) { $files += Join-Path $hts ('lib/HTS_'+$part+'.c') }
    if ($unix) {
        $definitions = @(
            'CHARSET_UTF_8','MECAB_USE_UTF8_ONLY','MECAB_WITHOUT_MUTEX_LOCK',
            'MECAB_DEFAULT_RC="dummy"','MECAB_DEFAULT_CHARSET="UTF-8"','DIC_VERSION=102',
            'PACKAGE="open_jtalk"','VERSION="1.11"','AUDIO_PLAY_NONE','NDEBUG',
            'HAVE_STDLIB_H','HAVE_STRING_H','HAVE_STDINT_H','HAVE_SYS_TYPES_H',
            'HAVE_UNISTD_H','HAVE_FCNTL_H','HAVE_SYS_STAT_H','HAVE_SYS_MMAN_H','HAVE_MMAP',
            'SIZEOF_CHAR=1','SIZEOF_SHORT=2','SIZEOF_INT=4','SIZEOF_LONG=8',
            'SIZEOF_LONG_LONG=8','SIZEOF_SIZE_T=8'
        )
    }
    if ($PrepareOnly) {
        $platform=if ($linux) { "glibc, $target" } else { "macOS, $target" }
        Write-Output "$Rid ($platform) audit preparation. Verified and safely extracted Open JTalk 1.11, HTS 1.10, UTF-8 dictionary 1.11 and NIT ATR503 voice 1.05."
        Write-Output ('POSIX definitions: '+(($definitions | ForEach-Object { '-D'+$_ }) -join ' '))
        Write-Output 'MeCab without iconv uses identity conversion. This preparation is limited to the pinned UTF-8 dictionary.'
        if ($linux) {
            Write-Output 'Required on the matching Linux host: Clang++, ELF linker, glibc/C++ runtime headers, nm and readelf (or compatible tools).'
        } else {
            Write-Output 'Required on the matching macOS host: Clang++, Mach-O linker, existing macOS SDK (-MacOsSdkPath), nm and otool (or compatible tools).'
        }
        Write-Output 'Audit still pending: PIC/hidden visibility and shared linking with undefined symbols rejected, exports/dependencies, runtime licenses and native invocation.'
        foreach ($file in $files) {
            if (-not [IO.File]::Exists($file)) { throw 'Pinned inference source missing.' }
            $language=if ($file.StartsWith($jtalk+[IO.Path]::DirectorySeparatorChar,$pathComparison) -and [IO.Path]::GetExtension($file) -eq '.c') { 'C11' } else { 'C++14' }
            $relative=if ($file.StartsWith($temporary+[IO.Path]::DirectorySeparatorChar,$pathComparison)) { [IO.Path]::GetRelativePath($temporary,$file) } else { [IO.Path]::GetRelativePath($repository,$file) }
            Write-Output ($language+' '+$relative.Replace('\','/'))
        }
        return
    }
    if (-not $unix) {
        $compiler=Join-Path $toolchain 'bin/clang++.exe'; $readobj=Join-Path $toolchain 'bin/llvm-readobj.exe'
    }
    $version=Invoke-Audit $compiler @('--version') 'version'
    if ($unix) {
        if ($version -notmatch 'clang version \d+\.') { throw 'Unix audit requires Clang++.' }
        $targetFlags=if ($linux) { @("--target=$target") } else { @('-arch',$target,'-isysroot',$sdk) }
        $common=@('--no-default-config')+$targetFlags+@('-O2','-std=c++14','-fPIC','-fvisibility=hidden','-fno-fast-math','-ffp-contract=off','-fno-lto','-ffunction-sections','-fdata-sections')
        $common+=@($definitions | ForEach-Object { '-D'+$_ })
    } else {
        if ($version -notmatch 'clang version 23\.1\.4\b') { throw 'Unexpected fixed LLVM version.' }
        $common=@('--no-default-config','--target=x86_64-w64-windows-gnu',('--sysroot='+$toolchain),'-stdlib=libc++','-O2','-std=c++14','-fno-fast-math','-ffp-contract=off','-fno-lto','-mguard=cf','-ffunction-sections','-fdata-sections','-DCHARSET_UTF_8','-DMECAB_USE_UTF8_ONLY','-DMECAB_WITHOUT_MUTEX_LOCK','-DMECAB_DEFAULT_RC="dummy"','-DMECAB_DEFAULT_CHARSET="UTF-8"','-DDIC_VERSION=102','-DPACKAGE="open_jtalk"','-DVERSION="1.11"','-DHAVE_WINDOWS_H','-DHAVE_STDLIB_H','-DHAVE_STRING_H','-DHAVE_STDINT_H','-DHAVE_SYS_TYPES_H','-DHAVE_IO_H','-DNOMINMAX','-DAUDIO_PLAY_NONE','-DSIZEOF_CHAR=1','-DSIZEOF_SHORT=2','-DSIZEOF_INT=4','-DSIZEOF_LONG=4','-DSIZEOF_LONG_LONG=8','-DSIZEOF_SIZE_T=8')
        $common += '-DNDEBUG'
        if ($VoicedPlanComparison) { $common += '-DTANAYOMI_AUDIT_VOICED_PLAN' }
    }
    $objects=@(); $headers=[Collections.Generic.HashSet[string]]::new()
    for ($i=0; $i -lt $files.Count; $i++) {
        $file=$files[$i]; $object=Join-Path $temporary ('unit-'+$i+'.obj'); $objects += $object
        # Preserve frontend C conversions; only HTS needs C++ exception unwind.
        $language=if ($file.StartsWith($jtalk+[IO.Path]::DirectorySeparatorChar,$pathComparison) -and [IO.Path]::GetExtension($file) -eq '.c') { @('-x','c','-std=c11') } else { @('-x','c++') }
        $log=Invoke-Audit $compiler ($common+$includeFlags+$language+@('-Wno-register','-include',$localHeader,'-c','-H','-o',$object,$file)) ('compile-'+$i)
        foreach ($line in ($log -split '\n')) {
            if ($line -match '^\.+ (.+)$') {
                $headerPath=[IO.Path]::GetFullPath($Matches[1].Trim())
                if (-not $unix -and -not $headerPath.StartsWith($toolchain+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and
                    -not $headerPath.StartsWith($temporary+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Header outside pinned extraction/toolchain.' }
                if (-not $unix -and $headerPath.StartsWith($toolchain+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and -not $verifiedToolchain.Contains($headerPath)) { throw 'Included toolchain file is absent from pinned ZIP.' }
                [void]$headers.Add($headerPath)
            }
        }
    }
    $map=Join-Path $output 'open-jtalk.map'
    $expectedExports='toj_abi_version','toj_clear','toj_load','toj_plan','toj_synthesize','toj_synthesize_drawn_out'
    if ($linux) {
        $library=Join-Path $output 'libtanayomi-open-jtalk.so'
        $exportMap=Join-Path $temporary 'exports.map'
        [IO.File]::WriteAllText($exportMap,('{ global: '+($expectedExports -join '; ')+'; local: *; };'))
        $linkFlags=@('-shared','-Wl,-z,defs','-Wl,--gc-sections','-Xlinker','-Map','-Xlinker',$map,'-Xlinker','--version-script','-Xlinker',$exportMap)
        $null=Invoke-Audit $compiler ($common+$linkFlags+@('-o',$library)+$objects) 'link'
        $header=[byte[]]::new(64)
        $inputFile=[IO.File]::OpenRead($library)
        try { $inputFile.ReadExactly($header,0,$header.Length) } finally { $inputFile.Dispose() }
        if ([Convert]::ToHexString($header,0,7) -ne '7F454C46020101' -or [Convert]::ToHexString($header,16,2) -ne '0300' -or $header[18] -ne $elfMachine -or $header[19] -ne 0) {
            throw "Expected an ELF64 little-endian shared object for $Rid."
        }
        $null=Invoke-Audit $nm @('-D','-g','--defined-only',$library) 'symbols'
        $symbols=@(Get-Content -LiteralPath (Join-Path $output 'symbols.stdout.txt') | Where-Object { $_.Trim() })
        $exports=@(foreach ($symbol in $symbols) {
            if ($symbol -cnotmatch '^\s*[0-9a-fA-F]+\s+T\s+(toj_\w+)\s*$') { throw 'Unexpected defined ELF export.' }
            $Matches[1]
        })
        if ((($exports | Sort-Object) -join ',') -cne ($expectedExports -join ',')) { throw 'Unexpected ELF C ABI exports.' }
        $null=Invoke-Audit $inspector @('-d','-W',$library) 'dependencies'
        $dynamic=[IO.File]::ReadAllText((Join-Path $output 'dependencies.stdout.txt'))
        $imports=@([regex]::Matches($dynamic,'(?m)\(NEEDED\)[^\r\n]*\[([^\]\r\n]+)\]') | ForEach-Object { $_.Groups[1].Value })
        if ('libc.so.6' -cnotin $imports -or @($imports | Where-Object { $_ -cmatch '[/\\]' }).Count -or $dynamic -cmatch '\((RPATH|RUNPATH)\)') {
            throw 'Unexpected ELF dependency or embedded runtime search path.'
        }
    } elseif ($mac) {
        $library=Join-Path $output 'libtanayomi-open-jtalk.dylib'
        $exportList=Join-Path $temporary 'exports.txt'
        [IO.File]::WriteAllLines($exportList,@($expectedExports | ForEach-Object { '_'+$_ }))
        $linkFlags=@('-dynamiclib','-Wl,-undefined,error','-Wl,-dead_strip','-Wl,-install_name,@rpath/libtanayomi-open-jtalk.dylib',
            '-Xlinker','-map','-Xlinker',$map,'-Xlinker','-exported_symbols_list','-Xlinker',$exportList)
        $null=Invoke-Audit $compiler ($common+$linkFlags+@('-o',$library)+$objects) 'link'
        $header=[byte[]]::new(32)
        $inputFile=[IO.File]::OpenRead($library)
        try { $inputFile.ReadExactly($header,0,$header.Length) } finally { $inputFile.Dispose() }
        if ([Convert]::ToHexString($header,0,4) -ne 'CFFAEDFE' -or [Convert]::ToHexString($header,4,4) -ne $machCpu -or [Convert]::ToHexString($header,12,4) -ne '06000000') {
            throw "Expected a thin Mach-O 64-bit dylib for $Rid."
        }
        $null=Invoke-Audit $nm @('-gU',$library) 'symbols'
        $symbols=@(Get-Content -LiteralPath (Join-Path $output 'symbols.stdout.txt') | Where-Object { $_.Trim() })
        $exports=@(foreach ($symbol in $symbols) {
            if ($symbol -cnotmatch '^\s*[0-9a-fA-F]+\s+T\s+_(toj_\w+)\s*$') { throw 'Unexpected defined Mach-O export.' }
            $Matches[1]
        })
        if ((($exports | Sort-Object) -join ',') -cne ($expectedExports -join ',')) { throw 'Unexpected Mach-O C ABI exports.' }
        $null=Invoke-Audit $inspector @('-l',$library) 'dependencies'
        $loadCommands=[IO.File]::ReadAllText((Join-Path $output 'dependencies.stdout.txt'))
        $dylibCommands=@([regex]::Matches($loadCommands,'(?m)^\s*cmd LC_(?:LOAD|LOAD_WEAK|REEXPORT|LOAD_UPWARD|LAZY_LOAD)_DYLIB\s*$'))
        $imports=@([regex]::Matches($loadCommands,'(?m)^\s*cmd LC_(?:LOAD|LOAD_WEAK|REEXPORT|LOAD_UPWARD|LAZY_LOAD)_DYLIB[^\S\r\n]*\r?\n\s*cmdsize \d+[^\S\r\n]*\r?\n\s*name ([^\r\n]+) \(offset \d+\)') | ForEach-Object { $_.Groups[1].Value })
        if ($dylibCommands.Count -ne $imports.Count -or '/usr/lib/libSystem.B.dylib' -cnotin $imports -or
            @($imports | Where-Object { $_ -cnotmatch '^/(usr/lib/|System/Library/Frameworks/)[A-Za-z0-9_./+-]+$' -or ($_ -split '/') -contains '..' }).Count -or
            $loadCommands -cmatch '(?m)^\s*cmd LC_RPATH\s*$') {
            throw 'Unexpected Mach-O dependency or embedded runtime search path.'
        }
    } else {
        $dll=Join-Path $output 'tanayomi-open-jtalk.dll'
        $null=Invoke-Audit $compiler ($common+@('--rtlib=compiler-rt','--unwindlib=libunwind','-shared','-static','-fuse-ld=lld','-Wl,--gc-sections','-Wl,--exclude-all-symbols','-Wl,--verbose',('-Wl,-Map,'+$map),'-o',$dll)+$objects) 'link'
        $pe=Invoke-Audit $readobj @('--coff-imports','--coff-exports',$dll) 'pe'
        $imports=@([regex]::Matches($pe,'(?m)^\s*Name: ([^\r\n]+\.dll)\s*$') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $exports=@([regex]::Matches($pe,'(?m)^\s*Name: ([^\r\n]+)\s*$') | ForEach-Object { $_.Groups[1].Value.Trim() } | Where-Object { $_ -notmatch '\.dll$' } | Sort-Object -Unique)
        if ($imports.Count -eq 0 -or 'msvcrt.dll' -notin $imports -or @($imports | Where-Object { $_ -notmatch '^(KERNEL32|msvcrt|ADVAPI32)\.dll$' }).Count) { throw 'Unexpected DLL dependency; audit before adoption.' }
        if (($exports -join ',') -ne ($expectedExports -join ',')) { throw 'Unexpected C ABI exports.' }
        $loaded=@([IO.File]::ReadAllLines((Join-Path $output 'link.stderr.txt')) | Where-Object { $_ -match '^ld\.lld: Loaded ' } | ForEach-Object { $_ -replace '^ld\.lld: Loaded ','' -replace ' for .*$','' } | Sort-Object -Unique)
        if (-not $loaded.Count -or @($loaded | Where-Object { $_ -notmatch '^(libc\+\+|libunwind|libclang_rt\.builtins-x86_64|libmingw32|libmingwex|libmsvcrt|libkernel32|libadvapi32)\.a\(' }).Count) { throw 'Unexpected archive member; audit before adoption.' }
        [IO.File]::WriteAllLines((Join-Path $output 'linked-members.txt'),$loaded)
    }
    [IO.File]::WriteAllLines((Join-Path $output 'includes.txt'),@($headers | Sort-Object))
    $licenses=Join-Path $output 'licenses'; [void][IO.Directory]::CreateDirectory($licenses)
    Copy-Item -LiteralPath (Join-Path $jtalk 'COPYING') -Destination (Join-Path $licenses 'open-jtalk-COPYING.txt')
    Copy-Item -LiteralPath (Join-Path $jtalk 'mecab/COPYING') -Destination (Join-Path $licenses 'mecab-COPYING.txt')
    Copy-Item -LiteralPath (Join-Path $jtalk 'mecab-naist-jdic/COPYING') -Destination (Join-Path $licenses 'dictionary-COPYING.txt')
    Copy-Item -LiteralPath (Join-Path $hts 'COPYING') -Destination (Join-Path $licenses 'hts-COPYING.txt')
    Copy-Item -LiteralPath (Join-Path $temporary 'hts_voice_nitech_jp_atr503_m001-1.05/COPYING') -Destination (Join-Path $licenses 'model-COPYING.txt')
    $notices=@('open-jtalk-LICENSE.txt','hts-engine-LICENSE.txt','open-jtalk-dictionary-LICENSE.txt','nit-atr503-LICENSE.txt')
    if (-not $unix) { $notices+='open-jtalk-runtime-LICENSE.txt' }
    foreach ($notice in $notices) {
        Copy-Item -LiteralPath (Join-Path $repository ('assets/'+$notice)) -Destination (Join-Path $licenses $notice)
    }
    Copy-Item -LiteralPath (Join-Path $temporary 'open_jtalk_dic_utf_8-1.11') -Destination (Join-Path $output 'dictionary') -Recurse
    Copy-Item -LiteralPath (Join-Path $temporary 'hts_voice_nitech_jp_atr503_m001-1.05/nitech_jp_atr503_m001.htsvoice') -Destination (Join-Path $output 'nitech_jp_atr503_m001.htsvoice')
    if ($unix) {
        $manifest=[ordered]@{
            purpose='Inference-only audit build; not approved for product adoption'
            rid=$Rid; adoptionApproved=$false; runtimeLicenseAuditComplete=$false; nativeInvocationVerified=$false
            librarySha256=(Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant()
            bridgeSha256=(Get-FileHash -LiteralPath $files[0] -Algorithm SHA256).Hash.ToLowerInvariant()
            archiveSha256=$archiveHashes; compilerVersion=$version.Trim()
            compiler=$compiler; nm=$nm; dependencyInspector=$inspector
            compilerSha256=(Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant()
            nmSha256=(Get-FileHash -LiteralPath $nm -Algorithm SHA256).Hash.ToLowerInvariant()
            dependencyInspectorSha256=(Get-FileHash -LiteralPath $inspector -Algorithm SHA256).Hash.ToLowerInvariant()
            imports=$imports; exports=$exports; sources=$files; flags=$common; linkFlags=$linkFlags
            frontendLanguage='C11'; htsAndBridgeLanguage='C++14'
        }
        if ($mac) {
            $manifest.sdkPath=$sdk
            $manifest.sdkCanonicalName=$sdkInfo.CanonicalName
            $manifest.sdkSettingsSha256=(Get-FileHash -LiteralPath $sdkSettings -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'open-jtalk-build.json') -Encoding utf8
    } else {
        [ordered]@{purpose='Inference-only audit build; not approved for product adoption';llvm='23.1.4';toolchainArchiveSha256=$toolchainHash;dllSha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant();bridgeSha256=(Get-FileHash -LiteralPath (Join-Path $repository 'native/open-jtalk-bridge.cpp') -Algorithm SHA256).Hash.ToLowerInvariant();imports=$imports;exports=$exports;sources=$files;flags=$common;frontendLanguage='C11';htsAndBridgeLanguage='C++14';patches=@('HTS fatal -> terminal typed exception/status; no native error text','HTS fopen -> UTF8 wide path shim','MeCab CreateFileA -> UTF8 wide path shim','MeCab dictionary writing methods/private helpers excluded; no optimizer/training objects','Windows CRT errors -> stderr; host must also configure this before DLL load');runtimeLicenseAuditComplete=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'open-jtalk-build.json') -Encoding utf8
    }
    $complete=$true
    Write-Output ('Audit output: '+$output)
    $format=if ($linux) { 'ELF' } elseif ($mac) { 'Mach-O' } else { 'PE' }
    Write-Output ($format+' imports: '+($imports -join ', '))
    Write-Output 'Native invocation/path/cancellation and runtime member licenses still require explicit review.'
} finally {
    if ($null -ne $nativeErrorGuard) { $nativeErrorGuard.Dispose() }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process') }
    if (-not $processRunning) {
        if ($createdTemporary) { Remove-Owned $temporary (Join-Path $local 'tmp') '^open-jtalk-[0-9a-f]{32}$' }
        if ($createdOutput -and -not $complete) { Remove-Owned $output $outputRoot '^audit-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$' }
    } else { Write-Warning ('Owned compiler exit unresolved; preserve '+$temporary+' and '+$output) }
}
