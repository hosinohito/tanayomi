param([ValidateSet('build', 'run', 'test', 'windows-volume-test', 'joins', 'native-test', 'neural-reference-test', 'neural-test', 'skia-native-test', 'open-jtalk-test', 'open-jtalk-product-test', 'bundled-speech-product-test', 'utau-context-product-test', 'utau-pronunciation-comparison', 'utau-devoicing-comparison', 'openvoice-reference-comparison', 'openvoice-long-phrase-diagnostic', 'preview', 'sample', 'publish', 'tryout')][string]$Action = 'build',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('Desktop', 'Android', 'iOS')][string]$ProjectTarget = 'Desktop',
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string]$RuntimeIdentifier = 'win-x64',
    [string]$SamplePath, [switch]$WithoutPitch, [string]$WorldLibrary, [switch]$WorldParallelResources, [switch]$WorldLifterAudit, [string]$OpenJtalkAudit, [string]$OpenJtalkExecutable, [switch]$UtauModes,
    [switch]$NeuralOnly, [switch]$WaveformOnly, [switch]$UseExistingPublish,
    [ValidateSet('all', 'aivis', 'voicevox', 'supertonic')][string]$BundledEngine = 'all',
    [string]$NeuralAudit, [string]$NeuralFixture, [switch]$NeuralProfile,
    [switch]$NeuralPreviousRuntime, [switch]$NeuralRuntimeBenchmark, [switch]$NeuralRuntimeLifetime, [switch]$NeuralRuntimeCancel,
    [string]$SkiaLibrary, [string]$SkiaSourceRoot, [switch]$SkiaFullSuite)
$ErrorActionPreference = 'Stop'
if ($UseExistingPublish -and $Action -ne 'tryout') { throw 'UseExistingPublish requires tryout.' }
if ($BundledEngine -ne 'all' -and $Action -ne 'bundled-speech-product-test') { throw 'BundledEngine requires bundled-speech-product-test.' }
if ($WorldLifterAudit -and ($Action -ne 'native-test' -or $WorldParallelResources)) { throw 'WorldLifterAudit requires native-test and excludes WorldParallelResources.' }
if ($WorldParallelResources -and $Action -ne 'native-test') { throw 'WorldParallelResources requires native-test.' }
if ($SkiaFullSuite -and $Action -ne 'skia-native-test') { throw 'SkiaFullSuite requires skia-native-test.' }
if ($NeuralProfile -and $Action -ne 'neural-test') { throw 'NeuralProfile requires neural-test.' }
if (($NeuralPreviousRuntime -or $NeuralRuntimeBenchmark -or $NeuralRuntimeLifetime -or $NeuralRuntimeCancel) -and $Action -ne 'neural-test') { throw 'Neural runtime audit options require neural-test.' }
if ($NeuralRuntimeBenchmark -and $NeuralProfile) { throw 'Neural runtime benchmark excludes profiling.' }
if ($NeuralRuntimeLifetime -and ($NeuralRuntimeBenchmark -or $NeuralProfile)) { throw 'Neural runtime lifetime excludes benchmarking and profiling.' }
if ($NeuralRuntimeCancel -and ($NeuralRuntimeBenchmark -or $NeuralRuntimeLifetime -or $NeuralProfile)) { throw 'Neural runtime cancellation excludes other runtime scopes and profiling.' }
$RuntimeIdentifier = $RuntimeIdentifier.ToLowerInvariant()
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Development commands require PowerShell 7 or later.' }
if ($ProjectTarget -ne 'Desktop' -and $Action -ne 'build') { throw 'Android/iOS currently support only build; signing, publishing and device deployment are not configured.' }
if ($PSBoundParameters.ContainsKey('RuntimeIdentifier') -and ($ProjectTarget -ne 'Desktop' -or $Action -notin 'publish', 'tryout')) {
    throw 'RuntimeIdentifier currently selects only Desktop publish/tryout output.'
}
$isWindowsHost = [OperatingSystem]::IsWindows()
$isMacHost = [OperatingSystem]::IsMacOS()
$pathComparison = if ($isWindowsHost) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if ($Action -eq 'tryout' -and (($RuntimeIdentifier.StartsWith('win-') -and -not $isWindowsHost) -or
    ($RuntimeIdentifier.StartsWith('osx-') -and -not $isMacHost) -or
    ($RuntimeIdentifier.StartsWith('linux-') -and -not [OperatingSystem]::IsLinux()))) {
    throw 'Tryout data migration requires the target OS to match this host. Use publish for cross-OS output without migrating data.'
}
if ($UtauModes -and $Action -ne 'utau-context-product-test') { throw 'UtauModes requires utau-context-product-test.' }
if ($NeuralOnly -and ($Action -ne 'utau-context-product-test' -or $UtauModes)) { throw 'NeuralOnly requires utau-context-product-test without other mode selectors.' }
if ($WaveformOnly -and ($Action -ne 'utau-context-product-test' -or $UtauModes -or $NeuralOnly)) { throw 'WaveformOnly requires utau-context-product-test without other mode selectors.' }
if ($WithoutPitch -and $Action -ne 'sample') { throw 'WithoutPitch is only a sample comparison option.' }
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.local/cli'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.local/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:TEMP = Join-Path $PSScriptRoot '.local/tmp'
$env:TMP = $env:TEMP
$env:TMPDIR = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
. (Join-Path $PSScriptRoot 'native/process-error-mode.ps1')
$nativeErrorGuard = Enter-NativeProcessErrorMode
try {
$sdkName = if ($isWindowsHost) { 'dotnet.exe' } else { 'dotnet' }
$sdk = Join-Path $PSScriptRoot ('.local/dotnet/' + $sdkName)
if (Test-Path -LiteralPath $sdk) { $env:DOTNET_ROOT = Split-Path $sdk }
else { $sdk = 'dotnet' }
$project = if ($Action -in 'test', 'windows-volume-test', 'joins', 'native-test', 'neural-reference-test', 'neural-test', 'skia-native-test', 'open-jtalk-test', 'open-jtalk-product-test', 'bundled-speech-product-test', 'utau-pronunciation-comparison', 'utau-devoicing-comparison', 'openvoice-reference-comparison', 'openvoice-long-phrase-diagnostic', 'preview', 'sample') { 'tests/Tanayomi.Tests' }
    elseif ($ProjectTarget -eq 'Android') { 'src/Tanayomi.Android' }
    elseif ($ProjectTarget -eq 'iOS') { 'src/Tanayomi.iOS' }
    else { 'src/Tanayomi.Desktop' }
$tryoutRoot = Join-Path $PSScriptRoot $(if ($RuntimeIdentifier -eq 'win-x64') { '.local/tryout' } else { '.local/tryout/' + $RuntimeIdentifier })
$publishDirectory = Join-Path $PSScriptRoot $(if ($RuntimeIdentifier -eq 'win-x64') { '.local/build/app' } else { '.local/build/publish/' + $RuntimeIdentifier })
$desktopExecutableName = if ($RuntimeIdentifier.StartsWith('win-')) { 'Tanayomi.Desktop.exe' } else { 'Tanayomi.Desktop' }

function Assert-MobileBuildPrerequisites {
    if ($ProjectTarget -eq 'Desktop') { return }
    if ($ProjectTarget -eq 'iOS' -and -not $isMacHost) { throw 'iOS builds require a Mac with Xcode. Remote builds and signing are not configured; no SDK will be downloaded.' }
    if (-not (Get-Command $sdk -ErrorAction SilentlyContinue)) { throw 'The pinned .NET SDK is unavailable. Prepare the documented development SDK before building mobile targets.' }
    $workloadOutput = & $sdk workload list
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect installed .NET workloads. No restore or SDK installation was attempted.' }
    $requiredWorkload = if ($ProjectTarget -eq 'Android') { 'android' } else { 'ios' }
    if (($workloadOutput -join "`n") -notmatch ('(?m)^\s*' + $requiredWorkload + '\s+')) {
        throw "The $requiredWorkload workload is not installed for the selected .NET SDK. Prepare it explicitly; this script does not install workloads."
    }
    $selectedSdkVersion = (& $sdk --version | Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($selectedSdkVersion)) { throw 'Could not determine the selected .NET SDK version.' }
    $sdkInventory = & $sdk --list-sdks
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate the selected .NET SDK packs.' }
    $sdkRootDirectory = $null
    foreach ($line in $sdkInventory) {
        if ($line -match ('^' + [regex]::Escape($selectedSdkVersion.Trim()) + '\s+\[(.+)\]$')) { $sdkRootDirectory = Split-Path -Parent $Matches[1]; break }
    }
    if (-not $sdkRootDirectory) { throw 'Could not locate packs for the selected .NET SDK.' }
    $sdkPackPattern = if ($ProjectTarget -eq 'Android') { 'Microsoft.Android.Sdk.*' } else { 'Microsoft.iOS.Sdk.net10*' }
    $sdkPacks = @(Get-ChildItem -LiteralPath (Join-Path $sdkRootDirectory 'packs') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like $sdkPackPattern -and @(Get-ChildItem -LiteralPath $_.FullName -Directory).Count -gt 0 })
    if ($sdkPacks.Count -eq 0) { throw "The $requiredWorkload workload record exists but its SDK pack is missing. Repair the prepared SDK explicitly; no restore was attempted." }
    if ($ProjectTarget -eq 'Android') {
        $androidSdk = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
        if (-not $androidSdk -or -not (Test-Path -LiteralPath (Join-Path $androidSdk 'platforms') -PathType Container) -or
            -not (Test-Path -LiteralPath (Join-Path $androidSdk 'build-tools') -PathType Container)) {
            throw 'Android build requires an existing SDK with platforms and build-tools; specify its path through ANDROID_HOME or ANDROID_SDK_ROOT. No SDK will be downloaded.'
        }
        $javaExecutable = if ($isWindowsHost) { 'java.exe' } else { 'java' }
        if ([string]::IsNullOrWhiteSpace($env:JAVA_HOME) -or
            -not (Test-Path -LiteralPath (Join-Path $env:JAVA_HOME ('bin/' + $javaExecutable)) -PathType Leaf) -or
            -not (Test-Path -LiteralPath (Join-Path $env:JAVA_HOME ('bin/' + $(if ($isWindowsHost) { 'javac.exe' } else { 'javac' }))) -PathType Leaf)) {
            throw 'Android build requires an existing compatible JDK specified through JAVA_HOME. No JDK will be downloaded.'
        }
    }
    else {
        if (-not (Test-Path -LiteralPath '/usr/bin/xcode-select' -PathType Leaf)) { throw 'iOS build requires an existing Xcode installation.' }
        $developerDirectory = & /usr/bin/xcode-select -p
        if ($LASTEXITCODE -ne 0 -or -not $developerDirectory -or -not (Test-Path -LiteralPath (Join-Path $developerDirectory 'Platforms/iPhoneOS.platform'))) {
            throw 'Select an existing full Xcode installation before building iOS; command-line tools alone are insufficient. No Xcode settings were changed.'
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot ($project + '/packages.lock.json')) -PathType Leaf)) {
        throw "$ProjectTarget dependency lock has not been established. Generate and review it using the prepared target SDK before enabling the normal locked build."
    }
}
function Assert-LocalTree([string]$Path) {
    $localRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.local'))
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($localRoot + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
        throw 'The app copy must stay inside this workspace .local directory.'
    }
    $ancestor = $resolved
    while ($ancestor.Length -ge $localRoot.Length) {
        if (Test-Path -LiteralPath $ancestor) {
            $entry = Get-Item -LiteralPath $ancestor -Force
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'App copy paths must not contain links.' }
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    if (Test-Path -LiteralPath $resolved) {
        foreach ($entry in Get-ChildItem -LiteralPath $resolved -Recurse -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'App copy contents must not contain links.' }
        }
    }
}
function Test-TryoutRuntime([string]$Directory) {
    $candidateExecutable = Join-Path $Directory $desktopExecutableName
    $candidateManifest = Join-Path $Directory 'Tanayomi.Desktop.deps.json'
    if (-not (Test-Path -LiteralPath $candidateExecutable -PathType Leaf) -or -not (Test-Path -LiteralPath $candidateManifest -PathType Leaf)) { return $false }
    Assert-LocalTree $candidateExecutable
    Assert-LocalTree $candidateManifest
    try {
        $metadata = Get-Content -LiteralPath $candidateManifest -Raw | ConvertFrom-Json -Depth 64
        return ([string]$metadata.runtimeTarget.name).EndsWith('/' + $RuntimeIdentifier, [StringComparison]::Ordinal)
    }
    catch { return $false }
}
function Copy-TryoutData([string]$Source, [string]$Destination, [bool]$RootLevel = $true) {
    New-Item -ItemType Directory -Path $Destination | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Force) {
        if (($RootLevel -and $entry.Name -eq 'tmp') -or $entry.Name.StartsWith('.import-', [StringComparison]::Ordinal)) { continue }
        $target = Join-Path $Destination $entry.Name
        if ($entry.PSIsContainer) { Copy-TryoutData $entry.FullName $target $false }
        else { Copy-Item -LiteralPath $entry.FullName -Destination $target }
    }
}
if ($Action -eq 'tryout') { Assert-LocalTree $tryoutRoot }
if ($Action -eq 'sample') {
    if ([string]::IsNullOrWhiteSpace($SamplePath)) { throw 'Specify a new WAV path with -SamplePath inside .local/previews.' }
    $SamplePath = [IO.Path]::GetFullPath($SamplePath)
    $sampleRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.local/previews'))
    if (-not $SamplePath.StartsWith($sampleRoot + [IO.Path]::DirectorySeparatorChar, $pathComparison) -or
        [IO.Path]::GetExtension($SamplePath) -ne '.wav') { throw 'SamplePath must be a WAV file inside .local/previews.' }
    Assert-LocalTree $SamplePath
    if (Test-Path -LiteralPath $SamplePath) { throw 'SamplePath must be new; existing review audio is never overwritten.' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($SamplePath))
}
if ($Action -eq 'utau-context-product-test') {
    # Test the specified existing product; do not restore, build or publish it.
    if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsWindows) { throw 'This check requires Windows PowerShell 7.' }
    if ([string]::IsNullOrWhiteSpace($OpenJtalkExecutable)) { throw 'Specify the product exe with -OpenJtalkExecutable.' }
    $productExecutable = [IO.Path]::GetFullPath($OpenJtalkExecutable)
    Assert-LocalTree $productExecutable
    if ([IO.Path]::GetFileName($productExecutable) -ne 'Tanayomi.Desktop.exe' -or -not [IO.File]::Exists($productExecutable)) {
        throw 'Specify an existing Tanayomi.Desktop.exe inside this workspace .local directory.'
    }
    $productTemporary = [IO.Path]::GetFullPath((Join-Path (Split-Path $productExecutable) 'data/tmp'))
    $contextRoot = Join-Path $productTemporary ('context-' + [Guid]::NewGuid().ToString('N'))
    Assert-LocalTree $contextRoot
    if (Test-Path -LiteralPath $contextRoot) { throw 'The context check requires a new temporary root.' }
    $process = [Diagnostics.Process]::new()
    $readCancellation = [Threading.CancellationTokenSource]::new()
    $readers = @(); $started = $false; $created = $false; $confirmed = $false

    # Poll .NET async reads without background PowerShell callbacks. Retain at
    # most 8 KiB per pipe; cleanup can drain/discard after a response-size error.
    function Read-ContextCheckPipes([object[]]$Readers, [bool]$Discard = $false) {
        foreach ($reader in $Readers) {
            if ($reader.Ended) { continue }
            if ($null -eq $reader.Pending) {
                $reader.Pending = $reader.Stream.ReadAsync($reader.Buffer, 0, $reader.Buffer.Length, $readCancellation.Token)
            }
            if (-not $reader.Pending.IsCompleted) { continue }
            $count = $reader.Pending.GetAwaiter().GetResult(); $reader.Pending = $null
            if ($count -eq 0) { $reader.Ended = $true; continue }
            if (-not $Discard) {
                if ($reader.Bytes.Length + $count -gt 16384) { throw 'Context check exceeded its bounded pipe response.' }
                $reader.Bytes.Write($reader.Buffer, 0, $count)
            }
        }
    }

    try {
        New-Item -ItemType Directory -Path $contextRoot | Out-Null
        $created = $true
        $start = [Diagnostics.ProcessStartInfo]::new($productExecutable)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.WorkingDirectory = Split-Path $productExecutable
        $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $start.ArgumentList.Add('--utau-context-check-worker'); $start.ArgumentList.Add($contextRoot)
        if ($UtauModes) { $start.ArgumentList.Add('--modes') }
        if ($NeuralOnly) { $start.ArgumentList.Add('--neural') }
        if ($WaveformOnly) { $start.ArgumentList.Add('--waveform') }
        foreach ($name in @('TEMP', 'TMP', 'TMPDIR')) { $start.Environment[$name] = $contextRoot }
        $process.StartInfo = $start
        if (-not $process.Start()) { throw 'Could not start the owned context check worker.' }
        $started = $true
        $process.StandardInput.Close()
        foreach ($stream in @($process.StandardOutput.BaseStream, $process.StandardError.BaseStream)) {
            $readers += [pscustomobject]@{ Stream = $stream; Buffer = [byte[]]::new(1024);
                Bytes = [IO.MemoryStream]::new(); Pending = $null; Ended = $false }
        }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            Read-ContextCheckPipes $readers
            if ($process.HasExited -and $readers[0].Ended -and $readers[1].Ended) { break }
            if ($watch.Elapsed.TotalSeconds -ge $(if ($UtauModes -or $NeuralOnly -or $WaveformOnly) { 180 } else { 60 })) { throw [TimeoutException]::new('Context check exceeded its deadline.') }
            [Threading.Thread]::Sleep(20)
        }
        if ($process.ExitCode -ne 0 -or $readers[1].Bytes.Length -ne 0) {
            $safeDiagnostics = @(([Text.Encoding]::UTF8.GetString($readers[1].Bytes.ToArray()) -split '\r?\n') | Where-Object {
                $_ -match '^world-error:[A-Za-z][A-Za-z0-9]*:[A-Fa-f0-9-]+$' -or
                $_ -match '^context-case:(Context|Simple|Timing|Flat|FastContext|Psola|PhaseVocoder|DirectPsola|OpenVoice):[0-4]:[A-Za-z][A-Za-z0-9]*$'
            }) -join ', '
            throw ('Product context worker failed: ' + $safeDiagnostics)
        }
        $json = ([Text.UTF8Encoding]::new($false, $true)).GetString($readers[0].Bytes.ToArray()).Trim()
        if ($json.Contains("`r") -or $json.Contains("`n")) { throw 'Context check must return exactly one JSON line.' }
        $report = $json | ConvertFrom-Json -Depth 8
        $modeNames = @('Context','Simple','Timing','Flat','FastContext','Psola','PhaseVocoder','DirectPsola')
        if ($UtauModes) {
            $count = @($report.Cases).Count
            if ($count -notin 40,45) { throw 'Invalid mode group count.' }
            if ($count -eq 45) { $modeNames += 'OpenVoice' }
        }
        $expectedCount = if ($WaveformOnly) { 20 } elseif ($NeuralOnly) { 5 } elseif ($UtauModes) { $modeNames.Count * 5 } elseif ($report.ContextRevision -eq 2) { 7 } else { 4 }
        if ($report.Check -cne 'utau-product-context' -or @($report.Cases).Count -ne $expectedCount) { throw 'Invalid context check response schema.' }
        for ($i = 0; $i -lt $expectedCount; $i++) {
            $case = $report.Cases[$i]
            $expectedCase = if ($UtauModes -or $WaveformOnly) { $i % 5 } else { $i }
            $expectedMode = if ($WaveformOnly) { @('Timing','Psola','PhaseVocoder','DirectPsola')[[int][Math]::Floor($i / 5)] } elseif ($NeuralOnly) { 'OpenVoice' } elseif ($UtauModes) { $modeNames[[int][Math]::Floor($i / 5)] } else { 'Context' }
            $expectedFallback = if ($expectedCase -eq 2 -and $expectedMode -in @('Context','Timing','FastContext','Psola','PhaseVocoder','DirectPsola')) { 1 } else { 0 }
            $expectedPeriodFallback = if ($expectedCase -eq 2 -and $expectedMode -eq 'DirectPsola') { 1 } else { 0 }
            foreach ($field in @('Case', 'FallbackCount', 'TimingFallbackCount', 'SampleRate', 'Samples', 'Peak', 'Seconds', 'ElapsedMilliseconds')) {
                if ($null -eq $case.$field) { throw 'Context check response is missing required numeric metadata.' }
            }
            if ($case.Case -ne $expectedCase -or $case.Mode -cne $expectedMode -or $case.FallbackCount -ne 0 -or [int]$case.PeriodFallbackCount -ne $expectedPeriodFallback -or $case.TimingFallbackCount -ne $expectedFallback -or $case.SampleRate -ne 44100 -or
                $case.Samples -lt 4410 -or $case.Samples -gt 1984500 -or $case.Samples -ne [Math]::Truncate($case.Samples) -or
                $case.Peak -lt 1 -or $case.Peak -gt 30147 -or $case.Peak -ne [Math]::Truncate($case.Peak) -or
                -not [double]::IsFinite($case.Seconds) -or [Math]::Abs($case.Seconds - $case.Samples / 44100.0) -gt 1e-9 -or
                -not [double]::IsFinite($case.ElapsedMilliseconds) -or $case.ElapsedMilliseconds -lt 0) {
                throw "Product context mismatch: mode=$expectedMode case=$expectedCase fallback=$($case.FallbackCount) period=$($case.PeriodFallbackCount) timing=$($case.TimingFallbackCount) expectedTiming=$expectedFallback samples=$($case.Samples) peak=$($case.Peak)."
            }
        }
    }
    finally {
        try {
            if ($started) {
                if (-not $process.HasExited) {
                    try { $process.Kill($true) } # This invocation's child tree only.
                    catch { if (-not $process.HasExited) { throw } }
                }
                if (-not $process.WaitForExit(3000)) { throw 'Owned context worker termination was not confirmed.' }
                $drainWatch = [Diagnostics.Stopwatch]::StartNew()
                while (@($readers | Where-Object { -not $_.Ended }).Count -gt 0 -and $drainWatch.Elapsed.TotalSeconds -lt 3) {
                    Read-ContextCheckPipes $readers $true
                    [Threading.Thread]::Sleep(20)
                }
                if (@($readers | Where-Object { -not $_.Ended }).Count -ne 0) { throw 'Owned context worker pipes did not close.' }
            }
            $confirmed = $true
        }
        finally {
            $readCancellation.Cancel()
            foreach ($reader in $readers) { $reader.Stream.Dispose(); $reader.Bytes.Dispose() }
            $process.Dispose(); $readCancellation.Dispose()
            if ($created -and $confirmed) {
                $resolvedRoot = [IO.Path]::GetFullPath($contextRoot)
                if (-not $resolvedRoot.StartsWith($productTemporary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                    [IO.Path]::GetFileName($resolvedRoot) -notmatch '^context-[0-9a-f]{32}$' -or
                    [IO.Path]::GetRelativePath($productTemporary, $resolvedRoot).Contains(':')) { throw 'Context cleanup refused an unowned root.' }
                Assert-LocalTree $resolvedRoot
                Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
            }
            elseif ($created) { Write-Warning ('Context child termination is unconfirmed; retain this owned root until it exits: ' + $contextRoot) }
        }
    }
    Write-Output $json
    Write-Output 'PASS: fixed public phrases, selected synthesis modes, expected timing fallback, bounded non-silent PCM; no playback or audio files.'
    exit 0
}
Assert-MobileBuildPrerequisites
if ($Action -in 'publish', 'tryout') {
    Assert-LocalTree $publishDirectory
    $existingPublishManifest = Join-Path $publishDirectory 'Tanayomi.Desktop.deps.json'
    if (((Test-Path -LiteralPath $existingPublishManifest) -or (Test-Path -LiteralPath (Join-Path $publishDirectory 'data'))) -and -not (Test-TryoutRuntime $publishDirectory)) {
        throw 'The existing publish output does not match the selected RID; it will not be overwritten or used as a data source.'
    }
    # Restore the complete fixed RID set in the lock; publish selects one RID.
    if ($UseExistingPublish) {
        if (-not (Test-TryoutRuntime $publishDirectory)) { throw 'No validated publish output exists for the selected RID.' }
        $LASTEXITCODE = 0
    } else {
        & $sdk restore $project --source https://api.nuget.org/v3/index.json --locked-mode --verbosity minimal -p:SelfContained=true
    }
} else {
    & $sdk restore $project --source https://api.nuget.org/v3/index.json --locked-mode --verbosity minimal
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
switch ($Action) {
    'build' { & $sdk build $project --no-restore -c $Configuration }
    'run' { & $sdk run --project $project --no-restore -c $Configuration }
    'test' { & $sdk run --project $project --no-restore -c $Configuration }
    'windows-volume-test' { & $sdk run --project $project --no-restore -c $Configuration -- --windows-volume-boost-check }
    'joins' { & $sdk run --project $project --no-restore -c $Configuration -- --utau-joins }
    { $_ -in 'neural-test', 'neural-reference-test', 'skia-native-test' } {
        $nativeCheck = '--neural-native-check'
        if ($Action -eq 'skia-native-test') {
            if ([string]::IsNullOrWhiteSpace($SkiaLibrary) -or [string]::IsNullOrWhiteSpace($SkiaSourceRoot)) { throw 'Specify -SkiaLibrary and -SkiaSourceRoot.' }
            $audit = [IO.Path]::GetFullPath($SkiaLibrary); $fixture = [IO.Path]::GetFullPath($SkiaSourceRoot)
            Assert-LocalTree $audit; Assert-LocalTree $fixture
            if (-not $isWindowsHost -or -not [IO.File]::Exists($audit) -or -not [IO.Directory]::Exists($fixture)) { throw 'Skia check requires Windows and the fixed library/source.' }
            $nativeCheck = if ($SkiaFullSuite) { '--with-skia-native' } else { '--skia-native-check' }
        } elseif ($Action -eq 'neural-reference-test') {
            $nativeCheck = '--neural-reference-check'
        } else {
        if (-not $isWindowsHost -or [string]::IsNullOrWhiteSpace($NeuralAudit) -or [string]::IsNullOrWhiteSpace($NeuralFixture)) {
            throw 'Specify -NeuralAudit and -NeuralFixture on Windows.'
        }
        $audit = [IO.Path]::GetFullPath($NeuralAudit); $fixture = [IO.Path]::GetFullPath($NeuralFixture)
        Assert-LocalTree $audit
        $fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'tests/Fixtures'))
        if (-not $fixture.StartsWith($fixtureRoot + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
            throw 'Neural fixture must be a tracked input under tests/Fixtures.'
        }
        if (-not [IO.Directory]::Exists($audit) -or -not (Test-Path -LiteralPath $fixture)) { throw 'Neural audit or fixture is missing.' }
        if ($NeuralProfile) { $nativeCheck = '--neural-profile-check' }
        }
        & $sdk build $project --no-restore -c $Configuration --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $runner = Join-Path $PSScriptRoot ('.local/build/bin/Tanayomi.Tests/' + $Configuration.ToLowerInvariant() + '/Tanayomi.Tests.exe')
        $start = [Diagnostics.ProcessStartInfo]::new($runner)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $start.WorkingDirectory = $PSScriptRoot
        $start.ArgumentList.Add($nativeCheck)
        if ($Action -ne 'neural-reference-test') { $start.ArgumentList.Add($audit); $start.ArgumentList.Add($fixture) }
        if ($NeuralPreviousRuntime) { $start.ArgumentList.Add('--previous-runtime') }
        if ($NeuralRuntimeBenchmark) { $start.ArgumentList.Add('--runtime-benchmark') }
        if ($NeuralRuntimeLifetime) { $start.ArgumentList.Add('--runtime-lifetime') }
        if ($NeuralRuntimeCancel) { $start.ArgumentList.Add('--runtime-cancel') }
        # Native diagnostics are emitted as text by the existing test runner.
        # The parent owns a wall deadline even if native cancellation fails.
        $child = [Diagnostics.Process]::new(); $child.StartInfo = $start; $started = $false
        try {
            $started = $child.Start()
            if (-not $started) { throw 'Could not start neural native check.' }
            $standardOutput = $child.StandardOutput.ReadToEndAsync(); $standardError = $child.StandardError.ReadToEndAsync()
            $watch = [Diagnostics.Stopwatch]::StartNew()
            while (-not $child.WaitForExit(1000)) {
                if ($watch.Elapsed.TotalSeconds -ge 180) { throw [TimeoutException]::new('Neural native check exceeded 180 seconds.') }
            }
            if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($standardOutput, $standardError), 5000)) { throw 'Neural native check pipes did not close.' }
            $outputLimit = if ($SkiaFullSuite) { 1MB } else { 16384 }
            if ($standardOutput.Result.Length -gt $outputLimit -or $standardError.Result.Length -gt 16384) { throw 'Native check response exceeded its limit.' }
            if ($standardOutput.Result) { Write-Output $standardOutput.Result.TrimEnd() }
            if ($standardError.Result) { throw ('Neural native check reported: ' + $standardError.Result.TrimEnd()) }
            if ($child.ExitCode -ne 0) { throw "Neural native check failed with exit code $($child.ExitCode)." }
        }
        finally {
            if ($started -and -not $child.HasExited) {
                $child.Kill($true)
                if (-not $child.WaitForExit(5000)) { throw 'Neural native check termination is unconfirmed.' }
            }
            $child.Dispose()
        }
    }
    'open-jtalk-product-test' {
        if ([string]::IsNullOrWhiteSpace($OpenJtalkExecutable)) { throw 'Specify the product exe with -OpenJtalkExecutable.' }
        $productExecutable = [IO.Path]::GetFullPath($OpenJtalkExecutable)
        Assert-LocalTree $productExecutable
        & $sdk run --project $project --no-restore -c $Configuration -- --open-jtalk-product-check $productExecutable
        if ($LASTEXITCODE -eq 0) { & $sdk run --project $project --no-restore -c $Configuration -- --open-jtalk-plan-product-check $productExecutable }
    }
    'bundled-speech-product-test' {
        if ([string]::IsNullOrWhiteSpace($OpenJtalkExecutable)) { throw 'Specify the product exe with -OpenJtalkExecutable.' }
        $productExecutable = [IO.Path]::GetFullPath($OpenJtalkExecutable)
        Assert-LocalTree $productExecutable
        & $sdk run --project $project --no-restore -c $Configuration -- --bundled-speech-product-check $productExecutable $BundledEngine
    }
    'open-jtalk-test' {
        if ([string]::IsNullOrWhiteSpace($OpenJtalkAudit)) { throw 'Specify the audit directory with -OpenJtalkAudit.' }
        $audit = [IO.Path]::GetFullPath($OpenJtalkAudit)
        Assert-LocalTree $audit
        & $sdk run --project $project --no-restore -c $Configuration -- --open-jtalk-integration $audit
    }
    'native-test' {
        if ([string]::IsNullOrWhiteSpace($WorldLibrary)) { throw 'Specify the audited DLL with -WorldLibrary.' }
        $library = [IO.Path]::GetFullPath($WorldLibrary)
        Assert-LocalTree $library
        if ([IO.Path]::GetFileName($library) -ne 'tanayomi-world.dll') { throw 'Expected tanayomi-world.dll.' }
        $manifest = Get-Content -LiteralPath (Join-Path (Split-Path $library) 'world-build.json') -Raw | ConvertFrom-Json
        if ((Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash -ne $manifest.dllSha256) { throw 'Native DLL does not match its build manifest.' }
        & $sdk build $project --no-restore -c $Configuration --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $runner = Join-Path $PSScriptRoot ('.local/build/bin/Tanayomi.Tests/' + $Configuration.ToLowerInvariant())
        $copy = Join-Path $runner 'tanayomi-world.dll'
        Assert-LocalTree $copy
        if (Test-Path -LiteralPath $copy) { throw 'The test directory already has a native DLL; do not overwrite it.' }
        $owned = $false
        try {
            [IO.File]::Copy($library, $copy, $false)
            $owned = $true
            $worldEntry = if ($WorldLifterAudit) { '--world-lifter-audit' } elseif ($WorldParallelResources) { '--world-parallel-resource-audit' } else { '--world-integration' }
            & (Join-Path $runner 'Tanayomi.Tests.exe') $worldEntry
        }
        finally { if ($owned) { Assert-LocalTree $copy; Remove-Item -LiteralPath $copy -Force } }
    }
    'preview' { & $sdk run --project $project --no-restore -c $Configuration -- --render }
    'sample' {
        $sampleOption = if ($WithoutPitch) { '--utau-sample-unpitched' } else { '--utau-sample' }
        & $sdk run --project $project --no-restore -c $Configuration -- $sampleOption $SamplePath
    }
    'utau-pronunciation-comparison' {
        $comparisonProduct = Join-Path $PSScriptRoot '.local/build/app/Tanayomi.Desktop.exe'
        if (-not (Test-Path -LiteralPath $comparisonProduct -PathType Leaf)) { throw 'Publish the development app before this comparison.' }
        & $sdk run --project $project --no-restore -c $Configuration -- --utau-pronunciation-comparison $comparisonProduct
    }
    'utau-devoicing-comparison' {
        $comparisonProduct = Join-Path $PSScriptRoot '.local/build/app/Tanayomi.Desktop.exe'
        if (-not (Test-Path -LiteralPath $comparisonProduct -PathType Leaf)) { throw 'Publish the development app before this comparison.' }
        & $sdk run --project $project --no-restore -c $Configuration -- --utau-devoicing-comparison $comparisonProduct
    }
    'openvoice-long-phrase-diagnostic' {
        $diagnosticProduct = Join-Path $PSScriptRoot '.local/build/app/Tanayomi.Desktop.exe'
        if (-not (Test-Path -LiteralPath $diagnosticProduct -PathType Leaf)) { throw 'Publish the development app before this diagnostic.' }
        & $sdk run --project $project --no-restore -c $Configuration -- --openvoice-long-phrase-diagnostic $diagnosticProduct
    }
    'openvoice-reference-comparison' {
        $comparisonProduct = Join-Path $PSScriptRoot '.local/build/app/Tanayomi.Desktop.exe'
        if (-not (Test-Path -LiteralPath $comparisonProduct -PathType Leaf)) { throw 'Publish the development app before this comparison.' }
        & $sdk run --project $project --no-restore -c $Configuration -- --openvoice-reference-comparison $comparisonProduct
    }
    { $_ -in 'publish', 'tryout' } {
        if (-not $UseExistingPublish) {
            & $sdk publish $project --no-restore -c Release -r $RuntimeIdentifier --self-contained true -o $publishDirectory -p:PublishResourcesDirectly=true
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        if (-not (Test-TryoutRuntime $publishDirectory)) { throw 'Published output does not match the selected RID.' }
        # Keep notices from the exact runtime pack bundled by this publish.
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'Tanayomi.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
        $runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
        if (-not $runtimeVersion) { throw 'Published .NET runtime version could not be determined.' }
        $runtimePackage = Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.$RuntimeIdentifier/$runtimeVersion"
        $noticeDirectory = Join-Path $publishDirectory 'licenses/dotnet'
        New-Item -ItemType Directory -Force -Path $noticeDirectory | Out-Null
        foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            Copy-Item -LiteralPath (Join-Path $runtimePackage $notice) -Destination (Join-Path $noticeDirectory $notice)
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.md') -Destination $publishDirectory
        if ($Action -eq 'tryout') {
            Assert-LocalTree $publishDirectory
            Assert-LocalTree $tryoutRoot
            New-Item -ItemType Directory -Force -Path $tryoutRoot | Out-Null
            $previous = Get-ChildItem -LiteralPath $tryoutRoot -Directory |
                Where-Object { $_.Name -match '^\d{8}-\d{6}-[a-f0-9]{6}$' -and (Test-TryoutRuntime $_.FullName) } |
                Sort-Object CreationTimeUtc -Descending | Select-Object -First 1
            $previousDirectory = if ($previous) { $previous.FullName }
                elseif (Test-TryoutRuntime $tryoutRoot) { $tryoutRoot }
                else { $publishDirectory }
            $id = [Guid]::NewGuid().ToString('N')
            $staging = Join-Path $tryoutRoot ('.creating-' + $id)
            $tryoutDirectory = Join-Path $tryoutRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $id.Substring(0,6))
            Assert-LocalTree $staging
            Assert-LocalTree $tryoutDirectory
            if (Test-Path -LiteralPath $tryoutDirectory) { throw 'The new tryout path already exists.' }
            New-Item -ItemType Directory -Path $staging | Out-Null
            try {
                foreach ($entry in Get-ChildItem -LiteralPath $publishDirectory -Force) {
                    if ($entry.Name -eq 'data') { continue }
                    Copy-Item -LiteralPath $entry.FullName -Destination $staging -Recurse
                }
                $previousData = Join-Path $previousDirectory 'data'
                if (Test-Path -LiteralPath $previousData) {
                    Assert-LocalTree $previousData
                    Copy-TryoutData $previousData (Join-Path $staging 'data')
                }
                if (-not $isWindowsHost) {
                    $sourceExecutable = Join-Path $publishDirectory $desktopExecutableName
                    $copiedExecutable = Join-Path $staging $desktopExecutableName
                    [IO.File]::SetUnixFileMode($copiedExecutable, [IO.File]::GetUnixFileMode($sourceExecutable))
                }
                # Only the newly created, checked staging directory is moved.
                Assert-LocalTree $staging
                Assert-LocalTree $tryoutDirectory
                Move-Item -LiteralPath $staging -Destination $tryoutDirectory
            }
            finally {
                if (Test-Path -LiteralPath $staging) {
                    Assert-LocalTree $staging
                    Remove-Item -LiteralPath $staging -Recurse -Force
                }
            }
            Write-Output (Join-Path $tryoutDirectory $desktopExecutableName)
        }
    }
}
exit $LASTEXITCODE
} finally { if ($null -ne $nativeErrorGuard) { $nativeErrorGuard.Dispose() } }







