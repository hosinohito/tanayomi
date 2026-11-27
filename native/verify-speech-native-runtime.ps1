#Requires -Version 7.4
<#
Read-only comparison of a WORLD/Open JTalk audit directory with the adopted Windows
runtime's portable source/include/member policy. Native behavior and overall
product distribution audits remain separate. No compiler or DLL is executed.
#>
[CmdletBinding()]
param([ValidateSet('World','OpenJtalk')][string]$Component, [string]$AuditDirectory)
$ErrorActionPreference='Stop'

function Assert-SpeechRuntimeSet([string[]]$Actual,[string[]]$Expected,[string]$Label) {
    $actualSet=@($Actual | Sort-Object -Unique)
    $expectedSet=@($Expected | Sort-Object -Unique)
    $differences=@(Compare-Object $expectedSet $actualSet -CaseSensitive)
    if ($differences.Count) {
        $added=@($differences | Where-Object SideIndicator -EQ '=>' | ForEach-Object InputObject)
        $removed=@($differences | Where-Object SideIndicator -EQ '<=' | ForEach-Object InputObject)
        throw "$Label differs. Added: $($added -join ', '); removed: $($removed -join ', ')"
    }
}

function ConvertTo-SpeechPortablePath([string]$Value) {
    $portable=$Value -replace '\\+','/'
    $portable=$portable -replace '(?i)[a-z]:/[^:\r\n]*/(?=\.local/)','<repo>/'
    $portable=$portable -replace '(?i)[a-z]:/[^:\r\n]*/(?=native/)','<repo>/'
    $portable=$portable -replace '<repo>/\.local/tmp/(?:open-jtalk|world-llvm)-[a-fA-F0-9]{32}','<work>'
    $portable=$portable -replace '<repo>/\.local/build/open-jtalk/audit-[0-9]{8}-[0-9]{6}-[a-fA-F0-9]{8}(?=/)','<output>'
    return $portable
}

function Get-SpeechInvocationDigest([string[]]$Arguments) {
    $canonical=@($Arguments | ForEach-Object {ConvertTo-SpeechPortablePath $_}) -join "`n"
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
}

function Test-SpeechNativeRuntime([string]$Name,[string]$Directory,[string]$PolicyPath) {
    if ([string]::IsNullOrWhiteSpace($Directory)) { throw 'AuditDirectory is required.' }
    $root=[IO.Path]::GetFullPath($Directory)
    foreach ($path in @($root,$PolicyPath)) {
        for ($current=$path; $current; $current=[IO.Path]::GetDirectoryName($current)) {
            $item=Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
            if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked runtime evidence path.' }
        }
    }
    $policy=Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
    if ($policy.version -ne 1 -or $Name -notin @('World','OpenJtalk')) { throw 'Unsupported runtime policy.' }
    $expected=$policy.components.$Name
    $dll=if ($Name -eq 'World') {'tanayomi-world.dll'} else {'tanayomi-open-jtalk.dll'}
    foreach ($file in @($expected.manifest,'linked-members.txt','includes.txt','pe.stdout.txt',$dll)) {
        $path=Join-Path $root $file
        $item=Get-Item -LiteralPath $path -Force
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid runtime evidence file.' }
    }
    $manifest=Get-Content -LiteralPath (Join-Path $root $expected.manifest) -Raw | ConvertFrom-Json
    if ($manifest.toolchainArchiveSha256 -ne $expected.toolchainArchiveSha256 -or $manifest.llvm -ne '23.1.4') { throw 'Runtime toolchain differs from adopted policy.' }
    if ((Get-FileHash -LiteralPath (Join-Path $root $dll) -Algorithm SHA256).Hash -ne $manifest.dllSha256) { throw 'Runtime DLL does not match its audit manifest.' }
    $members=@(Get-Content -LiteralPath (Join-Path $root 'linked-members.txt'))
    if (-not $members.Count -or @($members | Where-Object {$_ -notmatch '^[^/\\:]+\.a\([^/\\:]+\)$'}).Count) { throw 'Invalid runtime archive-member evidence.' }
    Assert-SpeechRuntimeSet $members $expected.members 'Runtime archive members'
    Assert-SpeechRuntimeSet $manifest.imports $expected.imports 'Manifest OS imports'
    $pe=Get-Content -LiteralPath (Join-Path $root 'pe.stdout.txt') -Raw
    if ($pe -notmatch '(?m)^Format: COFF-x86-64\r?$') { throw 'Runtime PE architecture differs from Windows x64.' }
    $imports=@([regex]::Matches($pe,'(?m)^Import \{\r?\n\s+Name: ([^\r\n]+)') | ForEach-Object {$_.Groups[1].Value})
    Assert-SpeechRuntimeSet $imports $expected.imports 'PE OS imports'
    if ($manifest.bridgeSha256 -ne $expected.bridgeSha256) { throw 'Compiled bridge differs from the adopted bridge.' }
    $repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    if ((Get-FileHash -LiteralPath (Join-Path $repository $expected.bridge) -Algorithm SHA256).Hash -ne $expected.bridgeSha256) { throw 'Repository bridge requires renewed source review.' }
    if ((Get-FileHash -LiteralPath (Join-Path $repository $expected.builder) -Algorithm SHA256).Hash -ne $expected.builderSha256) { throw 'Builder changed; source transformation review is required.' }
    $includes=@(Get-Content -LiteralPath (Join-Path $root 'includes.txt') | ForEach-Object {ConvertTo-SpeechPortablePath $_})
    Assert-SpeechRuntimeSet $includes $expected.includes 'Compiled include paths'
    $flags=if ($Name -eq 'World') {$manifest.compilerFlags} else {$manifest.flags}
    $normalizedFlags=@($flags | ForEach-Object {ConvertTo-SpeechPortablePath $_})
    if (($normalizedFlags -join "`n") -cne ($expected.compilerFlags -join "`n")) { throw 'Compiler flags differ from the adopted sequence.' }
    if ($Name -eq 'World') {
        if (($manifest.linkFlags -join "`n") -cne ($expected.linkFlags -join "`n") -or
            $manifest.worldArchiveSha256 -ne $expected.worldArchiveSha256 -or $manifest.worldRevision -ne $expected.worldRevision) { throw 'WORLD source or linker flags differ from adopted policy.' }
    } else {
        $sources=@($manifest.sources | ForEach-Object {ConvertTo-SpeechPortablePath $_})
        Assert-SpeechRuntimeSet $sources $expected.sources 'Compiled sources'
        $arguments=@(Get-ChildItem -LiteralPath $root -Filter 'compile-*.arguments.json' -File)
        if ($arguments.Count -ne @($expected.compileArguments.PSObject.Properties).Count) { throw 'Compile invocation count differs from adopted policy.' }
        foreach ($argumentFile in @($arguments)+@(Get-Item -LiteralPath (Join-Path $root 'link.arguments.json'))) {
            if ($argumentFile.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked compiler evidence.' }
            $actualDigest=Get-SpeechInvocationDigest @(Get-Content -LiteralPath $argumentFile.FullName -Raw | ConvertFrom-Json)
            $expectedDigest=if ($argumentFile.Name -eq 'link.arguments.json') {$expected.linkArgumentsSha256} else {$expected.compileArguments.($argumentFile.Name)}
            if ($actualDigest -cne $expectedDigest) { throw "Compiler/link invocation differs: $($argumentFile.Name)" }
        }
    }
    Write-Output "$Name runtime policy matched: $($expected.members.Count) members, $($includes.Count) include paths, source/bridge/compiler flags and OS imports. Product checks and distribution audit remain separate."
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        Test-SpeechNativeRuntime $Component $AuditDirectory (Join-Path $PSScriptRoot 'speech-native-runtime-policy.json')
    } catch { Write-Error "SPEECH-RUNTIME-POLICY: $($_.Exception.Message)" -ErrorAction Continue; exit 1 }
}
