# Synthetic package contents only. No engines, installer, network, or system changes.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$fixture = Join-Path $env:BYDLO_ROOT ('.tmp/license-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fixture | Out-Null
Copy-Item (Join-Path $env:BYDLO_ROOT 'LICENSE'), (Join-Path $env:BYDLO_ROOT 'THIRD_PARTY_NOTICES.md') $fixture
Copy-Item (Join-Path $env:BYDLO_ROOT 'licenses') $fixture -Recurse
Copy-Item (Join-Path $env:BYDLO_ROOT 'licenses/Velopack.txt') (Join-Path $fixture 'LICENSE-Velopack.txt')
function Verify { & (Join-Path $PSScriptRoot 'verify-app-licenses.ps1') -PublishDirectory $fixture }
function Reject([string]$Message) {
    try { Verify } catch {
        if ($_.Exception.Message -like "*$Message*") { return }
        throw
    }
    throw "Expected rejection: $Message"
}
Verify
foreach ($relative in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'LICENSE-Velopack.txt', 'licenses/README.md',
    'licenses/manifest.json', 'licenses/Inter.txt', 'licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt')) {
    $path = Join-Path $fixture $relative
    $bytes = [IO.File]::ReadAllBytes($path)
    try {
        [IO.File]::Delete($path)
        Reject "Missing application license: $relative"
        [IO.File]::WriteAllText($path, 'Synthetic damaged notice')
        Reject $relative
    }
    finally { [IO.File]::WriteAllBytes($path, $bytes) }
}
Verify
Write-Output 'Application license verification passed: complete payload, missing notices, changed notices.'
