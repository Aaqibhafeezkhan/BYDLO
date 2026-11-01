#!/usr/bin/env bash
# Safe project check: restore, build (Release), isolated unit tests.
# Never runs engines, never touches system networking.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
source "$root/scripts/env.sh"
cd "$root"

bash scripts/test-hooks.sh
dotnet build BYDLO.slnx -c Release -warnaserror --nologo -v quiet
dotnet test tests/Bydlo.Tests/Bydlo.Tests.csproj -c Release --no-build --nologo -v quiet --results-directory .tmp/TestResults
