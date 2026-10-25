#!/usr/bin/env bash
# Validates every example: it must compile and run to completion without an
# unhandled exception.
#
# Usage:
#   scripts/validate.sh              # validate every example
#   scripts/validate.sh 00 05 12     # validate only the listed example numbers
#
# Examples that need a live model call take 30-90 seconds each. Set
# SKIP_LIVE=1 to skip the examples that call a model, or COMPILE_ONLY=1 to
# build every example without running it.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXAMPLES="$ROOT/examples"

# Examples that make a live model call.
LIVE_EXAMPLES=(08 10 13 15 16 17 18 19 20 21 22 24 25 26 27 28 29 30 31)

# Run the unit test suite first if present
TESTS_DIR="$ROOT/tests/Durable.Tests"
if [[ -d "$TESTS_DIR" && "${SKIP_TESTS:-0}" != "1" ]]; then
  echo "=== Running Durable.Tests suite ==="
  if (cd "$TESTS_DIR" && dotnet test --nologo -v q); then
    echo "PASS     Durable.Tests"
  else
    echo "FAIL     Durable.Tests"
    exit 1
  fi
  echo
fi

is_live() {
  local number="$1"
  for live in "${LIVE_EXAMPLES[@]}"; do
    [[ "$live" == "$number" ]] && return 0
  done
  return 1
}

selected=("$@")
if [[ ${#selected[@]} -eq 0 ]]; then
  # bash 3.2 on macOS has no mapfile, so read the list with a while loop.
  while IFS= read -r line; do
    selected+=("$line")
  done < <(cd "$EXAMPLES" && ls *.cs | sed 's/\.cs$//' | sort)
fi

failures=0
for name in "${selected[@]}"; do
  file="$EXAMPLES/$name.cs"
  if [[ ! -f "$file" ]]; then
    echo "MISSING  $name.cs"
    failures=$((failures + 1))
    continue
  fi

  number="${name%%-*}"
  if [[ "${SKIP_LIVE:-0}" == "1" ]] && is_live "$number"; then
    echo "SKIP     $name.cs (live model)"
    continue
  fi

  echo "=== $name.cs ==="
  if [[ "${COMPILE_ONLY:-0}" == "1" ]]; then
    if (cd "$EXAMPLES" && dotnet build "$name.cs" >/dev/null 2>&1); then
      echo "PASS     $name.cs (compile)"
    else
      echo "FAIL     $name.cs (compile)"
      (cd "$EXAMPLES" && dotnet build "$name.cs" 2>&1) | grep -E "error" | head -10
      failures=$((failures + 1))
    fi
    continue
  fi

  output="$(cd "$EXAMPLES" && dotnet run "$name.cs" 2>&1)"
  status=$?

  if [[ $status -ne 0 ]]; then
    echo "FAIL     $name.cs (exit $status)"
    echo "$output" | grep -E "error|Unhandled|Exception" | head -10
    failures=$((failures + 1))
    continue
  fi

  if echo "$output" | grep -qE "Unhandled exception"; then
    echo "FAIL     $name.cs (unhandled exception)"
    echo "$output" | grep -A 5 "Unhandled exception" | head -12
    failures=$((failures + 1))
    continue
  fi

  echo "PASS     $name.cs"
  echo "$output" | sed 's/^/         /' | head -30
done

echo
if [[ $failures -eq 0 ]]; then
  echo "All examples passed."
else
  echo "$failures example(s) failed."
fi

exit $((failures > 0))
