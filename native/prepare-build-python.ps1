#Requires -Version 7.4
<#
Acquire a fixed portable development Python without an installer, PATH/registry
changes, Codex, or an existing Python. -Offline requires the cached archive.
-VerifyOnly reads/verifies an existing archive and expanded interpreter.
-VenvDirectory creates only a new venv under repository .local (never replaces one).
Python/wheel licenses remain in the archive; this is not a product runtime.
#>
[CmdletBinding()]
param([switch]$Offline,[switch]$VerifyOnly,[string]$VenvDirectory='')
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$local=Join-Path $repository '.local'
$temporaryRoot=Join-Path $local 'tmp'
$buildPythonVerifyOnly=$VerifyOnly
. (Join-Path $PSScriptRoot 'acquire-speech-native-inputs.ps1')
$VerifyOnly=$buildPythonVerifyOnly

function Read-BuildPythonArchive([string]$Archive,[string]$Root,[switch]$Verify) {
    $inputFile=[IO.File]::OpenRead($Archive)
    $gzip=[IO.Compression.GZipStream]::new($inputFile,[IO.Compression.CompressionMode]::Decompress)
    $tar=[System.Formats.Tar.TarReader]::new($gzip)
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total=0L
    try {
        while($entry=$tar.GetNextEntry()) {
            if($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::GlobalExtendedAttributes){continue}
            $name=$entry.Name.TrimEnd('/')
            if($name -eq 'python' -and $entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory){continue}
            if(-not $name.StartsWith('python/') -or $name -match '[:\\\x00-\x1f]' -or $name.Split('/') -contains '..' -or -not $names.Add($name)){throw 'Unsafe Python archive path.'}
            $total+=$entry.Length
            if($total -gt 1GB){throw 'Python archive exceeds its expansion bound.'}
            $path=Assert-SpeechNativeInputPath (Join-Path $Root $name.Substring(7)) $Root
            if($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory){if(-not $Verify){[void][IO.Directory]::CreateDirectory($path)};continue}
            if($entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile,[System.Formats.Tar.TarEntryType]::V7RegularFile)){throw 'Linked or unsupported Python archive entry.'}
            if($Verify){
                $expected=if($entry.Length){(Get-FileHash -InputStream $entry.DataStream -Algorithm SHA256).Hash}else{[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]::new(0)))}
                if(-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected){throw 'Expanded Python differs from its fixed archive.'}
            }else{
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
                $output=[IO.File]::Open($path,[IO.FileMode]::CreateNew)
                try{if($entry.Length){$entry.DataStream.CopyTo($output)}}finally{$output.Dispose()}
            }
        }
        if(-not $names.Count){throw 'Empty Python archive.'}
    }finally{$tar.Dispose();$gzip.Dispose();$inputFile.Dispose()}
}

function Invoke-BuildPython([string]$Executable,[string[]]$Arguments,[string]$Temporary) {
    $info=[Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.WorkingDirectory=$Temporary;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    foreach($argument in $Arguments){$info.ArgumentList.Add($argument)}
    foreach($name in @('TEMP','TMP','TMPDIR')){$info.Environment[$name]=$Temporary}
    $info.Environment['PYTHONDONTWRITEBYTECODE']='1'
    $info.Environment['PIP_DISABLE_PIP_VERSION_CHECK']='1'
    $info.Environment['PIP_CACHE_DIR']=Join-Path $Temporary 'pip-cache'
    $process=[Diagnostics.Process]::Start($info)
    try{
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(120000)){throw 'Development Python operation exceeded 120 seconds.'}
        if($process.ExitCode){throw ('Development Python failed: '+$stderr.GetAwaiter().GetResult().Trim())}
        return $stdout.GetAwaiter().GetResult().Trim()
    }finally{if(-not $process.HasExited){$process.Kill($true);$null=$process.WaitForExit(5000)};$process.Dispose()}
}

if($MyInvocation.InvocationName -ne '.') {
    $stage=$null;$temporary=$null;$createdVenv=$false;$guard=$null;$venv=$null;$originalTemporary=$null
    try{
        if(-not $IsWindows -or -not [Environment]::Is64BitProcess){throw 'Windows x64 PowerShell is required.'}
        if($VerifyOnly -and $VenvDirectory){throw '-VerifyOnly does not create a venv.'}
        if($VenvDirectory){
            $venv=Assert-SpeechNativeInputPath $VenvDirectory $local
            if(Test-Path -LiteralPath $venv){throw 'VenvDirectory already exists and was preserved. Choose a new path.'}
        }
        $lock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-python.lock.json') -Raw|ConvertFrom-Json
        if($lock.schema -ne 1 -or $lock.version -ne '3.12.14'){throw 'Unsupported development Python lock.'}
        $vendor=Join-Path $local 'vendor/python'
        $archiveEntry=@{Path=$lock.archive;Url=$lock.url;Sha256=$lock.sha256}
        $archive=Get-SpeechNativeInput $archiveEntry $vendor $temporaryRoot -Verify:($Offline -or $VerifyOnly)
        if((Get-Item -LiteralPath $archive).Length -ne $lock.bytes){throw 'Development Python archive size differs from lock.'}
        $destination=Assert-SpeechNativeInputPath (Join-Path $vendor $lock.directory) $vendor
        if(Test-Path -LiteralPath $destination){Read-BuildPythonArchive $archive $destination -Verify}
        elseif($VerifyOnly){throw 'Expanded development Python is missing.'}
        else{
            $stage=Assert-SpeechNativeInputPath (Join-Path $temporaryRoot ('build-python-extract-'+[guid]::NewGuid().ToString('N'))) $temporaryRoot
            [void][IO.Directory]::CreateDirectory($stage)
            Read-BuildPythonArchive $archive $stage
            [IO.Directory]::Move($stage,$destination);$stage=$null
        }
        $python=Join-Path $destination 'python.exe'
        if(-not $VerifyOnly){
            $temporary=Assert-SpeechNativeInputPath (Join-Path $temporaryRoot ('build-python-run-'+[guid]::NewGuid().ToString('N'))) $temporaryRoot
            [void][IO.Directory]::CreateDirectory($temporary)
            $originalTemporary=@($env:TEMP,$env:TMP,$env:TMPDIR)
            $env:TEMP=$temporary;$env:TMP=$temporary;$env:TMPDIR=$temporary
            . (Join-Path $PSScriptRoot 'process-error-mode.ps1');$guard=Enter-NativeProcessErrorMode
            $null=Invoke-BuildPython $python @('-I','-B','-c','import sys,struct,ssl,sqlite3,ctypes,zlib,venv,ensurepip; assert sys.version_info[:3]==(3,12,14) and struct.calcsize("P")==8') $temporary
            if($venv){
                $createdVenv=$true
                $null=Invoke-BuildPython $python @('-I','-B','-m','venv',$venv) $temporary
                $null=Invoke-BuildPython (Join-Path $venv 'Scripts/python.exe') @('-I','-B','-m','pip','--version') $temporary
                $createdVenv=$false
                Write-Output ('New development venv: '+$venv)
            }
        }
        Write-Output $python
    }catch{Write-Error ('BUILD-PYTHON: '+$_.Exception.Message) -ErrorAction Continue;exit 1}
    finally{
        if($guard){$guard.Dispose()}
        if($null -ne $originalTemporary){$env:TEMP=$originalTemporary[0];$env:TMP=$originalTemporary[1];$env:TMPDIR=$originalTemporary[2]}
        foreach($owned in @($stage,$temporary,$(if($createdVenv){$venv}else{$null}))){
            if($owned -and (Test-Path -LiteralPath $owned)){
                $null=Assert-SpeechNativeInputPath $owned $local
                foreach($entry in Get-ChildItem -LiteralPath $owned -Recurse -Force){$null=Assert-SpeechNativeInputPath $entry.FullName $owned}
                Remove-Item -LiteralPath $owned -Recurse -Force
            }
        }
    }
}
