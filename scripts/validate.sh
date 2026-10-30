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

# Examples that run with no Copilot sign-in against the recorded runtime. Three of them
# (32-jsonl-store, 36-durable-client-builder, 37-ef-core-store) exercise the durable store and
# the facade only and never ask the runtime for a session; the rest create a session, which the
# runtime serves from the recording and marks as synthesised. See docs/DESIGN.md section 6.1.
DETERMINISTIC_EXAMPLES=(
  00-conversation
  01-documents
  03-owned-conversations
  04-watches
  05-harness
  06-configuration
  09-registry-reload
  11-tasks
  12-recovery
  14-system-prompt
  17-print
  18-json
  23-child-tasks
  32-jsonl-store
  35-sandbox-sdk-observability-bypass
  36-durable-client-builder
  37-ef-core-store
)

# Why each remaining example cannot run against the recorded runtime. Every excluded
# example is named in the output with its reason, so a shrinking deterministic set is
# visible rather than silent. The reason is the assertion or request that the recording
# cannot satisfy, not a guess. bash 3.2 on macOS has no associative arrays, so this is a
# function rather than a map.
deterministic_exclusion_reason() {
  case "$1" in
    02-forks) echo "needs sessions.fork, which the runtime does not answer" ;;
    07-harness-conversations) echo "sets a reasoning effort, so the harness queries models.list for a model the fixture does not record" ;;
    08-context) echo "needs sessions.fork, which the runtime does not answer" ;;
    10-extension-state) echo "asserts a tool wrote into the document; the recording has no tool call" ;;
    13-chat) echo "asserts the model's answer; the recording has no model answer" ;;
    15-real-model) echo "asserts the answer streamed as deltas; the recording has no model answer" ;;
    16-coding-tools) echo "asserts the app's read_file tool ran; the recording has no tool call" ;;
    19-inbox) echo "asserts both prompts are in the transcript; the recording has no user-message events" ;;
    20-late-join) echo "asserts the snapshot holds the user message; the recording has no user-message events" ;;
    21-subagent-foreground) echo "asserts a subagent tool created a child conversation; the recording has no tool call" ;;
    22-subagent-background) echo "needs a subagent request the runtime does not answer" ;;
    24-compaction) echo "needs a compaction request the runtime does not answer" ;;
    25-coding-agent) echo "asserts the tool ran in the conversation's directory; the recording has no tool call" ;;
    26-plan-mode) echo "asserts the plan was written to the document; the recording has no tool call" ;;
    27-reviewer) echo "asserts the review loop ran more than one pass; the recording has no tool call" ;;
    28-sandbox-per-conversation) echo "asserts a file the sandboxed tool wrote; the recording has no tool call" ;;
    29-tool-override) echo "asserts the venv shell replaced the plain one; the recording has no tool call" ;;
    30-reload-and-restart) echo "waits for a runtime restart the recording does not perform" ;;
    31-permissions) echo "asserts a permission request the recording does not contain" ;;
    33-sandbox-sdk-filesystem) echo "asserts a write landed on disk; the recording has no tool call" ;;
    34-sandbox-sdk-network-credentials) echo "asserts the sandboxed shell sees a masked credential; the recording has no tool call" ;;
    *) echo "" ;;
  esac
}

# Why an example is not compared against itself across two runs. Only a genuine, already
# present source of run-to-run variation belongs here: a check that fails at random teaches
# people to ignore it, which is worse than not having the check.
#
# Every entry records a known-bad example, and the list is expected to shrink. Never add an
# entry to make a new failure go away: a non-deterministic example is a bug in that example,
# so fix the example. An entry added to silence a regression hides the failure this pass
# exists to catch, and it stays hidden for every run after it.
determinism_exclusion_reason() {
  case "$1" in
    23-child-tasks) echo "races a 20 ms delay against a 100 ms bank confirmation, so the number of refunded children varies between runs (examples/23-child-tasks.cs:38,133)" ;;
    *) echo "" ;;
  esac
}

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

# The model the fixture records for the session it records. This is the answer the runtime
# is expected to report, read from the recording rather than from the library, so the check
# below cannot agree with the code it is checking by construction.
recorded_model() {
  python3 - "$REPLAY_FIXTURE" <<'PY'
import json, sys
with open(sys.argv[1]) as handle:
    fixture = json.load(handle)
for session in fixture.get("sessions", []):
    if session.get("model"):
        print(session["model"])
        break
PY
}

# Whether an example asks the runtime for a session. Derived from the source so an example
# added later is covered without anyone remembering to list it here.
example_opens_session() {
  grep -qE 'RootAsync\(|CreateConversationAsync\(' "$EXAMPLES/$1.cs"
}

# Collapses the values that legitimately differ between two runs of the same example: the
# temporary workspace each run creates, the session id the runtime hands out, and any
# timestamp. The examples are not changed to make this easier; the normalisation lives here.
normalise_output() {
  sed -E \
    -e 's#/private/var/folders/[A-Za-z0-9_.+/-]+#<temp>#g' \
    -e 's#/var/folders/[A-Za-z0-9_.+/-]+#<temp>#g' \
    -e 's#/tmp/[A-Za-z0-9_.+/-]+#<temp>#g' \
    -e 's/s[0-9a-f]{32}/<session>/g' \
    -e 's/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/<guid>/g' \
    -e 's/[0-9a-f]{32}/<guid>/g' \
    -e 's/[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+(Z|[+-][0-9]{2}:[0-9]{2})/<timestamp>/g' \
    -e 's/[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?/<timestamp>/g'
}

# Runs one example against the recorded runtime and prints its combined output.
run_example() {
  local name="$1" runtime="$2" marker_file="$ROOT/.replay-markers-$$.txt"
  rm -f "$marker_file"
  if [[ -n "$runtime" ]]; then
    (cd "$EXAMPLES" && REPLAY_MARKER_PATH="$marker_file" DURABLE_RUNTIME_PATH="$runtime" dotnet run "$name.cs" 2>&1)
  else
    (cd "$EXAMPLES" && dotnet run "$name.cs" 2>&1)
  fi
  rm -f "$marker_file"
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

  # The deterministic examples. Each one runs against the recorded runtime; a session the
  # fixture does not record is served from the recording and marked as synthesised, so a
  # pass that proves less than a recorded transcript says so on its own PASS line.
  local name file output status marker_file markers ran=0 synthesised=0
  local expected_model model_failures=0
  expected_model="$(recorded_model 2>/dev/null)"
  if [[ -z "$expected_model" ]]; then
    echo "FAIL     the fixture records no model, so the effective-model check cannot run"
    failures=$((failures + 1))
  fi
  for name in "${DETERMINISTIC_EXAMPLES[@]}"; do
    file="$EXAMPLES/$name.cs"
    if [[ ! -f "$file" ]]; then
      echo "MISSING  $name.cs"
      failures=$((failures + 1))
      continue
    fi

    echo "=== $name.cs ==="
    marker_file="$ROOT/.replay-markers-$$.txt"
    rm -f "$marker_file"
    if [[ -n "$runtime" ]]; then
      output="$(cd "$EXAMPLES" && REPLAY_MARKER_PATH="$marker_file" DURABLE_RUNTIME_PATH="$runtime" dotnet run "$name.cs" 2>&1)"
    else
      output="$(cd "$EXAMPLES" && dotnet run "$name.cs" 2>&1)"
    fi
    status=$?
    markers="$(cat "$marker_file" 2>/dev/null)"
    rm -f "$marker_file"

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

    ran=$((ran + 1))
    if echo "$markers" | grep -q "synthesised session"; then
      synthesised=$((synthesised + 1))
      echo "PASS     $name.cs (synthesised session — not a recorded transcript)"
      echo "$markers" | sed 's/^/         /'
    else
      echo "PASS     $name.cs (recorded transcript)"
    fi
    echo "$output" | sed 's/^/         /' | head -30

    # The example prints the model the runtime reported. That print comes from the library,
    # so agreeing with it proves nothing on its own: compare it against the model the
    # recording names, which the library never reads. An example that opens a session must
    # print the line; an example that opens none must not invent one.
    if example_opens_session "$name"; then
      local printed
      printed="$(echo "$output" | sed -n 's/^effective model: //p')"
      if [[ -z "$printed" ]]; then
        echo "FAIL     $name.cs opens a session but printed no 'effective model:' line"
        model_failures=$((model_failures + 1))
      else
        local wrong=0 reported
        while IFS= read -r reported; do
          if [[ "$reported" != "$expected_model" ]]; then
            echo "FAIL     $name.cs reported model '$reported'; the recording names '$expected_model'"
            wrong=$((wrong + 1))
          fi
        done <<< "$printed"
        if [[ $wrong -eq 0 ]]; then
          echo "PASS     $name.cs effective model matches the recording ($expected_model)"
        else
          model_failures=$((model_failures + wrong))
        fi
      fi
    elif echo "$output" | grep -q '^effective model: '; then
      echo "FAIL     $name.cs opens no session but printed an 'effective model:' line"
      model_failures=$((model_failures + 1))
    fi
  done
  echo

  if [[ $model_failures -gt 0 ]]; then
    echo "$model_failures effective-model check(s) failed."
    failures=$((failures + model_failures))
  else
    echo "Effective model: every example that opens a session reported '$expected_model',"
    echo "the model the recording names."
  fi
  echo

  # The same example, run twice, must say the same thing. Volatile values are normalised
  # here rather than removed from the examples, so the examples keep printing what they
  # actually saw.
  echo "=== Determinism: each example run twice, output compared after normalisation ==="
  local repeat_failures=0 compared=0 skipped=0
  for name in "${DETERMINISTIC_EXAMPLES[@]}"; do
    if ! example_opens_session "$name"; then
      continue
    fi

    local skip_reason
    skip_reason="$(determinism_exclusion_reason "$name")"
    if [[ -n "$skip_reason" ]]; then
      printf 'SKIP     %-34s %s\n' "$name.cs" "$skip_reason"
      skipped=$((skipped + 1))
      continue
    fi

    local first second
    first="$(run_example "$name" "$runtime" | normalise_output)"
    second="$(run_example "$name" "$runtime" | normalise_output)"
    compared=$((compared + 1))
    if [[ "$first" == "$second" ]]; then
      echo "PASS     $name.cs identical across two runs"
    else
      echo "FAIL     $name.cs differs between two runs"
      diff <(printf '%s\n' "$first") <(printf '%s\n' "$second") | head -20 | sed 's/^/         /'
      repeat_failures=$((repeat_failures + 1))
    fi
  done
  echo
  if [[ $repeat_failures -gt 0 ]]; then
    echo "$repeat_failures example(s) were not deterministic across two runs."
    failures=$((failures + repeat_failures))
  else
    echo "Determinism: $compared example(s) produced identical output twice; $skipped skipped."
  fi
  echo

  # Everything else is excluded on the record, with the reason, so a shrinking
  # deterministic set is visible in the output rather than silent. The excluded count is
  # derived from what actually ran, so it cannot drift from the PASS lines above.
  local total excluded reason missing=0
  total="$(cd "$EXAMPLES" && ls *.cs | wc -l | tr -d ' ')"
  excluded=$((total - ran))

  echo "Deterministic: $ran of $total examples ran; $excluded excluded."
  if [[ $synthesised -gt 0 ]]; then
    echo "  $synthesised of the $ran ran on a synthesised session: the fixture does not record"
    echo "  the session the example asked for, so the runtime served it from the recording."
    echo "  Those passes prove the example's own code paths, not a recorded transcript."
  fi
  echo
  echo "  Excluded examples, and why:"
  for file in "$EXAMPLES"/*.cs; do
    name="$(basename "$file" .cs)"
    reason="$(deterministic_exclusion_reason "$name")"
    if [[ -n "$reason" ]]; then
      printf '    %-34s %s\n' "$name" "$reason"
    elif ! printf '%s\n' "${DETERMINISTIC_EXAMPLES[@]}" | grep -qx "$name"; then
      # An example that neither ran nor has a recorded reason would otherwise vanish from
      # the output, which is the failure mode this whole report exists to prevent.
      missing=$((missing + 1))
      printf '    %-34s %s\n' "$name" "no reason recorded; add it to deterministic_exclusion_reason()"
    fi
  done
  echo
  echo "  Run the excluded examples with scripts/validate.sh (or SKIP_LIVE=1 to compile"
  echo "  the live ones without running them)."
  echo

  if [[ $missing -gt 0 ]]; then
    echo "$missing excluded example(s) have no recorded reason."
    failures=$((failures + missing))
  fi

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

# bash 3.2 treats an empty array as unbound under `set -u`, so expand it only when set.
selected=()
if [[ ${#positional[@]} -gt 0 ]]; then
  selected=("${positional[@]}")
fi

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
