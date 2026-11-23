#Requires -Version 7.0
$ErrorActionPreference = 'Stop'

function Assert-UnlinkedFixturePath([string]$Path) {
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Fixture path contains a link.'
        }
    }
}

try {
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $scriptPath = Join-Path $repository 'native/build-open-jtalk.ps1'
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw ($errors -join [Environment]::NewLine) }

    # Execute only the literal Unix library-check branches. The Windows audit
    # and the script's tool launcher are never invoked by these fixtures.
    $nodes = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
            $node.Clauses[0].Item2.Extent.Text -match '^\{\s*\$library=Join-Path \$output ''libtanayomi-open-jtalk\.so'''
    }, $true))
    if ($nodes.Count -ne 1 -or $nodes[0].Clauses.Count -ne 2) { throw 'Unexpected Unix audit branch structure.' }
    $elf = $nodes[0].Clauses[0]
    $macho = $nodes[0].Clauses[1]
    if ($elf.Item1.Extent.Text -cne '$linux' -or $macho.Item1.Extent.Text -cne '$mac' -or
        $macho.Item2.Extent.Text -cnotmatch '^\{\s*\$library=Join-Path \$output ''libtanayomi-open-jtalk\.dylib''') {
        throw 'Unexpected Unix audit branches.'
    }
    $elfCheck = [scriptblock]::Create($elf.Item2.Extent.Text.Trim().Substring(1, $elf.Item2.Extent.Text.Trim().Length - 2))
    $machoCheck = [scriptblock]::Create($macho.Item2.Extent.Text.Trim().Substring(1, $macho.Item2.Extent.Text.Trim().Length - 2))

    $root = [IO.Path]::GetFullPath((Join-Path $repository '.local/tmp'))
    Assert-UnlinkedFixturePath $root
    $temporary = Join-Path $root ('open-jtalk-fixture-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    try {
        $output = $temporary
        $expectedExports = 'toj_abi_version','toj_clear','toj_load','toj_plan','toj_synthesize','toj_synthesize_drawn_out'
        $common = @(); $objects = @(); $map = Join-Path $output 'open-jtalk.map'
        $compiler = 'fixture'; $nm = 'fixture'; $inspector = 'fixture'

        function Invoke-Audit($tool, $arguments, $name) {
            if ($tool -cne 'fixture') { throw 'Unexpected tool outside fixture.' }
            switch ($name) {
                'link' {
                    $header = [byte[]]::new(64)
                    if ($linux) {
                        [Convert]::FromHexString('7F454C46020101').CopyTo($header, 0)
                        $header[16] = if ($case -eq 'filetype') { 2 } else { 3 }
                        $header[18] = if ($case -eq 'cpu') { 245 - $elfMachine } else { $elfMachine }
                        [IO.File]::WriteAllBytes((Join-Path $output 'libtanayomi-open-jtalk.so'), $header)
                    } else {
                        [Convert]::FromHexString('CFFAEDFE').CopyTo($header, 0)
                        $cpu = if ($case -eq 'cpu') { if ($machCpu -eq '07000001') { '0C000001' } else { '07000001' } } else { $machCpu }
                        [Convert]::FromHexString($cpu).CopyTo($header, 4)
                        $header[12] = if ($case -eq 'filetype') { 2 } else { 6 }
                        [IO.File]::WriteAllBytes((Join-Path $output 'libtanayomi-open-jtalk.dylib'), $header)
                    }
                }
                'symbols' {
                    $prefix = if ($linux) { '' } else { '_' }
                    $lines = @($expectedExports | ForEach-Object { '0000000000013f20 T ' + $prefix + $_ })
                    if ($case -eq 'extra') { $lines += '0000000000013f20 T ' + $prefix + 'toj_extra' }
                    if ($case -eq 'missing') { $lines = $lines[0..3] }
                    if ($case -eq 'undefined') { $lines[4] = '                 U ' + $prefix + 'toj_synthesize' }
                    [IO.File]::WriteAllLines((Join-Path $output 'symbols.stdout.txt'), $lines)
                }
                'dependencies' {
                    if ($linux) {
                        $lines = @(' 0x0000000000000001 (NEEDED) Shared library: [libc.so.6]',' 0x0000000000000001 (NEEDED) Shared library: [libstdc++.so.6]')
                        if ($case -eq 'runtime') { $lines = $lines[1..1] }
                        if ($case -eq 'search') { $lines += ' 0x000000000000001d (RUNPATH) Library runpath: [/tmp]' }
                        if ($case -eq 'foreign') { $lines += ' 0x0000000000000001 (NEEDED) Shared library: [/tmp/libother.so]' }
                    } else {
                        $lines = @('Load command 0','          cmd LC_ID_DYLIB','      cmdsize 64','         name @rpath/libtanayomi-open-jtalk.dylib (offset 24)',
                            'Load command 1','          cmd LC_LOAD_DYLIB','      cmdsize 56','         name /usr/lib/libSystem.B.dylib (offset 24)',
                            'Load command 2','          cmd LC_LOAD_WEAK_DYLIB','      cmdsize 56','         name /usr/lib/libc++.1.dylib (offset 24)')
                        if ($case -eq 'runtime') { $lines[7] = '         name /usr/lib/libobjc.A.dylib (offset 24)' }
                        if ($case -eq 'search') { $lines += 'Load command 3','          cmd LC_RPATH','      cmdsize 32','         path /tmp (offset 12)' }
                        if ($case -eq 'foreign') { $lines[11] = '         name @rpath/libother.dylib (offset 24)' }
                        if ($case -eq 'malformed') { $lines[11] = '         name /usr/lib/libc++.1.dylib' }
                    }
                    [IO.File]::WriteAllLines((Join-Path $output 'dependencies.stdout.txt'), $lines)
                }
                default { throw "Unexpected fixture tool step: $name" }
            }
        }

        foreach ($rid in 'linux-x64','linux-arm64','osx-x64','osx-arm64') {
            $linux = $rid.StartsWith('linux-')
            $arm = $rid.EndsWith('-arm64')
            $elfMachine = if ($arm) { 183 } else { 62 }
            $machCpu = if ($arm) { '0C000001' } else { '07000001' }
            $check = if ($linux) { $elfCheck } else { $machoCheck }
            $format = if ($linux) { 'ELF' } else { 'Mach-O' }
            $cases = [ordered]@{
                ok = ''; cpu = "for $rid."; filetype = "for $rid."
                extra = "Unexpected $format C ABI exports."; missing = "Unexpected $format C ABI exports."
                undefined = "Unexpected defined $format export."
                runtime = "Unexpected $format dependency or embedded runtime search path."
                search = "Unexpected $format dependency or embedded runtime search path."
                foreign = "Unexpected $format dependency or embedded runtime search path."
            }
            if (-not $linux) { $cases.malformed = $cases.foreign }
            foreach ($case in $cases.Keys) {
                try {
                    & $check
                    if ($case -ne 'ok') { throw "Unexpected acceptance for $rid / $case." }
                } catch {
                    if ($case -eq 'ok' -or -not $_.Exception.Message.EndsWith($cases[$case], [StringComparison]::Ordinal)) { throw }
                }
            }
            Write-Output "$rid audit fixtures passed ($($cases.Count) cases)."
        }
    } finally {
        Assert-UnlinkedFixturePath $temporary
        $resolved = (Resolve-Path -LiteralPath $temporary).Path
        if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) -or
            [IO.Path]::GetFileName($resolved) -cnotmatch '^open-jtalk-fixture-[a-f0-9]{32}$' -or
            @(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
            throw 'Unexpected fixture cleanup target.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
} catch {
    [Console]::Error.WriteLine('Open JTalk audit fixture failed: ' + $_.Exception.Message)
    exit 1
}
