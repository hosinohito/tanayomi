#Requires -Version 7.4
# Canonical source digest v1: ls-tree order, UTF-8 lines of
# mode SP type SP SHA256(blob bytes) TAB path LF. Gitlinks use their commit OID.
# Reads Git objects, so checkout EOLs and working-tree patches do not affect it.
function Get-OrtSourceCanonicalDigest([string]$GitExecutable,[string]$SourceDirectory,[switch]$CheckWorkingTree) {
    $source=[IO.Path]::GetFullPath($SourceDirectory)
    function Start-OrtObjectGit([string[]]$Arguments) {
        $info=[Diagnostics.ProcessStartInfo]::new($GitExecutable)
        $info.UseShellExecute=$false; $info.CreateNoWindow=$true
        $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true; $info.RedirectStandardInput=$true
        $info.StandardOutputEncoding=[Text.UTF8Encoding]::new($false,$true)
        $info.Environment['GIT_TERMINAL_PROMPT']='0'; $info.Environment['GIT_NO_LAZY_FETCH']='1'
        foreach($arg in @('-c',"safe.directory=$($source.Replace('\','/'))",'-C',$source)+$Arguments){$info.ArgumentList.Add($arg)}
        return [Diagnostics.Process]::Start($info)
    }
    $listing=Start-OrtObjectGit @('-c','core.quotePath=false','ls-tree','-r','HEAD')
    try {
        $out=$listing.StandardOutput.ReadToEndAsync();$err=$listing.StandardError.ReadToEndAsync()
        if(-not $listing.WaitForExit(60000)){throw 'ORT tree listing timed out.'}
        if($listing.ExitCode){throw 'Cannot read complete ORT Git tree.'}
        $lines=$out.GetAwaiter().GetResult().TrimEnd("`r","`n").Split("`n")
    } finally {if(-not $listing.HasExited){$listing.Kill($true);$null=$listing.WaitForExit(5000)};$listing.Dispose()}
    $entries=@(foreach($line in $lines){
        if($line.TrimEnd("`r") -notmatch '^(\d{6}) (blob|commit) ([a-f0-9]{40})\t([^\r\n\t]+)$'){throw 'Unsupported ORT tree entry.'}
        [PSCustomObject]@{Mode=$Matches[1];Type=$Matches[2];Oid=$Matches[3];Path=$Matches[4]}
    })
    if(-not $entries.Count){throw 'Empty ORT source tree.'}
    $objects=@($entries|Where-Object Type -EQ 'blob'|Select-Object -ExpandProperty Oid -Unique)
    if($CheckWorkingTree){
        $checkoutHash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
        $checkedDirectories=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        try {
            foreach($entry in $entries){
                $contentDigest=$entry.Oid
                if($entry.Type -eq 'blob'){
                    if($entry.Mode -notin @('100644','100755')){throw 'Unsupported checkout source entry mode.'}
                    $path=[IO.Path]::GetFullPath((Join-Path $source $entry.Path))
                    if(-not $path.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Checkout source escaped its boundary.'}
                    for($current=$path;$current.Length -ge $source.Length;$current=[IO.Path]::GetDirectoryName($current)){
                        if($checkedDirectories.Contains($current)){break}
                        if([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint){throw 'Linked source checkout entry.'}
                        if($current -ne $path){$null=$checkedDirectories.Add($current)}
                    }
                    $file=[IO.File]::OpenRead($path)
                    try{$contentDigest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file)).ToLowerInvariant()}finally{$file.Dispose()}
                }
                $checkoutHash.AppendData([Text.Encoding]::UTF8.GetBytes("$($entry.Mode) $($entry.Type) $contentDigest`t$($entry.Path)`n"))
            }
            return [PSCustomObject]@{Algorithm='git-tree-blob-sha256-v1';Sha256=[Convert]::ToHexString($checkoutHash.GetHashAndReset()).ToLowerInvariant();Entries=$entries.Count;Blobs=$objects.Count;Gitlinks=@($entries|Where-Object Type -EQ 'commit').Count}
        } finally {$checkoutHash.Dispose()}
    }
    $digests=@{}
    $process=Start-OrtObjectGit @('cat-file','--batch')
    $aggregate=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    try {
        $errors=$process.StandardError.ReadToEndAsync()
        $writing=$process.StandardInput.WriteAsync(($objects -join "`n")+"`n")
        $stream=$process.StandardOutput.BaseStream
        $buffer=[byte[]]::new(1024*1024)
        foreach($oid in $objects){
            if($timer.Elapsed.TotalSeconds -gt 180){throw 'ORT source hashing exceeded 180 seconds.'}
            $header=[Text.StringBuilder]::new()
            while(($value=$stream.ReadByte()) -ne 10){
                if($value -lt 0 -or $header.Length -gt 128){throw 'Incomplete Git object header.'}
                [void]$header.Append([char]$value)
            }
            if($header.ToString() -notmatch '^([a-f0-9]{40}) blob ([0-9]+)$' -or $Matches[1] -ne $oid){throw 'Git source object is missing or mismatched.'}
            $remaining=[long]$Matches[2]
            $hash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            try {
                while($remaining -gt 0){
                    $count=$stream.Read($buffer,0,[int][Math]::Min($remaining,$buffer.Length))
                    if($count -le 0){throw 'Incomplete Git source object.'}
                    $hash.AppendData($buffer,0,$count);$remaining-=$count
                }
                $digests[$oid]=[Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
            } finally {$hash.Dispose()}
            if($stream.ReadByte() -ne 10){throw 'Invalid Git source object delimiter.'}
        }
        $null=$writing.GetAwaiter().GetResult();$process.StandardInput.Close()
        if(-not $process.WaitForExit(10000) -or $process.ExitCode){throw 'Git source object reader failed.'}
        foreach($entry in $entries){
            $digest=if($entry.Type -eq 'blob'){$digests[$entry.Oid]}else{$entry.Oid}
            $aggregate.AppendData([Text.Encoding]::UTF8.GetBytes("$($entry.Mode) $($entry.Type) $digest`t$($entry.Path)`n"))
        }
        return [PSCustomObject]@{Algorithm='git-tree-blob-sha256-v1';Sha256=[Convert]::ToHexString($aggregate.GetHashAndReset()).ToLowerInvariant();Entries=$entries.Count;Blobs=$objects.Count;Gitlinks=@($entries|Where-Object Type -EQ 'commit').Count}
    } finally {
        $aggregate.Dispose()
        if(-not $process.HasExited){$process.Kill($true);$null=$process.WaitForExit(5000)}
        $process.Dispose()
    }
}
