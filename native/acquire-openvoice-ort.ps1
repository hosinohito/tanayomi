[CmdletBinding()]
param([Parameter(Mandatory)][string]$WorkRoot, [string]$GitExecutable = '', [string]$SourceRepository = '')
# Development-only source acquisition. Existing work roots are never modified.
$ErrorActionPreference = 'Stop'
try {
    if (-not $IsWindows) { throw 'This source preparation targets Windows.' }
    if ([string]::IsNullOrWhiteSpace($WorkRoot)) { throw 'Specify a new WorkRoot directly under repository .local/build.' }
    $taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $taskBuild = Join-Path $taskRepository '.local/build'
    $taskRoot = [IO.Path]::GetFullPath($WorkRoot).TrimEnd([char[]]'\/')
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($taskRoot), [IO.Path]::GetFullPath($taskBuild), [StringComparison]::OrdinalIgnoreCase)) { throw 'WorkRoot must be a direct child of repository .local/build.' }
    for ($taskCurrent = $taskRoot; $taskCurrent; $taskCurrent = [IO.Path]::GetDirectoryName($taskCurrent)) {
        if ((Test-Path -LiteralPath $taskCurrent) -and ((Get-Item -LiteralPath $taskCurrent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Source paths cannot traverse links.' }
    }
    if (Test-Path -LiteralPath $taskRoot) { throw 'WorkRoot already exists; choose a new path.' }
    $taskGit = if ($GitExecutable) { [IO.Path]::GetFullPath($GitExecutable) } else { (Get-Command git.exe -ErrorAction Stop).Source }
    $taskLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'openvoice-ort-source.lock.json') -Raw | ConvertFrom-Json
    if ($taskLock.schema -ne 1 -or $taskLock.algorithm -ne 'git-tree-blob-sha256-v1') { throw 'Unsupported ORT source lock.' }
    $taskOrigin = $taskLock.primary
    $taskLocalSource = -not [string]::IsNullOrWhiteSpace($SourceRepository)
    if ($taskLocalSource) {
        $taskOrigin = [IO.Path]::GetFullPath($SourceRepository)
        if (-not (Test-Path -LiteralPath $taskOrigin -PathType Container)) { throw 'SourceRepository must be an existing local Git checkout or transferred copy.' }
        for ($taskCurrent = $taskOrigin; $taskCurrent; $taskCurrent = [IO.Path]::GetDirectoryName($taskCurrent)) {
            if ((Get-Item -LiteralPath $taskCurrent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'SourceRepository cannot traverse links.' }
        }
    }
    $taskIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { if ([Security.Principal.WindowsPrincipal]::new($taskIdentity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run without OS administrator elevation.' } } finally { $taskIdentity.Dispose() }
    New-Item -ItemType Directory -Path $taskRoot | Out-Null
    $taskTemporary = Join-Path $taskRoot 'tmp'
    New-Item -ItemType Directory -Path $taskTemporary | Out-Null
    $taskOriginalTemporary = @($env:TEMP, $env:TMP, $env:TMPDIR)
    $env:TEMP = $taskTemporary; $env:TMP = $taskTemporary; $env:TMPDIR = $taskTemporary
    . (Join-Path $PSScriptRoot 'process-error-mode.ps1')
    $taskGuard = Enter-NativeProcessErrorMode
    try {
        function Invoke-SourceGit([string[]]$Arguments) {
            $taskInfo = [Diagnostics.ProcessStartInfo]::new($taskGit)
            $taskInfo.UseShellExecute = $false; $taskInfo.CreateNoWindow = $true
            $taskInfo.RedirectStandardOutput = $true; $taskInfo.RedirectStandardError = $true
            $taskInfo.WorkingDirectory = $taskRoot
            $taskInfo.Environment['GIT_TERMINAL_PROMPT'] = '0'
            $taskInfo.Environment['GIT_NO_LAZY_FETCH'] = '1'
            $taskInfo.Environment['GCM_INTERACTIVE'] = 'Never'
            $taskInfo.Environment['GIT_ASKPASS'] = ''
            foreach ($taskArgument in @('-c','credential.helper=','-c','core.askPass=')) { $taskInfo.ArgumentList.Add($taskArgument) }
            if ($taskLocalSource) {
                $taskInfo.ArgumentList.Add('-c'); $taskInfo.ArgumentList.Add("safe.directory=$($taskOrigin.Replace('\','/'))")
            }
            foreach ($taskArgument in $Arguments) { $taskInfo.ArgumentList.Add($taskArgument) }
            $taskProcess = [Diagnostics.Process]::Start($taskInfo)
            try {
                $taskOut = $taskProcess.StandardOutput.ReadToEndAsync(); $taskError = $taskProcess.StandardError.ReadToEndAsync()
                if (-not $taskProcess.WaitForExit(180000)) { $taskProcess.Kill($true); if (-not $taskProcess.WaitForExit(5000)) { throw 'Owned source Git process could not be reaped; retain source.' }; throw 'Source acquisition exceeded 180 seconds.' }
                if ($taskProcess.ExitCode -ne 0) { throw ('Git source preparation failed with exit code ' + $taskProcess.ExitCode + '; verify the selected repository and complete pinned Git objects.') }
                return $taskOut.GetAwaiter().GetResult().Trim()
            } finally { if (-not $taskProcess.HasExited) { $taskProcess.Kill($true); $null = $taskProcess.WaitForExit(5000) }; $taskProcess.Dispose() }
        }
        $taskSource = Join-Path $taskRoot 'ort-source'
        $taskClone = @('-c', 'core.autocrlf=false', 'clone', '--branch', $taskLock.tag, '--single-branch', '--no-checkout')
        $taskClone += if ($taskLocalSource) { @('--no-hardlinks') } else { @('--depth', '1') }
        $taskCandidate = Join-Path $taskRoot 'source-primary'
        try {
            $null = Invoke-SourceGit ($taskClone + @($taskOrigin, $taskCandidate))
        } catch {
            if ($taskLocalSource) { throw }
            Write-Output 'The primary ORT repository could not be fetched; trying the fixed source-only mirror.'
            $taskOrigin = $taskLock.mirror
            $taskCandidate = Join-Path $taskRoot 'source-mirror'
            $null = Invoke-SourceGit ($taskClone + @($taskOrigin, $taskCandidate))
        }
        $taskCommit = $taskLock.commit
        if ((Invoke-SourceGit @('-C', $taskCandidate, 'rev-parse', 'HEAD')) -ne $taskCommit -or
            (Invoke-SourceGit @('-C', $taskCandidate, 'rev-parse', "refs/tags/$($taskLock.tag)^{commit}")) -ne $taskCommit -or
            (Invoke-SourceGit @('-C', $taskCandidate, 'rev-parse', 'HEAD^{tree}')) -ne $taskLock.tree) { throw 'Selected tag, commit or tree does not match the source lock; source retained for inspection.' }
        . (Join-Path $PSScriptRoot 'verify-openvoice-ort-source.ps1')
        $taskDigest = Get-OrtSourceCanonicalDigest $taskGit $taskCandidate
        if ($taskDigest.Sha256 -ne $taskLock.canonicalSha256 -or $taskDigest.Entries -ne $taskLock.entries -or $taskDigest.Gitlinks -ne $taskLock.gitlinks) { throw 'ORT complete source-content digest differs from the trusted source lock; source retained for inspection.' }
        # The upstream attributes request native/CRLF checkout for some files. This
        # fresh developer checkout must contain the verified Git blob bytes exactly.
        # Override transforms only in this invocation's new clone, never user settings.
        $taskAttributes = [IO.File]::Open((Join-Path $taskCandidate '.git/info/attributes'), [IO.FileMode]::CreateNew)
        try { $taskAttributeBytes = [Text.Encoding]::ASCII.GetBytes("* -text -ident -filter`n"); $taskAttributes.Write($taskAttributeBytes) } finally { $taskAttributes.Dispose() }
        $null = Invoke-SourceGit @('-C', $taskCandidate, 'config', '--local', 'core.longpaths', 'true')
        $null = Invoke-SourceGit @('-C', $taskCandidate, '-c', 'core.autocrlf=false', 'checkout', '--detach', $taskCommit)
        if ((Get-OrtSourceCanonicalDigest $taskGit $taskCandidate -CheckWorkingTree).Sha256 -ne $taskLock.canonicalSha256) { throw 'ORT checkout bytes differ from the source-object lock; source retained for inspection.' }
        [IO.Directory]::Move($taskCandidate, $taskSource)
        $taskFailedPrimary = Join-Path $taskRoot 'source-primary'
        if ($taskCandidate -ne $taskFailedPrimary -and (Test-Path -LiteralPath $taskFailedPrimary)) {
            if ((Resolve-Path -LiteralPath $taskFailedPrimary).Path -ne $taskFailedPrimary -or [IO.Path]::GetDirectoryName($taskFailedPrimary) -ne $taskRoot) { throw 'Unexpected failed clone cleanup path.' }
            foreach ($taskEntry in @(Get-Item -LiteralPath $taskFailedPrimary -Force) + @(Get-ChildItem -LiteralPath $taskFailedPrimary -Recurse -Force)) {
                if ($taskEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Failed clone cleanup refused linked entries.' }
            }
            # Every owned Git process has exited before this success-only cleanup.
            Remove-Item -LiteralPath $taskFailedPrimary -Recurse -Force
        }
        Write-Output ('Verified complete ORT source: ' + $taskDigest.Sha256 + '; origin: ' + $taskOrigin)
        Write-Output ('Pinned source prepared: ' + $taskSource)
    } finally {
        if ($null -ne $taskGuard) { $taskGuard.Dispose() }
        $env:TEMP = $taskOriginalTemporary[0]; $env:TMP = $taskOriginalTemporary[1]; $env:TMPDIR = $taskOriginalTemporary[2]
        # Only this invocation's empty temporary directory is disposable. Failed
        # source downloads remain inspectable; choose a new WorkRoot to retry.
        if ((Test-Path -LiteralPath $taskTemporary) -and -not (Get-ChildItem -LiteralPath $taskTemporary -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $taskTemporary }
    }
} catch { Write-Error ('ORT source preparation: ' + $_.Exception.Message) -ErrorAction Continue; exit 1 }
