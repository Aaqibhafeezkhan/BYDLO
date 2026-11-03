param([Parameter(Mandatory)][string]$CrossBundle, [Parameter(Mandatory)][string]$NativeBundle)
$ErrorActionPreference = 'Stop'
function Read-Payload([string]$Root) {
    $engineRoot = [IO.Path]::GetFullPath((Join-Path $Root 'engines'))
    $hashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem $engineRoot -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($engineRoot, $file.FullName).Replace('\', '/')
        # Source tar metadata depends on the archiver host; compare distributed payload bytes.
        if ($relative.StartsWith('sources/', [StringComparison]::Ordinal)) { continue }
        $hashes.Add($relative, (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)
    }
    if ($hashes.Count -eq 0) { throw 'Empty engine payload' }
    return ,$hashes
}
$cross = Read-Payload $CrossBundle
$native = Read-Payload $NativeBundle
if ($cross.Count -ne $native.Count) { throw 'Different engine payload file counts' }
foreach ($entry in $cross.GetEnumerator()) {
    if (!$native.ContainsKey($entry.Key) -or $native[$entry.Key] -cne $entry.Value) {
        throw "Engine payload differs: $($entry.Key)"
    }
}
Write-Host "Identical Linux/Windows engine payload: $($cross.Count) files, SHA-256 verified."
