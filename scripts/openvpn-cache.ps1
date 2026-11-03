# Cache digests are recorded at build time and checked before any restored bytes are copied.
# This is corruption detection; source hashes and the exact CI cache key identify the inputs.
function Test-OpenVpnCache([string]$Directory) {
    $names = @('openvpn', 'COPYING', 'openssl.cnf')
    $manifest = Join-Path $Directory 'SHA256SUMS.json'
    if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) { return $false }
    $hashes = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -AsHashtable
    if ($hashes.Count -ne $names.Count) { throw 'Invalid OpenVPN cache manifest' }
    foreach ($name in $names) {
        $path = Join-Path $Directory $name
        if (!$hashes.ContainsKey($name) -or !(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Incomplete OpenVPN cache: $name"
        }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hashes[$name]) {
            throw "OpenVPN cache hash mismatch: $name"
        }
    }
    return $true
}

function Write-OpenVpnCacheManifest([string]$Directory) {
    $hashes = [ordered]@{}
    foreach ($name in @('openvpn', 'COPYING', 'openssl.cnf')) {
        $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $Directory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS.json') -Encoding utf8NoBOM
}
