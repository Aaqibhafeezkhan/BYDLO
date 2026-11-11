#Requires -Version 7
# Issue #411: build and sign the update catalog from the final release packages.
# The private key never lives in the repository: pass it through BYDLO_UPDATE_SIGNING_KEY.
# Nothing here executes a package; only sizes and SHA-256 digests are read.
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Channel = 'stable',
    # Defaults to UpdateTrustRoots.CurrentKeyId; then the key must match CurrentPublicKeySpki.
    [string]$KeyId,
    [string]$PrivateKeyPem = $env:BYDLO_UPDATE_SIGNING_KEY,
    # A year: clients on an older version must still reach the latest release when no new one ships for a while.
    [int]$ValidDays = 365,
    [datetime]$IssuedAt = [datetime]::UtcNow
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
if ([string]::IsNullOrWhiteSpace($PrivateKeyPem)) {
    throw 'BYDLO_UPDATE_SIGNING_KEY is missing: refusing to publish an unsigned update catalog.'
}
# GitHub secrets survive YAML as plain text; accept a base64-wrapped PEM as well.
if ($PrivateKeyPem -notmatch '-----BEGIN') {
    try { $PrivateKeyPem = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($PrivateKeyPem)) }
    catch { throw 'BYDLO_UPDATE_SIGNING_KEY is neither a PEM key nor base64-encoded PEM.' }
}
# Without -KeyId the release signs as the compiled-in current key, so a stale secret after a
# rotation fails here instead of shipping a catalog every client rejects.
$expectedSpki = $null
if (-not $PSBoundParameters.ContainsKey('KeyId')) {
    $roots = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/Bydlo.Core/Updates/UpdateTrustRoots.cs') -Raw
    $KeyId = [regex]::Match($roots, 'CurrentKeyId\s*=\s*"([^"]+)"').Groups[1].Value
    $expectedSpki = [regex]::Match($roots, 'CurrentPublicKeySpki\s*=\s*"([^"]+)"').Groups[1].Value
    if (!$KeyId -or !$expectedSpki) { throw 'Cannot read CurrentKeyId / CurrentPublicKeySpki from UpdateTrustRoots.cs.' }
}
$rids = @('win-x64','win-arm64','linux-x64','linux-arm64','osx-arm64')
$directory = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
# The catalog must describe the version that is actually inside the package, not just its file name.
function Get-PackageVersion([string]$path) {
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' }) | Select-Object -First 1
        if (!$entry) { throw "Package has no nuspec: $path" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
    $match = [regex]::Match($text, '<version>\s*([^<\s]+)\s*</version>')
    if (!$match.Success) { throw "Package has no version: $path" }
    return $match.Groups[1].Value
}
$packages = foreach ($rid in $rids) {
    $name = "BydloDesktop-$Version-$rid-full.nupkg"
    $path = Join-Path $directory $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing final package: $name" }
    $file = Get-Item -LiteralPath $path
    if ($file.LinkType -or $file.Length -eq 0) { throw "Invalid final package: $name" }
    $packageVersion = Get-PackageVersion $path
    if ($packageVersion -cne $Version -and $packageVersion -cne "$Version.0") {
        throw "Package version '$packageVersion' does not match $Version : $name"
    }
    [ordered]@{
        rid = $rid
        fileName = $name
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$issued = $IssuedAt.ToUniversalTime()
$payload = [ordered]@{
    schemaVersion = 1
    issuedAt = $issued.ToString('yyyy-MM-ddTHH:mm:ssZ')
    expiresAt = $issued.AddDays($ValidDays).ToString('yyyy-MM-ddTHH:mm:ssZ')
    channel = $Channel
    version = $Version
    packages = @($packages)
}
$bytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 5 -Compress))
$ecdsa = [Security.Cryptography.ECDsa]::Create()
try {
    # Accepts PKCS#8 ("PRIVATE KEY") and SEC1 ("EC PRIVATE KEY") PEM.
    $ecdsa.ImportFromPem($PrivateKeyPem)
    if ($expectedSpki -and [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo()) -cne $expectedSpki) {
        throw "BYDLO_UPDATE_SIGNING_KEY does not match UpdateTrustRoots.CurrentPublicKeySpki ($KeyId): clients would reject this catalog."
    }
    # ECDSA P-256 / SHA-256, IEEE P1363 (r||s) — the format Bydlo.Core.Updates expects.
    $signature = $ecdsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256)
} finally { $ecdsa.Dispose() }
$envelope = [ordered]@{
    payload = [Convert]::ToBase64String($bytes)
    keyId = $KeyId
    signature = [Convert]::ToBase64String($signature)
}
$target = Join-Path $directory 'releases-catalog.json'
[IO.File]::WriteAllText($target, ($envelope | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
Write-Host "Signed update catalog for $Version ($Channel) with key $KeyId; expires $($payload.expiresAt)."
