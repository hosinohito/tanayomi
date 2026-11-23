param([string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vendor = Join-Path $repo '.local/vendor/mmdagent/1.8'
$tmpRoot = Join-Path $repo '.local/tmp'
$archiveSha = 'F702F2109A07DCA103C7B9A5123A25C6DDA038F0D7FCC899FF0281D07E873A63'
$models = [ordered]@{
    'mei/mei_normal.htsvoice' = 'B19E8E5DDEF4D9D9559D654F395818E2BB3C0BBDBBCA533F96086DE21AC5DB2B'
    'mei/mei_angry.htsvoice' = '56A0759A4723CEC340E3BBA26199B64B9DF786F36C2851F7A15FB3D6D32B9471'
    'mei/mei_bashful.htsvoice' = '20F7ECD45E3DE5D9C43F28E4D9175C8DFEFB7A002CF4F03208D1431443626FBB'
    'mei/mei_happy.htsvoice' = '93EC37412999548074249841C6339880E5ED34E946EEE1B4DFC19460307F8B2D'
    'mei/mei_sad.htsvoice' = 'C06F110F7832C6E970B8E1D0870FF4F68A4F4D06CEABF8B7190A3C3892CB65F2'
    'takumi/takumi_normal.htsvoice' = '9130CC911C881CC2C4C442A67374D8601B436F9DD415B555FE3C2D2C94444D42'
    'takumi/takumi_angry.htsvoice' = '773FD59058B976D2B5E8B1091B30ABE80BB38C521C99BCE2BD806E9E2E6EA173'
    'takumi/takumi_happy.htsvoice' = 'D96F34309F3EA83322C0CFF53AA43362E42680DB350F966CE0A660A3A1292794'
    'takumi/takumi_sad.htsvoice' = 'F10A82F6B2A416FEB3332635C3F416698A14707E3337114A5ED81273C39E010D'
}
function Assert-LocalPath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { throw 'An empty path is not allowed.' }
    $absolute = [IO.Path]::GetFullPath($path)
    if (-not $absolute.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The path must remain inside the repository.'
    }
    $itemPath = $absolute
    while ($itemPath.Length -ge $repo.Length) {
        if ((Test-Path -LiteralPath $itemPath) -and ((Get-Item -LiteralPath $itemPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Linked paths are not allowed.'
        }
        $itemPath = [IO.Path]::GetDirectoryName($itemPath)
    }
    return $absolute
}
function Assert-Model([string]$path, [string]$expectedSha) {
    $null = Assert-LocalPath $path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expectedSha) { throw 'HTS voice checksum mismatch.' }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -gt 3MB) { throw 'HTS voice exceeds the supported size.' }
    $head = [Text.Encoding]::ASCII.GetString($bytes, 0, [Math]::Min(8192, $bytes.Length))
    foreach ($line in @('HTS_VOICE_VERSION:1.0', 'SAMPLING_FREQUENCY:48000', 'FRAME_PERIOD:240', 'FULLCONTEXT_FORMAT:HTS_TTS_JPN')) {
        if ($head -notmatch ('(?m)^' + [regex]::Escape($line) + '\r?$')) { throw ('Unsupported HTS voice header: ' + $line) }
    }
}
$null = Assert-LocalPath $vendor
$null = Assert-LocalPath $tmpRoot
if (Test-Path -LiteralPath $vendor) {
    foreach ($entry in $models.GetEnumerator()) { Assert-Model (Join-Path $vendor $entry.Key) $entry.Value }
    foreach ($voice in @('mei', 'takumi')) {
        foreach ($file in @('README.txt', 'COPYRIGHT.txt')) {
            $path = Assert-LocalPath (Join-Path $vendor "$voice/$file")
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'The source license files are missing.' }
        }
    }
    Write-Output 'MMDAgent 1.8: nine fixed HTS voices are already prepared.'
    return
}
$job = Join-Path $tmpRoot ('mmdagent-voices-' + [Guid]::NewGuid().ToString('N'))
$oldTemp = $env:TEMP; $oldTmp = $env:TMP
New-Item -ItemType Directory -Path $job -Force | Out-Null
try {
    $env:TEMP = $job; $env:TMP = $job
    if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
        $ArchivePath = Join-Path $job 'MMDAgent_Example-1.8.zip'
        $page = Invoke-WebRequest 'https://sourceforge.net/projects/mmdagent/files/MMDAgent_Example/MMDAgent_Example-1.8/MMDAgent_Example-1.8.zip/download' -TimeoutSec 30
        $download = [regex]::Match($page.Content, 'https://downloads\.sourceforge\.net/project/mmdagent/[^"<>\s]+').Value
        if (-not $download) { throw 'The official download link is unavailable.' }
        Invoke-WebRequest ([Net.WebUtility]::HtmlDecode($download)) -OutFile $ArchivePath -TimeoutSec 120
    } else { $ArchivePath = Assert-LocalPath $ArchivePath }
    if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $archiveSha) { throw 'MMDAgent 1.8 archive checksum mismatch.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stage = Join-Path $job 'voices'
    New-Item -ItemType Directory -Path $stage | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $files = @($models.Keys) + @('mei/COPYRIGHT.txt', 'mei/README.txt', 'takumi/COPYRIGHT.txt', 'takumi/README.txt')
        foreach ($relative in $files) {
            $entries = @($zip.Entries | Where-Object { $_.FullName -ceq ('MMDAgent_Example-1.8/Voice/' + $relative) })
            if ($entries.Count -ne 1 -or $entries[0].Length -lt 1 -or $entries[0].Length -gt 3MB) { throw 'Missing, duplicated or oversized voice entry.' }
            $target = Assert-LocalPath (Join-Path $stage $relative)
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
            $input = $entries[0].Open(); $output = [IO.File]::Open($target, [IO.FileMode]::CreateNew)
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            if ($models.Contains($relative)) { Assert-Model $target $models[$relative] }
        }
    } finally { $zip.Dispose() }
    $parent = Assert-LocalPath ([IO.Path]::GetDirectoryName($vendor))
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $null = Assert-LocalPath $stage; $null = Assert-LocalPath $vendor
    if (Test-Path -LiteralPath $vendor) { throw 'The fixed source directory appeared while preparing it.' }
    Move-Item -LiteralPath $stage -Destination $vendor
    Write-Output ('Prepared nine unmodified HTS voices and their source notices: ' + $vendor)
} finally {
    $env:TEMP = $oldTemp; $env:TMP = $oldTmp
    $cleanup = Assert-LocalPath $job
    if (Test-Path -LiteralPath $cleanup) {
        if (@(Get-ChildItem -LiteralPath $cleanup -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
            throw 'A link appeared in the temporary directory; cleanup was stopped.'
        }
        Remove-Item -LiteralPath $cleanup -Recurse -Force
    }
}
