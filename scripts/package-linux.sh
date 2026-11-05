#!/usr/bin/env bash
# Builds distribution packages (.deb and/or AppImage) from a verified Linux publish directory.
# Usage: scripts/package-linux.sh <linux-x64|linux-arm64> <publish-dir> <output-dir> [deb] [appimage]
# Without format arguments both are built. Needs dpkg-deb (deb) and mksquashfs from squashfs-tools (AppImage).
# Never executes payload binaries: the AppImage is assembled as <pinned runtime> + <SquashFS image>.
set -euo pipefail

# shellcheck source=scripts/env.sh
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"

# AppImage type2-runtime release 20251108 (static, embeds libfuse3). Signatures verified against
# the upstream signing key 570C77ACEA40C0F1B758902CBF96CCA56490F695 before pinning.
APPIMAGE_RUNTIME_RELEASE=20251108
declare -A APPIMAGE_RUNTIME_SHA256=(
    [x86_64]=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d
    [aarch64]=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444
)

die() { echo "package-linux: $*" >&2; exit 1; }

[[ $# -ge 3 ]] || die "usage: $0 <linux-x64|linux-arm64> <publish-dir> <output-dir> [deb] [appimage]"
rid=$1
publish=$(cd "$2" && pwd) || die "publish directory not found: $2"
mkdir -p "$3"
output=$(cd "$3" && pwd)
shift 3
formats=("$@")
[[ ${#formats[@]} -gt 0 ]] || formats=(deb appimage)

case "$rid" in
    linux-x64) deb_arch=amd64; appimage_arch=x86_64 ;;
    linux-arm64) deb_arch=arm64; appimage_arch=aarch64 ;;
    *) die "unsupported RID: $rid" ;;
esac
[[ -f "$publish/BYDLO" ]] || die "$publish/BYDLO is missing; publish src/Bydlo.Desktop for $rid first (docs/BUILD.md)"

# BYDLO_VERSION lets release jobs package an existing tag; otherwise the checkout version is used.
version=${BYDLO_VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$BYDLO_ROOT/Directory.Build.props" | head -n 1)}
version=${version#v}
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+([.+~-][0-9A-Za-z.+~-]+)?$ ]] || die "invalid version: $version"

assets="$BYDLO_ROOT/packaging/linux"
icon="$BYDLO_ROOT/src/Bydlo.App/Assets/bydlo-256.png"
staging="$BYDLO_ROOT/.tmp/linux-packages/$rid"
rm -rf "$staging"
mkdir -p "$staging"

# ZIP extraction may drop exec bits; restore them for the executables the bundle ships.
copy_payload() {
    mkdir -p "$1"
    cp -a "$publish/." "$1/"
    find "$1" -type f \( -name BYDLO -o -name Bydlo.Helper -o -name nfqws -o -name tpws -o -name sing-box \
        -o -name xray -o -name bydlo-amneziawg -o -name openvpn \) -exec chmod 755 {} +
}

build_deb() {
    command -v dpkg-deb > /dev/null || die "dpkg-deb not found (install dpkg)"
    local root="$staging/deb"
    copy_payload "$root/opt/bydlo"
    install -Dm755 "$assets/bydlo.sh" "$root/usr/bin/bydlo"
    install -Dm644 "$assets/bydlo.desktop" "$root/usr/share/applications/bydlo.desktop"
    install -Dm644 "$icon" "$root/usr/share/icons/hicolor/256x256/apps/bydlo.png"
    install -Dm644 "$BYDLO_ROOT/LICENSE" "$root/usr/share/doc/bydlo/copyright"
    install -Dm644 "$BYDLO_ROOT/THIRD_PARTY_NOTICES.md" "$root/usr/share/doc/bydlo/THIRD_PARTY_NOTICES.md"
    mkdir -p "$root/DEBIAN"
    # Self-contained .NET still loads ICU and OpenSSL from the system; Avalonia needs X11 and fontconfig.
    cat > "$root/DEBIAN/control" <<EOF
Package: bydlo
Version: $version
Architecture: $deb_arch
Maintainer: BYDLO contributors <https://github.com/endercodezz/BYDLO>
Installed-Size: $(du -sk --exclude=DEBIAN "$root" | cut -f1)
Depends: libc6 (>= 2.27), libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libfreetype6, libx11-6, libice6, libsm6, libxcursor1, libxext6, libxi6, libxrandr2, libicu78 | libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66 | libicu63 | libicu60, libssl3t64 | libssl3 | libssl1.1
Recommends: libsecret-1-0, libnotify-bin
Suggests: wireguard-tools
Section: net
Priority: optional
Homepage: https://github.com/endercodezz/BYDLO
Description: Bypass Your Digital Limitations Online
 One app for every protocol: sing-box, Xray, WireGuard, AmneziaWG, OpenVPN,
 Zapret and TG WS Proxy with pinned, checksum-verified engines.
EOF
    local deb="$output/bydlo_${version}_${deb_arch}.deb"
    dpkg-deb --root-owner-group -Zxz --build "$root" "$deb" > /dev/null
    echo "$deb"
}

appimage_runtime() {
    local expected=${APPIMAGE_RUNTIME_SHA256[$appimage_arch]}
    local cache="$BYDLO_ROOT/.tmp/appimage-runtime/$APPIMAGE_RUNTIME_RELEASE"
    local file="$cache/runtime-$appimage_arch"
    mkdir -p "$cache"
    if [[ ! -f "$file" ]] || ! echo "$expected  $file" | sha256sum -c --status; then
        curl -fsSL --proto '=https' -o "$file.part" \
            "https://github.com/AppImage/type2-runtime/releases/download/$APPIMAGE_RUNTIME_RELEASE/runtime-$appimage_arch"
        echo "$expected  $file.part" | sha256sum -c --status || { rm -f "$file.part"; die "AppImage runtime SHA-256 mismatch"; }
        mv "$file.part" "$file"
    fi
    echo "$file"
}

build_appimage() {
    command -v mksquashfs > /dev/null || die "mksquashfs not found (install squashfs-tools)"
    local appdir="$staging/BYDLO.AppDir"
    copy_payload "$appdir/usr/lib/bydlo"
    install -Dm755 "$assets/AppRun" "$appdir/AppRun"
    install -Dm644 "$assets/bydlo.desktop" "$appdir/bydlo.desktop"
    install -Dm644 "$assets/bydlo.desktop" "$appdir/usr/share/applications/bydlo.desktop"
    install -Dm644 "$icon" "$appdir/bydlo.png"
    install -Dm644 "$icon" "$appdir/usr/share/icons/hicolor/256x256/apps/bydlo.png"
    ln -s bydlo.png "$appdir/.DirIcon"
    local runtime image="$staging/BYDLO.squashfs"
    runtime=$(appimage_runtime)
    mksquashfs "$appdir" "$image" -root-owned -noappend -no-xattrs -comp zstd -quiet > /dev/null
    local appimage="$output/BYDLO-${version}-${appimage_arch}.AppImage"
    cat "$runtime" "$image" > "$appimage"
    chmod 755 "$appimage"
    echo "$appimage"
}

for format in "${formats[@]}"; do
    case "$format" in
        deb) build_deb ;;
        appimage) build_appimage ;;
        *) die "unknown format: $format (expected deb or appimage)" ;;
    esac
done
