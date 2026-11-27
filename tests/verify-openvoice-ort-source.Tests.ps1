#Requires -Version 7.4
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repository 'native/verify-openvoice-ort-source.ps1')
. (Join-Path $repository 'native/acquire-speech-native-inputs.ps1')
. (Join-Path $repository 'native/process-error-mode.ps1')
$temporaryRoot=Join-Path $repository '.local/tmp'
$fixture=Assert-SpeechNativeInputPath (Join-Path $temporaryRoot ('ort-source-test-'+[guid]::NewGuid().ToString('N'))) $temporaryRoot
$git=(Get-Command git.exe -ErrorAction Stop).Source
$oldTemp=@($env:TEMP,$env:TMP,$env:TMPDIR)
$guard=$null
$checks=0
function Invoke-FixtureGit([string[]]$Arguments) {
    $info=[Diagnostics.ProcessStartInfo]::new($git)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    foreach($arg in @('-c','core.autocrlf=false','-c',"core.hooksPath=$fixture/disabled-hooks",'-c',"core.excludesFile=$fixture/empty-config",'-c',"core.attributesFile=$fixture/empty-config",'-c','commit.gpgSign=false','-c','user.name=Fixture','-c','user.email=fixture@example.invalid','-C',$fixture)+$Arguments){$info.ArgumentList.Add($arg)}
    $process=[Diagnostics.Process]::Start($info)
    try {
        $output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(10000)){throw 'Fixture Git timed out.'}
        if($process.ExitCode){throw ('Fixture Git failed: '+$errors.GetAwaiter().GetResult().Trim())}
        return $output.GetAwaiter().GetResult().Trim()
    } finally {if(-not $process.HasExited){$process.Kill($true);$null=$process.WaitForExit(5000)};$process.Dispose()}
}
function Check([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message};$script:checks++}
function BlobDigest([byte[]]$Bytes){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()}
try {
    [void][IO.Directory]::CreateDirectory($fixture)
    [IO.File]::WriteAllBytes((Join-Path $fixture 'empty-config'),[byte[]]::new(0))
    $env:TEMP=$fixture;$env:TMP=$fixture;$env:TMPDIR=$fixture
    $guard=Enter-NativeProcessErrorMode
    $null=Invoke-FixtureGit @('init','--quiet')
    $binary=[byte[]]@(0,1,128,255,10,13)
    $text=[Text.Encoding]::UTF8.GetBytes("日本語`r`nsource")
    [IO.File]::WriteAllBytes((Join-Path $fixture 'binary.bin'),$binary)
    [IO.File]::WriteAllBytes((Join-Path $fixture 'text.txt'),$text)
    $null=Invoke-FixtureGit @('add','--','binary.bin','text.txt')
    $null=Invoke-FixtureGit @('commit','--quiet','-m','fixture')
    $baseline=Get-OrtSourceCanonicalDigest $git $fixture
    $canonical="100644 blob $(BlobDigest $binary)`tbinary.bin`n100644 blob $(BlobDigest $text)`ttext.txt`n"
    Check ($baseline.Sha256 -eq (BlobDigest ([Text.Encoding]::UTF8.GetBytes($canonical)))) 'Canonical digest does not cover exact binary/text bytes and names.'
    Check ($baseline.Entries -eq 2 -and $baseline.Blobs -eq 2 -and $baseline.Gitlinks -eq 0) 'Incorrect tree counts.'
    Check ((Get-OrtSourceCanonicalDigest $git $fixture -CheckWorkingTree).Sha256 -eq $baseline.Sha256) 'Exact checkout bytes do not match source object digest.'
    [IO.File]::WriteAllText((Join-Path $fixture 'text.txt'),'uncommitted checkout changes')
    Check ((Get-OrtSourceCanonicalDigest $git $fixture).Sha256 -eq $baseline.Sha256) 'Working-tree changes affected source-object digest.'
    Check ((Get-OrtSourceCanonicalDigest $git $fixture -CheckWorkingTree).Sha256 -ne $baseline.Sha256) 'Changed physical source bytes were ignored.'
    $null=Invoke-FixtureGit @('update-index','--chmod=+x','binary.bin')
    $null=Invoke-FixtureGit @('commit','--quiet','-m','mode')
    $mode=Get-OrtSourceCanonicalDigest $git $fixture
    Check ($mode.Sha256 -ne $baseline.Sha256) 'Entry mode change was ignored.'
    $null=Invoke-FixtureGit @('add','--','text.txt')
    $null=Invoke-FixtureGit @('commit','--quiet','-m','bytes')
    $changed=Get-OrtSourceCanonicalDigest $git $fixture
    Check ($changed.Sha256 -ne $mode.Sha256) 'Committed source change was ignored.'
    $null=Invoke-FixtureGit @('mv','text.txt','renamed.txt')
    $null=Invoke-FixtureGit @('commit','--quiet','-m','path')
    $renamed=Get-OrtSourceCanonicalDigest $git $fixture
    Check ($renamed.Sha256 -ne $changed.Sha256) 'Tracked path change was ignored.'
    $commit=Invoke-FixtureGit @('rev-parse','HEAD')
    $null=Invoke-FixtureGit @('update-index','--add','--cacheinfo',"160000,$commit,embedded")
    $null=Invoke-FixtureGit @('commit','--quiet','-m','gitlink')
    $linked=Get-OrtSourceCanonicalDigest $git $fixture
    Check ($linked.Gitlinks -eq 1 -and $linked.Entries -eq 3 -and $linked.Sha256 -ne $renamed.Sha256) 'Gitlink commit identity was ignored.'
    Write-Output "ORT canonical source fixtures: $checks passed."
} catch {Write-Error "ORT-SOURCE-TEST: $($_.Exception.Message)" -ErrorAction Continue;exit 1}
finally {
    if($guard){$guard.Dispose()}
    $env:TEMP=$oldTemp[0];$env:TMP=$oldTemp[1];$env:TMPDIR=$oldTemp[2]
    if(Test-Path -LiteralPath $fixture){
        $null=Assert-SpeechNativeInputPath $fixture $temporaryRoot
        foreach($entry in Get-ChildItem -LiteralPath $fixture -Recurse -Force){$null=Assert-SpeechNativeInputPath $entry.FullName $fixture}
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
