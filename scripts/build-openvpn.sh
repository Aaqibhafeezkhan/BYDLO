#!/usr/bin/env bash
# Compile only. Never run openvpn, its tests, or any network/driver setup.
set -euo pipefail
rid=$1
stage=$2
output=$3
openvpn_source=$4
openssl_source=$5
cap_source=${6:-}
case "$rid:$(uname -m)" in
  linux-x64:x86_64) target=linux-x86_64 ;;
  linux-arm64:aarch64|linux-arm64:arm64) target=linux-aarch64 ;;
  osx-x64:x86_64) target=darwin64-x86_64-cc ;;
  osx-arm64:arm64) target=darwin64-arm64-cc ;;
  *) echo 'OpenVPN must be built on a runner matching the target architecture' >&2; exit 1 ;;
esac
mkdir -p "$stage/openssl-build" "$stage/openvpn-build" "$output"
tar -xzf "$openssl_source" --strip-components=1 -C "$stage/openssl-build"
tar -xzf "$openvpn_source" --strip-components=1 -C "$stage/openvpn-build"
prefix="$stage/openssl-static"
if [[ "$rid" == osx-* ]]; then export MACOSX_DEPLOYMENT_TARGET=13.0; fi
(
  cd "$stage/openssl-build"
  perl Configure "$target" no-shared no-tests no-module --prefix="$prefix" --libdir=lib
  make -j2
  make install_sw
)
if [[ "$rid" == linux-* ]]; then
  mkdir -p "$stage/libcap-ng-build"
  tar -xzf "$cap_source" --strip-components=1 -C "$stage/libcap-ng-build"
  (
    cd "$stage/libcap-ng-build"
    autoreconf -fi
    ./configure --prefix="$prefix" --libdir="$prefix/lib" --enable-static --disable-shared --without-python --without-python3
    make -j2
    make install
  )
fi
(
  cd "$stage/openvpn-build"
  export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
  export CFLAGS="-O2"
  export LDFLAGS="-L$prefix/lib"
  # Static OpenSSL; system libc, kernel TUN and OS route/DNS tools remain OS dependencies.
  ./configure --disable-dco --disable-lzo --disable-lz4 --disable-pkcs11 --disable-plugins \
    --disable-plugin-auth-pam --disable-plugin-down-root --disable-systemd \
    --disable-dns-updown-by-default --disable-unit-tests --with-crypto-library=openssl
  make -j2
  cp src/openvpn/openvpn "$output/openvpn"
  cp COPYING "$output/COPYING"
  cp "$stage/openssl-build/apps/openssl.cnf" "$output/openssl.cnf"
  chmod 755 "$output/openvpn"
  if [[ "$rid" == linux-* ]]; then
    if readelf -d "$output/openvpn" | grep NEEDED | grep -E 'libssl|libcrypto|libcap-ng|liblzo|liblz4|libpkcs'; then
      echo 'Unexpected external engine dependency' >&2; exit 1
    fi
  else
    if otool -L "$output/openvpn" | tail -n +2 | grep -vE '^\s*(/usr/lib/|/System/Library/)'; then
      echo 'Unexpected external engine dependency' >&2; exit 1
    fi
  fi
)
