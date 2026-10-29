#!/usr/bin/env bash
# Validates every example: it must compile and run to completion without an
# unhandled exception.
#
# Usage:
#   scripts/validate.sh                 # validate every example
#   scripts/validate.sh 00 05 12        # validate only the listed example numbers
#   scripts/validate.sh --deterministic # run the deterministic (credential-free) suite
#
# Examples that need a signed-in Copilot CLI take 30-90 seconds each. Set
# SKIP_LIVE=1 to skip them, or COMPILE_ONLY=1 to build every example without
# running it.
#
# The deterministic mode is described in docs/DESIGN.md section 6. It runs the
# examples and suites that need no Copilot sign-in, against the recorded runtime
# in tools/ReplayRuntime, and it fails on the same terms as a live run.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXAMPLES="$ROOT/examples"

# Examples that need a signed-in Copilot CLI: they either make a live model call, or
# they set a reasoning effort, which makes the harness query models.list to confirm the
# model supports one.
LIVE_EXAMPLES=(07 08 10 13 15 16 17 18 19 20 21 22 24 25 26 27 28 29 30 31 33 34 35)

# Examples that run with no Copilot sign-in and no recorded session: they exercise the
# durable store only, so they never ask the runtime for a session.
DETERMINISTIC_EXAMPLES=(32-jsonl-store 37-ef-core-store)

# The replay runtime and its recorded fixture.
REPLAY_PROJECT="$ROOT/tools/ReplayRuntime/ReplayRuntime.csproj"
REPLAY_BIN="$ROOT/tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime"
REPLAY_FIXTURE="$ROOT/tools/ReplayRuntime/fixtures/basic.json"
REPLAY_PROBE="$ROOT/tools/polyglot-attach/python/attach.py"

# The four terms the examples must never use to describe their own behaviour: an
# example either calls the real thing or it does not exist. Matched case-insensitively
# anywhere under examples/. Each is spelled in fragments so this file does not match
# the rule it enforces.
BANNED_TERMS=("mo""ck" "fa""ke" "st""ub" "sim""ulat")

usage() {
  sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

deterministic=0
positional=()
for argument in "$@"; do
  case "$argument" in
    --deterministic) deterministic=1 ;;
    -h|--help) usage; exit 0 ;;
    --*) echo "validate.sh: unknown option '$argument'" >&2; usage >&2; exit 2 ;;
    *) positional+=("$argument") ;;
  esac
done

if [[ $deterministic -eq 1 && ${#positional[@]} -gt 0 ]]; then
  echo "validate.sh: --deterministic runs the whole deterministic suite; drop the example numbers." >&2
  exit 2
fi

# Fails when an example describes itself with one of the banned terms. The pattern is
# built from the term list so this script never matches itself, and the search is scoped
# to examples/ so build output elsewhere cannot trip it.
check_vocabulary() {
  local pattern
  pattern="$(printf '%s|' "${BANNED_TERMS[@]}")"
  pattern="${pattern%|}"

  local hits
  hits="$(grep -rniE "$pattern" "$EXAMPLES" \
    --include=*.cs \
    --exclude-dir=obj --exclude-dir=bin 2>/dev/null)"
  local status=$?

  if [[ $status -eq 0 ]]; then
    echo "FAIL     vocabulary"
    echo "$hits" | sed "s|$ROOT/||" | sed 's/^/         /'
    echo "         Examples must use the real runtime; describe recorded behaviour as"
    echo "         deterministic, recorded, replay, scripted, canned, or a fixture."
    return 1
  fi

  if [[ $status -gt 1 ]]; then
    echo "FAIL     vocabulary (grep exited $status)"
    return 1
  fi

  echo "PASS     vocabulary"
  return 0
}

# Builds the replay runtime and returns its path on stdout, or fails.
build_replay_runtime() {
  if ! dotnet build "$REPLAY_PROJECT" --nologo -v q >/dev/null 2>&1; then
    echo "validate.sh: could not build $REPLAY_PROJECT" >&2
    return 1
  fi

  if [[ ! -x "$REPLAY_BIN" ]]; then
    echo "validate.sh: the replay runtime was not produced at $REPLAY_BIN" >&2
    return 1
  fi

  printf '%s\n' "$REPLAY_BIN"
}

# Runs one test project and reports it the same way the example loop does.
run_test_project() {
  local label="$1" project="$2"
  echo "=== $label ==="
  if (cd "$ROOT" && dotnet test "$project" --nologo -v q); then
    echo "PASS     $label"
    return 0
  fi

  echo "FAIL     $label"
  return 1
}

run_deterministic() {
  local failures=0

  echo "=== Deterministic run (no Copilot sign-in, no credentials) ==="
  echo

  check_vocabulary || failures=$((failures + 1))
  echo

  # The recorded runtime suites: these exercise the runtime protocol and the durable
  # tool round trip with nothing recorded but the model's side of the conversation.
  if [[ "${SKIP_TESTS:-0}" != "1" ]]; then
    run_test_project "ReplayRuntime.Tests" "$ROOT/tests/ReplayRuntime.Tests/ReplayRuntime.Tests.csproj" \
      || failures=$((failures + 1))
    echo

    echo "=== ReplayToolDispatchTests (recorded runtime, real tool handler) ==="
    if (cd "$ROOT" && dotnet test "$ROOT/tests/Durable.Tests/Durable.Tests.csproj" \
        --nologo -v q --filter "FullyQualifiedName~ReplayToolDispatchTests"); then
      echo "PASS     ReplayToolDispatchTests"
    else
      echo "FAIL     ReplayToolDispatchTests"
      failures=$((failures + 1))
    fi
    echo
  fi

  local runtime
  if ! runtime="$(build_replay_runtime)"; then
    failures=$((failures + 1))
    runtime=""
  fi

  if [[ -n "$runtime" && -f "$REPLAY_FIXTURE" && -f "$REPLAY_PROBE" ]]; then
    echo "=== Recorded runtime answers a conversation from $REPLAY_FIXTURE ==="
    if python3 "$REPLAY_PROBE" "$runtime" --fixture "$REPLAY_FIXTURE"; then
      echo "PASS     replay runtime (recorded fixture)"
    else
      echo "FAIL     replay runtime (recorded fixture)"
      failures=$((failures + 1))
    fi
    echo
  fi

  # The store-only examples: they never open a Copilot session, so they need neither a
  # sign-in nor a recording. They still run the real code paths.
  local name file output status
  for name in "${DETERMINISTIC_EXAMPLES[@]}"; do
    file="$EXAMPLES/$name.cs"
    if [[ ! -f "$file" ]]; then
      echo "MISSING  $name.cs"
      failures=$((failures + 1))
      continue
    fi

    echo "=== $name.cs ==="
    if [[ -n "$runtime" ]]; then
      output="$(cd "$EXAMPLES" && DURABLE_RUNTIME_PATH="$runtime" dotnet run "$name.cs" 2>&1)"
    else
      output="$(cd "$EXAMPLES" && dotnet run "$name.cs" 2>&1)"
    fi
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

  # Everything else is excluded on the record, with the reason, so a shrinking
  # deterministic set is visible in the output rather than silent.
  local total excluded
  total="$(cd "$EXAMPLES" && ls *.cs | wc -l | tr -d ' ')"
  excluded=$((total - ${#DETERMINISTIC_EXAMPLES[@]}))
  echo "Deterministic: ${#DETERMINISTIC_EXAMPLES[@]} of $total examples ran; $excluded excluded."
  echo "  Excluded examples create a Copilot session, so they need a signed-in runtime;"
  echo "  the recorded runtime only answers the sessions its fixture names. Run them with"
  echo "  scripts/validate.sh (or SKIP_LIVE=1 to compile the live ones without running)."
  echo

  if [[ $failures -eq 0 ]]; then
    echo "Deterministic run passed."
  else
    echo "$failures deterministic check(s) failed."
  fi

  return $((failures > 0))
}

if [[ $deterministic -eq 1 ]]; then
  run_deterministic
  exit $?
fi

if ! check_vocabulary; then
  echo
  echo "1 example check(s) failed."
  exit 1
fi
echo

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

selected=("${positional[@]}")
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
    echo "SKIP     $name.cs (needs Copilot auth)"
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
