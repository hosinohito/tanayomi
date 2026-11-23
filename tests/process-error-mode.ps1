[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This process inheritance check requires Windows.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporaryRoot = Join-Path $repository '.local/tmp'
$temporary = Join-Path $temporaryRoot ('error-mode-' + [Guid]::NewGuid().ToString('N'))
$saved = @{}; $guard = $null; $nested = $null; $child = $null; $started = $false
try {
    for ($path = $temporaryRoot; $path; $path = [IO.Path]::GetDirectoryName($path)) {
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked test temporary root.' }
    }
    [void][IO.Directory]::CreateDirectory($temporary)
    foreach ($name in @('TEMP','TMP','TMPDIR')) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $temporary, 'Process')
    }
    $probe = @'
using System.Runtime.InteropServices;
public static class TanayomiErrorModeProbe {
    [DllImport("kernel32.dll", ExactSpelling=true)] public static extern uint GetErrorMode();
}
'@
    if (-not ('TanayomiErrorModeProbe' -as [type])) { Add-Type -TypeDefinition $probe }
    $original = [TanayomiErrorModeProbe]::GetErrorMode()
    . (Join-Path $repository 'native/process-error-mode.ps1')
    $guard = Enter-NativeProcessErrorMode
    if (([TanayomiErrorModeProbe]::GetErrorMode() -band 0x8003) -ne 0x8003) { throw 'Parent mode not enabled.' }
    $nested = Enter-NativeProcessErrorMode
    $guard.Dispose(); $guard = $null
    if (([TanayomiErrorModeProbe]::GetErrorMode() -band 0x8003) -ne 0x8003) { throw 'Nested guard released parent mode early.' }
    # No fault is induced. A hidden child reads the inherited OS state and
    # returns a deliberate nonzero exit code without registry/toolchain calls.
    $probeEncoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($probe))
    $body = "try { Add-Type -TypeDefinition ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$probeEncoded'))); [Console]::WriteLine([TanayomiErrorModeProbe]::GetErrorMode()); exit 7 } catch { [Console]::Error.WriteLine('probe failed'); exit 9 }"
    $start = [Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.ErrorDialog = $false
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.WorkingDirectory = $temporary
    foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body)))) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::new(); $child.StartInfo = $start; $started = $child.Start()
    if (-not $started) { throw 'Child did not start.' }
    $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(15000)) { throw 'Child exceeded its deadline.' }
    if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr),5000)) { throw 'Child pipes did not close.' }
    if ($child.ExitCode -ne 7 -or $stderr.Result.Length -ne 0 -or $stdout.Result.Length -gt 32 -or
        $stdout.Result.Trim() -notmatch '^\d+$' -or (([uint32]$stdout.Result.Trim()) -band 0x8003) -ne 0x8003) { throw 'Child did not inherit the error mode or return its expected failure code.' }
    $nested.Dispose(); $nested.Dispose(); $nested = $null
    if ([TanayomiErrorModeProbe]::GetErrorMode() -ne $original) { throw 'Parent mode was not restored.' }
    Write-Output 'PASS: inherited error mode, nested lifetime, nonzero child exit, original mode restored; no native fault induced.'
}
finally {
    if ($started -and -not $child.HasExited) { $child.Kill($true); if (-not $child.WaitForExit(5000)) { throw 'Owned child exit unconfirmed; temporary root retained.' } }
    if ($null -ne $child) { $child.Dispose() }
    if ($null -ne $nested) { $nested.Dispose() }
    if ($null -ne $guard) { $guard.Dispose() }
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name,$saved[$name],'Process') }
    $resolved = [IO.Path]::GetFullPath($temporary)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($temporaryRoot) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^error-mode-[0-9a-f]{32}$') { throw 'Test cleanup boundary mismatch.' }
    if (Test-Path -LiteralPath $resolved) {
        $entries = @((Get-Item -LiteralPath $resolved -Force)) + @(Get-ChildItem -LiteralPath $resolved -Recurse -Force)
        if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Test cleanup encountered a link; temporary root retained.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
