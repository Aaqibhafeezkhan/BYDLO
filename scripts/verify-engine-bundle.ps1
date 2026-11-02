param(
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($PublishDirectory)
$lock = Get-Content (Join-Path $repo 'engines/engines.lock.json') -Raw | ConvertFrom-Json -AsHashtable
$platform = $Rid.Replace('win-', 'windows-').Replace('osx-', 'macos-')
$expectedArm = $Rid.EndsWith('arm64')
foreach ($entry in $lock.engines) {
    $relative = if ($entry.assets.ContainsKey($platform)) { $entry.assets[$platform].executable } elseif ($entry.bundledExecutables -and $entry.bundledExecutables.ContainsKey($platform)) { $entry.bundledExecutables[$platform] } else { $null }
    if (!$relative) { continue }
    $path = Join-Path $output "engines/$($entry.id)/$($entry.version)/$relative"
    if (!(Test-Path -LiteralPath $path)) { throw "Missing bundled engine: $path" }
    # Read executable headers; never run --version, config checks or any bundled engine.
    $stream = [IO.File]::OpenRead($path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        $magic = $reader.ReadUInt32()
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
        } elseif ($entry.id -ne 'zapret-native' -or $magic -ne 3199925962) {
            throw "Unknown executable format: $path"
        }
    } finally { $stream.Dispose() }
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
foreach ($line in Get-Content (Join-Path $output 'ENGINES-SHA256SUMS.txt')) {
    $parts = $line -split '  ', 2
    if ($parts.Count -ne 2) { throw 'Invalid manifest line' }
    $path = [IO.Path]::GetFullPath((Join-Path $output $parts[1]))
    if (!$path.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw 'Manifest escapes output directory' }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $parts[0]) { throw "Bundled hash mismatch: $path" }
}
Write-Host "Verified $Rid bundle without executing engines."
