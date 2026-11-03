param(
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [switch]$PrebuiltOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($PublishDirectory)
$lock = Get-Content (Join-Path $repo 'engines/engines.lock.json') -Raw | ConvertFrom-Json -AsHashtable
$platform = $Rid.Replace('win-', 'windows-').Replace('osx-', 'macos-')
$expectedArm = $Rid.EndsWith('arm64')
if ($PrebuiltOnly -and $Rid -ne 'osx-arm64') { throw 'PrebuiltOnly is reserved for the macOS cross-build' }
function Read-BigEndianUInt32([IO.BinaryReader]$Reader) {
    $bytes = $Reader.ReadBytes(4)
    if ($bytes.Length -ne 4) { throw 'Truncated universal header' }
    [Array]::Reverse($bytes)
    return [BitConverter]::ToUInt32($bytes, 0)
}
function Assert-ExecutableArchitecture([string]$path) {
    # Read executable headers; never run --version, config checks or any bundled engine.
    $stream = [IO.File]::OpenRead($path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        $magic = $reader.ReadUInt32()
        if (($Rid.StartsWith('win-') -and ($magic -band 0xffff) -ne 0x5a4d) -or
            ($Rid.StartsWith('linux-') -and $magic -ne 0x464c457f) -or
            ($Rid.StartsWith('osx-') -and $magic -notin @(4277009103, 3199925962))) {
            throw "Wrong executable format for ${Rid}: $path"
        }
        if (($magic -band 0xffff) -eq 0x5a4d) {
            $stream.Position = 0x3c
            $stream.Position = $reader.ReadUInt32()
            if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid PE signature' }
            $machine = $reader.ReadUInt16()
            $expected = if ($expectedArm) { 0xaa64 } else { 0x8664 }
            if ($machine -ne $expected) { throw "Wrong PE architecture: $path" }
        } elseif ($magic -eq 0x464c457f) {
            $stream.Position = 18
            $expected = if ($expectedArm) { 183 } else { 62 }
            if ($reader.ReadUInt16() -ne $expected) { throw "Wrong ELF architecture: $path" }
        } elseif ($magic -eq 4277009103) {
            $expected = 0x100000c # macOS packages target Apple Silicon only.
            if ($reader.ReadUInt32() -ne $expected) { throw "Wrong Mach-O architecture: $path" }
        } elseif ($magic -eq 3199925962) {
            # FAT_MAGIC is big endian. Require an actual, bounded ARM64 Mach-O slice.
            $count = Read-BigEndianUInt32 $reader
            if ($count -lt 1 -or $count -gt 32) { throw 'Invalid universal architecture count' }
            $armOffset = $null
            for ($i = 0; $i -lt $count; $i++) {
                $cpu = Read-BigEndianUInt32 $reader
                $null = Read-BigEndianUInt32 $reader # subtype
                $offset = Read-BigEndianUInt32 $reader
                $size = Read-BigEndianUInt32 $reader
                $null = Read-BigEndianUInt32 $reader # alignment
                if ($offset -lt (8 + 20 * $count) -or $size -lt 32 -or ([long]$offset + $size) -gt $stream.Length) {
                    throw 'Invalid universal slice bounds'
                }
                if ($cpu -eq 0x100000c) { $armOffset = $offset }
            }
            if ($null -eq $armOffset) { throw "Missing Apple Silicon slice: $path" }
            $stream.Position = $armOffset
            if ($reader.ReadUInt32() -ne 4277009103 -or $reader.ReadUInt32() -ne 0x100000c) {
                throw "Invalid Apple Silicon slice: $path"
            }
        } else {
            throw "Unknown executable format: $path"
        }
    } finally { $stream.Dispose() }
}
$suffix = if ($Rid.StartsWith('win-')) { '.exe' } else { '' }
foreach ($relative in @("BYDLO$suffix", "helper/Bydlo.Helper$suffix")) {
    $path = Join-Path $output $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing application executable: $relative" }
    Assert-ExecutableArchitecture $path
}
foreach ($entry in $lock.engines) {
    if ($PrebuiltOnly -and $entry.id -in @('amneziawg', 'openvpn')) { continue }
    $relative = if ($entry.assets.ContainsKey($platform)) { $entry.assets[$platform].executable } elseif ($entry.bundledExecutables -and $entry.bundledExecutables.ContainsKey($platform)) { $entry.bundledExecutables[$platform] } else { $null }
    if (!$relative) { continue }
    $path = Join-Path $output "engines/$($entry.id)/$($entry.version)/$relative"
    if (!(Test-Path -LiteralPath $path)) { throw "Missing bundled engine: $path" }
    Assert-ExecutableArchitecture $path
}
if ($Rid.StartsWith('win-')) {
    $version = ($lock.engines | Where-Object id -eq 'openvpn').version
    $openvpn = Join-Path $output "engines/openvpn/$version"
    foreach ($os in @('Win10','Win11')) {
        foreach ($extension in @('inf','cat','sys')) {
            if (!(Test-Path (Join-Path $openvpn "dco/$os/ovpn-dco.$extension"))) { throw "Incomplete DCO $os package" }
        }
    }
    if (Test-Path (Join-Path $openvpn 'openvpn-gui.exe')) { throw 'OpenVPN GUI must not ship in the engine directory' }
    if (Get-ChildItem $openvpn -Filter 'openvpnserv*.exe') { throw 'OpenVPN services must not ship' }
}
foreach ($license in Get-ChildItem (Join-Path $repo 'engines/licenses') -File) {
    if (!(Test-Path -LiteralPath (Join-Path $output "engines/licenses/$($license.Name)") -PathType Leaf)) {
        throw "Missing bundled license: $($license.Name)"
    }
}
$requiredSources = @('sing-box-source.tar.gz', 'xray-source.tar.gz')
if (!$PrebuiltOnly) {
    $inputs = Get-Content (Join-Path $repo 'engines/bundle-inputs.lock.json') -Raw | ConvertFrom-Json -AsHashtable
    $requiredSources += @('openssl-source.tar.gz', 'amneziawg-with-dependencies.tar.gz',
        [IO.Path]::GetFileName(([uri]$inputs['openvpn-source'].url).AbsolutePath),
        'build-openvpn.sh', 'build-bundled-engines.ps1', 'openvpn-cache.ps1', 'native-build-cache.ps1')
}
foreach ($name in $requiredSources) {
    if (!(Test-Path -LiteralPath (Join-Path $output "engines/sources/$name") -PathType Leaf)) {
        throw "Missing corresponding source: $name"
    }
}
$manifestPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in Get-Content (Join-Path $output 'ENGINES-SHA256SUMS.txt')) {
    $parts = $line -split '  ', 2
    if ($parts.Count -ne 2) { throw 'Invalid manifest line' }
    $path = [IO.Path]::GetFullPath((Join-Path $output $parts[1]))
    if (!$path.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw 'Manifest escapes output directory' }
    if ($parts[0] -cnotmatch '^[0-9a-f]{64}$' -or !$manifestPaths.Add($path)) { throw 'Invalid or duplicate manifest entry' }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $parts[0]) { throw "Bundled hash mismatch: $path" }
}
foreach ($file in Get-ChildItem (Join-Path $output 'engines') -Recurse -File) {
    if (!$manifestPaths.Contains($file.FullName)) { throw "Unhashed bundled file: $($file.FullName)" }
}
Write-Host "Verified $Rid bundle without executing engines."
