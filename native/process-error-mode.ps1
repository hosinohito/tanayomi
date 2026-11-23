# Dot-source after TEMP/TMP point at the repository-owned .local directory,
# before starting native tools. No registry or persistent OS setting changes.
function Enter-NativeProcessErrorMode {
    [CmdletBinding()]
    param()
    if (-not $IsWindows) { return $null }
    $taskHelperSource = $MyInvocation.MyCommand.ScriptBlock.File
    if ([string]::IsNullOrWhiteSpace($taskHelperSource)) { throw 'Cannot verify native process helper source directory.' }
    $taskLocalRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($taskHelperSource)) '../.local'))
    foreach ($taskTemporaryName in @('TEMP', 'TMP')) {
        $taskTemporaryPath = [Environment]::GetEnvironmentVariable($taskTemporaryName, 'Process')
        if ([string]::IsNullOrWhiteSpace($taskTemporaryPath) -or -not [IO.Path]::IsPathFullyQualified($taskTemporaryPath) -or
            -not [IO.Path]::GetFullPath($taskTemporaryPath).StartsWith($taskLocalRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Directory]::Exists($taskTemporaryPath)) {
            throw 'Set an existing repository-owned .local TEMP/TMP before entering native process error mode.'
        }
        for ($taskCurrent = [IO.Path]::GetFullPath($taskTemporaryPath); $taskCurrent; $taskCurrent = [IO.Path]::GetDirectoryName($taskCurrent)) {
            if ([IO.Directory]::Exists($taskCurrent) -and ([IO.File]::GetAttributes($taskCurrent) -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Native process temporary path must not traverse links.'
            }
        }
    }
    if (-not ('Tanayomi.NativeTools.ProcessErrorModeGuard' -as [type])) {
        Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tanayomi.NativeTools
{
    public sealed class ProcessErrorModeGuard : IDisposable
    {
        private const uint Required = 0x0001u | 0x0002u | 0x8000u;
        private static readonly object Gate = new object();
        private static int active;
        private static uint original;
        private int disposed;

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern uint GetErrorMode();
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern uint SetErrorMode(uint mode);

        public static ProcessErrorModeGuard Enter()
        {
            lock (Gate)
            {
                if (active == 0)
                {
                    original = GetErrorMode();
                    try
                    {
                        SetErrorMode(original | Required);
                        if ((GetErrorMode() & Required) != Required)
                            throw new InvalidOperationException("Could not set inherited native process error mode.");
                    }
                    catch
                    {
                        SetErrorMode(original);
                        throw;
                    }
                }
                else if ((GetErrorMode() & Required) != Required)
                {
                    throw new InvalidOperationException("Native process error mode changed while a guard was active.");
                }
                checked { ++active; }
                return new ProcessErrorModeGuard();
            }
        }
        private ProcessErrorModeGuard() { }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (Gate)
            {
                if (--active == 0)
                {
                    SetErrorMode(original);
                    if (GetErrorMode() != original)
                        throw new InvalidOperationException("Could not restore native process error mode.");
                }
            }
        }
    }
}
'@
    }
    return [Tanayomi.NativeTools.ProcessErrorModeGuard]::Enter()
}
