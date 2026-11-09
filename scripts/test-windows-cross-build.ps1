# Synthetic data only. No engine, MSI, registry, driver or service is executed.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
. (Join-Path $PSScriptRoot 'extract-openvpn-msi.ps1')
$fixture = Join-Path $env:BYDLO_ROOT ('.tmp/windows-cross-test-' + [guid]::NewGuid().ToString('N'))
function Put([string]$path, [byte[]]$bytes) {
    New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
    [IO.File]::WriteAllBytes($path, $bytes)
}
function Reject([scriptblock]$action, [string]$expected) {
    try { & $action } catch {
        if ($_.Exception.Message -notlike "*$expected*") { throw }
        return
    }
    throw "Expected rejection: $expected"
}
try {
    foreach ($layout in @('SourceDir/OpenVPN', 'Program Files/OpenVPN')) {
        $root = Join-Path $fixture ([guid]::NewGuid().ToString('N'))
        Put (Join-Path $root "$layout/bin/openvpn.exe") ([byte[]]@(1,2,3))
        $dco = if ($layout.StartsWith('SourceDir')) { "$layout/Common Files/ovpn-dco" } else { 'Program Files/Common Files/ovpn-dco' }
        Put (Join-Path $root "$dco/Win10/ovpn-dco.inf") ([byte[]]@(4,5))
        $normalized = ConvertTo-OpenVpnSourceTree $root
        if ((Get-FileHash (Join-Path $normalized 'bin/openvpn.exe')).Hash -ne (Get-FileHash (Join-Path $root "$layout/bin/openvpn.exe")).Hash) {
            throw 'MSI normalization changed executable bytes'
        }
        if (!(Test-Path (Join-Path $normalized 'Common Files/ovpn-dco/Win10/ovpn-dco.inf'))) { throw 'Missing normalized driver' }
    }
    $lock = Get-Content (Join-Path $env:BYDLO_ROOT 'engines/engines.lock.json') -Raw | ConvertFrom-Json -AsHashtable
    $inputs = Get-Content (Join-Path $env:BYDLO_ROOT 'engines/bundle-inputs.lock.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($rid in @('win-x64','win-arm64')) {
        $bundle = Join-Path $fixture $rid
        New-Item -ItemType Directory -Force $bundle | Out-Null
        Copy-Item (Join-Path $env:BYDLO_ROOT 'LICENSE'), (Join-Path $env:BYDLO_ROOT 'THIRD_PARTY_NOTICES.md') $bundle
        Copy-Item (Join-Path $env:BYDLO_ROOT 'licenses') $bundle -Recurse
        Copy-Item (Join-Path $env:BYDLO_ROOT 'licenses/Velopack.txt') (Join-Path $bundle 'LICENSE-Velopack.txt')
        $platform = $rid.Replace('win-', 'windows-')
        # Minimal PE header, never executable code.
        $pe = [byte[]]::new(128)
        $pe[0] = 0x4d; $pe[1] = 0x5a
        [BitConverter]::GetBytes([uint32]0x40).CopyTo($pe, 0x3c)
        [BitConverter]::GetBytes([uint32]0x4550).CopyTo($pe, 0x40)
        $machine = if ($rid.EndsWith('arm64')) { 0xaa64 } else { 0x8664 }
        [BitConverter]::GetBytes([uint16]$machine).CopyTo($pe, 0x44)
        Put (Join-Path $bundle 'BYDLO.exe') $pe
        Put (Join-Path $bundle 'helper/Bydlo.Helper.exe') $pe
        foreach ($entry in $lock.engines) {
            $exe = if ($entry.assets.ContainsKey($platform)) { $entry.assets[$platform].executable } elseif ($entry.bundledExecutables) { $entry.bundledExecutables[$platform] }
            if ($exe) { Put (Join-Path $bundle "engines/$($entry.id)/$($entry.version)/$exe") $pe }
        }
        $version = ($lock.engines | Where-Object id -eq 'openvpn').version
        foreach ($os in @('Win10','Win11')) {
            foreach ($ext in @('inf','cat','sys')) {
                Put (Join-Path $bundle "engines/openvpn/$version/dco/$os/ovpn-dco.$ext") ([byte[]]@(1))
            }
        }
        foreach ($license in Get-ChildItem (Join-Path $env:BYDLO_ROOT 'engines/licenses') -File) {
            Put (Join-Path $bundle "engines/licenses/$($license.Name)") ([byte[]]@(1))
        }
        foreach ($name in @('sing-box-source.tar.gz','xray-source.tar.gz','openssl-source.tar.gz','amneziawg-with-dependencies.tar.gz',
            [IO.Path]::GetFileName(([uri]$inputs['openvpn-source'].url).AbsolutePath), 'build-openvpn.sh', 'build-bundled-engines.ps1',
            'openvpn-cache.ps1','native-build-cache.ps1','extract-openvpn-msi.ps1')) {
            Put (Join-Path $bundle "engines/sources/$name") ([byte[]]@(1))
        }
        function Hash-Bundle {
            Get-ChildItem (Join-Path $bundle 'engines') -Recurse -File | Sort-Object FullName | ForEach-Object {
                '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant(), [IO.Path]::GetRelativePath($bundle, $_.FullName).Replace('\','/')
            } | Set-Content (Join-Path $bundle 'ENGINES-SHA256SUMS.txt') -Encoding utf8NoBOM
        }
        function Verify { & (Join-Path $PSScriptRoot 'verify-engine-bundle.ps1') -Rid $rid -PublishDirectory $bundle }
        Hash-Bundle
        Verify
        $app = Join-Path $bundle 'BYDLO.exe'
        $wrong = $pe.Clone()
        [BitConverter]::GetBytes([uint16]0x14c).CopyTo($wrong, 0x44)
        Put $app $wrong
        Reject { Verify } 'Wrong PE architecture'
        Put $app $pe
        $engine = Get-ChildItem (Join-Path $bundle 'engines') -Recurse -File -Filter '*.exe' | Select-Object -First 1
        Put $engine.FullName $wrong
        Hash-Bundle
        Reject { Verify } 'Wrong PE architecture'
        Put $engine.FullName $pe
        Hash-Bundle
        Put (Join-Path $bundle 'engines/sources/sing-box-source.tar.gz') ([byte[]]@(2))
        Reject { Verify } 'Bundled hash mismatch'
    }
    Write-Host 'Windows cross-build fixture checks passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $allowed = [IO.Path]::GetFullPath((Join-Path $env:BYDLO_ROOT '.tmp')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped repository temp directory' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
