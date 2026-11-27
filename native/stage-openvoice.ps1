#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [Parameter(Mandatory)][string]$ModelDirectory,
    [Parameter(Mandatory)][string]$CrtDirectory,
    [Parameter(Mandatory)][string]$EigenArchive,
    [Parameter(Mandatory)][string]$DumpbinExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$local = Join-Path $repo '.local'
function LocalPath([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { throw 'Empty staging path.' }
    $path = [IO.Path]::GetFullPath($value)
    if (-not $path.StartsWith($local + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Staging inputs and outputs must be under repository .local.' }
    for ($p=$path; $p; $p=[IO.Path]::GetDirectoryName($p)) {
        if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked staging path refused.' }
    }
    return $path
}
$work = LocalPath $WorkRoot
$models = LocalPath $ModelDirectory
$crt = [IO.Path]::GetFullPath($CrtDirectory)
$eigen = LocalPath $EigenArchive
$out = LocalPath $OutputDirectory
if (Test-Path -LiteralPath $out) { throw 'OutputDirectory already exists; choose a new directory.' }
$build = Join-Path $work 'ort-build'
$source = Join-Path $work 'ort-source'
$policy = Get-Content (Join-Path $PSScriptRoot 'openvoice-runtime-policy.json') -Raw | ConvertFrom-Json
$request = Get-Content (Join-Path $build 'requested-build.json') -Raw | ConvertFrom-Json
if ($request.SourceCommit -ne $policy.source -or $request.CombinedSourcePatchSha256 -ne $policy.patch -or $request.Feature -ne 'FusedConvFloatCpuAndCol2im1D') { throw 'Only the fixed CPU FusedConv/Col2im build may be staged.' }
$previousTemp=$env:TEMP; $previousTmp=$env:TMP
$env:TEMP = $env:TMP = Join-Path $local 'tmp'
[void][IO.Directory]::CreateDirectory($env:TEMP)
. (Join-Path $PSScriptRoot 'process-error-mode.ps1')
$guard = Enter-NativeProcessErrorMode
function RunTool([string]$exe, [string[]]$arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach($arg in $arguments){$info.ArgumentList.Add($arg)}
    $process=[Diagnostics.Process]::Start($info)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(60000)){ $process.Kill($true); $process.WaitForExit(); throw 'Audit tool timed out.' }
        if($process.ExitCode){throw 'Audit tool failed.'}
        return $stdout.GetAwaiter().GetResult()
    } finally {$process.Dispose()}
}
$created=$false; $completed=$false
try {
    $git=(Get-Command git.exe -ErrorAction Stop).Source
    if((RunTool $git @('-C',$source,'rev-parse','HEAD')).Trim() -ne $policy.source){throw 'Source HEAD changed.'}
    $diff=(RunTool $git @('-C',$source,'diff','--ita-invisible-in-index','--no-ext-diff','--')).Replace("`r`n","`n").TrimEnd([char[]]"`r`n")+"`n"
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($diff))) -ne $policy.patch){throw 'Source patch changed.'}
    $dlls=@(Get-ChildItem -LiteralPath $build -Filter onnxruntime.dll -File -Recurse)
    if($dlls.Count -ne 1){throw 'Expected exactly one completed runtime DLL.'}
    $dll=$dlls[0].FullName
    $nativeHash=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    if ($nativeHash -ne '1054ABA195985786A160E1F0220527B34734FACD5CA32D8DBEF094A53A07846C') {
        $receipt=Get-Content (Join-Path $build 'completed-build.json') -Raw | ConvertFrom-Json
        if($receipt.ExitCode -ne 0 -or $receipt.SourceCommit -ne $policy.source -or $receipt.PatchSha256 -ne $policy.patch -or
            $receipt.NativeSha256 -ne $nativeHash -or $receipt.MapSha256 -ne (Get-FileHash (Join-Path $build 'onnxruntime.map')).Hash -or
            $receipt.RequestSha256 -ne (Get-FileHash (Join-Path $build 'requested-build.json')).Hash){throw 'Completed build receipt mismatch.'}
    }
    $headers=RunTool $DumpbinExecutable @('/headers',$dll)
    if($headers -notmatch '8664 machine'){throw 'Runtime is not Windows x64.'}
    $exports=RunTool $DumpbinExecutable @('/exports',$dll)
    foreach($name in $policy.exports){if($exports -notmatch ('(?m)\b'+[regex]::Escape($name)+'\b')){throw 'Runtime ABI export missing.'}}
    $dependencies=RunTool $DumpbinExecutable @('/dependents',$dll)
    foreach($match in [regex]::Matches($dependencies,'(?im)^\s+(\S+\.dll)\s*$')) {
        if($match.Groups[1].Value -notmatch '(?i)^(KERNEL32|ADVAPI32|dbghelp|api-ms-win-core-path-l1-1-0|MSVCP140|VCRUNTIME140|VCRUNTIME140_1)\.dll$|^api-ms-win-crt-[a-z0-9-]+\.dll$'){throw 'Unreviewed runtime dependency.'}
    }
    $librarySet=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($line in [IO.File]::ReadLines((Join-Path $build 'onnxruntime.map'))) {
        $tail=$line.TrimEnd()
        if(-not $tail.EndsWith('.obj',[StringComparison]::OrdinalIgnoreCase)){continue}
        $tail=$tail.Substring($tail.LastIndexOf(' ')+1)
        $separator=$tail.IndexOf(':')
        if($separator -gt 0){[void]$librarySet.Add(($tail.Substring(0,$separator) -replace '\.lib$',''))}
    }
    $libraries=@($librarySet | Sort-Object)
    if(-not $libraries.Count){throw 'No linked library evidence.'}
    foreach($library in $libraries){if($library -notin $policy.linkedLibraries){throw "Unreviewed linked library: $library"}}
    $inputs=@{}
    foreach($file in $policy.files){
        $path=switch($file.name){
            'onnxruntime-LICENSE.txt' {Join-Path $source 'LICENSE'}
            'onnxruntime-NOTICES.txt' {Join-Path $source 'ThirdPartyNotices.txt'}
            'eigen-source.zip' {$eigen}
            'eigen-LICENSE.txt' {Join-Path $repo 'assets/aivis/Eigen-MPL-2.0.txt'}
            'microsoft-vc-runtime-LICENSE.txt' {Join-Path $repo 'assets/microsoft-vc-runtime-LICENSE.txt'}
            {$_ -in 'msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll'} {Join-Path $crt $_}
            default {Join-Path $models $_}
        }
        if((Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256){throw "Fixed input changed: $($file.name)"}
        $inputs[$file.name]=$path
    }
    [void][IO.Directory]::CreateDirectory($out)
    $created=$true
    foreach($name in $inputs.Keys){[IO.File]::Copy($inputs[$name],(Join-Path $out $name))}
    [IO.File]::Copy($dll,(Join-Path $out 'onnxruntime.dll'))
    $hash=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    $bytes=(Get-Item -LiteralPath $dll).Length
    [IO.File]::WriteAllText((Join-Path $out 'generated-runtime.xml'),"<OpenVoice><Source>$($policy.source)</Source><Patch>$($policy.patch)</Patch><Sha256>$hash</Sha256><Bytes>$bytes</Bytes></OpenVoice>")
    [ordered]@{schema=1;runtimeSource=$policy.source;sourcePatch=$policy.patch;nativeSha256=$hash;nativeBytes=$bytes;linkedLibraries=$libraries;files=@($policy.files)+@(@{name='onnxruntime.dll';bytes=$bytes;sha256=$hash});managedTested=$false;publicDistributionApproved=$false}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $out 'openvoice-build.json') -Encoding utf8
    [ordered]@{OpenVoiceNativeDirectory=$out;OpenVoiceGeneratedManifestSha256=(Get-FileHash (Join-Path $out 'generated-runtime.xml') -Algorithm SHA256).Hash;ManagedTested=$false;DistributionApproved=$false}|ConvertTo-Json
    $completed=$true
} finally {
    if($created -and -not $completed){
        # Only this invocation's newly created, verified output may be removed.
        $cleanup=LocalPath $out
        Remove-Item -LiteralPath $cleanup -Recurse -Force
    }
    $guard.Dispose()
    $env:TEMP=$previousTemp; $env:TMP=$previousTmp
}
