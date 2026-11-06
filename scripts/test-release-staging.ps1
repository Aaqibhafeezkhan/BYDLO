# Issues 408/409: synthetic bytes only; no package/installer is executed.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$fixture = Join-Path $env:BYDLO_ROOT ('.tmp/release-test-' + [Guid]::NewGuid().ToString('N'))
$inputPath = Join-Path $fixture 'nested'
$version = '1.2.3'
$rids = @('win-x64','win-arm64','linux-x64','linux-arm64','osx-arm64')
function Put([string]$relative, [string]$content = 'synthetic bytes') {
    $path = Join-Path $inputPath $relative
    New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
    [IO.File]::WriteAllText($path, $content)
}
foreach ($rid in $rids) {
    $name = "BydloDesktop-$version-$rid-full.nupkg"
    Put "native/$rid/$name"
    $package = Get-Item (Join-Path $inputPath "native/$rid/$name")
    $feed = @{ Assets = @(@{ PackageId = 'BydloDesktop'; Version = $version; Type = 'Full'; FileName = $name
        Size = $package.Length; SHA256 = (Get-FileHash $package.FullName -Algorithm SHA256).Hash }) }
    Put "native/$rid/releases.$rid.json" ($feed | ConvertTo-Json -Depth 5 -Compress)
    if ($rid.StartsWith('win-')) {
        Put "native/$rid/BydloDesktop-$rid-Setup.exe"
        Put "native/$rid/BydloDesktop-$rid-Portable.zip"
    } elseif ($rid.StartsWith('linux-')) {
        Put "native/$rid/BydloDesktop-$rid.AppImage"
        $arch = if ($rid -eq 'linux-x64') { 'amd64' } else { 'arm64' }
        Put "linux/bydlo_${version}_${arch}.deb"
    } else { Put "native/$rid/BydloDesktop-$rid-Setup.pkg" }
    # Old assets, .app ZIP and reports must never leak into the flat release.
    Put "BYDLO-$rid.zip"
}
Put 'native/osx-arm64/BydloDesktop-osx-arm64-Portable.zip'
Put 'linux/BYDLO-1.2.3-x86_64.AppImage'
Put 'test-results/test.trx'
$stage = Join-Path $fixture 'staging'
& "$PSScriptRoot/stage-release.ps1" -InputDirectory $inputPath -OutputDirectory $stage -Version $version
$download = Join-Path $fixture 'download'
Copy-Item $stage $download -Recurse
& "$PSScriptRoot/stage-release.ps1" -InputDirectory $download -OutputDirectory $download -Version $version -VerifyOnly
function AssertDeterministicManifest([string]$directory) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $directory 'SHA256SUMS.txt'))
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) { throw 'SHA256SUMS must not have BOM' }
    if ($bytes -contains 13) { throw 'SHA256SUMS must use LF only' }
    $script:checks++
}
$script:checks = 2
AssertDeterministicManifest $stage
AssertDeterministicManifest $download
# The issue repro is the stock `sha256sum -c` on a flat download folder.
# PowerShell VerifyOnly already bans slashes via regex; also exercise the exact
# GNU command on synthetic bytes when available (Linux CI always has it).
$sha256sum = Get-Command sha256sum -ErrorAction SilentlyContinue
if ($sha256sum) {
    Push-Location -LiteralPath $stage
    try { & sha256sum -c SHA256SUMS.txt } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'GNU sha256sum failed on staged flat set' }
    Push-Location -LiteralPath $download
    try { & sha256sum -c SHA256SUMS.txt } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'GNU sha256sum failed on downloaded flat set' }
    $script:checks += 2
} else {
    Write-Host 'sha256sum not available; skipped GNU verification.'
}
function Reject([scriptblock]$action, [string]$message) {
    try { & $action } catch {
        if ($_.Exception.Message -notlike "*$message*") { throw "Unexpected failure: $_" }
        $script:checks++
        return
    }
    throw "Expected rejection: $message"
}
$output = Join-Path $fixture 'rejected'
$name = 'BydloDesktop-win-x64-Setup.exe'
$source = Join-Path $inputPath "native/win-x64/$name"
Put "duplicate/$name"
Reject { & "$PSScriptRoot/stage-release.ps1" $inputPath $output $version } 'Expected exactly one asset'
Remove-Item -LiteralPath (Join-Path $inputPath "duplicate/$name")
Remove-Item -LiteralPath $source
Reject { & "$PSScriptRoot/stage-release.ps1" $inputPath $output $version } 'Expected exactly one asset'
function Verify { & "$PSScriptRoot/stage-release.ps1" $download $download $version -VerifyOnly }
Put 'unused' # Does not affect verification of downloaded assets.
$manifest = Join-Path $download 'SHA256SUMS.txt'
$original = [IO.File]::ReadAllText($manifest).Replace("`r", '')
[IO.File]::WriteAllText($manifest, $original.Replace($name, "native/$name"))
Reject { Verify } 'Invalid checksum entry'
[IO.File]::WriteAllText($manifest, $original + ($original -split "`n")[0] + "`n")
Reject { Verify } 'duplicate checksum'
[IO.File]::WriteAllLines($manifest, [string[]]@(($original.Trim() -split "`n") | Select-Object -Skip 1))
Reject { Verify } 'Missing checksums'
[IO.File]::WriteAllText($manifest, $original)
$file = Join-Path $download $name
[IO.File]::AppendAllText($file, 'tampered')
Reject { Verify } 'Checksum mismatch'
[IO.File]::WriteAllText($file, 'synthetic bytes')
[IO.File]::WriteAllText((Join-Path $download 'test.trx'), 'report')
Reject { Verify } 'Unexpected release asset count'
Remove-Item -LiteralPath (Join-Path $download 'test.trx')
# Even an internally consistent checksum manifest cannot make a mismatched feed valid.
$feedFile = Join-Path $download 'releases.linux-arm64.json'
$feed = Get-Content $feedFile -Raw | ConvertFrom-Json
$feed.Assets[0].FileName = 'wrong-rid-full.nupkg'
$feed | ConvertTo-Json -Depth 5 | Set-Content $feedFile
$hash = (Get-FileHash $feedFile -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($manifest, [regex]::Replace($original, '[0-9a-f]{64}(  releases\.linux-arm64\.json)', $hash + '$1'))
Reject { Verify } 'Invalid full update feed'
Write-Host "Release staging regression passed ($script:checks checks)."
