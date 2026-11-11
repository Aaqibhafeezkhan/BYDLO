#Requires -Version 7
# Issue #411: generate the P-256 key pair that signs the update catalog.
# Run this locally, never in CI. It writes only outside the repository and prints the
# public SPKI base64 that belongs in Bydlo.Core/Updates/UpdateTrustRoots.cs.
param(
    [string]$KeyId = 'bydlo-update-2026-01',
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'bydlo-update-key')
)
$ErrorActionPreference = 'Stop'
$ecdsa = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
try {
    $spki = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
    $privateKey = $ecdsa.ExportPkcs8PrivateKeyPem()
} finally { $ecdsa.Dispose() }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$keyFile = Join-Path $OutputDirectory 'bydlo-update-signing-key.pem'
[IO.File]::WriteAllText($keyFile, $privateKey, [Text.UTF8Encoding]::new($false))
Write-Host "Key id: $KeyId"
Write-Host 'Public SPKI base64 — paste it into UpdateTrustRoots.CurrentPublicKeySpki:'
Write-Host $spki
Write-Host "Private key written to $keyFile"
Write-Host 'Store it as the BYDLO_UPDATE_SIGNING_KEY repository secret, then delete the file. Never commit it.'
