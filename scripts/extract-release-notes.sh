#!/usr/bin/env bash
# Extracts the release-notes section for one tag from docs/RELEASE_NOTES.md.
# Usage: extract-release-notes.sh <tag> [input] [output]
# Fails when the section is missing or duplicated; history stays in docs.
set -euo pipefail

tag="${1:?Usage: extract-release-notes.sh <tag> [input] [output]}"
input="${2:-docs/RELEASE_NOTES.md}"
output="${3:-.tmp/release-notes.md}"

version="${tag#v}"
# Exact top-level header, e.g. "# BYDLO v0.4.0".
header="# BYDLO v${version}"

if [ ! -f "$input" ]; then
  echo "Release notes input not found: ${input}" >&2
  exit 1
fi
count=$(grep -c -x -F -- "$header" "$input")
if [ "$count" -eq 0 ]; then
  echo "Release notes section not found for tag '${tag}': expected header '${header}' in ${input}" >&2
  exit 1
fi
if [ "$count" -gt 1 ]; then
  echo "Duplicate release notes headers for tag '${tag}' in ${input}" >&2
  exit 1
fi

mkdir -p "$(dirname "$output")"
awk -v hdr="$header" '
  $0 == hdr { printing = 1; next }
  printing && /^# BYDLO / { printing = 0 }
  printing { print }
' "$input" > "$output"

if [ ! -s "$output" ] || ! grep -q '[^[:space:]]' "$output"; then
  echo "Release notes section for tag '${tag}' is empty in ${input}" >&2
  exit 1
fi
echo "Wrote ${output} for ${tag}."
