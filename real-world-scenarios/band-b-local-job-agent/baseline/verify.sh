#!/usr/bin/env bash
# Checks the target after a run: the agent's edits are on disk, and the plan reports
# complete. Exit 0 means the loop did real work. Exit 1 names what is still wrong.
#
# Usage: verify.sh <target-dir>

set -uo pipefail

target="${1:?usage: verify.sh <target-dir>}"
fail=0

notes="$target/release-notes.md"

# 1. Headings demoted: the two known headings must have moved one level, the title must not.
grep -qx '### Overview' "$notes" || { echo "FAIL  release-notes.md: '### Overview' expected"; fail=1; }
grep -qx '### New' "$notes" || { echo "FAIL  release-notes.md: '### New' expected"; fail=1; }
grep -qx '# Release notes' "$notes" || { echo "FAIL  release-notes.md: title changed"; fail=1; }
# The fenced block's pseudo-heading must be untouched.
grep -qx '## This heading is inside a fence and must not change.' "$notes" \
  || { echo "FAIL  release-notes.md: fenced block changed"; fail=1; }
# No untouched H2 left outside the fence.
left="$(awk '/^```/{fence=!fence;next} !fence && /^## /{print}' "$notes")"
if [ -n "$left" ]; then
  echo "FAIL  release-notes.md: an H2 heading remains outside the fence:"
  echo "$left"
  fail=1
fi

# 2. Every bullet under the New section carries the marker exactly once.
new_bullets="$(awk '/^### New$/{f=1;next} /^#/{f=0} f && /^- /' "$notes")"
if [ -z "$new_bullets" ]; then
  echo "FAIL  release-notes.md: no bullets found under the New section"
  fail=1
else
  while IFS= read -r line; do
    case "$line" in
      *" <!-- NEW -->") ;;
      *) echo "FAIL  release-notes.md: bullet missing marker: $line"; fail=1 ;;
    esac
  done <<< "$new_bullets"
fi

# 3. Every section file opens with an H1.
for f in "$target"/docs/section-*.md; do
  first="$(head -n 1 "$f")"
  case "$first" in
    '# '*) ;;
    *) echo "FAIL  $(basename "$f"): first line is not an H1: $first"; fail=1 ;;
  esac
done

# 4. The two extra-defect items were actually applied.
grep -q '](./section-01.md)' "$target/docs/section-03.md" \
  || { echo "FAIL  docs/section-03.md: broken link not repointed"; fail=1; }
grep -q '2026-01-01' "$target/docs/section-09.md" \
  || { echo "FAIL  docs/section-09.md: stale date not replaced"; fail=1; }

# 5. The plan reports complete and no item is still open.
grep -qx 'Status: complete' "$target/PLAN.md" \
  || { echo "FAIL  PLAN.md: status is not 'complete'"; fail=1; }
if grep -q '^- \[ \]' "$target/PLAN.md"; then
  echo "FAIL  PLAN.md: open items remain:"
  grep '^- \[ \]' "$target/PLAN.md"
  fail=1
fi

if [ "$fail" -ne 0 ]; then
  exit 1
fi

echo "verified: the target matches the plan"
