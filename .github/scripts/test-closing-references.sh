#!/usr/bin/env bash
# Self-test for the closing-reference parser (issue #449). Feeds bodies to the parser only;
# it never calls the API and never closes anything.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
parser="$root/.github/scripts/closing-references.sh"
repo="endercodezz/BYDLO"
fail=0

check() {
  local expected="$1" body="$2" actual
  actual="$(printf '%s' "$body" | bash "$parser" "$repo" | paste -sd, -)"
  if [ "$actual" != "$expected" ]; then
    echo "FAIL (expected '$expected', got '$actual'): $body"
    fail=1
  fi
}

# Explicit keywords, all supported spellings.
check 12 'Closes #12'
check 12 'closes #12'
check 12 'CLOSES #12'
check 12 'Closes: #12'
check 12 'Fixes #12'
check 12 'Fixed #12'
check 12 'Resolves #12'
check 12 'Resolved #12'
check 12 'Close #12'

# Several references, deduplicated and sorted numerically.
check '12,34' 'Closes #12
Fixes #34'
check 12 'Closes #12
Closes #12'
check '9,12,34' 'Closes #34, fixes #9 and resolves #12'
check '12,34' 'Closes #12
Closes #34
Closes #12'

# Same-repository qualified references are accepted, foreign ones are not.
check 12 'Closes endercodezz/BYDLO#12'
check 12 'Closes ENDERCODEZZ/bydlo#12'
check '' 'Closes other/repo#12'
check '' 'Closes endercodezz/BYDLO-other#12'

# Bare mentions and keywords glued to a word never count.
check '' '#12'
check '' 'prefix #12'
check '' 'unfixes #12'
check 12 'Closes #12 and #34'
check 12 'Fixes #12, #34'
check '' 'Closes #0'
check '' 'Closes #'
check '' 'Closes 12'

# Examples in code and quotes never count.
check '' '```
Closes #12
```'
check '' '~~~
Fixes #12
~~~'
check '' 'Use `Closes #12` in the body.'
check '' '> Closes #12'
check '' '  > Fixes #12'

# A real reference next to an example still counts once.
check 34 '```
Closes #12
```
Closes #34'
check 34 'Use `Closes #12` in the body.
Closes #34'

# Line endings and surrounding prose.
check 12 'Some text.
Closes #12
More text.'
check 12 "$(printf 'Closes #12\r\n')"
check 12 'Closes	#12'

# A pull request reference is parsed here and rejected by the caller through the API.
check 12 'Closes #12'

# The repository argument is required and validated.
if printf 'Closes #12' | bash "$parser" >/dev/null 2>&1; then
  echo 'FAIL: a missing repository argument must be rejected'
  fail=1
fi
if printf 'Closes #12' | bash "$parser" 'not a repo' >/dev/null 2>&1; then
  echo 'FAIL: a malformed repository argument must be rejected'
  fail=1
fi

if [ "$fail" != 0 ]; then
  echo 'closing-reference parser: FAILED'
  exit 1
fi
echo 'closing-reference parser: OK'
