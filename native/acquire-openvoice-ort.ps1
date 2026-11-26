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
    $taskOrigin = 'https://github.com/microsoft/onnxruntime.git'
    if (-not [string]::IsNullOrWhiteSpace($SourceRepository)) {
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
        $taskClone = @('-c', 'core.autocrlf=false', 'clone', '--branch', 'v1.23.2', '--single-branch', '--no-checkout')
        $taskClone += if ($SourceRepository) { @('--no-hardlinks') } else { @('--depth', '1') }
        $null = Invoke-SourceGit ($taskClone + @($taskOrigin, $taskSource))
        $taskCommit = 'a83fc4d58cb48eb68890dd689f94f28288cf2278'
        if ((Invoke-SourceGit @('-C', $taskSource, 'rev-parse', 'HEAD')) -ne $taskCommit -or (Invoke-SourceGit @('-C', $taskSource, 'rev-parse', 'refs/tags/v1.23.2^{commit}')) -ne $taskCommit) { throw 'Selected tag does not match the pinned commit; source retained for inspection.' }
        $null = Invoke-SourceGit @('-C', $taskSource, '-c', 'core.autocrlf=false', 'checkout', '--detach', $taskCommit)
        Write-Output ('Pinned source prepared: ' + $taskSource)
    } finally {
        if ($null -ne $taskGuard) { $taskGuard.Dispose() }
        $env:TEMP = $taskOriginalTemporary[0]; $env:TMP = $taskOriginalTemporary[1]; $env:TMPDIR = $taskOriginalTemporary[2]
        # Only this invocation's empty temporary directory is disposable. Failed
        # source downloads remain inspectable; choose a new WorkRoot to retry.
        if ((Test-Path -LiteralPath $taskTemporary) -and -not (Get-ChildItem -LiteralPath $taskTemporary -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $taskTemporary }
    }
} catch { Write-Error ('ORT source preparation: ' + $_.Exception.Message) -ErrorAction Continue; exit 1 }
