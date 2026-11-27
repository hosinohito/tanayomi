[CmdletBinding()]
param(
    [ValidateSet('Plan','Acquire','Prepare','Verify','AcquireSdkInstaller','VerifySdk')][string]$Action='Plan',
    [switch]$IncludeSdkPayloads,
    [string]$WindowsSdkDirectory=''
)
# Downloads developer tools from Microsoft; never runs an installer or changes
# PATH, registry, Visual Studio instances, or application resources.
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=Join-Path $workspace '.local/vendor/windows-toolchain-16.10.2'
$cache=Join-Path $root 'downloads'
$vs=Join-Path $root 'VisualStudio'
$lock=Get-Content (Join-Path $PSScriptRoot 'windows-toolchain.lock.json') -Raw | ConvertFrom-Json
$checkedDirectories=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if($Action -eq 'VerifySdk'){
    $sdk=if([string]::IsNullOrWhiteSpace($WindowsSdkDirectory)){Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'}else{[IO.Path]::GetFullPath($WindowsSdkDirectory)}
    foreach($relative in @('Include/10.0.19041.0/ucrt/stdio.h','Include/10.0.19041.0/shared/sdkddkver.h','Include/10.0.19041.0/um/Windows.h','Include/10.0.19041.0/winrt/windows.foundation.h','Include/10.0.19041.0/cppwinrt/winrt/base.h','Lib/10.0.19041.0/ucrt/x64/ucrt.lib','Lib/10.0.19041.0/um/x64/kernel32.lib','bin/10.0.19041.0/x64/rc.exe','bin/10.0.19041.0/x64/mt.exe')){
        if(-not (Test-Path -LiteralPath (Join-Path $sdk $relative) -PathType Leaf)){throw ('SDK component missing: '+$relative)}
    }
    [pscustomobject]@{WindowsSdkDirectory=$sdk;Version=$lock.sdkVersion;RequiredFilesPresent=$true;FullSdkPayloadAudit=$false}|ConvertTo-Json
    return
}
function Assert-Owned([string]$Path){
    $p=[IO.Path]::GetFullPath($Path)
    if(-not $p.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Toolchain path escaped dedicated repository directory.'}
    for($a=$p;$a;$a=Split-Path -Parent $a){
        if($checkedDirectories.Contains($a)){break}
        if(Test-Path -LiteralPath $a){
            $item=Get-Item -LiteralPath $a -Force
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked toolchain paths are refused.'}
            if($item.PSIsContainer){$null=$checkedDirectories.Add($a)}
        }
    }
    return $p
}
$payloads=@(foreach($p in $lock.packages){foreach($f in $p.payloads){[pscustomobject]@{Package=$p.id;File=$f;Path=(Assert-Owned (Join-Path $cache ($p.id+'/'+$f.fileName)))}}})
if($IncludeSdkPayloads){$payloads+=@(foreach($f in $lock.sdk.payloads){[pscustomobject]@{Package=$lock.sdk.id;File=$f;Path=(Assert-Owned (Join-Path $cache ($lock.sdk.id+'/'+$f.fileName)))}})}
if($Action -eq 'AcquireSdkInstaller'){$payloads=@(foreach($f in @($lock.sdk.payloads|Where-Object {$_.fileName -eq 'winsdksetup.exe'})){[pscustomobject]@{Package=$lock.sdk.id;File=$f;Path=(Assert-Owned (Join-Path $cache ($lock.sdk.id+'/'+$f.fileName)))}})}
if($Action -eq 'Plan'){
    [pscustomobject]@{VisualStudioDirectory=$vs;Compiler=$lock.compiler;Toolset=$lock.toolset;WindowsSdkVersion=$lock.sdkVersion;DownloadBytes=($payloads.File|Measure-Object size -Sum).Sum;SdkPayloadsIncluded=[bool]$IncludeSdkPayloads;SdkPortableExtractionImplemented=$false;SdkInstaller=(Join-Path $cache ($lock.sdk.id+'/winsdksetup.exe'));SdkInstallerArguments='/features OptionId.DesktopCPPx64 OptionId.UWPCPP /quiet /norestart';InstallerExecuted=$false}|ConvertTo-Json
    return
}
foreach($p in $payloads){
    if($Action -in @('Acquire','AcquireSdkInstaller') -and -not (Test-Path -LiteralPath $p.Path)){
        $uri=[Uri]$p.File.url
        if($uri.Scheme -ne 'https' -or $uri.Host -ne 'download.visualstudio.microsoft.com'){throw 'Only Microsoft HTTPS tool payloads are allowed.'}
        New-Item -ItemType Directory -Path (Split-Path -Parent $p.Path) -Force | Out-Null
        $part=Assert-Owned ($p.Path+'.part-'+[Guid]::NewGuid().ToString('N'))
        try{
            Invoke-WebRequest -Uri $uri -OutFile $part -TimeoutSec 180
            # Some official VSIX responses differ from the catalog size while
            # matching its original SHA-256. SHA-256 is the content authority.
            if((Get-Item -LiteralPath $part).Length -gt ($p.File.size+1MB) -or (Get-FileHash -LiteralPath $part -Algorithm SHA256).Hash -ne $p.File.sha256){throw ('Pinned tool payload mismatch: '+$p.Package)}
            Move-Item -LiteralPath $part -Destination $p.Path
        }finally{if(Test-Path -LiteralPath $part){Remove-Item -LiteralPath $part -Force}}
    }
    if(-not (Test-Path -LiteralPath $p.Path -PathType Leaf) -or (Get-Item -LiteralPath $p.Path).Length -gt ($p.File.size+1MB) -or (Get-FileHash -LiteralPath $p.Path -Algorithm SHA256).Hash -ne $p.File.sha256){throw ('Missing or changed pinned tool payload; run Acquire: '+$p.Package)}
}
if($Action -in @('Acquire','AcquireSdkInstaller')){Write-Output 'Pinned Microsoft developer-tool payloads verified; no installer executed.';return}
# ZIP contents are the exact Visual Studio-relative tree; only Contents/ is
# extracted, preserving all compiler DLLs and licenses supplied in that tree.
$seen=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
$total=[long]0
foreach($p in @($payloads|Where-Object {$_.Package -ne $lock.sdk.id})){
    $zip=[IO.Compression.ZipFile]::OpenRead($p.Path)
    try{foreach($entry in $zip.Entries){
        if(-not $entry.FullName.StartsWith('Contents/',[StringComparison]::Ordinal) -or $entry.FullName.EndsWith('/')){continue}
        $relative=[Uri]::UnescapeDataString($entry.FullName.Substring(9))
        if([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or @($relative -split '[/\\]' | Where-Object {$_ -eq '..'}).Count){throw 'Unsafe relative toolchain ZIP path.'}
        $target=Assert-Owned (Join-Path $vs $relative)
        if(-not $target.StartsWith($vs+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or ($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000){throw 'Unsafe toolchain ZIP entry.'}
        $total+=$entry.Length
        if($total -gt 1GB){throw 'Expanded developer tools exceed the fixed limit.'}
        $stream=$entry.Open()
        try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if($seen.ContainsKey($target) -and $seen[$target] -ne $hash){throw 'Conflicting toolchain ZIP entries.'};$seen[$target]=$hash
        if(Test-Path -LiteralPath $target){if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash){throw 'Existing portable toolchain differs; preserve it for inspection.'}}
        elseif($Action -eq 'Prepare'){
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$false)
        }else{throw 'Portable toolchain is absent; run Prepare.'}
    }}finally{$zip.Dispose()}
}
foreach($file in Get-ChildItem -LiteralPath $vs -Recurse -File -Force){if(-not $seen.ContainsKey($file.FullName)){throw 'Unexpected file in portable toolchain; preserve it for inspection.'}}
$compiler=Assert-Owned (Join-Path $vs 'VC/Tools/MSVC/14.29.30037/bin/Hostx64/x64/cl.exe')
if([Diagnostics.FileVersionInfo]::GetVersionInfo($compiler).FileVersion -notlike '19.29.30038.1*'){throw 'Pinned compiler file version mismatch.'}
$ninja=Assert-Owned (Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe')
if(-not (Test-Path -LiteralPath $ninja -PathType Leaf)){throw 'Pinned Ninja missing.'}
if((Get-FileHash -LiteralPath $ninja -Algorithm SHA256).Hash -ne 'DBE413BABA338E88EF61619B09EDFAB4C7C5722920557B904A0F0C13D4EF8F47'){throw 'Pinned Ninja SHA-256 mismatch.'}
$crt=Assert-Owned (Join-Path $vs 'VC/Redist/MSVC/14.29.30036/x64/Microsoft.VC142.CRT')
$runtimeHashes=@{'msvcp140.dll'='43A9666C2F405258F165B53082860AC7838039A75FD2E85286085EDE0DA0273B';'vcruntime140.dll'='C865C3366A98431EC3A5959CB5AC3966081A43B82DFCD8BFEFAFE0146B1508DB';'vcruntime140_1.dll'='6C1E531C25AB2934A4EA9970598BC751D924D7CC5650DF3E1282B61D6CD24F42'}
foreach($name in $runtimeHashes.Keys){if((Get-FileHash -LiteralPath (Join-Path $crt $name) -Algorithm SHA256).Hash -ne $runtimeHashes[$name]){throw 'Pinned redistributable CRT mismatch.'}}
[pscustomobject]@{VisualStudioDirectory=$vs;NinjaExecutable=$ninja;RuntimeDirectory=$crt;Compiler=$lock.compiler;VerifiedFiles=$seen.Count;WindowsSdk='Use AcquireSdkInstaller, run the official setup explicitly, then VerifySdk; this script never runs setup.'}|ConvertTo-Json
