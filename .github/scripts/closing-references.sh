#!/usr/bin/env bash
# Pure parser for closing references in a pull request body (issue #449).
#
# Reads the body on stdin and writes one issue number per line, deduplicated and sorted.
# Usage: closing-references.sh <owner/repo>
#
# The grammar is deliberately strict. Only an explicit keyword followed by a reference counts:
#   Closes #12, closes: #12, Fixes owner/repo#12, Resolves #12
# Bare mentions (#12), keywords glued to a word (prefix #12), references to other repositories,
# fenced code blocks, inline code and blockquotes are all ignored, so an example in a description
# can never close an issue. Whether a number is an issue or a pull request is decided by the caller
# through the API; this script never touches the network.
set -euo pipefail

repo="${1:-}"
if [[ ! "$repo" =~ ^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$ ]]; then
  echo "usage: $0 <owner/repo>" >&2
  exit 2
fi
repo="${repo,,}"

body=$(cat)

# Drop fenced code blocks first, then inline code, then quoted lines.
text=$(printf '%s\n' "$body" \
  | tr -d '\r' \
  | sed -E '/^[[:space:]]*(```|~~~)/,/^[[:space:]]*(```|~~~)/d' \
  | sed -E 's/`[^`]*`//g' \
  | sed -E '/^[[:space:]]*>/d')

# A keyword must start a word, so "prefix #12" is not a closing reference.
refs=$(printf '%s\n' "$text" \
  | grep -oiE '(^|[^[:alnum:]_-])(closes|closed|close|fixes|fixed|fix|resolves|resolved|resolve)[[:space:]]*:?[[:space:]]*(#[0-9]+|[[:alnum:]._-]+/[[:alnum:]._-]+#[0-9]+)' \
  | tr '[:upper:]' '[:lower:]' \
  | sed -E 's/^[^[:alnum:]_-]?//' \
  | sed -E 's/^(closes|closed|close|fixes|fixed|fix|resolves|resolved|resolve)[[:space:]]*:?[[:space:]]*//' \
  || true)

while IFS= read -r ref; do
  [[ -n "$ref" ]] || continue
  if [[ "$ref" =~ ^#([1-9][0-9]*)$ ]]; then
    printf '%s\n' "${BASH_REMATCH[1]}"
  elif [[ "$ref" =~ ^([A-Za-z0-9._-]+/[A-Za-z0-9._-]+)#([1-9][0-9]*)$ ]]; then
    if [[ "${BASH_REMATCH[1],,}" == "$repo" ]]; then
      printf '%s\n' "${BASH_REMATCH[2]}"
    fi
  fi
done <<< "$refs" | sort -n -u
