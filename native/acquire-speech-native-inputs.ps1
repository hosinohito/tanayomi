#Requires -Version 7.4
<#
Acquire only the fixed source/model inputs used by the existing WORLD and Open JTalk
builders. Run from any directory. -VerifyOnly is read-only; -Plan lists origins.
Compiler preparation stays in build-world-llvm.ps1 -DownloadToolchain -PrepareOnly;
MMDAgent voice preparation stays in prepare-mmdagent-voices.ps1.
#>
[CmdletBinding()]
param([switch]$VerifyOnly, [switch]$Plan)
$ErrorActionPreference = 'Stop'

function Get-SpeechNativeInputManifest {
    $world = 'd625e7608ca23a870018f01e7c562ac683d9847f'
    $tohoku = '8e3306021db135c265f5eda5f062dc489707ddf8'
    @(
        @{ Path="world/world-$world.zip"; Url="https://codeload.github.com/mmorise/World/zip/$world"; Sha256='a62635555243dfe4fb5a859a882fd92713137ec29dbc7a86f0a01f78362f2e60' }
        @{ Path='open-jtalk/open_jtalk-1.11.tar.gz'; Url='https://downloads.sourceforge.net/project/open-jtalk/Open%20JTalk/open_jtalk-1.11/open_jtalk-1.11.tar.gz'; Mirrors=@('https://deb.debian.org/debian/pool/main/o/open-jtalk/open-jtalk_1.11.orig.tar.gz'); Sha256='20fdc6aeb6c757866034abc175820573db43e4284707c866fcd02c8ec18de71f' }
        @{ Path='open-jtalk/open_jtalk_dic_utf_8-1.11.tar.gz'; Url='https://downloads.sourceforge.net/project/open-jtalk/Dictionary/open_jtalk_dic-1.11/open_jtalk_dic_utf_8-1.11.tar.gz'; Sha256='33e9cd251bc41aa2bd7ca36f57abbf61eae3543ca25ca892ae345e394cb10549' }
        @{ Path='open-jtalk/hts_engine_API-1.10.tar.gz'; Url='https://downloads.sourceforge.net/project/hts-engine/hts_engine%20API/hts_engine_API-1.10/hts_engine_API-1.10.tar.gz'; Mirrors=@('https://distfiles.macports.org/hts_engine_API/hts_engine_API-1.10.tar.gz'); Sha256='e2132be5860d8fb4a460be766454cfd7c3e21cf67b509c48e1804feab14968f7' }
        @{ Path='open-jtalk/hts_voice_nitech_jp_atr503_m001-1.05.tar.gz'; Url='https://downloads.sourceforge.net/project/open-jtalk/HTS%20voice/hts_voice_nitech_jp_atr503_m001-1.05/hts_voice_nitech_jp_atr503_m001-1.05.tar.gz'; Mirrors=@('https://distfiles.macports.org/open_jtalk/hts_voice_nitech_jp_atr503_m001-1.05.tar.gz'); Sha256='2e555c88482267b2931c7dbc7ecc0e3df140d6f68fc913aa4822f336c9e0adfc' }
        foreach ($voice in @(
            @('neutral','ded6acb4243b93cc50896199d67b00a7c487662bfcdcd3f9f30a4fb0444b0b46'),
            @('angry','c246afac0d41ada4010db80ca673b6b51cb8e16e9f3a47487d0df3d2ca5b7ec8'),
            @('happy','6bf9559a0757c5250165de40dda034a7e95504f3a3ce4abce58532cee1040cc0'),
            @('sad','1c76d4d5646c46e13410651c4232b95af8b878e67afd370a605b5de792ba17ee')
        )) {
            $name="tohoku-f01-$($voice[0]).htsvoice"
            @{ Path="tohoku-f01/$tohoku/$name"; Url="https://raw.githubusercontent.com/icn-lab/htsvoice-tohoku-f01/$tohoku/$name"; Sha256=$voice[1] }
        }
        @{ Path="tohoku-f01/$tohoku/COPYRIGHT.txt"; Url="https://raw.githubusercontent.com/icn-lab/htsvoice-tohoku-f01/$tohoku/COPYRIGHT.txt"; Sha256='f7f7a1a696f062704825fcc495d330beeeac8c0ed38d8f5bdd8e90bdc3471c95' }
    )
}

function Assert-SpeechNativeInputPath([string]$Path, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) { throw 'Empty input path.' }
    $full=[IO.Path]::GetFullPath($Path)
    $boundary=[IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $full.StartsWith($boundary+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Input path escaped its boundary.' }
    for ($current=$full; $current; $current=[IO.Path]::GetDirectoryName($current)) {
        $item=Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked input path.' }
    }
    return $full
}

function Get-SpeechSourceForgeDownloadUrl([string]$Html,[string]$Original) {
    $origin=[Uri]$Original
    if ($origin.Scheme -ne 'https' -or $origin.Host -ne 'downloads.sourceforge.net') { throw 'Not a supported SourceForge origin.' }
    foreach($meta in [regex]::Matches($Html,'(?is)<meta\b[^>]*>')) {
        if($meta.Value -notmatch '(?i)http-equiv\s*=\s*["'']refresh["'']'){continue}
        $content=[regex]::Match($meta.Value,'(?is)\bcontent\s*=\s*(["''])(.*?)\1')
        if(-not $content.Success){continue}
        $value=[Net.WebUtility]::HtmlDecode($content.Groups[2].Value)
        if($value -notmatch '^\s*\d+(?:\.\d+)?;\s*url\s*=\s*(.+?)\s*$'){continue}
        $target=[Uri]($Matches[1].Trim('"',"'"))
        if($target.Scheme -ne 'https' -or $target.Host -ne 'downloads.sourceforge.net' -or $target.AbsolutePath -ne $origin.AbsolutePath) { throw 'SourceForge refresh escaped the fixed file origin.' }
        return $target.AbsoluteUri
    }
    throw 'SourceForge returned HTML without a fixed-file download refresh.'
}

function Invoke-SpeechNativeDownload([string]$Url,[string]$Destination) {
    $response=Invoke-WebRequest -Uri $Url -OutFile $Destination -PassThru -TimeoutSec 240 -MaximumRetryCount 2 -RetryIntervalSec 3
    # SourceForge's direct URL can lead to its public download page. Follow only
    # its one HTTPS meta refresh for the exact same fixed project/file path.
    # No scripts execute, and the final bytes still require the locked SHA-256.
    if(([Uri]$Url).Host -eq 'downloads.sourceforge.net' -and $response.Headers['Content-Type'] -match '^text/html') {
        if((Get-Item -LiteralPath $Destination).Length -gt 1MB){throw 'Unexpectedly large SourceForge download page.'}
        $next=Get-SpeechSourceForgeDownloadUrl ([IO.File]::ReadAllText($Destination)) $Url
        Remove-Item -LiteralPath $Destination -Force
        Invoke-WebRequest -Uri $next -OutFile $Destination -TimeoutSec 240 -MaximumRetryCount 2 -RetryIntervalSec 3
    }
}

function Get-SpeechNativeInput([hashtable]$Entry, [string]$Vendor, [string]$TemporaryRoot, [switch]$Verify,
    [scriptblock]$Fetch = { param($Url,$Destination) Invoke-SpeechNativeDownload $Url $Destination }) {
    if ($Entry.Path -match '[:\\]' -or $Entry.Path.Split('/') -contains '..' -or $Entry.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $Entry.Url -notmatch '^https://') { throw 'Invalid fixed input entry.' }
    $target=Assert-SpeechNativeInputPath (Join-Path $Vendor $Entry.Path) $Vendor
    if (Test-Path -LiteralPath $target) {
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $Entry.Sha256) { throw "Existing input differs and was preserved: $($Entry.Path)" }
        return $target
    }
    if ($Verify) { throw "Missing fixed input: $($Entry.Path)" }
    $part=Assert-SpeechNativeInputPath (Join-Path $TemporaryRoot ('speech-input-'+[Guid]::NewGuid().ToString('N')+'.part')) $TemporaryRoot
    [void][IO.Directory]::CreateDirectory($TemporaryRoot)
    try {
        $verified=$false
        $failures=[Collections.Generic.List[string]]::new()
        foreach ($url in @($Entry.Url)+@($Entry.Mirrors)) {
            if (-not $url) { continue }
            if ($url -notmatch '^https://') { throw 'Input mirror must use HTTPS.' }
            try {
                & $Fetch $url $part | Out-Null
                $null=Assert-SpeechNativeInputPath $part $TemporaryRoot
                if ((Get-FileHash -LiteralPath $part -Algorithm SHA256).Hash -ne $Entry.Sha256) { throw 'Downloaded bytes differ from the fixed SHA-256.' }
                $verified=$true
                break
            } catch {
                $failures.Add("${url}: $($_.Exception.Message)")
                if (Test-Path -LiteralPath $part) {
                    $null=Assert-SpeechNativeInputPath $part $TemporaryRoot
                    Remove-Item -LiteralPath $part -Force
                }
            }
        }
        if (-not $verified) { throw "Cannot acquire $($Entry.Path): $($failures -join '; ')" }
        $null=Assert-SpeechNativeInputPath $target $Vendor
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Move($part,$target,$false)
        return $target
    } finally {
        if (Test-Path -LiteralPath $part) {
            $null=Assert-SpeechNativeInputPath $part $TemporaryRoot
            Remove-Item -LiteralPath $part -Force
        }
    }
}

function Expand-SpeechWorldInput([string]$Archive, [string]$Vendor, [string]$TemporaryRoot, [string]$Folder, [switch]$Verify) {
    $destination=Assert-SpeechNativeInputPath (Join-Path $Vendor $Folder) $Vendor
    $zip=[IO.Compression.ZipFile]::OpenRead($Archive)
    $stage=$null
    try {
        $entries=@($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
        $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $total=0L
        foreach ($entry in $entries) {
            $total+=$entry.Length
            if ($total -gt 100MB -or -not $entry.FullName.StartsWith($Folder+'/') -or
                $entry.FullName -match '[:\\\x00-\x1f]' -or $entry.FullName.Split('/') -contains '..' -or
                -not $names.Add($entry.FullName) -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000 -or
                ($entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe WORLD archive entry.' }
        }
        if (-not $entries.Count) { throw 'Empty WORLD archive.' }
        if (Test-Path -LiteralPath $destination) {
            foreach ($entry in $entries) {
                $path=Assert-SpeechNativeInputPath (Join-Path $Vendor $entry.FullName) $destination
                $source=$entry.Open()
                try { $expected=(Get-FileHash -InputStream $source -Algorithm SHA256).Hash } finally { $source.Dispose() }
                if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw 'Existing WORLD source differs and was preserved.' }
            }
            return
        }
        if ($Verify) { throw 'Expanded WORLD source is missing.' }
        $stage=Assert-SpeechNativeInputPath (Join-Path $TemporaryRoot ('speech-world-'+[Guid]::NewGuid().ToString('N'))) $TemporaryRoot
        [void][IO.Directory]::CreateDirectory($stage)
        foreach ($entry in $entries) {
            $target=Assert-SpeechNativeInputPath (Join-Path $stage $entry.FullName) $stage
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            $source=$entry.Open()
            try {
                $output=[IO.File]::Open($target,[IO.FileMode]::CreateNew)
                try { $source.CopyTo($output) } finally { $output.Dispose() }
            } finally { $source.Dispose() }
        }
        $null=Assert-SpeechNativeInputPath $destination $Vendor
        [IO.Directory]::Move((Join-Path $stage $Folder),$destination)
    } finally {
        $zip.Dispose()
        if ($stage -and (Test-Path -LiteralPath $stage)) {
            $null=Assert-SpeechNativeInputPath $stage $TemporaryRoot
            foreach ($entry in Get-ChildItem -LiteralPath $stage -Recurse -Force) { $null=Assert-SpeechNativeInputPath $entry.FullName $stage }
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        if ($VerifyOnly -and $Plan) { throw '-VerifyOnly and -Plan are mutually exclusive.' }
        $repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        $vendor=Join-Path $repository '.local/vendor'
        $temporary=Join-Path $repository '.local/tmp'
        $manifest=Get-SpeechNativeInputManifest
        if ($Plan) { $manifest | ForEach-Object { [PSCustomObject]$_ }; return }
        foreach ($entry in $manifest) { Get-SpeechNativeInput $entry $vendor $temporary -Verify:$VerifyOnly }
        $revision='d625e7608ca23a870018f01e7c562ac683d9847f'
        Expand-SpeechWorldInput (Join-Path $vendor "world/world-$revision.zip") (Join-Path $vendor 'world') $temporary "World-$revision" -Verify:$VerifyOnly
        Write-Output 'Verified WORLD source, Open JTalk/HTS/NIT archives and four Tohoku models.'
    } catch { Write-Error "SPEECH-INPUT: $($_.Exception.Message)" -ErrorAction Continue; exit 1 }
}
