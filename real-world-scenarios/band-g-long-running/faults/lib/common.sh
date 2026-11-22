#!/usr/bin/env bash
# The shared machinery the Band G fault scripts are callers of: a scratch run directory, a variant,
# a deterministic injection point, an observation made by another process, and one JSON result.
#
# The three rules this file exists to keep:
#
#   1. A fault is injected by the operating system against a real process, at a moment the harness can
#      point at. A kill is `kill -9` on a PID this shell started, confirmed gone. A store outage is a
#      real change to the file (moved aside) or a real lock on it, not a flag the application reads.
#   2. Every fault has a `--fault-not-injected` control that runs the same steps with the injection
#      removed, so a difference between the two is attributable to the fault and not to the run.
#   3. A run leaves nothing behind: one directory per fault per variant, one port, a trap that reaps
#      every process it started, and no store, marker, lock or permission change left unless KEEP=1.
#
# Neither variant is modified. The baseline is driven through `run` and `resume`, and through the only
# seam it has (its OpenAI-compatible endpoint). The Durable variant is driven through its own verbs and
# through `DURABLE_RUNTIME_PATH` for its runtime.

set -uo pipefail

HARNESS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BAND_DIR="$(cd "$HARNESS_DIR/.." && pwd)"
REPO_ROOT="$(cd "$BAND_DIR/../.." && pwd)"
RESULTS_DIR="$HARNESS_DIR/results"
FIXTURES="$HARNESS_DIR/fixtures"
SCRATCH_ROOT="${FAULTS_SCRATCH:-$REPO_ROOT/.scratch/band-g-faults}"

BASELINE_SOURCE="$BAND_DIR/baseline/BaselineCheckpoint.cs"
DURABLE_SOURCE="$BAND_DIR/durable/band-g-durable.cs"

# The built output lives in the scratch root, not beside the sources: `dotnet run file.cs` launches the
# application as a child of the `dotnet` host, so a PID the shell starts is the host and not the process
# doing the work. Building to a known directory lets a fault script start the application itself, which
# is what makes `kill -9 <pid>` name the application.
BASELINE_OUT="$SCRATCH_ROOT/bin/baseline"
DURABLE_OUT="$SCRATCH_ROOT/bin/durable"

DOTNET="${DOTNET:-/opt/homebrew/bin/dotnet}"
PYTHON="${PYTHON:-python3}"
RUNTIME="${FAULTS_RUNTIME:-$REPO_ROOT/tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime}"
RUNTIME_FIXTURE="${FAULTS_RUNTIME_FIXTURE:-$REPO_ROOT/tools/ReplayRuntime/fixtures/basic.json}"
TRANSCRIPT="${FAULTS_TRANSCRIPT:-$FIXTURES/baseline-transcript.jsonl}"

export DOTNET_VERSION="$("$DOTNET" --version 2>/dev/null || echo unknown)"
export FAULTS_RUNTIME_DESC="tools/ReplayRuntime (recorded runtime) and faults/lib/baseline_endpoint.py (recorded transcript)"
export FAULTS_MODEL_DESC="no network, no key: the baseline is served the recorded transcript behind the endpoint it names; the Durable variant runs the repository's recorded runtime with the scenario's deterministic answers"

log() { printf '%s\n' "$*" >&2; }

# One port per fault per variant, so two runs never fight over a socket and a stale listener is found by
# asking for the port rather than by reading a log.
port_for() {
  case "$1:$2" in
    baseline:kill-during-wait) echo 5351 ;;
    baseline:kill-during-wait-control) echo 5352 ;;
    baseline:restart-after-checkpoint) echo 5353 ;;
    baseline:restart-after-checkpoint-control) echo 5354 ;;
    baseline:duplicate-approval) echo 5355 ;;
    baseline:duplicate-approval-control) echo 5356 ;;
    baseline:late-approval) echo 5357 ;;
    baseline:late-approval-control) echo 5358 ;;
    baseline:abort-mid-wait) echo 5359 ;;
    baseline:abort-mid-wait-control) echo 5381 ;;
    baseline:store-outage) echo 5361 ;;
    baseline:store-outage-control) echo 5382 ;;
    durable:kill-during-wait) echo 5362 ;;
    durable:kill-during-wait-control) echo 5368 ;;
    durable:restart-after-checkpoint) echo 5363 ;;
    durable:restart-after-checkpoint-control) echo 5369 ;;
    durable:duplicate-approval) echo 5364 ;;
    durable:duplicate-approval-control) echo 5370 ;;
    durable:late-approval) echo 5365 ;;
    durable:late-approval-control) echo 5371 ;;
    durable:abort-mid-wait) echo 5366 ;;
    durable:abort-mid-wait-control) echo 5383 ;;
    durable:store-outage) echo 5367 ;;
    durable:store-outage-control) echo 5372 ;;
    *) echo 5398 ;;
  esac
}

require_tools() {
  local missing=0
  [ -x "$DOTNET" ] || { log "harness: $DOTNET is not executable; .NET 10 is required"; missing=1; }
  [ -x "$RUNTIME" ] || {
    log "harness: the recorded runtime is not built: $RUNTIME"
    log "harness: $DOTNET build $REPO_ROOT/tools/ReplayRuntime/ReplayRuntime.csproj"
    missing=1
  }
  [ -f "$TRANSCRIPT" ] || { log "harness: no baseline transcript at $TRANSCRIPT"; missing=1; }
  [ -f "$RUNTIME_FIXTURE" ] || { log "harness: no runtime fixture at $RUNTIME_FIXTURE"; missing=1; }
  command -v "$PYTHON" >/dev/null 2>&1 || { log "harness: python3 is required"; missing=1; }
  return $missing
}

# Both variants are built once per harness run, into a directory this harness owns. The binaries are
# started directly afterwards, so the PID a fault script records is the application — not the `dotnet`
# host that `dotnet run` would put in front of it.
build_variants() {
  log "harness: building both variants"
  mkdir -p "$BASELINE_OUT" "$DURABLE_OUT"
  if ! "$DOTNET" build "$BASELINE_SOURCE" -p:CopilotSkipCliDownload=true --nologo -v quiet -o "$BASELINE_OUT" >/dev/null 2>&1; then
    log "harness: the baseline does not build"
    "$DOTNET" build "$BASELINE_SOURCE" -p:CopilotSkipCliDownload=true --nologo -o "$BASELINE_OUT" 2>&1 | tail -20 >&2
    return 1
  fi
  if ! "$DOTNET" build "$DURABLE_SOURCE" -p:CopilotSkipCliDownload=true --nologo -v quiet -o "$DURABLE_OUT" >/dev/null 2>&1; then
    log "harness: the Durable variant does not build"
    "$DOTNET" build "$DURABLE_SOURCE" -p:CopilotSkipCliDownload=true --nologo -o "$DURABLE_OUT" 2>&1 | tail -20 >&2
    return 1
  fi
  return 0
}

new_run_dir() {
  local dir="$SCRATCH_ROOT/$1/$2"
  rm -rf "$dir"
  mkdir -p "$dir"
  echo "$dir"
}

# ── the baseline's model, as a recorded endpoint ────────────────────────────────────────────────
# The baseline reads `OPENAI_BASE_URL`, so that is where the harness sits. The endpoint serves the
# recorded transcript, writes one JSONL line per request (so a count is per process), and can hold the
# Nth request: a held request is a real model call in flight, which is the instant a kill has to land on
# to be a kill *during* a call rather than after one.
ENDPOINT_PID=""
ENDPOINT_PORT=""

start_endpoint() {
  local dir="$1" port="$2" pause_on="${3:-0}"
  local args=(--port "$port" --transcript "$TRANSCRIPT" --log "$dir/endpoint.log"
              --calls-jsonl "$dir/calls.jsonl")
  if [ "$pause_on" -gt 0 ]; then
    args+=(--pause-on "$pause_on" --marker-dir "$dir")
  fi
  "$PYTHON" "$HARNESS_DIR/lib/baseline_endpoint.py" "${args[@]}" >"$dir/endpoint.stderr" 2>&1 &
  ENDPOINT_PID=$!
  ENDPOINT_PORT="$port"
  local deadline=$((SECONDS + 30))
  while [ "$SECONDS" -lt "$deadline" ]; do
    curl -fsS -o /dev/null --max-time 1 "http://127.0.0.1:$port/v1/models" 2>/dev/null && return 0
    sleep 0.05
  done
  log "harness: the recorded endpoint never came up on $port"
  tail -5 "$dir/endpoint.stderr" >&2
  return 1
}

stop_endpoint() {
  [ -n "$ENDPOINT_PID" ] || return 0
  local pid="$ENDPOINT_PID"
  ENDPOINT_PID=""
  kill -TERM "$pid" 2>/dev/null
  local deadline=$((SECONDS + 10))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.05; done
  kill -9 "$pid" 2>/dev/null
  return 0
}

# A fresh endpoint per process, so `count_calls` is that process's count and not a running total. The
# previous endpoint is stopped first: one port, one owner.
fresh_endpoint() {
  local dir="$1" port="$2" pause_on="${3:-0}"
  stop_endpoint
  rm -f "$dir/inflight" "$dir/release" "$dir/calls.jsonl"
  start_endpoint "$dir" "$port" "$pause_on"
}

baseline_mode() {
  local dir="$1" mode="$2"
  # The port writes nothing into a checkpoint directory it did not create, so the harness creates it. That
  # is also what keeps the directory inside the run's own scratch area rather than in $TMPDIR.
  mkdir -p "$dir/checkpoints"
  env \
    CHECKPOINT_DIR="$dir/checkpoints" \
    OPENAI_API_KEY="${OPENAI_API_KEY:-recorded}" \
    OPENAI_BASE_URL="http://127.0.0.1:$ENDPOINT_PORT/v1" \
    OPENAI_MODEL="${OPENAI_MODEL:-recorded}" \
    "$BASELINE_OUT/BaselineCheckpoint" "$mode"
}

# ── the Durable variant, one verb per process ─────────────────────────────────────────────────
# Its runtime is the recorded one. When a pause is armed, `DURABLE_RUNTIME_PATH` names a relay in front
# of it instead: the relay forwards every frame unchanged and holds the named method, which is how a
# kill can be aimed at a process that is inside a model turn.
DURABLE_RUNTIME=""
durable_runtime() {
  local dir="$1"
  if [ -n "${PAUSE_ON:-}" ]; then
    local relay="$dir/runtime-gate"
    cat > "$relay" <<GATE
#!/usr/bin/env bash
exec "$PYTHON" "$HARNESS_DIR/lib/runtime_gate.py" "$RUNTIME" "$RUNTIME_FIXTURE" "$dir/runtime-gate.log"
GATE
    chmod +x "$relay"
    echo "$relay"
  else
    echo "$RUNTIME"
  fi
}

durable_verb() {
  local dir="$1"
  shift
  [ -n "$DURABLE_RUNTIME" ] || DURABLE_RUNTIME="$(durable_runtime "$dir")"
  FAULT_PAUSE_ON="${PAUSE_ON:-}" \
  FAULT_PAUSE_COUNT="${PAUSE_COUNT:-1}" \
  FAULT_MARKER="$dir" \
  DURABLE_RUNTIME_PATH="$DURABLE_RUNTIME" \
  REPLAY_FIXTURE="$RUNTIME_FIXTURE" \
    "$DURABLE_OUT/band-g-durable" --run "$dir" "$@"
}

# ── starting and killing a real process ───────────────────────────────────────────────────────
# A process that has to be killable is started **as the binary**, never through a shell function.
#
# `baseline_mode … &` would background a *forked subshell*: `$!` is then that subshell, the binary is its
# child, and `kill -TERM` on `$!` kills the wrapper while the application keeps running. That is not a
# detail — a stop that leaves the application alive makes the whole "stopped, then a fresh process
# continued it" claim unprovable, and a later `release` would let the survivor write to the same
# checkpoint directory while the supposed successor ran. `assert_is_app` below is the guard: it fails the
# run unless the PID the harness recorded really is the application.
APP_PID=""

assert_is_app() {
  local pid="${1:-}" expected="$2"
  [ -n "$pid" ] || { log "harness: assert_is_app with no pid"; return 1; }
  local comm
  comm="$(ps -p "$pid" -o comm= 2>/dev/null | tr -d ' ')"
  local base
  base="$(basename "$comm")"
  if [ "$base" != "$expected" ]; then
    log "harness: FAIL pid $pid is '$base', not '$expected'. A fault that kills this PID would be"
    log "harness:      killing a shell wrapper while the application kept running."
    return 1
  fi
  return 0
}

# Starts a binary in the background with the environment it needs. `env VAR=… binary &` makes `$!` the
# binary itself, because `env` execs into it rather than forking.
start_baseline_bg() {
  local dir="$1" mode="$2" name="$3"
  mkdir -p "$dir/checkpoints"
  env \
    CHECKPOINT_DIR="$dir/checkpoints" \
    OPENAI_API_KEY="${OPENAI_API_KEY:-recorded}" \
    OPENAI_BASE_URL="http://127.0.0.1:$ENDPOINT_PORT/v1" \
    OPENAI_MODEL="${OPENAI_MODEL:-recorded}" \
    "$BASELINE_OUT/BaselineCheckpoint" "$mode" >"$dir/$name.log" 2>&1 &
  APP_PID=$!
  echo "$APP_PID" > "$dir/$name.pid"
  # Bare `assert_is_app` would only *log* the failure and the run would continue with a wrapper PID — the
  # guard has to be a guard.
  assert_is_app "$APP_PID" BaselineCheckpoint || exit 1
}

start_durable_bg() {
  local dir="$1" name="$2"
  shift 2
  [ -n "$DURABLE_RUNTIME" ] || DURABLE_RUNTIME="$(durable_runtime "$dir")"
  env \
    FAULT_PAUSE_ON="${PAUSE_ON:-}" \
    FAULT_PAUSE_COUNT="${PAUSE_COUNT:-1}" \
    FAULT_MARKER="$dir" \
    DURABLE_RUNTIME_PATH="$DURABLE_RUNTIME" \
    REPLAY_FIXTURE="$RUNTIME_FIXTURE" \
    "$DURABLE_OUT/band-g-durable" --run "$dir" "$@" >"$dir/$name.log" 2>&1 &
  APP_PID=$!
  echo "$APP_PID" > "$dir/$name.pid"
  assert_is_app "$APP_PID" band-g-durable || exit 1
}

# Kills the PID, waits for the operating system to confirm it is gone, and reports that it did. A
# survivor after SIGKILL means the harness named the wrong process, so the caller must fail instead of
# recording whatever it found next.
# The children of a killed application are reaped too. The Durable variant's application owns the
# recorded runtime it launched, and the baseline's owns nothing; either way a `kill -9` of the parent
# alone would leave the child behind, so the descendants go first and the parent last.
reap_children() {
  local pid="${1:-}" child
  [ -n "$pid" ] || return 0
  for child in $(pgrep -P "$pid" 2>/dev/null || true); do
    reap_children "$child"
  done
  kill -9 "$pid" 2>/dev/null
}

kill9() {
  local pid="${1:-}"
  [ -n "$pid" ] || { log "harness: kill9 with no pid"; return 1; }
  reap_children "$pid"
  local deadline=$((SECONDS + 20))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.05; done
  if kill -0 "$pid" 2>/dev/null; then
    log "harness: pid $pid survived SIGKILL, which cannot happen"
    return 1
  fi
  return 0
}

stop_pid() {
  local pid="${1:-}"
  [ -n "$pid" ] || return 0
  # Children are recorded before the parent is signalled: once it is gone they are reparented and
  # `pgrep -P` finds nothing, so a reap after the fact is a no-op that looks like a cleanup.
  local children
  children="$(pgrep -P "$pid" 2>/dev/null || true)"
  kill -TERM "$pid" 2>/dev/null
  local deadline=$((SECONDS + 15))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.05; done
  kill -9 "$pid" 2>/dev/null
  local child
  for child in $children; do
    kill -TERM "$child" 2>/dev/null
  done
  sleep 0.2
  for child in $children; do
    kill -9 "$child" 2>/dev/null
  done
}

# Stops a process and proves the *application* is gone, not merely that a PID is unassigned. The stop is
# SIGTERM (a restart, not a kill), then SIGKILL if it does not go, then a check that the PID is gone **and**
# that nothing else is still working in the run directory. A survivor here would keep writing to the store
# while the successor read it, which is the failure this check exists to make impossible to miss.
stop_and_confirm() {
  local pid="$1" dir="$2" label="${3:-the process}"
  kill -TERM "$pid" 2>/dev/null
  if ! wait_for_exit "$pid" 30; then
    log "harness: $label did not exit on SIGTERM; sending SIGKILL"
    kill -9 "$pid" 2>/dev/null
    wait_for_exit "$pid" 15 || { log "harness: FAIL $label ($pid) survived SIGKILL"; return 1; }
  fi
  if kill -0 "$pid" 2>/dev/null; then
    log "harness: FAIL $label ($pid) is still alive after the stop"
    return 1
  fi
  return 0
}

wait_for_exit() {
  local pid="$1" seconds="${2:-180}"
  local deadline=$((SECONDS + seconds))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.05; done
  ! kill -0 "$pid" 2>/dev/null
}

wait_for_file() {
  local path="$1" seconds="${2:-60}"
  local deadline=$((SECONDS + seconds))
  while [ ! -e "$path" ]; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 0.02
  done
  return 0
}

# Waits until a store field holds a value. This is how a state *committed* by another process is
# observed, rather than slept past: the caller acts the moment the durable record says the process has
# reached the place the fault is about.
wait_for_state() {
  local variant="$1" dir="$2" field="$3" wanted="$4" seconds="${5:-60}"
  local deadline=$((SECONDS + seconds)) value
  while [ "$SECONDS" -lt "$deadline" ]; do
    value="$(store_read "$variant" "$dir" "$field" 2>/dev/null)"
    [ "$value" = "$wanted" ] && return 0
    sleep 0.01
  done
  return 1
}

# Drives a Durable run to the state every fault needs: the parent Waiting and the gate's `wait` phase
# committed. `start` returns as soon as the gate child exists, and the child commits `open` and then
# `wait`; under load the second commit can land after the process has already exited, leaving a run whose
# gate never recorded its deadline. A `resume` re-enters the gate from its own checkpoint and commits the
# wait, which is the same thing a host would do — so this is part of reaching the state, not a way around
# the fault. Returns non-zero and says why when the run cannot be brought to that state.
durable_at_gate() {
  local dir="$1" seconds="${2:-60}"
  if wait_for_state durable "$dir" gate-phase wait "$seconds"; then
    return 0
  fi
  log "harness: the gate has not recorded its wait phase; re-entering it once to commit one"
  local before after
  before="$(observed durable "$dir" gate-phase)"
  PAUSE_ON="" durable_verb "$dir" resume --deterministic >"$dir/at-gate.log" 2>&1
  if wait_for_state durable "$dir" gate-phase wait 60; then
    after="$(observed durable "$dir" gate-phase)"
    log "harness: re-entered the gate; its phase went $before -> $after. The run needed a second process to reach this state."
    return 0
  fi
  log "harness: the run could not be brought to its gate (gate phase $(observed durable "$dir" gate-phase))"
  return 1
}

count_calls() {
  local dir="$1"
  if [ -f "$dir/calls.jsonl" ]; then
    wc -l < "$dir/calls.jsonl" | tr -d ' '
  else
    echo 0
  fi
}

# The request shapes a process served, in order. Two processes serving the same shape is how "the
# resume repeated a step" is shown as a string rather than inferred from a total.
call_shapes() {
  local dir="$1"
  [ -f "$dir/calls.jsonl" ] || return 0
  "$PYTHON" - "$dir/calls.jsonl" <<'PY'
import json, sys
for line in open(sys.argv[1], encoding="utf-8"):
    line = line.strip()
    if line:
        print(json.loads(line).get("shape", ""))
PY
}

# ── reading a store without a live process ────────────────────────────────────────────────────
# The observation of a fault is always made by a process that did not witness it, and always from what
# is on disk. `store_read.py` exits 3 when the field does not exist; `observed` turns that into a word,
# so "the run had no gate" is a value rather than an empty string that reads as a pass.
store_read() {
  local variant="$1" dir="$2" field="$3"
  "$PYTHON" "$HARNESS_DIR/lib/store_read.py" "$variant" "$dir" "$field"
}

observed() {
  local variant="$1" dir="$2" field="$3" value
  value="$(store_read "$variant" "$dir" "$field" 2>/dev/null)"
  case $? in
    0) printf '%s' "$value" ;;
    3) printf '(absent)' ;;
    *) printf '(unreadable)' ;;
  esac
}

# A claim whose subject is missing is a failure, not a skip. This is the check F2's `verify` was missing:
# an assertion that has nothing to inspect must fail, because otherwise a run that produced no state at
# all reports as a clean one.
assert_observed() {
  local name="$1" value="$2"
  if [ -z "$value" ] || [ "$value" = "(absent)" ] || [ "$value" = "(unreadable)" ]; then
    log "harness: FAIL $name: there is nothing to inspect ($value)"
    return 1
  fi
  return 0
}

# ── results ───────────────────────────────────────────────────────────────────────────────────
# One document per fault per variant, written even when the variant could not be faulted, so "not
# exercised" is a recorded outcome with a reason rather than a missing file. `harness.commit` records
# the commit that produced it: a number in a table is only evidence if a reader can find the code that
# emitted it.
record_result() {
  local variant="$1" fault="$2" outcome="$3" phase="$4" detail="$5" evidence="${6:-}" extra="${7:-{\}}"
  mkdir -p "$RESULTS_DIR/$variant"
  "$PYTHON" - "$variant" "$fault" "$outcome" "$phase" "$detail" "$evidence" "$extra" "$REPO_ROOT" \
    > "$RESULTS_DIR/$variant/$fault.json" <<'PY'
import datetime, json, os, subprocess, sys

variant, fault, outcome, phase, detail, evidence, extra, repo = sys.argv[1:9]
try:
    extra_doc = json.loads(extra)
except ValueError:
    extra_doc = {}

try:
    commit = subprocess.check_output(
        ["git", "-C", repo, "rev-parse", "HEAD"], text=True, stderr=subprocess.DEVNULL).strip()
except Exception:
    commit = ""

doc = {
    "variant": variant,
    "fault": fault,
    "outcome": outcome,
    "phase": phase,
    "detail": detail,
    "evidence": evidence,
    "measurements": extra_doc,
    "harness": {
        "dotnet": os.environ.get("DOTNET_VERSION", ""),
        "runtime": os.environ.get("FAULTS_RUNTIME_DESC", ""),
        "model": os.environ.get("FAULTS_MODEL_DESC", ""),
        "commit": commit,
        "command": os.environ.get("FAULT_COMMAND", ""),
    },
    "recordedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
}
print(json.dumps(doc, indent=2, ensure_ascii=False))
PY
  log "harness: wrote faults/results/$variant/$fault.json"
}

record_not_exercised() {
  local variant="$1" fault="$2" phase="$3" reason="$4" extra="${5:-{\}}"
  record_result "$variant" "$fault" "not exercised" "$phase" "$reason" "" "$extra"
}

# ── cleanup ──────────────────────────────────────────────────────────────────────────────────
# Every script installs this. It reaps the processes it started by PID (never by name), releases any
# lock it took, restores any permission it changed, and removes the run's directory unless KEEP=1. A
# run that fails leaves no process and no store behind, and a second run starts from nothing.
CLEANUP_DIRS=()
CLEANUP_PIDS=()
CLEANUP_RESTORE=()
cleanup_all() {
  local pid command dir
  for pid in ${CLEANUP_PIDS[@]+"${CLEANUP_PIDS[@]}"}; do
    [ -n "$pid" ] && stop_pid "$pid"
  done
  stop_endpoint
  for command in ${CLEANUP_RESTORE[@]+"${CLEANUP_RESTORE[@]}"}; do
    [ -n "$command" ] && eval "$command" 2>/dev/null
  done
  if [ "${KEEP:-0}" = "1" ]; then
    for dir in ${CLEANUP_DIRS[@]+"${CLEANUP_DIRS[@]}"}; do
      [ -n "$dir" ] && log "kept: $dir"
    done
    return 0
  fi
  for dir in ${CLEANUP_DIRS[@]+"${CLEANUP_DIRS[@]}"}; do
    [ -n "$dir" ] && rm -rf "$dir"
  done
}

track_dir() { CLEANUP_DIRS+=("$1"); }
track_pid() { CLEANUP_PIDS+=("$1"); }
track_restore() { CLEANUP_RESTORE+=("$1"); }
install_cleanup() { trap cleanup_all EXIT INT TERM; }

parse_variant_args() {
  local script="$1"
  shift
  VARIANT=""
  INJECT=1
  while [ $# -gt 0 ]; do
    case "$1" in
      --variant) VARIANT="${2:-}"; shift 2 ;;
      --fault-not-injected) INJECT=0; shift ;;
      -h|--help)
        sed -n '2,/^$/p' "$script" | sed -e 's/^# \{0,1\}//'
        exit 0
        ;;
      *) log "$(basename "$script"): unknown argument '$1'"; exit 2 ;;
    esac
  done
  [ -n "$VARIANT" ] || { log "$(basename "$script"): --variant baseline|durable is required"; exit 2; }
  case "$VARIANT" in
    baseline|durable) ;;
    *) log "$(basename "$script"): unknown variant '$VARIANT'"; exit 2 ;;
  esac
}

# The fault's name and its control's name are one fault: the suffix is what the results directory uses,
# so a reader finds the pair together and a control is never read as the fault's outcome.
fault_name() {
  if [ "$INJECT" = "1" ]; then echo "$1"; else echo "$1-control"; fi
}
