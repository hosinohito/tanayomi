#Requires -Version 7.4
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repository 'native/acquire-speech-native-inputs.ps1')
. (Join-Path $repository 'native/verify-speech-native-runtime.ps1')
$temporaryRoot=Join-Path $repository '.local/tmp'
$fixture=Assert-SpeechNativeInputPath (Join-Path $temporaryRoot ('speech-policy-test-'+[guid]::NewGuid().ToString('N'))) $temporaryRoot
$policyPath=Join-Path $repository 'native/speech-native-runtime-policy.json'
$checks=0
function Reject([scriptblock]$Action) {
    $rejected=$false
    try {& $Action | Out-Null} catch {$rejected=$true}
    if (-not $rejected) {throw 'Changed runtime evidence was accepted.'}
    $script:checks++
}
try {
    [void][IO.Directory]::CreateDirectory($fixture)
    foreach ($component in @('World','OpenJtalk')) {
        $fixturePolicy=Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
        $expected=$fixturePolicy.components.$component
        $testPolicyPath=Join-Path $fixture 'policy.json'
        if ($component -eq 'OpenJtalk') {
            $expected.compileArguments=[PSCustomObject]@{'compile-0.arguments.json'=(Get-SpeechInvocationDigest @('-O2','fixture.cpp'))}
            $expected.linkArgumentsSha256=Get-SpeechInvocationDigest @('-shared','fixture.obj')
            @('-O2','fixture.cpp') | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'compile-0.arguments.json')
            @('-shared','fixture.obj') | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'link.arguments.json')
        }
        $fixturePolicy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $testPolicyPath
        $dll=if ($component -eq 'World') {'tanayomi-world.dll'} else {'tanayomi-open-jtalk.dll'}
        [IO.File]::WriteAllText((Join-Path $fixture $dll),'fixture DLL bytes')
        $manifest=[ordered]@{llvm='23.1.4';toolchainArchiveSha256=$expected.toolchainArchiveSha256;dllSha256=(Get-FileHash -LiteralPath (Join-Path $fixture $dll)).Hash;imports=$expected.imports;bridgeSha256=$expected.bridgeSha256;flags=$expected.compilerFlags;compilerFlags=$expected.compilerFlags;linkFlags=$expected.linkFlags;worldArchiveSha256=$expected.worldArchiveSha256;worldRevision=$expected.worldRevision;sources=$expected.sources}
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture $expected.manifest)
        $expected.members | Set-Content -LiteralPath (Join-Path $fixture 'linked-members.txt')
        $expected.includes | Set-Content -LiteralPath (Join-Path $fixture 'includes.txt')
        "Format: COFF-x86-64`nImport {`n  Name: msvcrt.dll`n}`nImport {`n  Name: KERNEL32.dll`n}" | Set-Content -LiteralPath (Join-Path $fixture 'pe.stdout.txt')
        Test-SpeechNativeRuntime $component $fixture $testPolicyPath | Out-Null
        $checks++
        'libunexpected.a(unapproved.obj)' | Add-Content -LiteralPath (Join-Path $fixture 'linked-members.txt')
        Reject {Test-SpeechNativeRuntime $component $fixture $testPolicyPath}
        $expected.members[1..($expected.members.Count-1)] | Set-Content -LiteralPath (Join-Path $fixture 'linked-members.txt')
        Reject {Test-SpeechNativeRuntime $component $fixture $testPolicyPath}
        $expected.members | Set-Content -LiteralPath (Join-Path $fixture 'linked-members.txt')
        $manifest.toolchainArchiveSha256='changed'
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture $expected.manifest)
        Reject {Test-SpeechNativeRuntime $component $fixture $testPolicyPath}
        $manifest.toolchainArchiveSha256=$expected.toolchainArchiveSha256
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture $expected.manifest)
        "Format: COFF-x86-64`nImport {`n  Name: unapproved.dll`n}" | Set-Content -LiteralPath (Join-Path $fixture 'pe.stdout.txt')
        Reject {Test-SpeechNativeRuntime $component $fixture $testPolicyPath}
        "Format: COFF-x86-64`nImport {`n  Name: msvcrt.dll`n}`nImport {`n  Name: KERNEL32.dll`n}" | Set-Content -LiteralPath (Join-Path $fixture 'pe.stdout.txt')
        'D:/other-repository/.local/vendor/unapproved/header.h' | Add-Content -LiteralPath (Join-Path $fixture 'includes.txt')
        Reject {Test-SpeechNativeRuntime $component $fixture $testPolicyPath}
        $expected.includes | Set-Content -LiteralPath (Join-Path $fixture 'includes.txt')
        $manifest.bridgeSha256='changed'
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture $expected.manifest)
        Reject {Test-SpeechNativeRuntime $component $fixture $testPolicyPath}
        $manifest.bridgeSha256=$expected.bridgeSha256
        $manifest.flags=@('-O3'); $manifest.compilerFlags=@('-O3')
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture $expected.manifest)
        Reject {Test-SpeechNativeRuntime $component $fixture $testPolicyPath}
        $manifest.flags=$expected.compilerFlags; $manifest.compilerFlags=$expected.compilerFlags
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture $expected.manifest)
        if ($component -eq 'OpenJtalk') {
            @('-O3','fixture.cpp') | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'compile-0.arguments.json')
            Reject {Test-SpeechNativeRuntime $component $fixture $testPolicyPath}
            @('-O2','fixture.cpp') | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'compile-0.arguments.json')
        }
        [IO.File]::WriteAllText((Join-Path $fixture $dll),'changed DLL')
        Reject {Test-SpeechNativeRuntime $component $fixture $testPolicyPath}
    }
    if ((ConvertTo-SpeechPortablePath 'D:\new repository\.local\tmp\open-jtalk-0123456789abcdef0123456789abcdef\mecab.h') -ne '<work>/mecab.h') {throw 'Relocated temporary path normalization failed.'}
    $checks++
    if ((ConvertTo-SpeechPortablePath 'D:\native\project\native\open-jtalk-bridge.cpp') -ne '<repo>/native/open-jtalk-bridge.cpp') {throw 'Repository named native was not normalized correctly.'}
    $checks++
    if ((ConvertTo-SpeechPortablePath '-Wl,-Map,D:\new checkout\.local\build\open-jtalk\audit-20261127-094152-519b9c70\open-jtalk.map') -ne '-Wl,-Map,<output>/open-jtalk.map') {throw 'Generated output directory normalization failed.'}
    $checks++
    Write-Output "Speech runtime policy fixtures: $checks passed."
} catch {Write-Error "SPEECH-POLICY-TEST: $($_.Exception.Message)" -ErrorAction Continue; exit 1}
finally {
    if (Test-Path -LiteralPath $fixture) {
        $null=Assert-SpeechNativeInputPath $fixture $temporaryRoot
        foreach ($entry in Get-ChildItem -LiteralPath $fixture -Recurse -Force) {$null=Assert-SpeechNativeInputPath $entry.FullName $fixture}
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
