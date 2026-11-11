param([Parameter(Mandatory)][string]$PublishDirectory)
# Read-only verification of notices in a loose publish directory or extracted package.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$payload = [IO.Path]::GetFullPath($PublishDirectory)
$catalog = Get-Content (Join-Path $repo 'licenses/manifest.json') -Raw | ConvertFrom-Json
if ($catalog.formatVersion -ne 1 -or $catalog.files.Count -eq 0) { throw 'Invalid application license catalog' }
foreach ($relative in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'LICENSE-Velopack.txt', 'licenses/README.md', 'licenses/manifest.json')) {
    $path = Join-Path $payload $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Missing application license: $relative"
    }
    $source = if ($relative -eq 'LICENSE-Velopack.txt') { 'licenses/Velopack.txt' } else { $relative }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $repo $source) -Algorithm SHA256).Hash) {
        throw "Application license differs from checkout: $relative"
    }
}
foreach ($entry in $catalog.files) {
    $relative = "licenses/$($entry.file)"
    # The catalog is repository-owned, but fail closed on accidental paths outside licenses/.
    if ($entry.file -ne [IO.Path]::GetFileName($entry.file) -or $entry.file -notlike '*.txt') {
        throw 'Invalid application license catalog path'
    }
    $path = Join-Path $payload $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing application license: $relative" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
        throw "Application license hash mismatch: $relative"
    }
}
