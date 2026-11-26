[CmdletBinding()]
param(
    [string]$WorkRoot = '',
    [string]$GitExecutable = '',
    [string]$CmakeExecutable = '',
    [string]$NinjaExecutable = '',
    [string]$VisualStudioDirectory = '',
    [string]$WindowsSdkDirectory = '',
    [string]$PythonExecutable = '',
    [switch]$FusedConv,
    [switch]$Col2im1D,
    [switch]$RowSplit,
    [ValidateRange(1, 2)][int]$Parallel = 2,
    [ValidateRange(1, 600)][int]$BuildDeadlineSeconds = 600,
    [switch]$PrepareOnly,
    [switch]$ConfigureOnly
)
# Development-only CPU build. Requires a separately prepared, pinned source
# checkout and tool environment; never copies artifacts into a running product.
$ErrorActionPreference = 'Stop'
if ($PrepareOnly -and $ConfigureOnly) { throw 'PrepareOnly and ConfigureOnly cannot be combined.' }
if ([string]::IsNullOrWhiteSpace($WorkRoot)) { throw 'Specify an explicit WorkRoot directly under repository .local/build.' }
if (-not $IsWindows) { throw 'The audited native configuration targets Windows x64.' }
$taskIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if ([Security.Principal.WindowsPrincipal]::new($taskIdentity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run the development build without OS administrator elevation.'
    }
} finally { $taskIdentity.Dispose() }
$taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskLocal = Join-Path $taskRepository '.local'
$taskRoot = [IO.Path]::GetFullPath($WorkRoot).TrimEnd([char[]]'\/')
$taskBuildRoot = [IO.Path]::GetFullPath((Join-Path $taskLocal 'build')).TrimEnd([char[]]'\/')
if (-not [string]::Equals([IO.Path]::GetDirectoryName($taskRoot), $taskBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WorkRoot must be an explicit direct child of repository .local/build.'
}
$taskFusedCombinedHash = '8EE7B67F1DC0AE2318075A6528CC090433FA3A0BBD843F89943B7D329E1C5C34'
$taskCol2imCombinedHash = 'E8D0642CAE49EF71C28A9475266AC26B2140B3C0C10B21FB45CD4F820800DC5E'
$taskRowSplitCombinedHash = 'F612751739B6F77EF973A8DEC2178616D4804FE75ACEDC81FAECE4D42769A4E6'
if ($RowSplit -and -not $Col2im1D) { throw 'RowSplit requires Col2im1D.' }
if ($Col2im1D -and -not $FusedConv) { throw 'Col2im1D requires FusedConv.' }
if ($Col2im1D -and $taskCol2imCombinedHash -notmatch '\A[0-9A-F]{64}\z') { throw 'Col2im1D combined diff is not pinned.' }
if ($FusedConv) {
    if ([string]::IsNullOrWhiteSpace($PythonExecutable)) { throw 'FusedConv requires an explicit existing PythonExecutable.' }
    if ($taskFusedCombinedHash -notmatch '\A[0-9A-F]{64}\z') { throw 'FusedConv combined source diff hash has not been independently pinned.' }
}
$taskSource = Join-Path $taskRoot 'ort-source'
$taskOutput = Join-Path $taskRoot 'ort-build'
$taskTemporary = Join-Path $taskRoot 'tmp'
foreach ($taskPath in @($taskRoot, $taskSource, $taskOutput, $taskTemporary)) {
    if ([string]::IsNullOrWhiteSpace($taskPath) -or -not [IO.Path]::GetFullPath($taskPath).StartsWith($taskRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build boundary invalid.' }
    for ($taskCurrent = $taskPath; $taskCurrent; $taskCurrent = [IO.Path]::GetDirectoryName($taskCurrent)) {
        if ((Test-Path -LiteralPath $taskCurrent) -and ((Get-Item -LiteralPath $taskCurrent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Build paths cannot traverse links.' }
    }
}
New-Item -ItemType Directory -Path $taskOutput, $taskTemporary -Force | Out-Null
$env:TEMP = $taskTemporary; $env:TMP = $taskTemporary; $env:TMPDIR = $taskTemporary
$env:PYTHONDONTWRITEBYTECODE = '1'
. (Join-Path $PSScriptRoot 'process-error-mode.ps1')
$taskErrorModeGuard = Enter-NativeProcessErrorMode
try {
$taskGit = if ([string]::IsNullOrWhiteSpace($GitExecutable)) { (Get-Command git.exe -ErrorAction Stop).Source } else { [IO.Path]::GetFullPath($GitExecutable) }
$taskPatchFile = Join-Path $PSScriptRoot 'openvoice-ort-cpu.patch'
$taskPatchHash = '35153DEECEA8BA604A9BCF4EAD8E16FAAC36E88B2A33BB94ECDCC13F1DB60381'
if ((Get-FileHash -LiteralPath $taskPatchFile -Algorithm SHA256).Hash -ne $taskPatchHash) { throw 'Pinned CPU patch hash mismatch.' }
$taskDeltaHash = $null
$taskRowSplitPatchFile = Join-Path $PSScriptRoot 'openvoice-row-split.patch'
$taskRowSplitPatchHash = '6352EF7F40BBD37A76ACB5E5887FB1E12B004229F0AA3A58531912C450E0AE12'
if ($RowSplit -and (Get-FileHash -LiteralPath $taskRowSplitPatchFile -Algorithm SHA256).Hash -ne $taskRowSplitPatchHash) { throw 'Pinned RowSplit patch hash mismatch.' }
$taskCol2imPatchFile = Join-Path $PSScriptRoot 'openvoice-col2im1d.patch'
$taskCol2imPatchHash = 'B4AE50EBCEE62D02F64F40ABA3C9CA8FAB316B94782543DABD652D66EBBE822E'
if ($Col2im1D -and (Get-FileHash -LiteralPath $taskCol2imPatchFile -Algorithm SHA256).Hash -ne $taskCol2imPatchHash) { throw 'Pinned Col2im1D patch hash mismatch.' }
if ($FusedConv) {
    $taskDeltaFile = Join-Path $PSScriptRoot 'openvoice-fused-conv.patch'
    $taskDeltaHash = '8C4D50B30EE44DEFD5AD6461BB06C56273FF01C09F1CDF993145C8ED40B071A9'
    if ((Get-FileHash -LiteralPath $taskDeltaFile -Algorithm SHA256).Hash -ne $taskDeltaHash) { throw 'Pinned FusedConv delta hash mismatch.' }
}
function Invoke-OwnedGit([string[]]$Arguments) {
    $taskGitInfo = [Diagnostics.ProcessStartInfo]::new($taskGit)
    $taskGitInfo.UseShellExecute = $false; $taskGitInfo.CreateNoWindow = $true
    $taskGitInfo.RedirectStandardOutput = $true; $taskGitInfo.RedirectStandardError = $true
    $taskGitInfo.WorkingDirectory = $taskRoot
    if ($FusedConv) { $taskGitInfo.Environment['GIT_NO_LAZY_FETCH'] = '1' }
    foreach ($taskArgument in @('--no-pager', '-C', $taskSource) + $Arguments) { $taskGitInfo.ArgumentList.Add($taskArgument) }
    $taskGitProcess = [Diagnostics.Process]::Start($taskGitInfo)
    try {
        $taskGitText = $taskGitProcess.StandardOutput.ReadToEndAsync(); $taskGitError = $taskGitProcess.StandardError.ReadToEndAsync()
        if (-not $taskGitProcess.WaitForExit(30000)) {
            $taskGitProcess.Kill($true)
            if (-not $taskGitProcess.WaitForExit(5000)) { throw 'Owned Git process could not be reaped.' }
            throw 'Owned Git operation exceeded its deadline.'
        }
        return [pscustomobject]@{ExitCode=$taskGitProcess.ExitCode;Output=$taskGitText.GetAwaiter().GetResult();Error=$taskGitError.GetAwaiter().GetResult()}
    } finally {
        if (-not $taskGitProcess.HasExited) { $taskGitProcess.Kill($true); $taskGitProcess.WaitForExit(5000) | Out-Null }
        $taskGitProcess.Dispose()
    }
}
$taskRevision = Invoke-OwnedGit @('rev-parse', 'HEAD')
$taskActual = $taskRevision.Output.Trim()
if ($taskRevision.ExitCode -ne 0 -or $taskActual -ne 'a83fc4d58cb48eb68890dd689f94f28288cf2278') { throw 'ORT source commit mismatch.' }
$taskPython = if ([string]::IsNullOrWhiteSpace($PythonExecutable)) { Join-Path $taskRoot 'venv\Scripts\python.exe' } else { [IO.Path]::GetFullPath($PythonExecutable) }
if (-not [string]::IsNullOrWhiteSpace($PythonExecutable) -and -not (Test-Path -LiteralPath $taskPython -PathType Leaf)) { throw 'Explicit existing Python executable missing.' }
$taskCmake = if ([string]::IsNullOrWhiteSpace($CmakeExecutable)) { Join-Path $taskRoot 'venv\Lib\site-packages\cmake\data\bin\cmake.exe' } else { [IO.Path]::GetFullPath($CmakeExecutable) }
$taskVisualStudio = if ([string]::IsNullOrWhiteSpace($VisualStudioDirectory)) { Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019\BuildTools' } else { [IO.Path]::GetFullPath($VisualStudioDirectory) }
$taskVc = Join-Path $taskVisualStudio 'VC\Tools\MSVC\14.29.30037'
$taskSdk = if ([string]::IsNullOrWhiteSpace($WindowsSdkDirectory)) { Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10' } else { [IO.Path]::GetFullPath($WindowsSdkDirectory) }
$taskSdkVersion = '10.0.19041.0'
$taskCompilerBin = Join-Path $taskVc 'bin\Hostx64\x64'
$taskSdkBin = Join-Path $taskSdk ('bin\' + $taskSdkVersion + '\x64')
$taskCompiler = Join-Path $taskCompilerBin 'cl.exe'
$taskNinja = if ([string]::IsNullOrWhiteSpace($NinjaExecutable)) { Join-Path $taskVisualStudio 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe' } else { [IO.Path]::GetFullPath($NinjaExecutable) }
foreach ($taskTool in @($taskCmake, $taskNinja, $taskCompiler, (Join-Path $taskCompilerBin 'link.exe'), (Join-Path $taskCompilerBin 'ml64.exe'), (Join-Path $taskSdkBin 'rc.exe'), (Join-Path $taskSdkBin 'mt.exe'))) {
    if (-not (Test-Path -LiteralPath $taskTool -PathType Leaf)) { throw 'Existing pinned build tool missing.' }
}
# Fixed native-only environment. Do not invoke VsDevCmd/vcvars, vswhere, cmd
# discovery or registry utilities. Only these explicit include/library paths
# participate in the audited MSVC/SDK configuration.
$taskIncludes = @((Join-Path $taskVc 'include')) + @('ucrt','shared','um','winrt','cppwinrt' | ForEach-Object { Join-Path $taskSdk ('Include\' + $taskSdkVersion + '\' + $_) })
$taskLibraries = @((Join-Path $taskVc 'lib\x64'), (Join-Path $taskSdk ('Lib\' + $taskSdkVersion + '\ucrt\x64')), (Join-Path $taskSdk ('Lib\' + $taskSdkVersion + '\um\x64')))
foreach ($taskDirectory in $taskIncludes + $taskLibraries) { if (-not (Test-Path -LiteralPath $taskDirectory -PathType Container)) { throw 'Pinned include/library directory missing.' } }
if ([string]::IsNullOrWhiteSpace($env:SystemRoot)) { throw 'Windows SystemRoot missing.' }
$taskEnvironment = @{
    PATH=(@($taskCompilerBin, $taskSdkBin, (Split-Path -Parent $taskGit), (Join-Path $env:SystemRoot 'System32'), $env:SystemRoot) -join ';')
    INCLUDE=($taskIncludes -join ';'); LIB=($taskLibraries -join ';'); LIBPATH=(Join-Path $taskVc 'lib\x64')
    VSINSTALLDIR=($taskVisualStudio + '\'); VCINSTALLDIR=(Join-Path $taskVisualStudio 'VC\')
    WindowsSdkDir=($taskSdk + '\'); WindowsSDKVersion=($taskSdkVersion + '\'); VCToolsInstallDir=($taskVc + '\')
}
if ($FusedConv) { $taskEnvironment['GIT_NO_LAZY_FETCH'] = '1' }
$taskCompilerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskCompiler).FileVersion
$taskCmakeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskCmake).FileVersion
if ($taskCompilerVersion -notlike '19.29.30038.1*' -or $taskCmakeVersion -ne '3.31.6' -or $taskSdkVersion -ne '10.0.19041.0') {
    throw 'This pinned audit expects MSVC 19.29.30038.1, CMake 3.31.6 and SDK 10.0.19041.0. Other versions need a new artifact audit.'
}

function Invoke-OwnedBuild([string[]]$Arguments, [int]$Seconds, [string]$Name) {
    $taskStart = [Diagnostics.ProcessStartInfo]::new($taskCmake)
    $taskStart.UseShellExecute = $false; $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardOutput = $true; $taskStart.RedirectStandardError = $true
    $taskStart.WorkingDirectory = $taskRoot
    foreach ($taskArgument in $Arguments) { $taskStart.ArgumentList.Add($taskArgument) }
    foreach ($taskName in @('CL','_CL_','LINK','_LINK_','CPATH','CPLUS_INCLUDE_PATH','C_INCLUDE_PATH','LIBRARY_PATH')) { $null = $taskStart.Environment.Remove($taskName) }
    if ($FusedConv) {
        $null = $taskStart.Environment.Remove('PYTHONHOME')
        $null = $taskStart.Environment.Remove('PYTHONPATH')
        $taskStart.Environment['PYTHONNOUSERSITE'] = '1'
    }
    foreach ($taskPair in $taskEnvironment.GetEnumerator()) { $taskStart.Environment[$taskPair.Key] = $taskPair.Value }
    $taskProcess = [Diagnostics.Process]::Start($taskStart)
    try {
        $taskStdout = $taskProcess.StandardOutput.ReadToEndAsync()
        $taskStderr = $taskProcess.StandardError.ReadToEndAsync()
        if (-not $taskProcess.WaitForExit($Seconds * 1000)) {
            $taskProcess.Kill($true)
            if (-not $taskProcess.WaitForExit(5000)) { throw 'Owned build process could not be reaped; retain output.' }
            [IO.File]::WriteAllText((Join-Path $taskOutput ($Name + '.stdout.log')), $taskStdout.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $taskOutput ($Name + '.stderr.log')), $taskStderr.GetAwaiter().GetResult())
            throw ($Name + ' exceeded its bounded deadline.')
        }
        $taskOut = $taskStdout.GetAwaiter().GetResult(); $taskError = $taskStderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $taskOutput ($Name + '.stdout.log')), $taskOut)
        [IO.File]::WriteAllText((Join-Path $taskOutput ($Name + '.stderr.log')), $taskError)
        [pscustomobject]@{Phase=$Name;ExitCode=$taskProcess.ExitCode;StdoutLines=($taskOut -split "`n").Length;StderrLines=($taskError -split "`n").Length} | ConvertTo-Json -Compress
        if ($taskProcess.ExitCode -ne 0) {
            ($taskError -split "`n" | Select-Object -Last 14) -join "`n" | Write-Output
            ($taskOut -split "`n" | Select-Object -Last 14) -join "`n" | Write-Output
            throw ($Name + ' failed; logs retained under owned output.')
        }
    } finally {
        if (-not $taskProcess.HasExited) { $taskProcess.Kill($true); $taskProcess.WaitForExit(5000) | Out-Null }
        $taskProcess.Dispose()
    }
}

$taskFlags = @('-S', (Join-Path $taskSource 'cmake'), '-B', $taskOutput, '-G', 'Ninja',
    ('-DCMAKE_MAKE_PROGRAM=' + $taskNinja), '-DCMAKE_BUILD_TYPE=Release', '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL',
    ('-DCMAKE_C_COMPILER=' + $taskCompiler), ('-DCMAKE_CXX_COMPILER=' + $taskCompiler),
    ('-DCMAKE_ASM_MASM_COMPILER=' + (Join-Path $taskCompilerBin 'ml64.exe')), ('-DCMAKE_RC_COMPILER=' + (Join-Path $taskSdkBin 'rc.exe')),
    ('-DFETCHCONTENT_BASE_DIR=' + (Join-Path $taskOutput 'deps')), '-DFETCHCONTENT_QUIET=OFF',
    ('-DPython_EXECUTABLE=' + $taskPython),
    ('-DPython3_EXECUTABLE=' + $taskPython),
    ('-DCMAKE_SHARED_LINKER_FLAGS=/MAP:' + (Join-Path $taskOutput 'onnxruntime.map')),
    '-Donnxruntime_BUILD_SHARED_LIB=ON','-Donnxruntime_BUILD_UNIT_TESTS=OFF','-Donnxruntime_BUILD_BENCHMARKS=OFF',
    '-Donnxruntime_ENABLE_PYTHON=OFF','-Donnxruntime_ENABLE_TRAINING=OFF','-Donnxruntime_ENABLE_TRAINING_OPS=OFF',
    '-Donnxruntime_ENABLE_TRAINING_APIS=OFF','-Donnxruntime_USE_TELEMETRY=OFF','-Donnxruntime_DEV_MODE=OFF',
    '-Donnxruntime_ENABLE_LTO=OFF','-Donnxruntime_DISABLE_CONTRIB_OPS=ON','-Donnxruntime_DISABLE_ML_OPS=ON',
    '-Donnxruntime_USE_CUDA=OFF','-Donnxruntime_USE_ROCM=OFF','-Donnxruntime_USE_DNNL=OFF','-Donnxruntime_USE_OPENVINO=OFF',
    '-Donnxruntime_USE_NNAPI_BUILTIN=OFF','-Donnxruntime_USE_COREML=OFF','-Donnxruntime_USE_DML=OFF','-Donnxruntime_USE_QNN=OFF',
    '-Donnxruntime_USE_TENSORRT=OFF','-Donnxruntime_USE_NCCL=OFF','-Donnxruntime_USE_MPI=OFF','-Donnxruntime_USE_AZURE=OFF',
    '-Donnxruntime_USE_XNNPACK=OFF','-Donnxruntime_USE_WEBGPU=OFF','-Donnxruntime_USE_WEBNN=OFF','-Donnxruntime_USE_CANN=OFF',
    '-Donnxruntime_USE_RKNPU=OFF','-Donnxruntime_USE_VITISAI=OFF','-Donnxruntime_USE_MIMALLOC=OFF',
    '-Donnxruntime_USE_WINML=OFF','-Donnxruntime_MSVC_STATIC_RUNTIME=OFF','-Dprotobuf_MSVC_STATIC_RUNTIME=OFF',
    '-Dprotobuf_BUILD_TESTS=OFF')
# Fresh CMake compiler-description files interpolate these values in all modes.
# Native Windows backslashes would become CMake escapes (for example \P).
$taskFlags = @($taskFlags | ForEach-Object { $_.Replace('\', '/') })
if ($FusedConv) {
    $taskFlags += @('-Donnxruntime_TANAYOMI_FUSED_CONV=ON', '-DFETCHCONTENT_FULLY_DISCONNECTED=ON')
    # The isolated source must already contain exactly the fixed base patch,
    # or exactly the independently pinned combined candidate. Intent-to-add
    # keeps the new schema visible in the complete working-tree diff.
    $taskStaged = Invoke-OwnedGit @('diff', '--cached', '--ita-invisible-in-index', '--no-ext-diff', '--quiet')
    $taskUntracked = Invoke-OwnedGit @('ls-files', '--others', '--exclude-standard')
    $taskIgnored = Invoke-OwnedGit @('ls-files', '--others', '--ignored', '--exclude-standard')
    if ($taskStaged.ExitCode -ne 0 -or $taskUntracked.ExitCode -ne 0 -or $taskIgnored.ExitCode -ne 0 -or
        -not [string]::IsNullOrWhiteSpace($taskUntracked.Output) -or -not [string]::IsNullOrWhiteSpace($taskIgnored.Output)) {
        throw 'FusedConv source contains staged or untracked changes, including ignored inputs, outside its pinned working diff.'
    }
    $taskInitialDiff = Invoke-OwnedGit @('diff', '--ita-invisible-in-index', '--no-ext-diff', '--')
    if ($taskInitialDiff.ExitCode -ne 0) { throw 'Could not inspect isolated FusedConv source.' }
    $taskInitialPatch = $taskInitialDiff.Output.Replace("`r`n", "`n").TrimEnd([char[]]"`r`n") + "`n"
    $taskInitialHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($taskInitialPatch)))
    if ($taskInitialHash -eq $taskPatchHash) {
        $taskGitApply = Invoke-OwnedGit @('apply', '--check', $taskDeltaFile)
        if ($taskGitApply.ExitCode -ne 0) { throw 'FusedConv delta preflight failed.' }
        $taskGitApply = Invoke-OwnedGit @('apply', $taskDeltaFile)
        if ($taskGitApply.ExitCode -ne 0) { throw 'FusedConv delta application failed; retain isolated source for inspection.' }
        $taskGitIntent = Invoke-OwnedGit @('add', '--intent-to-add', '--', 'onnxruntime/core/graph/contrib_ops/tanayomi_fused_conv_schema.cc')
        if ($taskGitIntent.ExitCode -ne 0) { throw 'FusedConv schema intent-to-add failed; retain isolated source for inspection.' }
    } elseif ($taskInitialHash -ne $taskFusedCombinedHash -and -not ($Col2im1D -and $taskInitialHash -eq $taskCol2imCombinedHash) -and -not ($RowSplit -and $taskInitialHash -eq $taskRowSplitCombinedHash)) { throw 'Isolated FusedConv source is neither the exact base nor the complete candidate.' }
    if ($Col2im1D -and $taskInitialHash -ne $taskCol2imCombinedHash -and $taskInitialHash -ne $taskRowSplitCombinedHash) {
        $taskGitApply = Invoke-OwnedGit @('apply', '--check', $taskCol2imPatchFile)
        if ($taskGitApply.ExitCode -ne 0) { throw 'Col2im1D preflight failed.' }
        $taskGitApply = Invoke-OwnedGit @('apply', $taskCol2imPatchFile)
        if ($taskGitApply.ExitCode -ne 0) { throw 'Col2im1D application failed.' }
    }
    if ($RowSplit -and $taskInitialHash -ne $taskRowSplitCombinedHash) {
        $taskGitApply = Invoke-OwnedGit @('apply', '--check', $taskRowSplitPatchFile)
        if ($taskGitApply.ExitCode -ne 0) { throw 'RowSplit preflight failed.' }
        $taskGitApply = Invoke-OwnedGit @('apply', $taskRowSplitPatchFile)
        if ($taskGitApply.ExitCode -ne 0) { throw 'RowSplit application failed.' }
    }
} else {
    # Apply only to an unchanged fixed checkout, or accept precisely this patch.
    $taskGitStatus = Invoke-OwnedGit @('diff', '--no-ext-diff', '--quiet')
    if ($taskGitStatus.ExitCode -eq 0) {
        $taskGitApply = Invoke-OwnedGit @('apply', '--check', $taskPatchFile)
        if ($taskGitApply.ExitCode -ne 0) { throw 'CPU patch preflight failed.' }
        $taskGitApply = Invoke-OwnedGit @('apply', $taskPatchFile)
        if ($taskGitApply.ExitCode -ne 0) { throw 'CPU patch application failed.' }
    } elseif ($taskGitStatus.ExitCode -ne 1) { throw 'Could not inspect source changes.' }
}
$taskGitDiff = if ($FusedConv) {
    Invoke-OwnedGit @('diff', '--ita-invisible-in-index', '--no-ext-diff', '--')
} else {
    Invoke-OwnedGit @('diff', '--no-ext-diff', '--')
}
if ($taskGitDiff.ExitCode -ne 0) { throw 'Could not inspect patched source.' }
$taskPatch = $taskGitDiff.Output.Replace("`r`n", "`n").TrimEnd([char[]]"`r`n") + "`n"
$taskSavedPatch = Join-Path $taskOutput $(if ($RowSplit) { 'cpu-fused-col2im1d-rowsplit.patch' } elseif ($Col2im1D) { 'cpu-fused-col2im1d.patch' } elseif ($FusedConv) { 'cpu-fused-conv.patch' } else { 'cpu-only-device-discovery.patch' })
[IO.File]::WriteAllText($taskSavedPatch, $taskPatch, [Text.UTF8Encoding]::new($false))
$taskExpectedPatchHash = if ($RowSplit) { $taskRowSplitCombinedHash } elseif ($Col2im1D) { $taskCol2imCombinedHash } elseif ($FusedConv) { $taskFusedCombinedHash } else { $taskPatchHash }
if ((Get-FileHash -LiteralPath $taskSavedPatch -Algorithm SHA256).Hash -ne $taskExpectedPatchHash) { throw 'Checkout has changes beyond the pinned source patch.' }
$taskRequestedBuild = @{SourceCommit=$taskActual;SourcePatchSha256=$taskPatchHash;Flags=$taskFlags;Compiler=$taskCompilerVersion;Cmake=$taskCmakeVersion;Sdk=$taskSdkVersion;Parallel=$Parallel;BuildDeadlineSeconds=$BuildDeadlineSeconds}
if ($FusedConv) {
    $taskRequestedBuild.Feature = 'FusedConvFloatCpuOnly'
    $taskRequestedBuild.DeltaPatchSha256 = $taskDeltaHash
    $taskRequestedBuild.CombinedSourcePatchSha256 = $taskFusedCombinedHash
    $taskRequestedBuild.PythonExecutable = $taskPython
}
if ($Col2im1D) {
    $taskRequestedBuild.Feature = 'FusedConvFloatCpuAndCol2im1D'
    $taskRequestedBuild.Col2imPatchSha256 = $taskCol2imPatchHash
    $taskRequestedBuild.CombinedSourcePatchSha256 = $taskCol2imCombinedHash
}
if ($RowSplit) {
    $taskRequestedBuild.Feature = 'FusedConvFloatCpuCol2im1DRowSplit'
    $taskRequestedBuild.RowSplitPatchSha256 = $taskRowSplitPatchHash
    $taskRequestedBuild.CombinedSourcePatchSha256 = $taskRowSplitCombinedHash
}
[pscustomobject]$taskRequestedBuild |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $taskOutput 'requested-build.json') -Encoding utf8
if ($PrepareOnly) { Write-Output 'Pinned source, patch, tools and build settings prepared; no configure or compilation performed.'; return }
Invoke-OwnedBuild $taskFlags 300 'configure'
if ($ConfigureOnly) { Write-Output 'Pinned CPU configuration and dependencies prepared; no compilation performed.'; return }
Invoke-OwnedBuild @('--build', $taskOutput, '--config', 'Release', '--target', 'onnxruntime', '--parallel', [string]$Parallel) $BuildDeadlineSeconds 'build'
Get-ChildItem -LiteralPath $taskOutput -Filter 'onnxruntime.dll' -Recurse -File | ForEach-Object {
    [pscustomobject]@{Path=$_.FullName;Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} | ConvertTo-Json -Compress
}
} finally {
    if ($null -ne $taskErrorModeGuard) { $taskErrorModeGuard.Dispose() }
}
