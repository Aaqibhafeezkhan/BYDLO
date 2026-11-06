# Build/verify the exact flat set published to Releases. Never execute package contents.
param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$rids = @('win-x64','win-arm64','linux-x64','linux-arm64','osx-arm64')
$expected = [Collections.Generic.List[string]]::new()
foreach ($rid in $rids) {
    $expected.Add("BydloDesktop-$Version-$rid-full.nupkg")
    $expected.Add("releases.$rid.json")
    if ($rid.StartsWith('win-')) {
        $expected.Add("BydloDesktop-$rid-Setup.exe")
        $expected.Add("BydloDesktop-$rid-Portable.zip")
    } elseif ($rid.StartsWith('linux-')) {
        $expected.Add("BydloDesktop-$rid.AppImage")
        $arch = if ($rid -eq 'linux-x64') { 'amd64' } else { 'arm64' }
        $expected.Add("bydlo_${Version}_${arch}.deb")
    } else {
        $expected.Add("BydloDesktop-$rid-Setup.pkg")
    }
}
$inputPath = (Resolve-Path -LiteralPath $InputDirectory).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (!$VerifyOnly) {
    if (Test-Path -LiteralPath $outputPath) { throw 'Output staging directory must not exist' }
    # Do not merge artifacts before checking: a merge silently overwrites duplicate basenames.
    $files = @(Get-ChildItem -LiteralPath $inputPath -Recurse -File)
    $selected = foreach ($name in $expected) {
        $matches = @($files | Where-Object { $_.Name -ieq $name })
        if ($matches.Count -ne 1 -or $matches[0].Name -cne $name) { throw "Expected exactly one asset: $name (found $($matches.Count))" }
        if ($matches[0].Length -eq 0 -or $matches[0].LinkType) { throw "Invalid asset: $name" }
        $matches[0]
    }
    New-Item -ItemType Directory -Path $outputPath | Out-Null
    foreach ($file in $selected) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $outputPath $file.Name) }
    $lines = foreach ($name in ($expected | Sort-Object -CaseSensitive)) {
        (Get-FileHash -LiteralPath (Join-Path $outputPath $name) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name
    }
    [IO.File]::WriteAllLines((Join-Path $outputPath 'SHA256SUMS.txt'), [string[]]$lines, [Text.UTF8Encoding]::new($false))
}
$actual = @(Get-ChildItem -LiteralPath $outputPath -Force)
if ($actual.Count -ne $expected.Count + 1) { throw 'Unexpected release asset count' }
foreach ($file in $actual) {
    if ($file.PSIsContainer -or $file.LinkType -or ($file.Name -cne 'SHA256SUMS.txt' -and $file.Name -cnotin $expected)) {
        throw "Unexpected release asset: $($file.Name)"
    }
}
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in [IO.File]::ReadAllLines((Join-Path $outputPath 'SHA256SUMS.txt'))) {
    if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9._-]+)$') { throw "Invalid checksum entry: $line" }
    $hash, $name = $Matches[1], $Matches[2]
    if ($name -cnotin $expected -or !$seen.Add($name)) { throw "Unexpected/duplicate checksum: $name" }
    if ((Get-FileHash -LiteralPath (Join-Path $outputPath $name) -Algorithm SHA256).Hash -ine $hash) { throw "Checksum mismatch: $name" }
}
if ($seen.Count -ne $expected.Count) { throw 'Missing checksums' }
# Check feeds against the actual full packages, so all five RID channels remain usable.
foreach ($rid in $rids) {
    $feed = Get-Content -LiteralPath (Join-Path $outputPath "releases.$rid.json") -Raw | ConvertFrom-Json
    $assets = @($feed.Assets)
    $name = "BydloDesktop-$Version-$rid-full.nupkg"
    if ($assets.Count -ne 1 -or $assets[0].FileName -cne $name -or $assets[0].Type -cne 'Full' -or
        $assets[0].PackageId -cne 'BydloDesktop' -or $assets[0].Version -cne $Version) { throw "Invalid full update feed: $rid" }
    $package = Get-Item -LiteralPath (Join-Path $outputPath $name)
    if ($assets[0].Size -ne $package.Length -or
        $assets[0].SHA256 -ine (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash) { throw "Feed package integrity mismatch: $rid" }
}
Write-Host "Verified $($actual.Count) flat release assets for all five RIDs."
