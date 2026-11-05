#!/usr/bin/env bash
# Regenerates nix/deps.json (NuGet lock for the Nix package) after Directory.Packages.props changes.
# Only restores packages from nuget.org; never builds or runs the app. Requires Nix with flakes.
set -euo pipefail

# shellcheck source=scripts/env.sh
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
cd "$BYDLO_ROOT"
nix build .#bydlo.fetch-deps -o .tmp/nix-fetch-deps
.tmp/nix-fetch-deps nix/deps.json
