$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
. (Join-Path $PSScriptRoot 'native-build-cache.ps1')
$stage = Join-Path $env:TMP ('native-cache-test-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $stage 'source'
$cache = Join-Path $stage 'cache'
New-Item -ItemType Directory -Force -Path $source | Out-Null
$files = @('openvpn', 'COPYING', 'openssl.cnf')
foreach ($name in $files) { Set-Content -LiteralPath (Join-Path $source $name) -Value ('fake data: ' + $name) }
if (Test-NativeBuildCache $cache 'sdk-and-inputs-v1' $files) { throw 'Missing cache must miss' }
Save-NativeBuildCache $cache $source 'sdk-and-inputs-v1' $files
if (!(Test-NativeBuildCache $cache 'sdk-and-inputs-v1' $files)) { throw 'Valid cache must hit' }
if (Test-NativeBuildCache $cache 'sdk-and-inputs-v2' $files) { throw 'Changed SDK or inputs must miss' }
Set-Content -LiteralPath (Join-Path $cache 'openvpn') -Value 'corrupt dummy binary'
if (Test-NativeBuildCache $cache 'sdk-and-inputs-v1' $files) { throw 'Corrupted file must miss' }
Save-NativeBuildCache $cache $source 'sdk-and-inputs-v1' $files
Rename-Item -LiteralPath (Join-Path $cache 'COPYING') -NewName 'missing-copying'
if (Test-NativeBuildCache $cache 'sdk-and-inputs-v1' $files) { throw 'Incomplete cache must miss' }
Set-Content -LiteralPath (Join-Path $cache 'cache-manifest.json') -Value '{'
if (Test-NativeBuildCache $cache 'sdk-and-inputs-v1' $files) { throw 'Invalid manifest must miss' }
Write-Host 'Native build cache checks passed: valid, missing, changed inputs, corruption, incomplete and invalid manifest.'
