#!/bin/sh
# Launcher installed as /usr/bin/bydlo by the .deb and Arch packages.
# BYDLO_LAUNCHER keeps autostart / import associations on this stable path.
export BYDLO_LAUNCHER="${BYDLO_LAUNCHER:-/usr/bin/bydlo}"
exec /opt/bydlo/BYDLO "$@"
