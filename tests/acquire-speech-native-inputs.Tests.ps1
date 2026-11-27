#Requires -Version 7.4
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repository 'native/acquire-speech-native-inputs.ps1')
$temporaryRoot=Join-Path $repository '.local/tmp'
$fixture=Assert-SpeechNativeInputPath (Join-Path $temporaryRoot ('speech-input-test-'+[guid]::NewGuid().ToString('N'))) $temporaryRoot
$checks=0
function Check([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Reject([scriptblock]$Action,[string]$Message) {
    $rejected=$false
    try { & $Action | Out-Null } catch { $rejected=$true }
    Check $rejected $Message
}
try {
    [void][IO.Directory]::CreateDirectory($fixture)
    $vendor=Join-Path $fixture 'vendor'
    $scratch=Join-Path $fixture 'tmp'
    $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('original')))
    $entry=@{Path='model/voice';Url='https://example.invalid/voice';Sha256=$sha}
    $fetch={param($Url,$Destination) [IO.File]::WriteAllText($Destination,'original',[Text.UTF8Encoding]::new($false))}
    $target=Get-SpeechNativeInput $entry $vendor $scratch -Fetch $fetch
    Check ([IO.File]::ReadAllText($target) -eq 'original') 'Verified download was not published.'
    $null=Get-SpeechNativeInput $entry $vendor $scratch -Fetch {throw 'Existing bytes must not be fetched.'}
    $checks++
    [IO.File]::WriteAllText($target,'user original')
    Reject {Get-SpeechNativeInput $entry $vendor $scratch -Fetch $fetch} 'Mismatched existing input was accepted.'
    Check ([IO.File]::ReadAllText($target) -eq 'user original') 'Existing input was changed.'
    $entry.Path='model/missing'
    Reject {Get-SpeechNativeInput $entry $vendor $scratch -Verify} 'Missing verify input was accepted.'
    Reject {Get-SpeechNativeInput $entry $vendor $scratch -Fetch {param($u,$p) [IO.File]::WriteAllText($p,'wrong')}} 'Corrupt download was accepted.'
    Check (-not (Test-Path -LiteralPath (Join-Path $vendor $entry.Path))) 'Corrupt payload was published.'
    Check (@(Get-ChildItem -LiteralPath $scratch -Force).Count -eq 0) 'Download staging was left behind.'
    $entry.Path='model/mirror'
    $entry.Mirrors=@('https://mirror.invalid/voice')
    $null=Get-SpeechNativeInput $entry $vendor $scratch -Fetch {
        param($url,$destination)
        $text=if ($url -like 'https://mirror.invalid/*') {'original'} else {'HTML download page'}
        [IO.File]::WriteAllText($destination,$text,[Text.UTF8Encoding]::new($false))
    }
    Check ([IO.File]::ReadAllText((Join-Path $vendor $entry.Path)) -eq 'original') 'Same-SHA mirror fallback failed.'
    foreach ($path in @('../outside','C:/outside','bad\name','')) {
        $entry.Path=$path
        Reject {Get-SpeechNativeInput $entry $vendor $scratch -Fetch $fetch} "Unsafe path was accepted: $path"
    }
    $archive=Join-Path $fixture 'world.zip'
    $zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
    try {
        $member=$zip.CreateEntry('World-test/src/fixture.cpp')
        $writer=[IO.StreamWriter]::new($member.Open())
        try {$writer.Write('source')} finally {$writer.Dispose()}
    } finally {$zip.Dispose()}
    $world=Join-Path $vendor 'world'
    [void][IO.Directory]::CreateDirectory($world)
    Expand-SpeechWorldInput $archive $world $scratch 'World-test'
    $source=Join-Path $world 'World-test/src/fixture.cpp'
    Check ([IO.File]::ReadAllText($source) -eq 'source') 'WORLD source was not staged.'
    Expand-SpeechWorldInput $archive $world $scratch 'World-test' -Verify
    $checks++
    [IO.File]::WriteAllText($source,'user source')
    Reject {Expand-SpeechWorldInput $archive $world $scratch 'World-test'} 'Modified WORLD source was accepted.'
    Check ([IO.File]::ReadAllText($source) -eq 'user source') 'Modified WORLD source was overwritten.'
    $badArchive=Join-Path $fixture 'bad.zip'
    $zip=[IO.Compression.ZipFile]::Open($badArchive,[IO.Compression.ZipArchiveMode]::Create)
    try { $null=$zip.CreateEntry('World-bad/../escape') } finally {$zip.Dispose()}
    Reject {Expand-SpeechWorldInput $badArchive $world $scratch 'World-bad'} 'Archive traversal was accepted.'
    Check (-not (Test-Path -LiteralPath (Join-Path $world 'World-bad'))) 'Unsafe WORLD output was published.'
    Check (@(Get-ChildItem -LiteralPath $scratch -Force).Count -eq 0) 'Extraction staging was left behind.'
    $origin='https://downloads.sourceforge.net/project/example/file.tar.gz'
    $url=Get-SpeechSourceForgeDownloadUrl '<meta http-equiv="refresh" content="5; url=https://downloads.sourceforge.net/project/example/file.tar.gz?ts=public&amp;use_mirror=twds">' $origin
    Check ($url -eq ($origin+'?ts=public&use_mirror=twds')) 'SourceForge fixed-file refresh was not decoded.'
    Reject {Get-SpeechSourceForgeDownloadUrl '<meta http-equiv="refresh" content="5; url=https://other.invalid/file.tar.gz">' $origin} 'SourceForge refresh changed host.'
    Reject {Get-SpeechSourceForgeDownloadUrl '<meta http-equiv="refresh" content="5; url=https://downloads.sourceforge.net/project/changed/file.tar.gz">' $origin} 'SourceForge refresh changed fixed file.'
    Reject {Get-SpeechSourceForgeDownloadUrl '<html>no refresh</html>' $origin} 'Arbitrary HTML was accepted as download guidance.'
    Write-Output "Speech native acquisition fixtures: $checks passed."
} catch { Write-Error "SPEECH-INPUT-TEST: $($_.Exception.Message)" -ErrorAction Continue; exit 1 }
finally {
    if (Test-Path -LiteralPath $fixture) {
        $null=Assert-SpeechNativeInputPath $fixture $temporaryRoot
        foreach ($entry in Get-ChildItem -LiteralPath $fixture -Recurse -Force) { $null=Assert-SpeechNativeInputPath $entry.FullName $fixture }
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
