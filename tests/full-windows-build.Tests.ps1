#Requires -Version 7.4
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tmp=Join-Path $repo ('.local/tmp/full-build-test-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($tmp)
$sdk=Join-Path $repo '.local/dotnet/dotnet.exe'
if(-not (Test-Path $sdk)){$sdk=(Get-Command dotnet.exe -ErrorAction Stop).Source}
$saved=@{}
foreach($name in @('TEMP','TMP','TMPDIR','DOTNET_CLI_HOME','NUGET_PACKAGES')){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$env:TEMP=$env:TMP=$env:TMPDIR=$tmp
$env:DOTNET_CLI_HOME=Join-Path $repo '.local/cli'
$env:NUGET_PACKAGES=Join-Path $repo '.local/nuget'
. (Join-Path $repo 'native/process-error-mode.ps1')
$guard=Enter-NativeProcessErrorMode
try {
    $props=@{TanayomiFullBuild='true';RuntimeIdentifier='win-x64';WorldNativeEnabled='true';OpenJtalkNativeEnabled='true';OpenVoiceNativeEnabled='true';AuditedSkiaEnabled='true'}
    foreach($name in @('OpenJtalkNativeDirectory','TohokuOpenJtalkDirectory','MmdagentVoiceDirectory','VoicevoxDirectory','SupertonicDirectory','AivisDirectory','OpenVoiceNativeDirectory','SkiaNativeDirectory')){$props[$name]=Join-Path $tmp $name}
    $props.SkiaNativeNotice=Join-Path $props.SkiaNativeDirectory 'skia-native-NOTICES.txt'
    $target=Join-Path $repo 'native/FullWindowsBuild.targets'
    [xml]$definition=Get-Content $target -Raw
    foreach($item in $definition.Project.Target.ItemGroup.FullBuildRequired){
        foreach($pathTemplate in $item.Include.Split(';')){
            $path=$pathTemplate
            foreach($key in $props.Keys){$path=$path.Replace('$('+ $key +')',$props[$key])}
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
            [IO.File]::WriteAllText($path,'fixture')
        }
    }
    $project=Join-Path $tmp 'fixture.proj'
    $xml='<Project><PropertyGroup>'
    foreach($key in $props.Keys){$xml+='<'+$key+'>'+[Security.SecurityElement]::Escape($props[$key])+'</'+$key+'>'}
    $xml+='</PropertyGroup><Import Project="'+[Security.SecurityElement]::Escape($target)+'" /></Project>'
    [IO.File]::WriteAllText($project,$xml)
    function Check([bool]$Expected,[string[]]$Extra){
        $result=& $sdk msbuild $project -t:ValidateFullWindowsBuild -nologo @Extra 2>&1
        if(($LASTEXITCODE -eq 0) -ne $Expected){throw ('Unexpected full-build result: '+($result -join "`n"))}
    }
    Check $true @()
    Check $false @('-p:WorldNativeEnabled=false')
    Check $false @('-p:AuditedSkiaEnabled=false')
    Check $false @('-p:OpenVoiceNativeEnabled=false')
    Check $false @('-p:RuntimeIdentifier=linux-x64')
    foreach($missing in @((Join-Path $props.SkiaNativeDirectory 'libSkiaSharp.dll'),(Join-Path $props.OpenVoiceNativeDirectory 'vcruntime140.dll'))){
        Remove-Item -LiteralPath $missing
        Check $false @()
        [IO.File]::WriteAllText($missing,'fixture')
    }
    Remove-Item -LiteralPath (Join-Path $props.TohokuOpenJtalkDirectory 'tohoku-f01-sad.htsvoice')
    Check $false @()
    'PASS full build completeness gate: 8 cases'
    # Generated OpenVoice identifiers must be explicitly pinned, never merely
    # trusted because a manifest happens to be adjacent to the native DLL.
    $native=Join-Path $tmp 'generated-openvoice'
    [void][IO.Directory]::CreateDirectory($native)
    $dll=Join-Path $native 'onnxruntime.dll'
    [IO.File]::WriteAllText($dll,'fixture')
    $manifest=Join-Path $native 'generated-runtime.xml'
    $policy=Get-Content (Join-Path $repo 'native/openvoice-runtime-policy.json') -Raw|ConvertFrom-Json
    $valid="<OpenVoice><Source>$($policy.source)</Source><Patch>$($policy.patch)</Patch><Sha256>$((Get-FileHash $dll).Hash)</Sha256><Bytes>7</Bytes></OpenVoice>"
    [IO.File]::WriteAllText($manifest,$valid)
    $pin=(Get-FileHash $manifest).Hash
    $identityProject=Join-Path $tmp 'identity.proj'
    $identityImport=[Security.SecurityElement]::Escape((Join-Path $repo 'native/OpenVoiceGenerated.targets'))
    $nativeXml=[Security.SecurityElement]::Escape($native)
    [IO.File]::WriteAllText($identityProject,"<Project><PropertyGroup><OpenVoiceNativeDirectory>$nativeXml</OpenVoiceNativeDirectory><IntermediateOutputPath>$nativeXml/</IntermediateOutputPath></PropertyGroup><Import Project=`"$identityImport`" /></Project>")
    function IdentityCheck([bool]$Expected,[string]$Pin){
        $result=& $sdk msbuild $identityProject -t:GenerateOpenVoiceIdentity -nologo "-p:OpenVoiceGeneratedManifestSha256=$Pin" 2>&1
        if(($LASTEXITCODE -eq 0) -ne $Expected){throw ('Unexpected OpenVoice identity result: '+($result -join "`n"))}
    }
    IdentityCheck $true $pin
    IdentityCheck $false ('0'*64)
    [IO.File]::WriteAllText($dll,'changed')
    IdentityCheck $false $pin
    [IO.File]::WriteAllText($dll,'fixture')
    [IO.File]::WriteAllText($manifest,$valid.Replace($policy.source,('0'*40)))
    IdentityCheck $false ((Get-FileHash $manifest).Hash)
    [IO.File]::WriteAllText($manifest,$valid.Replace('<Bytes>7</Bytes>','<Bytes>7; injected</Bytes>'))
    IdentityCheck $false ((Get-FileHash $manifest).Hash)
    'PASS generated OpenVoice identity: 5 cases'
} finally {
    $guard.Dispose()
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}
    if(-not $tmp.StartsWith((Join-Path $repo '.local/tmp')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup path escaped fixture root.'}
    if((Get-Item -LiteralPath $tmp).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked cleanup refused.'}
    Remove-Item -LiteralPath $tmp -Recurse -Force
}
