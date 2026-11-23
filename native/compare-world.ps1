[CmdletBinding()]
param(
    [string]$InputWave,
    [Parameter(Mandatory)][string]$OutputPrefix,
    [double]$FlatF0 = 0,
    [switch]$Utau,
    [switch]$Prosody,
    [switch]$Timing,
    [switch]$Join,
    [switch]$Context,
    [switch]$ContextPitch,
    [switch]$ContextOriginalTiming,
    [switch]$ContextDuration,
    [switch]$ContextConsonants,
    [string]$OpenJtalkAudit
)
$ErrorActionPreference = 'Stop'
if ($ContextConsonants -and -not $ContextDuration) { throw 'ContextConsonants requires ContextDuration.' }
if ($ContextDuration -and (-not $Context -or $ContextPitch -or $ContextOriginalTiming)) { throw 'ContextDuration requires Context and excludes pitch comparison modes.' }
if ($ContextPitch -and -not $Context) { throw 'ContextPitch requires Context.' }
if ($ContextOriginalTiming -and (-not $Context -or $ContextPitch)) { throw 'ContextOriginalTiming requires Context and excludes ContextPitch.' }
if (@(@($Utau, $Prosody, $Timing, $Join, $Context) | Where-Object { $_.IsPresent }).Count -gt 1) { throw 'Comparison modes are mutually exclusive.' }
if ($Context -ne (-not [string]::IsNullOrWhiteSpace($OpenJtalkAudit))) { throw 'Context requires OpenJtalkAudit, which is only used by Context.' }
$usesBundledBank = $Utau -or $Prosody -or $Timing -or $Join -or $Context
if ($usesBundledBank -and -not $PSBoundParameters.ContainsKey('FlatF0')) { $FlatF0 = 259.4256 }
if ($usesBundledBank -and -not [string]::IsNullOrWhiteSpace($InputWave)) { throw 'Bundled-bank modes do not also accept InputWave.' }
if ($Prosody -and $FlatF0 -eq 0) { throw 'Prosody requires an explicit positive reference F0 (default 259.4256 Hz).' }
if ($Timing -and $FlatF0 -ne 259.4256) { throw 'Timing compares only 180/140 ms at the fixed 259.4256 Hz reference.' }
if ($Join -and $FlatF0 -ne 259.4256) { throw 'Join compares only baseline/vowelcarry at 180 ms and the fixed 259.4256 Hz reference.' }
if ($Context -and $FlatF0 -ne 259.4256) { throw 'Context uses the fixed 259.4256 Hz mean reference.' }
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'WORLD comparison requires Windows x64 PowerShell 7.' }
if (-not [double]::IsFinite($FlatF0) -or ($FlatF0 -ne 0 -and ($FlatF0 -lt 40 -or $FlatF0 -gt 1200))) {
    throw 'FlatF0 must be 0 (voiced-frame median), or 40 to 1200 Hz.'
}
if ((-not $usesBundledBank -and [string]::IsNullOrWhiteSpace($InputWave)) -or [string]::IsNullOrWhiteSpace($OutputPrefix)) { throw 'Specify OutputPrefix and an input or bundled-bank comparison mode.' }

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$local = Join-Path $repository '.local'
$build = Join-Path $local 'build/world'
$previews = Join-Path $local 'previews'
$sdk = Join-Path $local 'dotnet/dotnet.exe'
$project = Join-Path $repository 'src/Tanayomi.Desktop/Tanayomi.Desktop.csproj'
$id = [Guid]::NewGuid().ToString('N')
$temporary = Join-Path $local ('tmp/world-compare-' + $id)
$executable = Join-Path $build 'bin/Tanayomi.Desktop/release/Tanayomi.Desktop.exe'
$job = Join-Path ([IO.Path]::GetDirectoryName($executable)) ('data/tmp/world-compare-' + $id)
$savedEnvironment = @{}
$nativeErrorGuard = $null
$ownedOutputs = [Collections.Generic.List[string]]::new()
$lock = $null
$complete = $false
$retainTemporary = $false

function Assert-NoLinks([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'WORLD comparison paths cannot traverse links.'
        }
    }
}
function Assert-Contained([string]$Path, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) { throw 'Empty comparison path boundary.' }
    $full = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $full.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetRelativePath($boundary, $full).Contains(':')) { throw 'Comparison path escaped its allowed directory.' }
    Assert-NoLinks $full
    return $full
}
function Remove-OwnedDirectory([string]$Path, [string]$Root) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $checked = Assert-Contained $Path $Root
    if ([IO.Path]::GetFileName($checked) -notmatch '^world-compare-[0-9a-f]{32}$') { throw 'Cleanup refused an unowned directory.' }
    if (@(Get-ChildItem -LiteralPath $checked -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -ne 0) {
        throw 'Cleanup refused a linked entry.'
    }
    Remove-Item -LiteralPath $checked -Recurse -Force
}

# Read both pipes concurrently into bounded buffers. No asynchronous PowerShell
# callbacks/runspaces, shell command interpolation, or additional executable.
function Invoke-HiddenProcess([string]$File, [string[]]$Arguments, [int]$Seconds, [int]$Limit) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $repository
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $cancellation = [Threading.CancellationTokenSource]::new()
    $readers = @()
    $started = $false
    try {
        if (-not $process.Start()) { throw 'Could not start the comparison process.' }
        $started = $true
        $process.StandardInput.Close()
        foreach ($stream in @($process.StandardOutput.BaseStream, $process.StandardError.BaseStream)) {
            $buffer = [byte[]]::new(1024)
            $readers += [pscustomobject]@{ Stream = $stream; Buffer = $buffer; Bytes = [IO.MemoryStream]::new();
                Pending = $stream.ReadAsync($buffer, 0, $buffer.Length, $cancellation.Token) }
        }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            foreach ($reader in $readers) {
                if ($null -ne $reader.Pending -and $reader.Pending.IsCompleted) {
                    $count = $reader.Pending.GetAwaiter().GetResult()
                    $reader.Pending = $null
                    if ($count -gt 0) {
                        if ($reader.Bytes.Length + $count -gt $Limit) { throw 'Comparison process exceeded its bounded response size.' }
                        $reader.Bytes.Write($reader.Buffer, 0, $count)
                        $reader.Pending = $reader.Stream.ReadAsync($reader.Buffer, 0, $reader.Buffer.Length, $cancellation.Token)
                    }
                }
            }
            if ($process.HasExited -and $null -eq $readers[0].Pending -and $null -eq $readers[1].Pending) { break }
            if ($watch.Elapsed.TotalSeconds -ge $Seconds) { throw [TimeoutException]::new('Comparison process exceeded its time limit.') }
            [Threading.Thread]::Sleep(20)
        }
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        return [pscustomobject]@{ ExitCode = $process.ExitCode;
            Output = $utf8.GetString($readers[0].Bytes.ToArray()); Error = $utf8.GetString($readers[1].Bytes.ToArray()) }
    }
    finally {
        $cancellation.Cancel()
        try {
            if ($started -and -not $process.HasExited) {
                try {
                    $process.Kill($true) # Only this invocation's owned child tree.
                    if (-not $process.WaitForExit(3000)) { throw 'Comparison child did not stop.' }
                }
                catch {
                    $script:retainTemporary = $true
                    throw 'Could not stop the owned comparison child. Its temporary folders are retained until it exits.'
                }
            }
        }
        finally {
            foreach ($reader in $readers) { $reader.Bytes.Dispose() }
            $process.Dispose()
            $cancellation.Dispose()
        }
    }
}

function Copy-NewFile([string]$Source, [string]$Destination) {
    Assert-NoLinks $Destination
    $sourceStream = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $output = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $ownedOutputs.Add($Destination)
        try { $sourceStream.CopyTo($output) } finally { $output.Dispose() }
    } finally { $sourceStream.Dispose() }
}

try {
    if (-not $usesBundledBank) {
        $inputPath = [IO.Path]::GetFullPath($InputWave, (Get-Location).ProviderPath)
        Assert-NoLinks $inputPath
        if (-not [IO.File]::Exists($inputPath) -or [IO.Path]::GetExtension($inputPath) -ine '.wav') { throw 'InputWave must name an existing WAV file.' }
    }
    $prefix = Assert-Contained ([IO.Path]::GetFullPath($OutputPrefix, (Get-Location).ProviderPath)) $previews
    if ([string]::IsNullOrWhiteSpace([IO.Path]::GetFileName($prefix))) { throw 'OutputPrefix must include a new filename prefix.' }
    if ($Context) { $contextAudit = Assert-Contained ([IO.Path]::GetFullPath($OpenJtalkAudit)) (Join-Path $local 'build/open-jtalk') }
    $outputs = if ($Context) { @(($prefix + '-baseline.wav'), ($prefix + '-context.wav'), ($prefix + '-result.json')) }
        elseif ($Join) { @(($prefix + '-baseline.wav'), ($prefix + '-vowelcarry.wav'), ($prefix + '-result.json')) }
        elseif ($Timing) { @(($prefix + '-180ms.wav'), ($prefix + '-140ms.wav'), ($prefix + '-result.json')) }
        elseif ($Prosody) { @(($prefix + '-flat.wav'), ($prefix + '-phrase.wav'), ($prefix + '-accent.wav'), ($prefix + '-result.json')) }
        else { @(($prefix + '-original.wav'), ($prefix + '-flat.wav'), ($prefix + '-result.json')) }
    foreach ($path in $outputs) {
        $null = Assert-Contained $path $previews
        if (Test-Path -LiteralPath $path) { throw 'Comparison outputs must be new; review artifacts are never overwritten.' }
    }
    foreach ($path in @($build, $temporary, $job)) { $null = Assert-Contained $path $local }
    Assert-NoLinks $sdk
    if (-not [IO.File]::Exists($sdk)) { throw 'The repository .local/dotnet SDK is required.' }
    $nativeDirectory = Join-Path $local 'build/native-world'
    $dll = Join-Path $nativeDirectory 'tanayomi-world.dll'
    Assert-NoLinks $dll
    if (-not [IO.File]::Exists($dll)) { throw 'The audited adopted DLL is required in .local/build/native-world.' }
    [void][IO.Directory]::CreateDirectory($build)
    $lock = [IO.File]::Open((Join-Path $build 'compare.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    [void][IO.Directory]::CreateDirectory($temporary)
    foreach ($name in @('DOTNET_ROOT', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'AVALONIA_TELEMETRY_OPTOUT',
        'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'DOTNET_CLI_USE_MSBUILD_SERVER',
        'MSBUILDDISABLENODEREUSE', 'TEMP', 'TMP', 'TMPDIR')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    $env:DOTNET_ROOT = [IO.Path]::GetDirectoryName($sdk)
    $env:DOTNET_CLI_HOME = Join-Path $local 'cli'
    $env:NUGET_PACKAGES = Join-Path $local 'nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    # This invocation owns its temporary tree. A resident compiler's analyzer
    # shadow copies would keep that tree open after the comparison completes.
    # Ignore shared servers without shutting down another build's processes.
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = 'false'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:TEMP = $temporary; $env:TMP = $temporary; $env:TMPDIR = $temporary
    . (Join-Path $PSScriptRoot 'process-error-mode.ps1')
    $nativeErrorGuard = Enter-NativeProcessErrorMode
    $properties = @('-p:WorldComparison=true', "-p:WorldNativeDirectory=$nativeDirectory", "-p:ArtifactsPath=$build", '-p:UseSharedCompilation=false', '-nr:false')
    $restored = Invoke-HiddenProcess $sdk (@('restore', $project, '--locked-mode', '--source', 'https://api.nuget.org/v3/index.json', '--verbosity', 'minimal') + $properties) 300 1048576
    if ($restored.ExitCode -ne 0) { throw ('Comparison restore failed: ' + $restored.Output + $restored.Error) }
    $built = Invoke-HiddenProcess $sdk (@('build', $project, '--no-restore', '--disable-build-servers', '-c', 'Release', '--verbosity', 'minimal') + $properties) 300 1048576
    if ($built.ExitCode -ne 0) { throw ('Comparison build failed: ' + $built.Output + $built.Error) }
    Assert-NoLinks $executable
    if (-not [IO.File]::Exists($executable)) { throw 'Comparison executable was not produced in its separate build directory.' }
    [void][IO.Directory]::CreateDirectory($job)
    $copiedInput = Join-Path $job 'input.wav'
    if (-not $usesBundledBank) {
        $source = [IO.File]::Open($inputPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            if ($source.Length -lt 44 -or $source.Length -gt 4MB) { throw 'Input WAV exceeds the bounded comparison size.' }
            $copy = [IO.File]::Open($copiedInput, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $source.CopyTo($copy) } finally { $copy.Dispose() }
        } finally { $source.Dispose() }
    }
    $original = Join-Path $job 'original.wav'; $flat = Join-Path $job 'flat.wav'
    $phrase = Join-Path $job 'phrase.wav'; $accent = Join-Path $job 'accent.wav'
    $baseline = Join-Path $job 'mora-180.wav'; $faster = Join-Path $job 'mora-140.wav'
    if ($Join) { $faster = Join-Path $job 'vowelcarry.wav' }
    if ($Context) { $faster = Join-Path $job 'context.wav' }
    $env:TEMP = [IO.Path]::GetDirectoryName($job); $env:TMP = $env:TEMP; $env:TMPDIR = $env:TEMP
    $hzArgument = $FlatF0.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
    if ($Context) {
        $workerArguments = @('--world-context-worker', $job, $baseline, $faster, $hzArgument, $contextAudit)
        if ($ContextPitch) { $workerArguments += 'pitch-control' }
        if ($ContextOriginalTiming) { $workerArguments += 'original-timing' }
        if ($ContextDuration) { $workerArguments += $(if ($ContextConsonants) { 'duration-consonants' } else { 'duration-control' }) }
        $responseLimit = 65536
    }
    elseif ($Timing -or $Join) {
        $pairMode = if ($Join) { '--world-join-worker' } else { '--world-timing-worker' }
        $workerArguments = @($pairMode, $job, $baseline, $faster, $hzArgument)
        $responseLimit = 32768
    }
    elseif ($Prosody) {
        $workerArguments = @('--world-prosody-worker', $job, $flat, $phrase, $accent, $hzArgument)
        $responseLimit = 16384
    }
    else {
        $workerMode = if ($Utau) { '--world-utau-worker' } else { '--world-compare-worker' }
        $workerInput = if ($Utau) { $job } else { $copiedInput }
        $workerArguments = @($workerMode, $workerInput, $original, $flat, $hzArgument)
        $responseLimit = 4096
    }
    $compared = Invoke-HiddenProcess $executable $workerArguments 120 $responseLimit
    if ($compared.ExitCode -ne 0) {
        $safeError = $compared.Error.Trim()
        if ($safeError -notmatch '^world-error:(native:[1-5]|[A-Za-z0-9_]+:[0-9A-F]{8})$') { $safeError = 'No valid sanitized diagnostic.' }
        throw "WORLD comparison failed (exit $($compared.ExitCode)): $safeError"
    }
    $result = $compared.Output | ConvertFrom-Json
    if ($Context) {
        if ([bool]$result.DurationControl -ne [bool]$ContextDuration) { throw 'Unexpected duration control mode.' }
        if ([bool]$result.ConsonantTiming -ne [bool]$ContextConsonants) { throw 'Unexpected consonant timing mode.' }
        if ([bool]$result.PitchControl -ne [bool]($ContextPitch -or $ContextOriginalTiming) -or [bool]$result.OriginalTiming -ne [bool]$ContextOriginalTiming) { throw 'Unexpected pitch control mode.' }
        if ($ContextPitch -or $ContextOriginalTiming) {
            foreach ($property in @('SampleCount','SpectrumSha256','AperiodicitySha256','VoicingSha256')) {
                if ($result.Conditions[0].Analysis.$property -ne $result.Conditions[1].Analysis.$property) { throw 'Pitch control changes non-pitch data.' }
            }
        }
        if ($null -eq $result.Gain -or -not [double]::IsFinite([double]$result.Gain) -or $result.Gain -le 0 -or $result.Gain -gt 1) { throw 'Invalid context gain.' }
        if ($result.Comparison -ne 'context' -or $result.TargetF0 -ne 259.4256 -or @($result.Conditions).Count -ne 2 -or
            $result.ModelFrames -lt 1 -or $result.ModelFrames -gt 9000 -or $result.ManagedDoubleBufferBytes -gt 256MB -or
            @($result.MappedUnits).Count -gt 128 -or @($result.ModelPhones).Count -gt 8192) { throw 'Invalid context controls.' }
        if ($ContextOriginalTiming -or $ContextDuration) {
            if (@($result.SourceRanges).Count -ne 0) { throw 'Original timing must not apply source overrides.' }
        } else {
        if (@($result.SourceRanges).Count -ne 1) { throw 'Missing measured source range.' }
        $sourceRange = $result.SourceRanges[0]
        if ($sourceRange.UnitIndex -ne 27 -or $sourceRange.SampleRate -ne 44100 -or
            $sourceRange.StartSample -ne $sourceRange.OtoOffsetSample -or
            ($sourceRange.PreutteranceSample - $sourceRange.StartSample) -ne 2690 -or
            ($sourceRange.OriginalStableSample - $sourceRange.StartSample) -ne 5777 -or
            ($sourceRange.StableSample - $sourceRange.StartSample) -ne 3131 -or
            ($sourceRange.EndSample - $sourceRange.StartSample) -ne 4895 -or
            $sourceRange.GrainCount -lt 2 -or $sourceRange.MaximumSourceGapSamples -ne 0 -or
            $sourceRange.MaximumNominalDeviationSamples -gt 264) { throw 'Unexpected measured source range.' }
        }
        for ($i = 0; $i -lt 2; $i++) {
            $condition = $result.Conditions[$i]
            if ($condition.Name -ne @('baseline','context')[$i] -or $condition.ClippedSamples -ne 0 -or
                $condition.Analysis.SampleCount -lt 4410 -or $condition.Analysis.SampleCount -gt 1984500 -or
                $condition.Analysis.FftSize -ne 2048 -or $condition.Analysis.VoicedFrames -lt 10 -or
                [Math]::Abs($condition.Analysis.Track.MeanHz - 259.4256) -gt 0.000001) { throw 'Invalid context audio metadata.' }
            foreach ($name in @('RawRms','RawPeak','RmsGain','OutputRms','OutputPeak','PcmRms','PcmPeak')) {
                if ($null -eq $condition.$name -or -not [double]::IsFinite([double]$condition.$name) -or $condition.$name -le 0) { throw 'Invalid context levels.' }
            }
            foreach ($name in @('SpectrumSha256','AperiodicitySha256','VoicingSha256')) {
                if ($condition.Analysis.$name -notmatch '^[0-9A-F]{64}$') { throw 'Invalid context analysis evidence.' }
            }
            foreach ($name in @('MeanHz','GeometricMeanHz','MedianHz','MinimumHz','MaximumHz','MaximumAdjacentSemitones')) {
                if ($null -eq $condition.Analysis.Track.$name -or -not [double]::IsFinite([double]$condition.Analysis.Track.$name) -or $condition.Analysis.Track.$name -lt 0) { throw 'Invalid context F0 track.' }
            }
        }
    }
    elseif ($Timing -or $Join) {
        $comparison = if ($Join) { 'join' } else { 'timing' }
        if ($result.Comparison -ne $comparison -or $result.FlatF0 -ne 259.4256 -or @($result.Conditions).Count -ne 2 -or
            $result.LongVowelAdditionalMilliseconds -ne 160 -or $result.ClosureMilliseconds -ne 85 -or $result.PauseMilliseconds -ne 110) {
            throw 'Invalid timing comparison controls.'
        }
        foreach ($name in @('Gain', 'ManagedDoubleBufferBytes')) {
            if ($null -eq $result.$name -or -not [double]::IsFinite([double]$result.$name) -or $result.$name -le 0) { throw 'Nonfinite timing result.' }
        }
        if ($result.Gain -gt 1 -or $result.ManagedDoubleBufferBytes -gt 256MB) { throw 'Timing comparison exceeded its bounds.' }
        $durations = if ($Join) { @(180, 180) } else { @(180, 140) }
        $pairNames = if ($Join) { @('baseline', 'vowelcarry') } else { @('mora-180', 'mora-140') }
        for ($i = 0; $i -lt 2; $i++) {
            $condition = $result.Conditions[$i]; $analysis = $condition.Analysis
            if ($condition.Name -ne $pairNames[$i] -or $analysis.MoraDurationMilliseconds -ne $durations[$i] -or
                $analysis.CarryPureVowels -isnot [bool] -or $analysis.CarryPureVowels -ne ($Join -and $i -eq 1) -or
                $analysis.SampleCount -lt 4410 -or $analysis.SampleCount -gt 1984500 -or
                $analysis.FrameCount -lt 2 -or $analysis.FrameCount -gt 9001 -or $analysis.FftSize -ne 2048 -or
                $analysis.FrameCount -ne ([Math]::Floor(1000.0 * $analysis.SampleCount / 44100 / 5.0) + 1) -or
                $analysis.VoicedFrames -lt 10 -or $analysis.VoicedFrames -gt $analysis.FrameCount -or
                @($analysis.Anchors).Count -lt 1 -or @($analysis.Anchors).Count -gt 128 -or
                $analysis.ManagedDoubleBufferBytes -le 0 -or $analysis.ManagedDoubleBufferBytes -gt 256MB) { throw 'Invalid timing analysis layout.' }
            foreach ($name in @('SpectrumSha256', 'AperiodicitySha256', 'VoicingSha256')) {
                if ($analysis.$name -notmatch '^[0-9A-F]{64}$') { throw 'Invalid per-condition timing hash.' }
            }
            foreach ($name in @('RawRms', 'RawPeak', 'RmsGain', 'OutputRms', 'OutputPeak', 'PcmRms', 'PcmPeak')) {
                if ($null -eq $condition.$name -or -not [double]::IsFinite([double]$condition.$name) -or $condition.$name -le 0) { throw 'Invalid timing audio levels.' }
            }
            foreach ($name in @('MeanHz', 'GeometricMeanHz', 'MedianHz', 'MinimumHz', 'MaximumHz', 'MaximumAdjacentSemitones')) {
                if ($null -eq $analysis.Track.$name -or -not [double]::IsFinite([double]$analysis.Track.$name) -or $analysis.Track.$name -lt 0) { throw 'Invalid timing F0 track.' }
            }
            if ([Math]::Abs($analysis.Track.MeanHz - 259.4256) -gt 0.000001 -or $analysis.Track.MinimumHz -ne 259.4256 -or
                $analysis.Track.MaximumHz -ne 259.4256 -or $analysis.Track.MaximumAdjacentSemitones -ne 0 -or
                $condition.RmsGain -gt 1 -or $condition.OutputPeak -gt 0.920000001 -or $condition.PcmPeak -gt 0.920000001 -or
                $null -eq $condition.ClippedSamples -or $condition.ClippedSamples -ne 0 -or
                [Math]::Abs($condition.OutputRms - $result.Conditions[0].OutputRms) -gt 0.000000001) { throw 'Timing controls were not preserved.' }
        }
        $before = @($result.Conditions[0].Analysis.Anchors); $after = @($result.Conditions[1].Analysis.Anchors)
        if ($before.Count -ne $after.Count -or
            ($Timing -and $result.Conditions[1].Analysis.SampleCount -ge $result.Conditions[0].Analysis.SampleCount) -or
            ($Join -and $result.Conditions[1].Analysis.SampleCount -ne $result.Conditions[0].Analysis.SampleCount)) { throw 'Pair comparison changed its intended duration controls.' }
        for ($i = 0; $i -lt $before.Count; $i++) {
            if ($before[$i].UnitIndex -ne $after[$i].UnitIndex -or $before[$i].Alias -cne $after[$i].Alias -or
                $before[$i].Pause -cne $after[$i].Pause -or $before[$i].Extensions -ne $after[$i].Extensions -or
                ($null -eq $before[$i].Alias -and ($before[$i].EndSample - $before[$i].StartSample) -ne ($after[$i].EndSample - $after[$i].StartSample))) {
                throw 'Timing comparison changed units or pause duration.'
            }
            if ($Join -and ($before[$i].StartSample -ne $after[$i].StartSample -or
                $before[$i].VowelOnsetSample -ne $after[$i].VowelOnsetSample -or $before[$i].EndSample -ne $after[$i].EndSample)) {
                throw 'Join comparison changed actual unit placement.'
            }
            if ($Timing -and $null -ne $before[$i].Alias -and
                ($null -eq $before[$i].VowelOnsetSample -or $null -eq $after[$i].VowelOnsetSample -or
                 ($before[$i].VowelOnsetSample - $before[$i].StartSample) -ne ($after[$i].VowelOnsetSample - $after[$i].StartSample) -or
                 (($before[$i].EndSample - $before[$i].StartSample) - ($after[$i].EndSample - $after[$i].StartSample)) -ne 1764)) {
                throw 'Timing comparison changed lead placement or did not shorten a sound unit by exactly 40 ms.'
            }
        }
    }
    else {
        $maximumSamples = if ($usesBundledBank) { 1984500 } else { 1323000 }
        $maximumFrames = if ($usesBundledBank) { 9001 } else { 6001 }
        if ($result.SampleCount -lt 4410 -or $result.SampleCount -gt $maximumSamples -or $result.FrameCount -lt 2 -or $result.FrameCount -gt $maximumFrames -or
            $result.FftSize -ne 2048 -or $result.VoicedFrames -lt 10 -or $result.VoicedFrames -gt $result.FrameCount -or
            $result.FlatF0 -lt 40 -or $result.FlatF0 -gt 1200 -or $result.Gain -le 0 -or $result.Gain -gt 1) { throw 'Invalid WORLD comparison result.' }
        $finiteFields = if ($Prosody) { @('FlatF0', 'Gain', 'ContourScale', 'ManagedDoubleBufferBytes') } else { @('FlatF0', 'OriginalPeak', 'FlatPeak', 'Gain') }
        foreach ($name in $finiteFields) {
            if ($null -eq $result.$name -or -not [double]::IsFinite([double]$result.$name) -or $result.$name -lt 0) { throw 'Nonfinite WORLD comparison result.' }
        }
        if ($Prosody) {
            if ($result.ContourScale -le 0 -or $result.ContourScale -gt 1 -or $result.ManagedDoubleBufferBytes -gt 256MB -or
                $result.SpectrumSha256 -notmatch '^[0-9A-F]{64}$' -or $result.AperiodicitySha256 -notmatch '^[0-9A-F]{64}$' -or
                @($result.Conditions).Count -ne 3 -or @($result.Anchors).Count -gt 128) { throw 'Invalid prosody comparison result.' }
            $conditionNames = @('flat', 'phrase', 'phrase-accent')
            for ($i = 0; $i -lt 3; $i++) {
                $condition = $result.Conditions[$i]
                if ($condition.Name -ne $conditionNames[$i]) { throw 'Unexpected prosody condition order.' }
                foreach ($name in @('RawRms', 'RawPeak', 'RmsGain', 'OutputRms', 'OutputPeak', 'PcmRms', 'PcmPeak')) {
                    if ($null -eq $condition.$name -or -not [double]::IsFinite([double]$condition.$name) -or $condition.$name -le 0) { throw 'Invalid prosody audio levels.' }
                }
                foreach ($name in @('MeanHz', 'GeometricMeanHz', 'MedianHz', 'MinimumHz', 'MaximumHz', 'MaximumAdjacentSemitones')) {
                    if ($null -eq $condition.Track.$name -or -not [double]::IsFinite([double]$condition.Track.$name) -or $condition.Track.$name -lt 0) { throw 'Invalid prosody F0 track.' }
                }
                if ([Math]::Abs($condition.Track.MeanHz - $FlatF0) -gt 0.000001 -or $condition.OutputPeak -gt 0.920000001 -or
                    $condition.PcmPeak -gt 0.920000001 -or $condition.ClippedSamples -ne 0 -or
                    $condition.Track.MinimumHz -lt 40 -or $condition.Track.MaximumHz -gt 1200 -or
                    [Math]::Abs($condition.OutputRms - $result.Conditions[0].OutputRms) -gt 0.000000001) { throw 'Prosody controls were not preserved.' }
            }
        }
    }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($prefix))
    if ($Timing -or $Join -or $Context) {
        Copy-NewFile $baseline $outputs[0]
        Copy-NewFile $faster $outputs[1]
    }
    elseif ($Prosody) {
        Copy-NewFile $flat $outputs[0]
        Copy-NewFile $phrase $outputs[1]
        Copy-NewFile $accent $outputs[2]
    }
    else {
        Copy-NewFile $original $outputs[0]
        Copy-NewFile $flat $outputs[1]
    }
    $jsonPath = $outputs[-1]
    $jsonFile = [IO.File]::Open($jsonPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $ownedOutputs.Add($jsonPath)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($compared.Output); $jsonFile.Write($bytes, 0, $bytes.Length) }
    finally { $jsonFile.Dispose() }
    $complete = $true
    $outputs | Write-Output
}
finally {
    if ($null -ne $nativeErrorGuard) { $nativeErrorGuard.Dispose() }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if ($lock) { $lock.Dispose() }
    if (-not $complete) {
        foreach ($path in $ownedOutputs) {
            $null = Assert-Contained $path $previews
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not $retainTemporary) {
        try { Remove-OwnedDirectory $job ([IO.Path]::GetDirectoryName($job)) }
        finally { Remove-OwnedDirectory $temporary (Join-Path $local 'tmp') }
    }
    else { Write-Warning "Owned process is still unresolved; retain $job and $temporary until it exits, then clean only these folders." }
}
