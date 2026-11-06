#!/usr/bin/env bash
# Read/extract synthetic Debian archives through the real PKGBUILD. Never install or run payloads.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
fixture=$(mktemp -d "$BYDLO_ROOT/.tmp/arch-package-XXXXXXXX")
for fixture_arch in x86_64 aarch64; do
    (
        source "$BYDLO_ROOT/packaging/arch/bydlo-bin/PKGBUILD"
        export CARCH=$fixture_arch
        work="$fixture/$fixture_arch"
        root="$work/debroot"
        mkdir -p "$root/opt/bydlo/engines/test/1.0" "$root/opt/bydlo/helper" \
            "$root/usr/share/icons/hicolor/256x256/apps" "$work/output"
        for name in BYDLO helper/Bydlo.Helper engines/test/1.0/engine LICENSE THIRD_PARTY_NOTICES.md \
            engines/engines.lock.json ENGINES-SHA256SUMS.txt; do
            printf '%s\n' "synthetic-$name" > "$root/opt/bydlo/$name"
        done
        cp "$BYDLO_ROOT/src/Bydlo.App/Assets/bydlo-256.png" "$root/usr/share/icons/hicolor/256x256/apps/bydlo.png"
        bsdtar -cJf "$work/data.tar.xz" -C "$root" .
        deb_arch=amd64
        [[ $fixture_arch != aarch64 ]] || deb_arch=arm64
        cd "$work"
        # Minimal ar container: prepare() only reads data.tar.xz, never DEBIAN scripts.
        ar rc "bydlo_${pkgver}_${deb_arch}.deb" data.tar.xz
        rm data.tar.xz
        prepare
        pkgdir="$work/output"
        package
        for name in BYDLO helper/Bydlo.Helper engines/test/1.0/engine LICENSE THIRD_PARTY_NOTICES.md \
            engines/engines.lock.json ENGINES-SHA256SUMS.txt; do
            cmp "$root/opt/bydlo/$name" "$pkgdir/opt/bydlo/$name"
        done
        case "$(uname -s)" in
            MINGW*|MSYS*) echo 'Windows filesystem: POSIX mode checks deferred to Linux CI' ;;
            *)
                test -x "$pkgdir/opt/bydlo/BYDLO"
                test -x "$pkgdir/opt/bydlo/helper/Bydlo.Helper"
                test -x "$pkgdir/usr/bin/bydlo"
                ;;
        esac
        grep -q 'BYDLO_LAUNCHER' "$pkgdir/usr/bin/bydlo"
        grep -q '^Exec=bydlo$' "$pkgdir/usr/share/applications/bydlo.desktop"
        cmp "$root/usr/share/icons/hicolor/256x256/apps/bydlo.png" "$pkgdir/usr/share/icons/hicolor/256x256/apps/bydlo.png"
        test -s "$pkgdir/usr/share/licenses/bydlo-bin/LICENSE"
        test -s "$pkgdir/usr/share/licenses/bydlo-bin/THIRD_PARTY_NOTICES.md"
        # Keep generated metadata paired with the actual supported URLs/hashes.
        for value in "${source_x86_64[@]}" "${source_aarch64[@]}" \
            "${sha256sums_x86_64[@]}" "${sha256sums_aarch64[@]}"; do
            grep -qF "$value" "$BYDLO_ROOT/packaging/arch/bydlo-bin/.SRCINFO"
        done
        echo "Arch .deb extraction regression passed: $fixture_arch"
    )
done
