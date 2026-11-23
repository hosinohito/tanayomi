#Requires -Version 7.0
[CmdletBinding()]
param([ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*$')][string]$Username = 'hosinohito')
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This registration uses Windows Credential Manager.' }
$git = (Get-Command git.exe -CommandType Application -ErrorAction Stop).Source
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.local/tmp'))
for ($current = $root; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The temporary directory must not contain links.'
    }
}
$temporary = Join-Path $root ('github-auth-' + [Guid]::NewGuid().ToString('N'))
$oldTemp = $env:TEMP; $oldTmp = $env:TMP
$guard = $null; $child = $null; $secret = $null; $password = $null; $started = $false
try {
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $env:TEMP = $temporary; $env:TMP = $temporary
    . (Join-Path $PSScriptRoot 'native/process-error-mode.ps1')
    $guard = Enter-NativeProcessErrorMode
    $secret = Read-Host 'GitHub token (hidden)' -AsSecureString
    $password = [Net.NetworkCredential]::new('', $secret).Password
    if ([string]::IsNullOrWhiteSpace($password) -or $password.Contains("`r") -or $password.Contains("`n")) {
        throw 'Enter one nonempty token.'
    }
    $start = [Diagnostics.ProcessStartInfo]::new($git)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.WorkingDirectory = $PSScriptRoot
    $start.ArgumentList.Add('credential-manager'); $start.ArgumentList.Add('store')
    $start.Environment['GCM_CREDENTIAL_STORE'] = 'wincredman'
    $start.Environment['GCM_INTERACTIVE'] = 'Never'
    $start.Environment['GCM_TRACE'] = '0'
    $start.Environment['GCM_TRACE_SECRETS'] = '0'
    $child = [Diagnostics.Process]::new(); $child.StartInfo = $start
    if (-not $child.Start()) { throw 'Credential Manager did not start.' }
    $started = $true
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    $child.StandardInput.Write("protocol=https`nhost=github.com`nusername=$Username`npassword=$password`n`n")
    $child.StandardInput.Close(); $password = $null
    if (-not $child.WaitForExit(15000)) { throw 'Credential Manager timed out.' }
    $null = $stdout.GetAwaiter().GetResult(); $null = $stderr.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0) { throw "Credential Manager failed (exit $($child.ExitCode)). Token was not displayed." }
    Write-Host 'Saved in Windows Credential Manager. The token was not written to a project file.'
} finally {
    $password = $null
    if ($secret) { $secret.Dispose() }
    if ($child) {
        if ($started -and -not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }
        $child.Dispose()
    }
    if ($guard) { $guard.Dispose() }
    $env:TEMP = $oldTemp; $env:TMP = $oldTmp
    if (Test-Path -LiteralPath $temporary) {
        if (-not [IO.Path]::GetFullPath($temporary).StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid cleanup path.' }
        if (Get-ChildItem -LiteralPath $temporary -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked temporary output retained.' }
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
