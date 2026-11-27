#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorldDirectory,
    [Parameter(Mandatory)][string]$OpenJtalkDirectory,
    [Parameter(Mandatory)][string]$OpenVoiceDirectory,
    [Parameter(Mandatory)][string]$SkiaDirectory,
    [Parameter(Mandatory)][string]$AivisDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$OpenVoiceManifestSha256,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$SkiaManifestSha256,
    [switch]$CheckOnly
)
$ErrorActionPreference='Stop'
if(-not $IsWindows -or -not [Environment]::Is64BitProcess){throw 'Windows x64 PowerShell is required.'}
$repo=$PSScriptRoot
$local=[IO.Path]::GetFullPath((Join-Path $repo '.local'))
function InputDirectory([string]$Path){
    $p=[IO.Path]::GetFullPath($Path)
    if(-not $p.StartsWith($local+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Generated inputs must be inside this checkout .local.'}
    if(-not (Test-Path -LiteralPath $p -PathType Container)){throw "Input directory missing: $p"}
    for($parent=$p;$parent;$parent=Split-Path -Parent $parent){
        if((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked input paths are refused.'}
    }
    return $p
}
$values=@{
    WorldNativeDirectory=(InputDirectory $WorldDirectory)
    OpenJtalkNativeDirectory=(InputDirectory $OpenJtalkDirectory)
    OpenVoiceNativeDirectory=(InputDirectory $OpenVoiceDirectory)
    SkiaNativeDirectory=(InputDirectory $SkiaDirectory)
    AivisDirectory=(InputDirectory $AivisDirectory)
    OpenVoiceGeneratedManifestSha256=$OpenVoiceManifestSha256.ToUpperInvariant()
    SkiaGeneratedManifestSha256=$SkiaManifestSha256.ToUpperInvariant()
}
foreach($entry in @(@('OpenVoiceNativeDirectory','generated-runtime.xml','OpenVoiceGeneratedManifestSha256'),@('SkiaNativeDirectory','skia-generated.xml','SkiaGeneratedManifestSha256'))){
    if((Get-FileHash (Join-Path $values[$entry[0]] $entry[1])).Hash -ne $values[$entry[2]]){throw 'Generated stage manifest differs from the supplied build receipt.'}
}
$voices=Get-Content (Join-Path $values.AivisDirectory 'voices.json') -Raw|ConvertFrom-Json
foreach($id in @('aivis-jvnv-f1-jp','aivis-jvnv-f2-jp','aivis-jvnv-m1-jp','aivis-jvnv-m2-jp')){
    if($id -notin $voices.voices.id){throw "Full build requires JVNV voice: $id"}
}
$saved=@{}
foreach($name in $values.Keys){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process');[Environment]::SetEnvironmentVariable($name,$values[$name],'Process')}
$saved.TanayomiFullBuild=$env:TanayomiFullBuild
foreach($name in @('TEMP','TMP','TMPDIR','DOTNET_CLI_HOME','NUGET_PACKAGES')){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$env:TEMP=$env:TMP=$env:TMPDIR=Join-Path $local 'tmp'
[void][IO.Directory]::CreateDirectory($env:TEMP)
$env:DOTNET_CLI_HOME=Join-Path $local 'cli'
$env:NUGET_PACKAGES=Join-Path $local 'nuget'
. (Join-Path $repo 'native/process-error-mode.ps1')
$guard=Enter-NativeProcessErrorMode
$pwsh=(Get-Process -Id $PID).Path
function Dev([string[]]$Arguments){
    & $pwsh -NoProfile -File (Join-Path $repo 'dev.ps1') @Arguments
    if($LASTEXITCODE -ne 0){throw "Full build step failed: $($Arguments[0]) (exit $LASTEXITCODE)"}
}
try {
    foreach($component in @(@('World',$values.WorldNativeDirectory),@('OpenJtalk',$values.OpenJtalkNativeDirectory))){
        & $pwsh -NoProfile -File (Join-Path $repo 'native/verify-speech-native-runtime.ps1') -Component $component[0] -AuditDirectory $component[1]
        if($LASTEXITCODE -ne 0){throw "Native source/runtime policy failed: $($component[0])"}
    }
    # Normal tests may have no RID. Enable the full-build gate only for publish.
    $env:TanayomiFullBuild=$null
    if(-not $CheckOnly){
        Dev @('test','-Configuration','Release')
        Dev @('native-test','-Configuration','Release','-WorldLibrary',(Join-Path $values.WorldNativeDirectory 'tanayomi-world.dll'))
        Dev @('open-jtalk-test','-Configuration','Release','-OpenJtalkAudit',$values.OpenJtalkNativeDirectory)
        Dev @('neural-test','-Configuration','Release','-NeuralAudit',$values.OpenVoiceNativeDirectory,'-NeuralFixture',(Join-Path $repo 'tests/Fixtures/openvoice-oracle-v1.json'))
    }
    $env:TanayomiFullBuild='true'
    if($CheckOnly){
        $sdk=Join-Path $local 'dotnet/dotnet.exe'
        if(-not (Test-Path $sdk)){$sdk=(Get-Command dotnet.exe -ErrorAction Stop).Source}
        & $sdk msbuild (Join-Path $repo 'src/Tanayomi.Desktop/Tanayomi.Desktop.csproj') '-t:ValidateFullWindowsBuild;ValidateAuditedSkia;ValidateOpenVoiceGeneratedIdentity' -p:RuntimeIdentifier=win-x64 -nologo
        if($LASTEXITCODE -ne 0){throw 'Full build inputs are incomplete.'}
        return
    }
    Dev @('publish')
    $env:TanayomiFullBuild=$null
    $product=Join-Path $local 'build/app/Tanayomi.Desktop.exe'
    Dev @('open-jtalk-product-test','-Configuration','Release','-OpenJtalkExecutable',$product)
    Dev @('bundled-speech-product-test','-Configuration','Release','-OpenJtalkExecutable',$product)
    Dev @('utau-context-product-test','-Configuration','Release','-OpenJtalkExecutable',$product,'-NeuralOnly')
    Dev @('utau-context-product-test','-Configuration','Release','-OpenJtalkExecutable',$product,'-WaveformOnly')
    Write-Output ('Full Windows build and product checks passed: '+(Join-Path $local 'build/app/Tanayomi.Desktop.exe'))
} finally {
    $guard.Dispose()
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}
}
