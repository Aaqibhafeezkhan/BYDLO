# Build package containers with synthetic engine headers; never run an engine or installer.
param(
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$ApplicationPublishDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$fixture = Join-Path $env:BYDLO_ROOT ('.tmp/native-fixture-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $fixture 'payload'
New-Item -ItemType Directory -Force $payload | Out-Null
Copy-Item (Join-Path $ApplicationPublishDirectory '*') $payload -Recurse
$lock = Get-Content (Join-Path $env:BYDLO_ROOT 'engines/engines.lock.json') -Raw | ConvertFrom-Json -AsHashtable
$inputs = Get-Content (Join-Path $env:BYDLO_ROOT 'engines/bundle-inputs.lock.json') -Raw | ConvertFrom-Json -AsHashtable
$platform = $Rid.Replace('win-','windows-').Replace('osx-','macos-')
$arm = $Rid.EndsWith('arm64')
$header = [byte[]]::new(128)
if ($Rid.StartsWith('win-')) {
    $header[0] = 0x4d; $header[1] = 0x5a
    [BitConverter]::GetBytes([uint32]64).CopyTo($header,0x3c)
    [BitConverter]::GetBytes([uint32]0x4550).CopyTo($header,64)
    [BitConverter]::GetBytes([uint16]$(if ($arm) { 0xaa64 } else { 0x8664 })).CopyTo($header,68)
} elseif ($Rid.StartsWith('linux-')) {
    ([byte[]]@(0x7f,0x45,0x4c,0x46)).CopyTo($header,0)
    [BitConverter]::GetBytes([uint16]$(if ($arm) { 183 } else { 62 })).CopyTo($header,18)
} else {
    [BitConverter]::GetBytes([uint32]4277009103).CopyTo($header,0)
    [BitConverter]::GetBytes([uint32]0x100000c).CopyTo($header,4)
}
function Put([string]$relative,[byte[]]$bytes) {
    $path = Join-Path $payload $relative
    New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
    [IO.File]::WriteAllBytes($path,$bytes)
}
$suffix = if ($Rid.StartsWith('win-')) { '.exe' } else { '' }
Put "helper/Bydlo.Helper$suffix" $header
foreach ($entry in $lock.engines) {
    $relative = if ($entry.assets -and $entry.assets.ContainsKey($platform)) { $entry.assets[$platform].executable }
        elseif ($entry.bundledExecutables -and $entry.bundledExecutables.ContainsKey($platform)) { $entry.bundledExecutables[$platform] } else { $null }
    if ($relative) { Put "engines/$($entry.id)/$($entry.version)/$relative" $header }
}
if ($Rid.StartsWith('win-')) {
    $openvpn = ($lock.engines | Where-Object id -eq 'openvpn').version
    foreach ($os in @('Win10','Win11')) {
        foreach ($extension in @('inf','cat','sys')) { Put "engines/openvpn/$openvpn/dco/$os/ovpn-dco.$extension" ([byte[]]@(1)) }
    }
}
foreach ($license in Get-ChildItem (Join-Path $env:BYDLO_ROOT 'engines/licenses') -File) {
    Put "engines/licenses/$($license.Name)" ([IO.File]::ReadAllBytes($license.FullName))
}
$sources = @('sing-box-source.tar.gz','xray-source.tar.gz','openssl-source.tar.gz','amneziawg-with-dependencies.tar.gz',
    [IO.Path]::GetFileName(([uri]$inputs['openvpn-source'].url).AbsolutePath),
    'build-openvpn.sh','build-bundled-engines.ps1','openvpn-cache.ps1','native-build-cache.ps1','extract-openvpn-msi.ps1')
foreach ($source in $sources) { Put "engines/sources/$source" ([byte[]]@(1)) }
Get-ChildItem (Join-Path $payload 'engines') -Recurse -File | Sort-Object FullName | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetRelativePath($payload,$_.FullName).Replace('\','/')
} | Set-Content (Join-Path $payload 'ENGINES-SHA256SUMS.txt') -Encoding utf8NoBOM
if (!$Rid.StartsWith('win-')) {
    foreach ($file in Get-ChildItem $payload -Recurse -File | Where-Object { $_.Name -in @('BYDLO','Bydlo.Helper','nfqws','tpws','sing-box','xray','bydlo-amneziawg','openvpn') }) {
        & chmod 755 $file.FullName
        if ($LASTEXITCODE -ne 0) { throw 'Fixture chmod failed' }
    }
}
& (Join-Path $PSScriptRoot 'package-native.ps1') -Rid $Rid -PublishDirectory $payload -OutputDirectory (Join-Path $fixture 'packages')
Write-Host "Native package smoke passed for $Rid (synthetic engine headers, no executables started)."
