#Requires -Version 7
# Issue #411: prove that a catalog signed by sign-update-catalog.ps1 verifies in Bydlo.Core.
# Synthetic packages and a throwaway key only; nothing is published or executed.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ".tmp/update-catalog-interop-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
$version = '9.8.7'
foreach ($rid in 'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-arm64') {
    $path = Join-Path $fixture "BydloDesktop-$version-$rid-full.nupkg"
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($archive.CreateEntry('BydloDesktop.nuspec').Open())
        try { $writer.Write("<package><metadata><id>BydloDesktop</id><version>$version</version></metadata></package>") }
        finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
}
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
try {
    $pem = $key.ExportPkcs8PrivateKeyPem()
    $spki = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
} finally { $key.Dispose() }
& "$PSScriptRoot/sign-update-catalog.ps1" -ReleaseDirectory $fixture -Version $version -KeyId 'interop-test' -PrivateKeyPem $pem
[IO.File]::WriteAllText((Join-Path $fixture 'public-key.txt'), $spki)
$env:BYDLO_UPDATE_CATALOG_INTEROP_DIR = $fixture
try {
    dotnet test (Join-Path $root 'tests/Bydlo.Tests') -c $Configuration --no-build --filter 'FullyQualifiedName~UpdateCatalogInteropTests'
    if ($LASTEXITCODE -ne 0) { throw 'Catalog signed by the release script did not verify in Bydlo.Core.' }
} finally {
    Remove-Item Env:BYDLO_UPDATE_CATALOG_INTEROP_DIR
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
