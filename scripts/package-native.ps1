param(
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$RequireSigning
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$payload = (Resolve-Path -LiteralPath $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (($Rid.StartsWith('win-') -and !$IsWindows) -or ($Rid.StartsWith('linux-') -and !$IsLinux) -or ($Rid.StartsWith('osx-') -and !$IsMacOS)) {
    throw 'Native packages must be built on their target OS'
}
# Read-only checks happen before any packaging/signing. Never execute payload binaries.
& (Join-Path $PSScriptRoot 'verify-engine-bundle.ps1') -Rid $Rid -PublishDirectory $payload
$version = ([xml](Get-Content (Join-Path $env:BYDLO_ROOT 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
$toolDir = Join-Path $env:BYDLO_ROOT '.tmp/tools'
$vpk = Join-Path $toolDir $(if ($IsWindows) { 'vpk.exe' } else { 'vpk' })
if (!(Test-Path -LiteralPath $vpk)) {
    dotnet tool install vpk --version 1.2.161 --tool-path $toolDir
    if ($LASTEXITCODE -ne 0) { throw 'Velopack tool installation failed' }
}
$main = if ($IsWindows) { 'BYDLO.exe' } else { 'BYDLO' }
$icon = Join-Path $env:BYDLO_ROOT 'src/Bydlo.App/Assets/bydlo-256.png'
$arguments = @('--skip-updates','--yes','pack','--packId','BydloDesktop','--packTitle','BYDLO',
    '--packAuthors','BYDLO contributors','--packVersion',"$version",'--packDir',$payload,
    '--mainExe',$main,'--runtime',$Rid,'--channel',$Rid,'--outputDir',$output,
    '--delta','None','--noDefaultExclude','--exclude','^$')
if ($IsWindows) {
    $icon = Join-Path $env:BYDLO_ROOT 'src/Bydlo.App/Assets/bydlo.ico'
    if ($env:BYDLO_AZURE_SIGN_METADATA) {
        $arguments += @('--azureTrustedSignFile',$env:BYDLO_AZURE_SIGN_METADATA,'--signExclude','[\\/]engines[\\/]')
    } elseif ($RequireSigning) { throw 'Missing Azure signing metadata / OIDC credentials; refusing unsigned release' }
} elseif ($IsLinux) {
    $arguments += @('--categories','Network;Utility;')
} else {
    # iconutil requires the standard iconset names, including Retina sizes.
    $iconset = Join-Path $env:BYDLO_ROOT '.tmp/bydlo.iconset'
    New-Item -ItemType Directory -Force $iconset | Out-Null
    foreach ($size in @(16,32,128,256,512)) {
        foreach ($scale in @(1,2)) {
            $pixels = $size * $scale
            $suffix = if ($scale -eq 2) { '@2x' } else { '' }
            & sips -z $pixels $pixels $icon --out (Join-Path $iconset "icon_${size}x${size}$suffix.png") | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Icon conversion failed' }
        }
    }
    $icon = Join-Path $env:BYDLO_ROOT '.tmp/bydlo.icns'
    & iconutil -c icns $iconset -o $icon
    if ($LASTEXITCODE -ne 0) { throw 'iconutil failed' }
    $arguments += @('--bundleId','dev.bydlo.desktop')
    if ($env:BYDLO_MAC_APP_IDENTITY -and $env:BYDLO_MAC_INSTALLER_IDENTITY -and $env:BYDLO_MAC_NOTARY_PROFILE) {
        # Sign nested Mach-O files first, then update the payload manifest. Velopack signs
        # its own updater and the outer bundle without changing these bytes again.
        foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File) {
            $stream = [IO.File]::OpenRead($file.FullName)
            try { $bytes = [byte[]]::new(4); $count = $stream.Read($bytes,0,4) } finally { $stream.Dispose() }
            if ($count -eq 4 -and [BitConverter]::ToUInt32($bytes,0) -in @(4277009103,3199925962)) {
                & codesign --force --options runtime --timestamp --sign $env:BYDLO_MAC_APP_IDENTITY --entitlements (Join-Path $PSScriptRoot 'macos-entitlements.plist') $file.FullName
                if ($LASTEXITCODE -ne 0) { throw "codesign failed: $($file.Name)" }
            }
        }
        $manifest = Get-ChildItem (Join-Path $payload 'engines') -Recurse -File | Sort-Object FullName | ForEach-Object {
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetRelativePath($payload,$_.FullName).Replace('\','/')
        }
        [IO.File]::WriteAllLines((Join-Path $payload 'ENGINES-SHA256SUMS.txt'),[string[]]$manifest)
        & (Join-Path $PSScriptRoot 'verify-engine-bundle.ps1') -Rid $Rid -PublishDirectory $payload
        $arguments += @('--signAppIdentity',$env:BYDLO_MAC_APP_IDENTITY,'--signInstallIdentity',$env:BYDLO_MAC_INSTALLER_IDENTITY,
            '--notaryProfile',$env:BYDLO_MAC_NOTARY_PROFILE,'--signDisableDeep')
        if ($env:BYDLO_MAC_KEYCHAIN) { $arguments += @('--keychain',$env:BYDLO_MAC_KEYCHAIN) }
    } elseif ($RequireSigning) { throw 'Missing Apple identities/notary profile; refusing unsigned release' }
}
$arguments += @('--icon',$icon)
& $vpk @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native packaging failed' }
# Read nupkg as ZIP; verify actual packaged bytes, not only the staging directory.
$package = @(Get-ChildItem -LiteralPath $output -Filter '*-full.nupkg')
if ($package.Count -ne 1) { throw 'Expected one full update package' }
$verification = Join-Path $env:BYDLO_ROOT ('.tmp/native-verify-' + [Guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($package[0].FullName,$verification)
$packagedRoot = Join-Path $verification 'lib/app'
if ($IsLinux) {
    # Linux update packages contain an AppImage, not loose application files.
    # Locate its validated SquashFS superblock and extract with a read-only tool.
    # Never execute AppImage's --appimage-extract entry point.
    $image = Join-Path $packagedRoot 'BydloDesktop.AppImage'
    $bytes = [IO.File]::ReadAllBytes($image)
    $offset = -1
    for ($i = [Array]::IndexOf($bytes,[byte]0x68); $i -ge 0 -and $i + 96 -le $bytes.Length; $i = [Array]::IndexOf($bytes,[byte]0x68,$i + 1)) {
        if ($bytes[$i+1] -eq 0x73 -and $bytes[$i+2] -eq 0x71 -and $bytes[$i+3] -eq 0x73 -and
            [BitConverter]::ToUInt16($bytes,$i+28) -eq 4 -and [BitConverter]::ToUInt16($bytes,$i+30) -eq 0) {
            $used = [BitConverter]::ToUInt64($bytes,$i+40)
            if ($used -ge 96 -and $used -le ($bytes.Length - $i)) { $offset = $i; break }
        }
    }
    if ($offset -lt 0) { throw 'Missing valid AppImage SquashFS superblock' }
    $bytes = $null
    if (!(Get-Command unsquashfs -ErrorAction SilentlyContinue)) { throw 'Install squashfs-tools for read-only AppImage inspection' }
    $packagedRoot = Join-Path $verification 'appdir'
    & unsquashfs -no-progress -d $packagedRoot -o $offset $image
    if ($LASTEXITCODE -ne 0) { throw 'Read-only AppImage extraction failed' }
}
$packagedPayload = if ($IsLinux) { Join-Path $packagedRoot 'usr/bin' } elseif ($IsMacOS) { Join-Path $packagedRoot 'Contents/MacOS' } else { $packagedRoot }
& (Join-Path $PSScriptRoot 'verify-engine-bundle.ps1') -Rid $Rid -PublishDirectory $packagedPayload
if ($IsLinux) {
    if (!(Test-Path (Join-Path $packagedRoot 'BydloDesktop.desktop')) -or !(Test-Path (Join-Path $packagedRoot 'BydloDesktop.png'))) {
        throw 'Missing Linux desktop entry or icon'
    }
}
if ($IsMacOS -and !(Test-Path (Join-Path $packagedRoot 'Contents/Info.plist'))) { throw 'Missing Info.plist' }
if (!(Test-Path (Join-Path $output "releases.$Rid.json"))) { throw 'Missing architecture-specific update feed' }
$extension = if ($IsWindows) { '*Setup.exe' } elseif ($IsLinux) { '*.AppImage' } else { '*.pkg' }
if (!(Get-ChildItem -LiteralPath $output -Filter $extension)) { throw "Missing native package: $extension" }
if ($IsWindows -and $RequireSigning) {
    foreach ($file in @((Get-ChildItem -LiteralPath $output -Filter '*Setup.exe').FullName,
        (Join-Path $packagedPayload 'BYDLO.exe'), (Join-Path $packagedPayload 'helper/Bydlo.Helper.exe'))) {
        if ((Get-AuthenticodeSignature -LiteralPath $file).Status -ne 'Valid') { throw "Invalid package signature: $file" }
    }
}
Write-Host "Verified native package and feed for $Rid without executing payload binaries."
