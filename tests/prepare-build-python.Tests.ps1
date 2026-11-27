#Requires -Version 7.4
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repository 'native/prepare-build-python.ps1')
$temporaryRoot=Join-Path $repository '.local/tmp'
$fixture=Assert-SpeechNativeInputPath (Join-Path $temporaryRoot ('build-python-test-'+[guid]::NewGuid().ToString('N'))) $temporaryRoot
$checks=0
function Check([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Message){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed $Message}
function Write-Archive([string]$Path,[string]$Name,[switch]$Link){
    $file=[IO.File]::Create($Path)
    $gzip=[IO.Compression.GZipStream]::new($file,[IO.Compression.CompressionLevel]::Optimal)
    $tar=[System.Formats.Tar.TarWriter]::new($gzip)
    try{
        if($Link){$entry=[System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::SymbolicLink,$Name);$entry.LinkName='../outside';$tar.WriteEntry($entry)}
        else{
            $entry=[System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile,$Name)
            $entry.DataStream=[IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes('source'))
            try{$tar.WriteEntry($entry)}finally{$entry.DataStream.Dispose()}
            $entry=[System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile,'python/empty')
            $tar.WriteEntry($entry)
        }
    }finally{$tar.Dispose();$gzip.Dispose();$file.Dispose()}
}
try{
    [void][IO.Directory]::CreateDirectory($fixture)
    $archive=Join-Path $fixture 'python.tar.gz';$output=Join-Path $fixture 'python'
    Write-Archive $archive 'python/file.txt'
    Read-BuildPythonArchive $archive $output
    Check ([IO.File]::ReadAllText((Join-Path $output 'file.txt')) -eq 'source') 'Archive payload was not extracted.'
    Check ((Get-Item -LiteralPath (Join-Path $output 'empty')).Length -eq 0) 'Empty archive member was not extracted.'
    Read-BuildPythonArchive $archive $output -Verify;$checks++
    [IO.File]::WriteAllText((Join-Path $output 'file.txt'),'user bytes')
    Reject {Read-BuildPythonArchive $archive $output -Verify} 'Changed interpreter bytes were accepted.'
    Check ([IO.File]::ReadAllText((Join-Path $output 'file.txt')) -eq 'user bytes') 'Verify changed existing bytes.'
    $bad=Join-Path $fixture 'bad.tar.gz';Write-Archive $bad 'python/../escape'
    Reject {Read-BuildPythonArchive $bad (Join-Path $fixture 'bad')} 'Archive traversal was accepted.'
    Check (-not (Test-Path -LiteralPath (Join-Path $fixture 'escape'))) 'Archive escaped its staging directory.'
    $link=Join-Path $fixture 'link.tar.gz';Write-Archive $link 'python/link' -Link
    Reject {Read-BuildPythonArchive $link (Join-Path $fixture 'linked')} 'Linked archive entry was accepted.'
    [IO.File]::WriteAllText((Join-Path $fixture 'keep'),'existing venv bytes')
    & (Join-Path $repository 'native/prepare-build-python.ps1') -VenvDirectory $fixture 2>$null | Out-Null
    Check ($LASTEXITCODE -ne 0) 'An existing venv directory was accepted.'
    Check ([IO.File]::ReadAllText((Join-Path $fixture 'keep')) -eq 'existing venv bytes') 'An existing venv was changed.'
    $readonlyVenv=Join-Path $fixture 'verify-must-not-create'
    & (Join-Path $repository 'native/prepare-build-python.ps1') -VerifyOnly -VenvDirectory $readonlyVenv 2>$null | Out-Null
    Check ($LASTEXITCODE -ne 0) 'VerifyOnly accepted a venv creation request.'
    Check (-not (Test-Path -LiteralPath $readonlyVenv)) 'VerifyOnly created a venv.'
    Write-Output "Build Python preparation fixtures: $checks passed."
}catch{Write-Error ('BUILD-PYTHON-TEST: '+$_.Exception.Message) -ErrorAction Continue;exit 1}
finally{
    if(Test-Path -LiteralPath $fixture){
        $null=Assert-SpeechNativeInputPath $fixture $temporaryRoot
        foreach($entry in Get-ChildItem -LiteralPath $fixture -Recurse -Force){$null=Assert-SpeechNativeInputPath $entry.FullName $fixture}
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
