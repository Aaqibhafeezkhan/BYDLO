# Synthetic offline fixtures only. No downloads, engines, or system changes.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
. (Join-Path $PSScriptRoot 'openvpn-cache.ps1')
$repo = $env:BYDLO_ROOT
$fixture = Join-Path $repo ('.tmp/bundle-test-' + [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $fixture 'publish'
$lock = Get-Content (Join-Path $repo 'engines/engines.lock.json') -Raw | ConvertFrom-Json -AsHashtable
$script:checks = 0
function Require([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Reject([scriptblock]$Action, [string]$Expected) {
    try { & $Action } catch {
        Require ($_.Exception.Message -like "*$Expected*") "Unexpected failure: $_"
        return
    }
    throw "Expected rejection: $Expected"
}
function Put([string]$Path, [byte[]]$Bytes) {
    New-Item -ItemType Directory -Force (Split-Path $Path -Parent) | Out-Null
    [IO.File]::WriteAllBytes($Path, $Bytes)
}
function Hash-Bundle {
    Get-ChildItem (Join-Path $bundle 'engines') -Recurse -File | Sort-Object FullName | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),
            [IO.Path]::GetRelativePath($bundle, $_.FullName).Replace('\', '/')
    } | Set-Content (Join-Path $bundle 'ENGINES-SHA256SUMS.txt') -Encoding utf8NoBOM
}
function Verify { & (Join-Path $PSScriptRoot 'verify-engine-bundle.ps1') -Rid osx-arm64 -PublishDirectory $bundle -PrebuiltOnly }
function Mutate([string]$Path, [byte[]]$Bytes, [string]$Expected) {
    $original = [IO.File]::ReadAllBytes($Path)
    try {
        [IO.File]::WriteAllBytes($Path, $Bytes)
        Hash-Bundle # Architecture checks must reject even with a matching digest.
        Reject { Verify } $Expected
    } finally { [IO.File]::WriteAllBytes($Path, $original); Hash-Bundle }
}
try {
    New-Item -ItemType Directory -Force $bundle | Out-Null
    Copy-Item (Join-Path $repo 'LICENSE'), (Join-Path $repo 'THIRD_PARTY_NOTICES.md') $bundle
    Copy-Item (Join-Path $repo 'licenses') $bundle -Recurse
    Copy-Item (Join-Path $repo 'licenses/Velopack.txt') (Join-Path $bundle 'LICENSE-Velopack.txt')
    # Minimal little-endian ARM64 Mach-O header (never executable code).
    $thin = [byte[]]::new(32)
    [BitConverter]::GetBytes([uint32]4277009103).CopyTo($thin, 0)
    [BitConverter]::GetBytes([uint32]0x100000c).CopyTo($thin, 4)
    $app = Join-Path $bundle 'BYDLO'
    $helper = Join-Path $bundle 'helper/Bydlo.Helper'
    Put $app $thin
    Put $helper $thin
    $paths = @{}
    foreach ($id in @('sing-box', 'xray', 'zapret-native')) {
        $entry = $lock.engines | Where-Object id -eq $id
        $paths[$id] = Join-Path $bundle "engines/$id/$($entry.version)/$($entry.assets['macos-arm64'].executable)"
        Put $paths[$id] $thin
    }
    $fat = [byte[]]::new(64)
    # FAT_MAGIC, one ARM64 slice at offset 32, size 32, aligned to 2^2.
    ([byte[]]@(0xca,0xfe,0xba,0xbe, 0,0,0,1, 1,0,0,0x0c, 0,0,0,0,
        0,0,0,32, 0,0,0,32, 0,0,0,2)).CopyTo($fat, 0)
    $thin.CopyTo($fat, 32)
    Put $paths['zapret-native'] $fat
    foreach ($license in Get-ChildItem (Join-Path $repo 'engines/licenses') -File) {
        Put (Join-Path $bundle "engines/licenses/$($license.Name)") ([byte[]]@(1))
    }
    foreach ($name in @('sing-box-source.tar.gz', 'xray-source.tar.gz')) {
        Put (Join-Path $bundle "engines/sources/$name") ([byte[]]@(1))
    }
    Hash-Bundle
    Verify
    $script:checks++
    $wrongCpu = $thin.Clone()
    [BitConverter]::GetBytes([uint32]0x1000007).CopyTo($wrongCpu, 4)
    Mutate $app $wrongCpu 'Wrong Mach-O architecture'
    Mutate $helper $wrongCpu 'Wrong Mach-O architecture'
    [IO.File]::Delete($helper)
    Reject { Verify } 'Missing application executable: helper/Bydlo.Helper'
    Put $helper $thin
    Mutate $paths['sing-box'] $wrongCpu 'Wrong Mach-O architecture'
    Mutate $paths['sing-box'] ([byte[]]@(0x7f,0x45,0x4c,0x46,0,0,0,0)) 'Wrong executable format'
    $noArm = $fat.Clone(); $noArm[11] = 7
    Mutate $paths['zapret-native'] $noArm 'Missing Apple Silicon slice'
    $badBounds = $fat.Clone(); $badBounds[19] = 63
    Mutate $paths['zapret-native'] $badBounds 'Invalid universal slice bounds'
    $badSlice = $fat.Clone(); $badSlice[36] = 7
    Mutate $paths['zapret-native'] $badSlice 'Invalid Apple Silicon slice'
    $missing = $paths['xray']
    $saved = [IO.File]::ReadAllBytes($missing)
    [IO.File]::Delete($missing)
    Reject { Verify } 'Missing bundled engine'
    Put $missing $saved
    $source = Join-Path $bundle 'engines/sources/xray-source.tar.gz'
    [IO.File]::Delete($source)
    Reject { Verify } 'Missing corresponding source'
    Put $source ([byte[]]@(1))
    $license = Join-Path $bundle 'engines/licenses/zapret-MIT.txt'
    [IO.File]::Delete($license)
    Reject { Verify } 'Missing bundled license'
    Put $license ([byte[]]@(1))
    Hash-Bundle
    Put $source ([byte[]]@(2))
    Reject { Verify } 'Bundled hash mismatch'
    Hash-Bundle
    Put (Join-Path $bundle 'engines/unlisted') ([byte[]]@(1))
    Reject { Verify } 'Unhashed bundled file'
    Hash-Bundle
    $manifest = Join-Path $bundle 'ENGINES-SHA256SUMS.txt'
    Add-Content $manifest (Get-Content $manifest -First 1)
    Reject { Verify } 'Invalid or duplicate manifest entry'
    Hash-Bundle
    [IO.File]::WriteAllText($manifest, '')
    Reject { Verify } 'Unhashed bundled file'
    # The partial artifact must never pass the full release gate.
    Reject { & (Join-Path $PSScriptRoot 'verify-engine-bundle.ps1') -Rid osx-arm64 -PublishDirectory $bundle } 'Missing bundled engine'

    $cache = Join-Path $fixture 'cache'
    New-Item -ItemType Directory -Force $cache | Out-Null
    Require (!(Test-OpenVpnCache $cache)) 'Empty cache should rebuild'
    foreach ($name in @('openvpn', 'COPYING', 'openssl.cnf')) { Put (Join-Path $cache $name) ([byte[]]@(1)) }
    Require (!(Test-OpenVpnCache $cache)) 'Legacy cache without digests should rebuild'
    Write-OpenVpnCacheManifest $cache
    Require (Test-OpenVpnCache $cache) 'Valid cache rejected'
    Put (Join-Path $cache 'openvpn') ([byte[]]@(2))
    Reject { Test-OpenVpnCache $cache } 'OpenVPN cache hash mismatch'
    Write-OpenVpnCacheManifest $cache
    [IO.File]::Delete((Join-Path $cache 'COPYING'))
    Reject { Test-OpenVpnCache $cache } 'Incomplete OpenVPN cache'
    Write-Host "Passed $script:checks offline bundle/cache checks."
} finally {
    # Only the unique fixture directory created above is removed.
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath((Join-Path $repo '.tmp')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped repository temp directory' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
