# Read and write build-cache manifests only. Never execute cached binaries.
function Test-NativeBuildCache {
    param([string]$Directory, [string]$Identity, [string[]]$Files)
    try {
        $manifestPath = Join-Path $Directory 'cache-manifest.json'
        if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $false }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        if ($manifest.version -ne 1 -or $manifest.identity -cne $Identity -or
            $manifest.files -isnot [System.Collections.IDictionary] -or $manifest.files.Count -ne $Files.Count) { return $false }
        foreach ($name in $Files) {
            $path = Join-Path $Directory $name
            if (!$manifest.files.Contains($name) -or !(Test-Path -LiteralPath $path -PathType Leaf) -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.files[$name]) { return $false }
        }
        return $true
    } catch { return $false }
}

function Save-NativeBuildCache {
    param([string]$Directory, [string]$Source, [string]$Identity, [string[]]$Files)
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $hashes = [ordered]@{}
    foreach ($name in $Files) {
        $target = Join-Path $Directory $name
        Copy-Item -LiteralPath (Join-Path $Source $name) -Destination $target -Force
        $hashes[$name] = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    # Publish the manifest last so interrupted copies cannot produce a cache hit.
    $manifestPath = Join-Path $Directory 'cache-manifest.json'
    @{ version = 1; identity = $Identity; files = $hashes } | ConvertTo-Json |
        Set-Content -LiteralPath ($manifestPath + '.tmp') -Encoding utf8NoBOM
    Move-Item -LiteralPath ($manifestPath + '.tmp') -Destination $manifestPath -Force
}
