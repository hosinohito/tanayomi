[CmdletBinding()]
param(
    [ValidateSet('Plan','Acquire','Prepare','Compile','Audit','Stage')][string]$Action='Plan',
    [string]$WorkRoot='', [string]$OutputDirectory='', [string]$PythonExecutable='',
    [string]$VisualStudioDirectory='', [string]$WindowsSdkDirectory='', [string]$NinjaExecutable='',
    [string]$StageDirectory='', [string]$ManagedAuditPath='', [string]$CrtDirectory='',
    [ValidateRange(1,2)][int]$Parallel=2,
    [ValidateRange(1,600)][int]$BuildDeadlineSeconds=600,
    [switch]$Resume
)
# Windows x64 /MD, fixed source and tools. Never replaces application/tryout files.
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$work=if([string]::IsNullOrWhiteSpace($WorkRoot)){Join-Path $workspace '.local/skia-rebuild'}else{[IO.Path]::GetFullPath($WorkRoot)}
$output=if([string]::IsNullOrWhiteSpace($OutputDirectory)){Join-Path $workspace '.local/build/skia-rebuild/win-x64-md'}else{[IO.Path]::GetFullPath($OutputDirectory)}
$source=Join-Path $work 'source/skia';$audit=Join-Path $work 'audit'
$python=if([string]::IsNullOrWhiteSpace($PythonExecutable)){Join-Path $workspace '.local/neural-openvoice/venv/Scripts/python.exe'}else{[IO.Path]::GetFullPath($PythonExecutable)}
$visualStudio=if([string]::IsNullOrWhiteSpace($VisualStudioDirectory)){'C:/Program Files (x86)/Microsoft Visual Studio/2019/BuildTools'}else{[IO.Path]::GetFullPath($VisualStudioDirectory).Replace('\','/')}
$vc=Join-Path $visualStudio 'VC'
$sdk=if([string]::IsNullOrWhiteSpace($WindowsSdkDirectory)){'C:/Program Files (x86)/Windows Kits/10'}else{[IO.Path]::GetFullPath($WindowsSdkDirectory).Replace('\','/')}
$ninja=if([string]::IsNullOrWhiteSpace($NinjaExecutable)){Join-Path $visualStudio 'Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe'}else{[IO.Path]::GetFullPath($NinjaExecutable)}
$vc=$vc.Replace('\','/');$sdk=$sdk.Replace('\','/')
$gn=Join-Path $work 'tools/gn.exe'
$dumpbin=Join-Path $vc 'Tools/MSVC/14.29.30037/bin/Hostx64/x64/dumpbin.exe'
function Assert-SkiaPath([string]$Path,[string]$Boundary){
    $absolute=[IO.Path]::GetFullPath($Path);$boundary=[IO.Path]::GetFullPath($Boundary).TrimEnd('\','/')
    if(-not $absolute.StartsWith($boundary+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Skia output must stay inside its repository-owned boundary.'}
    for($parent=$absolute;$parent;$parent=Split-Path -Parent $parent){if((Test-Path -LiteralPath $parent)-and ((Get-Item -LiteralPath $parent -Force).Attributes-band [IO.FileAttributes]::ReparsePoint)){throw 'Linked Skia input/output paths are refused.'}}
}
Assert-SkiaPath $work (Join-Path $workspace '.local')
if((Split-Path -Parent $work)-cne (Join-Path $workspace '.local') -or (Split-Path -Leaf $work)-notmatch '^skia-rebuild(?:-[a-z0-9-]+)?$'){throw 'WorkRoot must be a dedicated .local/skia-rebuild[-name] directory.'}
Assert-SkiaPath $output (Join-Path $workspace '.local/build/skia-rebuild')
$argsText=@"
target_os="win"
target_cpu="x64"
is_official_build=true
is_debug=false
is_component_build=false
is_static_skiasharp=false
win_vc="$vc"
win_sdk="$sdk"
win_toolchain_version="14.29.30037"
win_vcvars_version="14.2"
win_sdk_version="10.0.19041.0"
clang_win=""
skia_enable_tools=false
skia_enable_fontmgr_win=true
skia_enable_fontmgr_win_gdi=false
skia_use_dng_sdk=false
skia_use_piex=false
skia_use_harfbuzz=false
skia_use_icu=false
skia_use_sfntly=false
skia_use_system_expat=false
skia_use_system_libjpeg_turbo=false
skia_use_system_libpng=false
skia_use_system_libwebp=false
skia_use_system_zlib=false
skia_enable_skottie=true
skia_use_gl=true
skia_use_vulkan=true
skia_use_direct3d=true
skia_use_wuffs=true
extra_cflags=[ "-DSKIA_C_DLL", "/MD", "/EHsc", "/Z7", "/guard:cf", "-D_HAS_AUTO_PTR_ETC=1" ]
extra_ldflags=[ "/DEBUG:FULL", "/DEBUGTYPE:CV,FIXUP", "/guard:cf", "/DELAYLOAD:d3d12.dll", "/DELAYLOAD:dxgi.dll", "/DELAYLOAD:D3DCOMPILER_47.dll", "/DEFAULTLIB:delayimp", "/MAP:$($output.Replace('\','/'))/libSkiaSharp.map", "/MAPINFO:EXPORTS", "/VERBOSE:LIB" ]
"@


# Fixed native-only environment. Dot-sourcing defines a function only; no child,
# vcvars/VsDevCmd, registry discovery or inherited SDK search path is used.
function Get-SkiaVsEnvironment {
    param([Parameter(Mandatory)][IDisposable]$ErrorModeGuard)
    if(-not [OperatingSystem]::IsWindows() -or $null -eq $ErrorModeGuard){throw 'A live Windows native error-mode guard is required.'}
    $vc=Join-Path $script:vc 'Tools/MSVC/14.29.30037'
    $sdkVersion='10.0.19041.0'
    $compilerBin=Join-Path $vc 'bin\Hostx64\x64'
    $sdkBin=Join-Path $sdk ('bin\'+$sdkVersion+'\x64')
    foreach($tool in @('cl.exe','link.exe','lib.exe','ml64.exe')){
        if(-not(Test-Path -LiteralPath (Join-Path $compilerBin $tool) -PathType Leaf)){throw 'Pinned VC build tool missing.'}
    }
    foreach($tool in @('rc.exe','mt.exe')){
        if(-not(Test-Path -LiteralPath (Join-Path $sdkBin $tool) -PathType Leaf)){throw 'Pinned Windows SDK tool missing.'}
    }
    if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $compilerBin 'cl.exe')).FileVersion -notlike '19.29.30038.1*'){throw 'Pinned compiler version changed.'}
    $includes=@((Join-Path $vc 'include'))+@('ucrt','shared','um','winrt','cppwinrt'|ForEach-Object{Join-Path $sdk ('Include\'+$sdkVersion+'\'+$_)})
    $libraries=@((Join-Path $vc 'lib\x64'),(Join-Path $sdk ('Lib\'+$sdkVersion+'\ucrt\x64')),(Join-Path $sdk ('Lib\'+$sdkVersion+'\um\x64')))
    foreach($directory in $includes+$libraries){if(-not(Test-Path -LiteralPath $directory -PathType Container)){throw 'Pinned include/library directory missing.'}}
    if([string]::IsNullOrWhiteSpace($env:SystemRoot)){throw 'Windows SystemRoot missing.'}
    $result=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $result['PATH']=@($compilerBin,$sdkBin,(Join-Path $env:SystemRoot 'System32'),$env:SystemRoot)-join ';'
    $result['INCLUDE']=$includes-join ';';$result['LIB']=$libraries-join ';';$result['LIBPATH']=Join-Path $vc 'lib\x64'
    $result['VSINSTALLDIR']=$visualStudio+'\';$result['VCINSTALLDIR']=Join-Path $visualStudio 'VC\'
    $result['VCToolsInstallDir']=$vc+'\';$result['VCToolsVersion']='14.29.30037'
    $result['WindowsSdkDir']=$sdk+'\';$result['WindowsSDKVersion']=$sdkVersion+'\'
    $result['VSLANG']='1033'
    return $result
}

function Invoke-SkiaCommand([string]$Executable,[string[]]$Arguments,[string]$Name,[int]$Seconds=180,[switch]$Compiler){
    $start=[Diagnostics.ProcessStartInfo]::new($Executable);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.WorkingDirectory=if(Test-Path -LiteralPath $source -PathType Container){$source}else{$workspace};$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    if($Compiler){foreach($pair in $fixedEnvironment.GetEnumerator()){$start.Environment[$pair.Key]=$pair.Value}}
    foreach($environmentName in @('CL','_CL_','LINK','_LINK_','CPATH','CPLUS_INCLUDE_PATH','C_INCLUDE_PATH','LIBRARY_PATH')){
        if($start.Environment.ContainsKey($environmentName)-and -not [string]::IsNullOrWhiteSpace($start.Environment[$environmentName])){throw 'Implicit compiler/linker environment flags are refused.'}
    }
    $stdoutPath=Join-Path $audit ($Name+'.stdout.txt');$stderrPath=Join-Path $audit ($Name+'.stderr.txt')
    $stdout=[IO.File]::Open($stdoutPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $stderr=[IO.File]::Open($stderrPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start;$started=$false;$confirmed=$false;$exitCode=$null
    $watch=[Diagnostics.Stopwatch]::StartNew();$copyOut=$null;$copyErr=$null;$killed=$false
    try{
        if(-not $process.Start()){throw 'Could not start the owned Skia tool.'};$started=$true
        if($Compiler){try{$process.PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal}catch{throw 'Could not lower the owned Skia compiler priority.'}}
        $copyOut=$process.StandardOutput.BaseStream.CopyToAsync($stdout);$copyErr=$process.StandardError.BaseStream.CopyToAsync($stderr)
        $maximum=if($Compiler){64MB}else{16MB}
        while(-not $process.WaitForExit(250)){
            if($watch.Elapsed.TotalSeconds-ge $Seconds){throw ('Owned Skia '+$Name+' deadline exceeded.')}
            if($stdout.Length+$stderr.Length-gt $maximum){throw 'Owned Skia tool output exceeded its bound.'}
        }
        $exitCode=$process.ExitCode
        if(-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($copyOut,$copyErr),5000)){throw 'Owned Skia output pipes did not close.'}
        if($stdout.Length+$stderr.Length-gt $maximum){throw 'Owned Skia tool output exceeded its bound.'}
        $stdout.Flush();$stderr.Flush()
        if($exitCode-ne 0){throw ('Owned Skia tool failed, exit='+$exitCode+'; inspect '+$Name+'.stderr.txt and .stdout.txt.')}
        if($Compiler){return [pscustomobject]@{ExitCode=$exitCode;ElapsedSeconds=$watch.Elapsed.TotalSeconds;Pid=$process.Id}}
        $reading=[IO.File]::Open($stdoutPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        try{$reader=[IO.StreamReader]::new($reading,[Text.Encoding]::UTF8);return $reader.ReadToEnd()}finally{$reading.Dispose()}
    }finally{
        try{
            if($started){
                if(-not $process.HasExited){$killed=$true;try{$process.Kill($true)}catch{if(-not $process.HasExited){throw}}}
                if(-not $process.WaitForExit(5000)){throw 'Owned Skia tool termination unconfirmed.'}
                if($null-ne $copyOut -and -not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($copyOut,$copyErr),5000)){throw 'Owned Skia descendant pipes remain open.'}
            }
            $confirmed=$true
        }finally{
            [pscustomobject]@{ExitCode=$exitCode;ElapsedSeconds=$watch.Elapsed.TotalSeconds;ExitConfirmed=$confirmed;KilledTree=$killed;Compiler=[bool]$Compiler;DeadlineSeconds=$Seconds}|
                ConvertTo-Json|Set-Content -LiteralPath (Join-Path $audit ($Name+'.result.json')) -Encoding utf8
            if($confirmed){$process.Dispose();$stdout.Dispose();$stderr.Dispose()}
            else{$script:SkiaUnconfirmedChild=[pscustomobject]@{Process=$process;Stdout=$stdout;Stderr=$stderr};Write-Warning 'Owned Skia child/output retained; no further launch is allowed.'}
        }
    }
}
function Set-DirectSkiaToolchain {
    $path=Join-Path $source 'gn/toolchain/BUILD.gn';$hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if($hash-ceq 'FE3D4723543289327B563001B2078FCFD77009D9FF5E9F1E58B9A99B10745A38'){return}
    if($hash-cne '6CD433DC1E0C7F72A070E6F9A10E7596BA4B7C0725153BFFB94305D4F1128D78'){throw 'Unexpected MSVC toolchain source is preserved.'}
    $patch=Join-Path $PSScriptRoot 'skia-direct-msvc.patch'
    if((Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash-cne 'FF9AE3D61436B99BEAB5C1A34E0A863A087F98B97D0C91A1FF51BDD030A3407D'){throw 'Reviewed Skia patch changed.'}
    $lines=[IO.File]::ReadAllLines($path);$result=[Collections.Generic.List[string]]::new();$position=0
    $inHunk=$false
    foreach($line in [IO.File]::ReadAllLines($patch)){
        if($line.StartsWith('--- ') -or $line.StartsWith('+++ ')){continue}
        if($line-match '^@@ -(\d+)(?:,\d+)? \+\d+(?:,\d+)? @@'){
            $wanted=[int]$Matches[1]-1
            while($position-lt $wanted){$result.Add($lines[$position]);$position++};$inHunk=$true;continue
        }
        if(-not $inHunk){throw 'Unexpected reviewed patch format.'}
        if($line.StartsWith('+')){$result.Add($line.Substring(1));continue}
        if($line.StartsWith(' ') -or $line.StartsWith('-')){
            if($position-ge $lines.Length -or $lines[$position]-cne $line.Substring(1)){throw 'Patch context mismatch; source preserved.'}
            if($line[0]-eq ' '){$result.Add($lines[$position])};$position++;continue
        }
        throw 'Unexpected reviewed patch hunk.'
    }
    while($position-lt $lines.Length){$result.Add($lines[$position]);$position++}
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($result-join "`n")+"`n")
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))-cne 'FE3D4723543289327B563001B2078FCFD77009D9FF5E9F1E58B9A99B10745A38'){throw 'Patched source hash mismatch; original source preserved.'}
    [IO.File]::WriteAllBytes($path,$bytes)
}
function Read-SkiaExports([string]$Text){@([regex]::Matches($Text,'(?m)^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)')|ForEach-Object{$_.Groups[1].Value}|Sort-Object -CaseSensitive)}
function Get-SkiaGraphPins {
    @(Get-ChildItem -LiteralPath $output -Filter '*.ninja' -File -Recurse|Sort-Object FullName|ForEach-Object{
        Assert-SkiaPath $_.FullName $output
        [pscustomobject]@{Path=[IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/');Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
function Stage-VerifiedSkia {
    if([string]::IsNullOrWhiteSpace($StageDirectory) -or [string]::IsNullOrWhiteSpace($ManagedAuditPath) -or [string]::IsNullOrWhiteSpace($CrtDirectory)){throw 'Stage requires explicit StageDirectory, ManagedAuditPath and CrtDirectory.'}
    $stage=[IO.Path]::GetFullPath($StageDirectory);Assert-SkiaPath $stage (Join-Path $workspace '.local/build')
    if(Test-Path -LiteralPath $stage){throw 'Existing Skia stage is preserved; choose a new StageDirectory.'}
    $managedPath=[IO.Path]::GetFullPath($ManagedAuditPath);Assert-SkiaPath $managedPath (Join-Path $workspace '.local')
    $crt=[IO.Path]::GetFullPath($CrtDirectory)
    $candidate=Join-Path $output 'libSkiaSharp.dll';$candidateHash=(Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
    $receiptPath=Join-Path $audit 'formal-compile.json';$receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
    $planPath=Join-Path $audit 'formal-plan.json';$plan=Get-Content -LiteralPath $planPath -Raw|ConvertFrom-Json
    if(-not $receipt.ExitConfirmed -or $receipt.CandidateSha256-cne $candidateHash -or $receipt.MapSha256-cne (Get-FileHash -LiteralPath (Join-Path $output 'libSkiaSharp.map') -Algorithm SHA256).Hash -or
       $receipt.PlanSha256-cne (Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash -or $receipt.SourcePinsSha256-cne (Get-FileHash -LiteralPath (Join-Path $audit 'formal-source-pins.json') -Algorithm SHA256).Hash){throw 'Stage requires the unchanged successful formal compile receipt, source pins, plan, DLL and MAP.'}
    if($plan.Output-cne $output -or -not $plan.SourceVerified -or $plan.CompileCommands-ne 1248 -or $plan.DngPiex -or $plan.StaticCrt -or
       $plan.ArgsSha256-cne (Get-FileHash -LiteralPath (Join-Path $output 'args.gn') -Algorithm SHA256).Hash -or
       $plan.ToolchainSha256-cne (Get-FileHash -LiteralPath (Join-Path $source 'gn/toolchain/BUILD.gn') -Algorithm SHA256).Hash -or
       ($plan.GraphPins|ConvertTo-Json -Depth 4 -Compress)-cne ((Get-SkiaGraphPins)|ConvertTo-Json -Depth 4 -Compress)){throw 'Stage requires unchanged formal prepared source and build graph.'}
    if((Get-Item -LiteralPath $managedPath).Length-gt 1MB){throw 'Managed Skia audit result exceeds its bound.'}
    $managed=Get-Content -LiteralPath $managedPath -Raw|ConvertFrom-Json
    if($managed.NativeSha256-cne $candidateHash -or $managed.ManagedPackage-cne '3.119.4' -or -not $managed.MalformedInputRejected -or
       $managed.Japanese.MissingGlyphs-ne 0 -or $managed.Japanese.DarkPixels-lt 500 -or $managed.Japanese.DarkPixels-gt 20000 -or
       @($managed.Decodes).Count-ne 5 -or @($managed.Encodes|Where-Object{$_.Supported -and $_.Format-in @('Png','Jpeg','Webp')}).Count-ne 3){throw 'Stage requires a successful managed raster/codec/Japanese smoke result for the exact DLL.'}
    $runtimePins=@{'msvcp140.dll'='43A9666C2F405258F165B53082860AC7838039A75FD2E85286085EDE0DA0273B';'vcruntime140.dll'='C865C3366A98431EC3A5959CB5AC3966081A43B82DFCD8BFEFAFE0146B1508DB';'vcruntime140_1.dll'='6C1E531C25AB2934A4EA9970598BC751D924D7CC5650DF3E1282B61D6CD24F42'}
    foreach($name in $runtimePins.Keys){$path=Join-Path $crt $name;for($parent=$path;$parent;$parent=Split-Path -Parent $parent){if((Get-Item -LiteralPath $parent -Force).Attributes-band [IO.FileAttributes]::ReparsePoint){throw 'Linked CRT staging input refused.'}};if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash-cne $runtimePins[$name]){throw 'Fixed CRT staging input changed.'}}
    $staging=$stage+'.stage-'+[Guid]::NewGuid().ToString('N');Assert-SkiaPath $staging (Join-Path $workspace '.local/build')
    try{
        New-Item -ItemType Directory -Path (Join-Path $staging 'audit')|Out-Null
        Copy-Item -LiteralPath $candidate -Destination (Join-Path $staging 'libSkiaSharp.dll')
        foreach($name in $runtimePins.Keys){Copy-Item -LiteralPath (Join-Path $crt $name) -Destination (Join-Path $staging $name)}
        foreach($name in @('formal-plan.json','formal-compile.json','formal-source-pins.json','formal-dll-audit.json','formal-license-inputs.json')){Copy-Item -LiteralPath (Join-Path $audit $name) -Destination (Join-Path $staging ('audit/'+$name))}
        Copy-Item -LiteralPath $managedPath -Destination (Join-Path $staging 'audit/managed-smoke.json')
        Copy-Item -LiteralPath (Join-Path $audit 'Skia-NOTICE.txt') -Destination (Join-Path $staging 'skia-native-NOTICES.txt')
        [ordered]@{schema=2;target='win-x64';skiaSharp='3.119.4';skiaCommit='7dbfc07dd33181f84e0958afb7ee805c6c769f0b';dng=$false;piex=$false;crt='dynamic /MD';library='libSkiaSharp.dll';sha256=$candidateHash;namedExports=924;validated=@('native ABI','managed raster','Japanese glyphs','PNG JPEG WebP GIF decode','PNG JPEG WebP encode');gpuValidated=$false;publicDistributionAuditComplete=$false}|
            ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $staging 'skia-build.json') -Encoding utf8
        $document=[Xml.XmlDocument]::new();$root=$document.CreateElement('SkiaBuild');[void]$document.AppendChild($root)
        foreach($pair in @{Schema='1';SourceCommit='7dbfc07dd33181f84e0958afb7ee805c6c769f0b';Runtime='dynamic /MD';PeVerified='true';ManagedVerified='true';CandidateSha256=$candidateHash}.GetEnumerator()){$root.SetAttribute($pair.Key,$pair.Value)}
        foreach($file in Get-ChildItem -LiteralPath $staging -File -Recurse|Sort-Object FullName){$entry=$document.CreateElement('File');$entry.SetAttribute('Path',[IO.Path]::GetRelativePath($staging,$file.FullName).Replace('\','/'));$entry.SetAttribute('Sha256',(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash);[void]$root.AppendChild($entry)}
        $document.Save((Join-Path $staging 'skia-generated.xml'))
        if((Get-FileHash -LiteralPath (Join-Path $staging 'libSkiaSharp.dll') -Algorithm SHA256).Hash-cne $candidateHash){throw 'Candidate changed while staging.'}
        foreach($name in $runtimePins.Keys){if((Get-FileHash -LiteralPath (Join-Path $staging $name) -Algorithm SHA256).Hash-cne $runtimePins[$name]){throw 'Fixed CRT changed while staging.'}}
        if(Test-Path -LiteralPath $stage){throw 'Skia destination appeared while staging; existing directory preserved.'}
        [IO.Directory]::Move($staging,$stage)
        [pscustomobject]@{StageDirectory=$stage;SkiaGeneratedManifestSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'skia-generated.xml') -Algorithm SHA256).Hash;CandidateSha256=$candidateHash;DistributionApproved=$false}|ConvertTo-Json
    }finally{if(Test-Path -LiteralPath $staging){Assert-SkiaPath $staging (Join-Path $workspace '.local/build');Remove-Item -LiteralPath $staging -Recurse -Force}}
}
function Write-SkiaNotices {
    $notice=[Text.StringBuilder]::new()
    [void]$notice.AppendLine('Isolated SkiaSharp 3.119.4 / Skia 7dbfc07dd33181f84e0958afb7ee805c6c769f0b')
    [void]$notice.AppendLine('DNG SDK and Piex are excluded. Dynamic MSVC runtime terms are provided separately by the application.')
    [void]$notice.AppendLine('Source configuration changes: restricted DEPS allowlist, no per-command VS initialization, /MD, DNG/Piex disabled.')
    [void]$notice.AppendLine('This software is based in part on the work of the Independent JPEG Group.')
    $files=@('LICENSE','AUTHORS','third_party/externals/d3d12allocator/LICENSE.txt','third_party/externals/d3d12allocator/NOTICES.txt','third_party/externals/expat/COPYING','third_party/externals/libjpeg-turbo/LICENSE.md','third_party/externals/libjpeg-turbo/README.ijg','third_party/externals/libpng/LICENSE','third_party/externals/libwebp/COPYING','third_party/externals/libwebp/PATENTS','third_party/externals/spirv-cross/LICENSE','third_party/externals/vulkanmemoryallocator/LICENSE.txt','third_party/externals/vulkan-headers/LICENSE.md','third_party/externals/wuffs/LICENSE','third_party/externals/zlib/LICENSE')
    $licenseInputs=@(foreach($file in $files){$path=Join-Path $source $file;[void]$notice.AppendLine("`n===== $file =====`n");[void]$notice.AppendLine([IO.File]::ReadAllText($path));[pscustomobject]@{Relative=$file;Bytes=(Get-Item -LiteralPath $path).Length;Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}})
    foreach($file in @('src/xamarin/sk_compatpaint.cpp','src/c/sk_general.cpp','modules/skcms/skcms.cc','third_party/externals/spirv-cross/spirv_cross.cpp','third_party/externals/zlib/crc32_simd.c','third_party/externals/zlib/adler32_simd.c','third_party/externals/zlib/cpu_features.c','third_party/externals/zlib/contrib/optimizations/chunkcopy.h')){
        [void]$notice.AppendLine("`n===== $file copyright/license header =====`n")
        $block=[regex]::Match([IO.File]::ReadAllText((Join-Path $source $file)),'\A/\*[\s\S]*?\*/').Value
        if([string]::IsNullOrEmpty($block)){throw 'Required source license header missing.'};[void]$notice.AppendLine($block)
    }
    [void]$notice.AppendLine("`n===== Khronos spirv.hpp permission notice =====`n")
    [void]$notice.AppendLine((Get-Content -LiteralPath (Join-Path $source 'third_party/externals/spirv-cross/spirv.hpp') -TotalCount 23)-join "`n")
    [void]$notice.AppendLine("`n===== Wuffs generated source notice =====`n")
    [void]$notice.AppendLine('Copyright 2017 The Wuffs Authors. Licensed under the Apache License, Version 2.0.')
    [void]$notice.AppendLine("`n===== Chromium BSD license (fixed official 120.0.6099.109 tag) =====`n")
    [void]$notice.AppendLine([IO.File]::ReadAllText((Join-Path $audit 'Chromium-BSD-LICENSE.txt')))
    [IO.File]::WriteAllText((Join-Path $audit 'Skia-NOTICE.txt'),$notice.ToString(),[Text.UTF8Encoding]::new($false))
    $licenseInputs|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $audit 'formal-license-inputs.json') -Encoding utf8
}
if($Action-eq 'Plan'){
    [pscustomobject]@{WorkRoot=$work;Source=$source;Output=$output;Python=$python;VisualStudio=$visualStudio;WindowsSdk=$sdk;Ninja=$ninja;Target='SkiaSharp';Runtime='/MD';Dng=$false;Piex=$false;Parallel=$Parallel;BuildDeadlineSeconds=$BuildDeadlineSeconds;
        Acquisition='native/build-skia.ps1 -Action Acquire';TransferLimitBytes=600MB;ExpandedLimitBytes=2GB;CompilationStarted=$false}|
        ConvertTo-Json -Depth 4
    exit 0
}
if(-not [OperatingSystem]::IsWindows()){throw 'The audited Skia configuration requires Windows x64.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent();try{if([Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Skia helper must run without administrator privileges.'}}finally{$identity.Dispose()}
$required=@($python)
if($Action-ne 'Acquire'){$required+=@($gn,$ninja,$dumpbin,(Join-Path $work 'acquisition.json'))}
foreach($path in $required){if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'Existing pinned Skia source/tool prerequisite missing; acquisition is a separate explicit operation.'}}
if($Action-ne 'Acquire'){
    if((Get-FileHash -LiteralPath $gn -Algorithm SHA256).Hash-cne 'D9144C267332EEB220F23FFE41696A826437854216531476D46BEE807C452511'){throw 'Pinned GN changed.'}
    if((Get-FileHash -LiteralPath $ninja -Algorithm SHA256).Hash-cne 'DBE413BABA338E88EF61619B09EDFAB4C7C5722920557B904A0F0C13D4EF8F47'){throw 'Pinned Ninja changed.'}
}
foreach($name in @('dng_sdk','piex')){if(Test-Path -LiteralPath (Join-Path $source ('third_party/externals/'+$name))){throw 'DNG/Piex source is forbidden in this checkout.'}}
New-Item -ItemType Directory -Path $audit -Force|Out-Null
$temporary=Join-Path $workspace ('.local/tmp/skia-formal-'+[Guid]::NewGuid().ToString('N'));Assert-SkiaPath $temporary (Join-Path $workspace '.local/tmp')
New-Item -ItemType Directory -Path $temporary|Out-Null
$env:TEMP=$temporary;$env:TMP=$temporary;$env:TMPDIR=$temporary;$env:PYTHONDONTWRITEBYTECODE='1';$env:PYTHONNOUSERSITE='1';$env:GIT_SYNC_DEPS_PATH=Join-Path $work 'DEPS.selected'
. (Join-Path $PSScriptRoot 'process-error-mode.ps1')
$guard=$null;$script:SkiaUnconfirmedChild=$null;$attempt='formal-'+$Action.ToLowerInvariant()+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6)
try{
    $guard=Enter-NativeProcessErrorMode
    if($Action-eq 'Acquire'){
        $acquired=Invoke-SkiaCommand $python @('-B',(Join-Path $PSScriptRoot 'acquire-skia.py'),'--work-root',$work,'--acquire') ($attempt+'-source') 600
        Write-Output $acquired.Trim();Write-Output 'PASS: bounded source/GN acquisition; no compiler was launched.'
    }else{
        $fixedEnvironment=Get-SkiaVsEnvironment -ErrorModeGuard $guard
        $verification=Invoke-SkiaCommand $python @('-B',(Join-Path $PSScriptRoot 'acquire-skia.py'),'--work-root',$work,'--verify') ($attempt+'-source')
        $verification|Set-Content -LiteralPath (Join-Path $audit 'formal-source-pins.json') -Encoding utf8
    }
    if($Action-eq 'Prepare'){
        Set-DirectSkiaToolchain
        New-Item -ItemType Directory -Path $output -Force|Out-Null
        $argsText=$argsText.Replace("`r`n","`n")
        $argsFile=Join-Path $output 'args.gn'
        if((Test-Path -LiteralPath $argsFile) -and [IO.File]::ReadAllText($argsFile)-cne $argsText){throw 'An existing different GN configuration is preserved.'}
        [IO.File]::WriteAllText($argsFile,$argsText,[Text.UTF8Encoding]::new($false))
        $version=Invoke-SkiaCommand $gn @('--version') ($attempt+'-version')
        $generated=Invoke-SkiaCommand $gn @('gen',$output,('--script-executable='+$python)) ($attempt+'-gen')
        $dependencies=Invoke-SkiaCommand $gn @('desc',$output,'//:SkiaSharp','deps','--all',('--script-executable='+$python)) ($attempt+'-deps')
        $commands=Invoke-SkiaCommand $ninja @('-C',$output,'-t','commands','SkiaSharp') ($attempt+'-commands')
        if($dependencies-match '(?i)dng_sdk|piex' -or $commands-match '(?i)SkRawCodec\.cpp|[/\\]piex[/\\]|(?<!\S)/MTd?(?=\s)|vcvarsall\.bat|vsdevcmd\.bat|\breg(?:\.exe)?\s+query'){throw 'Forbidden RAW/static CRT/environment initialization remains in the selected graph.'}
        $compiles=@($commands-split '\r?\n'|Where-Object{$_-match '(?i)[/\\]cl\.exe"?\s'})
        if($compiles.Count-ne 1248 -or @($compiles|Where-Object{$_-notmatch '(?<!\S)/MD(?=\s)'}).Count){throw 'Every one of the 1248 C/C++ commands must use /MD.'}
        $baseline=Join-Path $audit 'commands-before-single-env.txt';$baselineCompared=$false
        if(Test-Path -LiteralPath $baseline){
            $prefix='^cmd /c set __VSCMD_ARG_NO_LOGO=1 && set VSCMD_START_DIR=%CD% && "[^\"]*/vcvarsall\.bat" amd64 10\.0\.19041\.0 -vcvars_ver=14\.2 &&\s*'
            $before=@([IO.File]::ReadAllLines($baseline)|Where-Object{$_}|ForEach-Object{[regex]::Replace($_,$prefix,'').TrimStart()}|Sort-Object -CaseSensitive)
            $after=@($commands-split '\r?\n'|Where-Object{$_}|ForEach-Object{$_.TrimStart()}|Sort-Object -CaseSensitive)
            if($before.Count-ne $after.Count){throw 'Selected command count changed.'}
            for($index=0;$index-lt $before.Count;$index++){if($before[$index]-cne $after[$index]){throw 'Arguments changed beyond removal of the reviewed initialization prefix.'}}
            $baselineCompared=$true
        }
        [pscustomobject]@{SourceCommit='7dbfc07dd33181f84e0958afb7ee805c6c769f0b';CompileCommands=$compiles.Count;ArgsSha256=(Get-FileHash -LiteralPath $argsFile -Algorithm SHA256).Hash;
            ToolchainSha256=(Get-FileHash -LiteralPath (Join-Path $source 'gn/toolchain/BUILD.gn') -Algorithm SHA256).Hash;GraphPins=@(Get-SkiaGraphPins);SourceVerified=$true;BaselineCompared=$baselineCompared;DngPiex=$false;StaticCrt=$false;CompileStarted=$false;Version=$version.Trim();Output=$output}|
            ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $audit 'formal-plan.json') -Encoding utf8
        Write-Output $generated.Trim();Write-Output 'PASS: formal Skia preparation; no compiler was launched.'
    }
    elseif($Action-eq 'Compile'){
        $plan=Get-Content -LiteralPath (Join-Path $audit 'formal-plan.json') -Raw|ConvertFrom-Json
        if(-not $plan.SourceVerified -or $plan.CompileCommands-ne 1248 -or $plan.DngPiex -or $plan.StaticCrt){throw 'Verified formal Prepare output required.'}
        if($plan.Output-cne $output -or -not @($plan.GraphPins).Count -or ($plan.GraphPins|ConvertTo-Json -Depth 4 -Compress)-cne ((Get-SkiaGraphPins)|ConvertTo-Json -Depth 4 -Compress) -or
           (Get-FileHash -LiteralPath (Join-Path $output 'args.gn') -Algorithm SHA256).Hash-cne $plan.ArgsSha256 -or
           (Get-FileHash -LiteralPath (Join-Path $source 'gn/toolchain/BUILD.gn') -Algorithm SHA256).Hash-cne $plan.ToolchainSha256){throw 'Prepared Skia configuration changed.'}
        $previous=@(Get-ChildItem -LiteralPath $audit -File|Where-Object{$_.Name-match 'compile.*(?:\.|-)result\.json$' -and (Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json).Compiler})
        if($previous.Count -and -not $Resume){throw 'A previous compile exists; review it and explicitly choose -Resume.'}
        foreach($record in $previous){if(-not (Get-Content -LiteralPath $record.FullName -Raw|ConvertFrom-Json).ExitConfirmed){throw 'Previous compiler exit is unconfirmed.'}}
        $result=Invoke-SkiaCommand $ninja @('-C',$output,'-j',[string]$Parallel,'SkiaSharp') $attempt $BuildDeadlineSeconds -Compiler
        [pscustomobject]@{ExitConfirmed=$true;CandidateSha256=(Get-FileHash -LiteralPath (Join-Path $output 'libSkiaSharp.dll') -Algorithm SHA256).Hash;MapSha256=(Get-FileHash -LiteralPath (Join-Path $output 'libSkiaSharp.map') -Algorithm SHA256).Hash;
            PlanSha256=(Get-FileHash -LiteralPath (Join-Path $audit 'formal-plan.json') -Algorithm SHA256).Hash;SourcePinsSha256=(Get-FileHash -LiteralPath (Join-Path $audit 'formal-source-pins.json') -Algorithm SHA256).Hash}|
            ConvertTo-Json|Set-Content -LiteralPath (Join-Path $audit 'formal-compile.json') -Encoding utf8
        Write-Output ('PASS: owned Skia compile, '+[Math]::Round($result.ElapsedSeconds,3)+'s; application outputs unchanged.')
    }
    elseif($Action-in @('Audit','Stage')){
        $target=Join-Path $output 'libSkiaSharp.dll';$reference=Join-Path $workspace '.local/nuget/skiasharp.nativeassets.win32/3.119.4/runtimes/win-x64/native/libSkiaSharp.dll'
        foreach($path in @($target,$reference,(Join-Path $output 'libSkiaSharp.map'))){if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'Completed candidate/reference/MAP required.'}}
        if((Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash-cne '7DEC3BA900AB353491E6446F0083739924C6F8DD668832E2F09D38EBFFDBBE1C'){throw 'Pinned original ABI reference changed.'}
        $headers=Invoke-SkiaCommand $dumpbin @('/headers',$target) ($attempt+'-headers') 20
        $dependencies=Invoke-SkiaCommand $dumpbin @('/dependents',$target) ($attempt+'-dependents') 20
        $imports=Invoke-SkiaCommand $dumpbin @('/imports',$target) ($attempt+'-imports') 20
        $exports=Read-SkiaExports (Invoke-SkiaCommand $dumpbin @('/exports',$target) ($attempt+'-exports') 20)
        $expected=Read-SkiaExports (Invoke-SkiaCommand $dumpbin @('/exports',$reference) ($attempt+'-reference') 20)
        if($exports.Count-ne 924 -or $expected.Count-ne 924 -or @($expected|Where-Object{$_-cnotin $exports}).Count){throw 'Named C exports differ from the fixed ABI reference.'}
        $dlls=@([regex]::Matches($dependencies,'(?im)^\s+([^\s]+\.dll)\s*$')|ForEach-Object{$_.Groups[1].Value}|Sort-Object -Unique)
        if($headers-notmatch '8664 machine' -or -not @($dlls|Where-Object{$_-match '(?i)^vcruntime140\.dll$'}).Count){throw 'x64 dynamic CRT candidate required.'}
        foreach($dependency in $dlls){if($dependency-notmatch '(?i)^(KERNEL32|USER32|ole32|FONTSUB|d3d12|dxgi|D3DCOMPILER_47|MSVCP140|VCRUNTIME140|VCRUNTIME140_1)\.dll$|^api-ms-win-crt-[a-z0-9-]+\.dll$'){throw 'Candidate imports an unaudited native dependency.'}}
        foreach($dependency in @('MSVCP140.dll','VCRUNTIME140.dll','VCRUNTIME140_1.dll')){if($dependency-notin $dlls){throw 'Required dynamic CRT import is absent.'}}
        $map=[IO.File]::ReadAllText((Join-Path $output 'libSkiaSharp.map'));$objects=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($match in [regex]::Matches($map,'(?m)(\S+):([^\s]+\.obj)\s*$')){[void]$objects.Add($match.Groups[1].Value+':'+$match.Groups[2].Value)}
        foreach($object in $objects){if($object-match '(?i)\bdng_|\bpiex\b|SkRawCodec|dng_sdk|^(libcmt|libcpmt|libucrt|libvcruntime)(\.lib)?:'){throw 'RAW or static CRT object present in the linked map.'}}
        Write-SkiaNotices
        [pscustomobject]@{CandidateSha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash;CandidateBytes=(Get-Item -LiteralPath $target).Length;NamedExports=$exports.Count;Dependencies=$dlls;UniqueLibraryObjects=$objects.Count;
            DngPiexObjects=0;StaticCrtObjects=0;SourceCommit='7dbfc07dd33181f84e0958afb7ee805c6c769f0b';GpuTested=$false;AvaloniaTested=$false;DistributionApproved=$false}|
            ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $audit 'formal-dll-audit.json') -Encoding utf8
        if($Action-eq 'Stage'){Stage-VerifiedSkia}
        else{Write-Output 'PASS: formal PE/ABI/object audit; managed/UI and distribution checks remain separate.'}
    }
}finally{
    if($null-eq $script:SkiaUnconfirmedChild){
        if((Get-Item -LiteralPath $temporary -Force).Attributes-band [IO.FileAttributes]::ReparsePoint){throw 'Linked Skia temporary root retained.'}
        Remove-Item -LiteralPath $temporary -Recurse -Force
        if($null-ne $guard){$guard.Dispose()}
    }else{$script:RetainedSkiaGuard=$guard;Write-Warning ('Retain owned Skia temporary root: '+$temporary)}
}
