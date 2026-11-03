# Read-only extraction. Never invoke msiexec or execute the extracted payload.
function Expand-OpenVpnMsi([string]$Msi, [string]$Destination, [string]$LessMsi = '') {
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    New-Item -ItemType Directory -Force $destinationPath | Out-Null
    if ($IsWindows) {
        if (!$LessMsi) { throw 'The pinned lessmsi executable is required on Windows' }
        & $LessMsi x $Msi ($destinationPath + [IO.Path]::DirectorySeparatorChar) | Out-Null
    } else {
        & msiextract -C $destinationPath $Msi | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { throw "Read-only MSI extraction failed: $LASTEXITCODE" }
    return ConvertTo-OpenVpnSourceTree $destinationPath
}

function ConvertTo-OpenVpnSourceTree([string]$destinationPath) {
    # msitools and lessmsi resolve MSI directory identifiers differently. Locate the
    # product by its payload, then expose one stable tree to the bundle builder.
    $executables = @(Get-ChildItem $destinationPath -Recurse -File -Filter 'openvpn.exe')
    if ($executables.Count -ne 1) { throw 'MSI must contain exactly one OpenVPN executable' }
    $product = Split-Path (Split-Path $executables[0].FullName -Parent) -Parent
    $normalized = Join-Path $destinationPath 'SourceDir/OpenVPN'
    if ($product -cne $normalized) {
        New-Item -ItemType Directory -Force $normalized | Out-Null
        Copy-Item (Join-Path $product '*') $normalized -Recurse -Force
    }
    $driver = Join-Path $normalized 'Common Files/ovpn-dco'
    if (!(Test-Path -LiteralPath $driver)) {
        $candidates = @(Get-ChildItem $destinationPath -Recurse -Directory -Filter 'ovpn-dco' |
            Where-Object { Test-Path (Join-Path $_.FullName 'Win10/ovpn-dco.inf') })
        if ($candidates.Count -ne 1) { throw 'MSI must contain exactly one DCO driver package' }
        New-Item -ItemType Directory -Force (Split-Path $driver -Parent) | Out-Null
        Copy-Item -LiteralPath $candidates[0].FullName -Destination $driver -Recurse -Force
    }
    return $normalized
}
