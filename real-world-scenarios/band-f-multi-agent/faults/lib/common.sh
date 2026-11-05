#!/usr/bin/env bash
# The shared machinery every Band F fault script uses: publish once, start a variant, wait for a
# state the variant committed, fault it, observe who holds control, record it.
#
# A fault script is a caller of this file, not a copy of it. Four things live here:
#
#   - a *run directory*: scratch with a store (durable) or a checkpoint directory (baseline),
#     recreated per run so nothing is inherited;
#   - a *variant*: how to build it and how to start it;
#   - an *injection point*: waited for by reading the variant's committed state, never by sleeping
#     and hoping, and never by watching a log line;
#   - a *result*: one JSON file per fault per variant, written even when the fault could not be
#     exercised, so "not exercised" is a recorded outcome rather than a missing file.
#
# The rule this file exists to keep: who held control is read out of the state the variant says
# control lives in (`lib/holder.py`), not out of anything a process printed. The two variants are
# read at the same two places and reported in the same shape, so a difference between the rows is
# a difference between the variants and not between two readers.

set -uo pipefail

HARNESS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BAND_DIR="$(cd "$HARNESS_DIR/.." && pwd)"
REPO_ROOT="$(cd "$BAND_DIR/../.." && pwd)"
RESULTS_DIR="$HARNESS_DIR/results"
SCRATCH_ROOT="${FAULTS_SCRATCH:-$REPO_ROOT/.scratch/band-f-faults}"

DOTNET="${DOTNET:-/opt/homebrew/bin/dotnet}"
PYTHON="${PYTHON:-python3}"
BASELINE_DIR="$BAND_DIR/baseline"
DURABLE_DIR="$BAND_DIR/durable"

AGENTS="triage_agent refund_agent order_agent return_agent"

log() { printf '%s\n' "$*" >&2; }

# ── prerequisites ──────────────────────────────────────────────────────────

# The baseline port needs an OpenAI-compatible endpoint and refuses to run without one. When
# OPENAI_API_KEY is unset but an OPENROUTER_API_KEY is present, this harness uses it and records
# that it did (`apiKeyFrom` in every baseline result's harness block), rather than reading an
# equivalence the operator never stated. `baseline_unrunnable_reason` is what a baseline row quotes
# when there is no endpoint at all.
if [ -z "${OPENAI_API_KEY:-}" ] && [ -n "${OPENROUTER_API_KEY:-}" ]; then
  export OPENAI_API_KEY="$OPENROUTER_API_KEY"
  FAULTS_KEY_SOURCE="OPENROUTER_API_KEY"
elif [ -n "${OPENAI_API_KEY:-}" ]; then
  FAULTS_KEY_SOURCE="OPENAI_API_KEY"
else
  FAULTS_KEY_SOURCE=""
fi
export FAULTS_KEY_SOURCE

baseline_unrunnable_reason() {
  [ -n "${FAULTS_KEY_SOURCE:-}" ] && return 0
  echo "the baseline port needs an OpenAI-compatible endpoint and refuses to run without one: OPENAI_API_KEY is unset and there is no OPENROUTER_API_KEY to use instead, so this variant could not be started at all"
}

# The baseline port takes an OpenAI-compatible endpoint; the durable variant takes the SDK's bundled
# runtime. The harness publishes the same endpoint defaults the baseline README documents, so a
# result's detail can say which endpoint produced it.
export OPENAI_BASE_URL="${OPENAI_BASE_URL:-https://openrouter.ai/api/v1}"
export OPENAI_MODEL="${OPENAI_MODEL:-openai/gpt-4o-mini}"

require_tools() {
  local missing=0
  if [ ! -x "$DOTNET" ]; then log "harness: $DOTNET is not executable; .NET 10 is required"; missing=1; fi
  command -v "$PYTHON" >/dev/null 2>&1 || { log "harness: python3 is required"; missing=1; }
  command -v lsof >/dev/null 2>&1 || { log "harness: lsof is required to find the application's own pid"; missing=1; }
  return $missing
}

# ── building ──────────────────────────────────────────────────────────────

prepare_variant() {
  local variant="$1" out="$2"
  [ -f "$out/.built" ] && return 0
  mkdir -p "$out"
  log "harness: building $variant"
  case "$variant" in
    baseline)
      "$DOTNET" build "$BASELINE_DIR/BaselineHandoff.cs" -v quiet --nologo >/dev/null 2>&1 || {
        log "harness: the baseline does not build"
        return 1
      }
      ;;
    durable)
      "$DOTNET" build "$DURABLE_DIR/BandFDurable.csproj" -v quiet --nologo >/dev/null 2>&1 || {
        log "harness: the durable variant does not build"
        return 1
      }
      [ -f "$DURABLE_DIR/bin/Debug/net10.0/BandFDurable.dll" ] || {
        log "harness: the durable build produced no BandFDurable.dll"
        return 1
      }
      ;;
  esac
  : > "$out/.built"
}

# ── starting a variant ────────────────────────────────────────────────────

# The application's own pid. Both wrappers (`run.sh`, `resume.sh`) start the app and wait for it, so
# killing the wrapper would leave the app reparented to pid 1 — where nothing this harness holds can
# reach it and where it keeps the store lock. Every variant therefore identifies the app by the
# state file the app holds open, not by `$!`.
# Where a variant keeps its own state. The harness creates only the run directory around it, so the
# variant's scripts see a path that does not exist and create it themselves — which is the path they
# also refuse to touch if they did not create it. A fault therefore cannot be handed a directory the
# variant's own guard would (correctly) say no to.
state() { echo "$2/state"; }

app_pid() {
  local variant="$1" rundir="$2"
  local base; base="$(state "$variant" "$rundir")"
  local probe
  case "$variant" in
    durable) probe="$base/store.jsonl.lock" ;;
    baseline) probe="$base/index.jsonl" ;;
  esac
  local pid
  pid="$(lsof -t "$probe" 2>/dev/null | head -1)"
  if [ -z "$pid" ]; then
    # The lock sidecar exists from the first commit; before that the store file itself is the probe.
    case "$variant" in
      durable) probe="$base/store.jsonl" ;;
    esac
    pid="$(lsof -t "$probe" 2>/dev/null | head -1)"
  fi
  echo "$pid"
}

# Starts `run` for a variant in the background. Returns immediately; the app's pid is written to
# <rundir>/pid once it holds the store, and `await_app_pid` waits for that.
start_run() {
  local variant="$1" rundir="$2"
  shift 2
  local out="$rundir/.starter"
  local base; base="$(state "$variant" "$rundir")"
  mkdir -p "$rundir"
  if [ "$variant" = "durable" ]; then
    ( cd "$DURABLE_DIR" && KEEP=1 RUN_DIR="$base" BAND_F_ROUTING="${BAND_F_ROUTING:-scripted}" \
        "$DURABLE_DIR/run.sh" "$@" >"$out.log" 2>&1 ) &
  else
    ( cd "$BAND_DIR" && CHECKPOINT_DIR="$base" "$BAND_DIR/run.sh" >"$out.log" 2>&1 ) &
  fi
  echo $! > "$rundir/wrapper.pid"
}

start_resume() {
  local variant="$1" rundir="$2"
  shift 2
  local out="$rundir/.resume"
  local tag="${RESUME_TAG:-resume}"
  local base; base="$(state "$variant" "$rundir")"
  if [ "$variant" = "durable" ]; then
    ( cd "$DURABLE_DIR" && KEEP=1 RUN_DIR="$base" BAND_F_ROUTING="${BAND_F_ROUTING:-scripted}" \
        "$DURABLE_DIR/resume.sh" "$@" >"$out.$tag.log" 2>&1 ) &
  else
    ( cd "$BAND_DIR" && CHECKPOINT_DIR="$base" CONTROL_LOG="${CONTROL_LOG:-$base/control-transfer.jsonl}" \
        "$BAND_DIR/run.sh" --resume "$base" >"$out.$tag.log" 2>&1 ) &
  fi
  echo $! > "$rundir/wrapper.$tag.pid"
}

# Waits until the variant's store is held by a live application, then records its pid.
await_app_pid() {
  local variant="$1" rundir="$2" seconds="${3:-120}"
  local deadline=$((SECONDS + seconds)) pid
  while [ "$SECONDS" -lt "$deadline" ]; do
    pid="$(app_pid "$variant" "$rundir")"
    if [ -n "$pid" ]; then
      echo "$pid" > "$rundir/pid"
      echo "$pid"
      return 0
    fi
    if [ -f "$rundir/wrapper.pid" ] && ! kill -0 "$(cat "$rundir/wrapper.pid")" 2>/dev/null; then
      return 1
    fi
    sleep 0.2
  done
  return 1
}

# Runs a variant to completion in the foreground and prints its status.
run_to_completion() {
  local variant="$1" rundir="$2"
  shift 2
  local base; base="$(state "$variant" "$rundir")"
  if [ "$variant" = "durable" ]; then
    ( cd "$DURABLE_DIR" && KEEP=1 RUN_DIR="$base" BAND_F_ROUTING="${BAND_F_ROUTING:-scripted}" \
        "$DURABLE_DIR/run.sh" "$@" )
  else
    ( cd "$BAND_DIR" && CHECKPOINT_DIR="$base" "$BAND_DIR/run.sh" )
  fi
}

resume_to_completion() {
  local variant="$1" rundir="$2"
  shift 2
  local tag="${RESUME_TAG:-resume}"
  local base; base="$(state "$variant" "$rundir")"
  if [ "$variant" = "durable" ]; then
    ( cd "$DURABLE_DIR" && KEEP=1 RUN_DIR="$base" BAND_F_ROUTING="${BAND_F_ROUTING:-scripted}" \
        "$DURABLE_DIR/resume.sh" "$@" ) > "$rundir/.resume.$tag.log" 2>&1
  else
    ( cd "$BAND_DIR" && CHECKPOINT_DIR="$base" CONTROL_LOG="${CONTROL_LOG:-$base/control-transfer.jsonl}" \
        "$BAND_DIR/run.sh" --resume "$base" ) > "$rundir/.resume.$tag.log" 2>&1
  fi
  echo $?
}

# ── the injection points, read out of committed state ────────────────────

# The variant's own committed holder record, as JSON.
holder_json() {
  local variant="$1" rundir="$2"
  case "$variant" in
    durable) "$PYTHON" "$HARNESS_DIR/lib/holder.py" durable "$(state "$variant" "$rundir")/store.jsonl" ;;
    baseline) "$PYTHON" "$HARNESS_DIR/lib/holder.py" baseline "$(state "$variant" "$rundir")" ;;
  esac
}

holder_now() {
  local variant="$1" rundir="$2"
  holder_json "$variant" "$rundir" |
    "$PYTHON" -c 'import json,sys; d=json.load(sys.stdin); print(d.get("holder") or "")'
}

# The agent (never the coordinator) the newest state names as holding control, read from the state.
# Empty until the store names one. This is the injection point for every fault that needs the
# specialist to be in control: the state says so before the fault is injected, so the fault knows
# the moment it is acting on.
await_holder() {
  local variant="$1" rundir="$2" wanted="$3" seconds="${4:-180}"
  local deadline=$((SECONDS + seconds)) current
  while [ "$SECONDS" -lt "$deadline" ]; do
    current="$(holder_now "$variant" "$rundir")"
    case " $wanted " in
      *" $current "*) [ -n "$current" ] && return 0 ;;
    esac
    sleep 0.2
  done
  return 1
}

# Waits until the durable variant's checkpoint is at a given phase (and, optionally, turn).
await_phase() {
  local rundir="$1" phase="$2" turn="${3:-}"
  local deadline=$((SECONDS + ${4:-180})) j
  while [ "$SECONDS" -lt "$deadline" ]; do
    j="$(holder_json durable "$rundir")"
    local got_phase got_turn
    got_phase="$(printf '%s' "$j" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin)["detail"].get("phase") or "")')"
    got_turn="$(printf '%s' "$j" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin)["detail"].get("turn"))')"
    if [ "$got_phase" = "$phase" ] && { [ -z "$turn" ] || [ "$got_turn" = "$turn" ]; }; then
      return 0
    fi
    sleep 0.2
  done
  return 1
}

# The pid a variant's own seam announced, once the commit it names has returned. The seam prints
# `[seam] ready-to-kill pid=<n>` and then waits; the harness stops that pid. Reading a line the
# variant wrote is legitimate here because the seam exists to be found by a harness, and because what
# it names is a pid, not a holder: control is still read from committed state.
await_seam_pid() {
  local rundir="$1" seconds="${2:-300}"
  local deadline=$((SECONDS + seconds)) log="$rundir/.starter.log"
  while [ "$SECONDS" -lt "$deadline" ]; do
    if [ -f "$log" ] && grep -q 'ready-to-kill pid=' "$log" 2>/dev/null; then
      grep -o 'ready-to-kill pid=[0-9]*' "$log" | head -1 | cut -d= -f2
      return 0
    fi
    if [ -f "$rundir/wrapper.pid" ] && ! kill -0 "$(cat "$rundir/wrapper.pid")" 2>/dev/null; then
      return 1
    fi
    sleep 0.2
  done
  return 1
}

# Waits until the variant's committed holder state differs from a snapshot taken by the caller, and
# prints the fresh JSON. This is how a fault observes the *first* thing recovery did, rather than
# only where the run ended. A process that finishes without moving the state is not a failure of the
# wait: the timeout returns the final state and the caller decides what that means.
await_state_change() {
  local variant="$1" rundir="$2" before="$3" seconds="${4:-180}"
  local deadline=$((SECONDS + seconds)) now
  while [ "$SECONDS" -lt "$deadline" ]; do
    now="$(holder_json "$variant" "$rundir")"
    if [ "$now" != "$before" ]; then
      printf '%s' "$now"
      return 0
    fi
    if [ -f "$rundir/wrapper.resume.pid" ] && ! kill -0 "$(cat "$rundir/wrapper.resume.pid")" 2>/dev/null; then
      break
    fi
    sleep 0.2
  done
  holder_json "$variant" "$rundir"
}

# Waits for a wrapper the harness started (`wrapper`, or `wrapper.<tag>`) to exit. The wrapper is
# `run.sh`/`resume.sh`, which waits for the application in turn, so this is how a caller observes the
# end of a foreground run it started in the background.
wait_wrapper() {
  local rundir="$1" tag="${2:-wrapper}" seconds="${3:-300}"
  local pid_file="$rundir/$tag.pid"
  [ -f "$pid_file" ] || return 1
  local pid; pid="$(cat "$pid_file")"
  local deadline=$((SECONDS + seconds))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.2; done
  if kill -0 "$pid" 2>/dev/null; then return 1; fi
  return 0
}

# Recovery, in as many processes as it takes and no more. A resume is a process like any other, so a
# recovery that needed two of them is recorded as that (`attempts`), not hidden.
#
# The baseline needs the retry: every resume of a workflow that still has work to do aborts inside
# the framework with `Expected exactly one update for key 'SharedState'`. The aborted attempt leaves
# a newer checkpoint behind; the next process starts from that one and aborts there too. The harness
# tries a bounded number of times and records every abort, because "the port's resume cannot
# continue a killed workflow" is the finding, not something to paper over with a loop that pretends
# to succeed.
RECOVERY_ATTEMPTS=0
RECOVERY_ERRORS=""
RECOVERY_STATUS=1
RECOVERY_LAST_LOG=""
recover_variant() {
  local variant="$1" rundir="$2" max="${3:-3}"
  RECOVERY_ATTEMPTS=0
  RECOVERY_ERRORS=""
  RECOVERY_LAST_LOG=""
  local attempt status log
  for attempt in $(seq 1 "$max"); do
    RECOVERY_ATTEMPTS="$attempt"
    RESUME_TAG="resume$attempt"
    status="$(resume_to_completion "$variant" "$rundir")"
    log="$rundir/.resume.resume$attempt.log"
    RECOVERY_LAST_LOG="$log"
    if [ "$variant" = "baseline" ] &&
       grep -qi "Expected exactly one update for key 'SharedState'" "$log" 2>/dev/null; then
      if [ -n "$RECOVERY_ERRORS" ]; then RECOVERY_ERRORS="$RECOVERY_ERRORS; "; fi
      RECOVERY_ERRORS="${RECOVERY_ERRORS}attempt $attempt aborted with the framework's \"Expected exactly one update for key 'SharedState'\""
      continue
    fi
    RECOVERY_STATUS="$status"
    return "$status"
  done
  RECOVERY_STATUS=1
  return 1
}

# Waits for a background resume's wrapper to exit, for the faults that observe a process while it is
# still running (the duplicate delivery does this).
wait_resume_wrapper() {
  local rundir="$1" tag="${RESUME_TAG:-resume}" seconds="${2:-400}"
  wait_wrapper "$rundir" "wrapper.$tag" "$seconds"
}

# ── stopping ──────────────────────────────────────────────────────────────

kill_tree() {
  local pid="${1:-}"
  [ -n "$pid" ] || return 0
  local child
  for child in $(pgrep -P "$pid" 2>/dev/null || true); do kill_tree "$child"; done
  kill -9 "$pid" 2>/dev/null || true
  local deadline=$((SECONDS + 10))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.1; done
}

# A real SIGKILL of the application's own pid, confirmed gone. Not the wrapper's, not a clean stop:
# nothing in the process runs afterwards, so what is in the store is what the commits put there.
kill_and_confirm() {
  local variant="$1" rundir="$2"
  local pid; pid="$(cat "$rundir/pid" 2>/dev/null || true)"
  [ -n "$pid" ] || return 1
  kill -9 "$pid" 2>/dev/null || return 1
  local deadline=$((SECONDS + 20))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.1; done
  if kill -0 "$pid" 2>/dev/null; then
    log "harness: pid $pid survived SIGKILL"
    return 1
  fi
  KILLED_PID="$pid"
  echo "$pid"
}

# SIGTERM then wait, confirmed gone. The clean stop `restart-after-checkpoint` needs, and
# deliberately not what `kill-mid-delegation` uses.
stop_and_confirm() {
  local variant="$1" rundir="$2"
  local pid; pid="$(cat "$rundir/pid" 2>/dev/null || true)"
  [ -n "$pid" ] || return 1
  kill -TERM "$pid" 2>/dev/null || true
  local deadline=$((SECONDS + 30))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.1; done
  if kill -0 "$pid" 2>/dev/null; then
    log "harness: pid $pid did not stop on SIGTERM"
    return 1
  fi
  STOPPED_PID="$pid"
  echo "$pid"
}

# Reaps everything a run started: the application (by pid), its wrapper, and anything else whose
# command line names the run's directory. Used by the cleanup trap, never between a fault and its
# restart.
reap() {
  local variant="$1" rundir="$2"
  local pid
  pid="$(cat "$rundir/pid" 2>/dev/null || true)"
  kill_tree "$pid"
  for f in "$rundir/wrapper.pid" "$rundir"/wrapper.*.pid; do
    [ -f "$f" ] || continue
    kill_tree "$(cat "$f" 2>/dev/null || true)"
  done
  local stray
  for stray in $(pgrep -f "$rundir" 2>/dev/null || true); do
    [ "$stray" = "$$" ] && continue
    kill -9 "$stray" 2>/dev/null || true
  done
  rm -f "$rundir/pid" "$rundir/wrapper.pid" "$rundir"/wrapper.*.pid
}

# ── observing ─────────────────────────────────────────────────────────────

# The control records the durable store holds, one JSON object per line.
durable_wire() {
  "$PYTHON" "$HARNESS_DIR/lib/holder.py" --wire "$1"
}

# A count over the durable store, read from the file: how many wire records match an event, how many
# versions a turn's output has, how many times the specialist's tool result was recorded.
durable_count() {
  local store="$1" what="$2" turn="${3:-}"
  "$PYTHON" - "$store" "$what" "$turn" <<'PY'
import json, sys
store, what, turn = sys.argv[1], sys.argv[2], sys.argv[3]
wire, outputs = [], {}
try:
    handle = open(store, encoding="utf-8")
except OSError:
    print(0); raise SystemExit(0)
for line in handle:
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    if record.get("type") != "document-version":
        continue
    payload = record.get("payload") or {}
    kind = payload.get("kind")
    value = payload.get("value") or {}
    if kind == "band-f.wire":
        wire.append(value)
    elif kind == "band-f.specialist-output":
        outputs.setdefault(value.get("turn"), []).append(value)
if what == "delegations":
    print(sum(1 for w in wire if str(w.get("event", "")).startswith("delegated")
              and (turn == "" or str(w.get("turn")) == turn)))
elif what == "reused":
    print(sum(1 for w in wire if w.get("event") == "reused-output"
              and (turn == "" or str(w.get("turn")) == turn)))
elif what == "produced":
    print(sum(1 for w in wire if w.get("event") == "produced"
              and (turn == "" or str(w.get("turn")) == turn)))
elif what == "output-versions":
    print(len(outputs.get(int(turn), [])) if turn != "" else sum(len(v) for v in outputs.values()))
elif what == "model-turns":
    values = outputs.get(int(turn), []) if turn != "" else [o for v in outputs.values() for o in v]
    print(sum(int(o.get("modelTurns") or 0) for o in values))
elif what == "outputs":
    print(sum(len(v) for v in outputs.values()))
elif what == "turns":
    print(len({w.get("turn") for w in wire if w.get("turn") is not None}))
else:
    print(-1)
PY
}

# The baseline's own control-transfer log, counted for cross-checking only: the harness never
# attributes control from it (that is what `holder.py` is for), but a re-delivery is visible here
# as a repeated user turn and a repeated tool run.
baseline_log_count() {
  local rundir="$1" what="$2"
  "$PYTHON" - "$(state baseline "$rundir")/control-transfer.jsonl" "$what" <<'PY'
import json, sys
path, what = sys.argv[1], sys.argv[2]
records = []
try:
    handle = open(path, encoding="utf-8")
except OSError:
    print(0); raise SystemExit(0)
for line in handle:
    line = line.strip()
    if not line:
        continue
    try:
        records.append(json.loads(line))
    except ValueError:
        continue
if what == "turn_end":
    print(sum(1 for r in records if r.get("Event") == "turn_end"))
elif what == "handoffs":
    print(sum(1 for r in records if r.get("Event") == "handoff"))
elif what == "control":
    print(sum(1 for r in records if r.get("Event") == "control"))
elif what == "resumed":
    print(sum(1 for r in records if r.get("Event") == "resumed"))
else:
    print(-1)
PY
}

# How many checkpoints the baseline's framework store holds: the count `holder.py` reads, so a
# "the store did not move" claim is checkable without opening a single checkpoint document by hand.
baseline_checkpoint_count() {
  "$PYTHON" - "$(state baseline "$1")" <<'PY'
import json, os, sys
directory = sys.argv[1]
path = os.path.join(directory, "index.jsonl")
try:
    handle = open(path, encoding="utf-8")
except OSError:
    print(0); raise SystemExit(0)
count = 0
for line in handle:
    line = line.strip()
    if line:
        try:
            if (json.loads(line).get("checkpointInfo") or {}).get("checkpointId"):
                count += 1
        except ValueError:
            pass
print(count)
PY
}

# The multiset of specialist tool results, as a sorted JSON object of result -> count. Comparing two
# of these is how a duplicate delivery is detected: a result the control run produced once and the
# fault run produced twice is the doubling; one produced once in each is not.
# The authors of the checkpointed conversation, in order, as a JSON list. Snapshotting this at the
# injection point is what lets a caller name the first agent the *recovered* process put in control:
# the first author beyond the snapshot is that agent.
baseline_authors() {
  "$PYTHON" "$HARNESS_DIR/lib/holder.py" --authors "$(state baseline "$1")"
}

tool_counts() {
  local variant="$1" rundir="$2"
  if [ "$variant" = "durable" ]; then
    "$PYTHON" - "$(state durable "$rundir")/store.jsonl" <<'COUNTS'
import collections, json, sys
counts = collections.Counter()
for line in open(sys.argv[1], encoding="utf-8"):
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    if record.get("type") != "document-version":
        continue
    payload = record.get("payload") or {}
    if payload.get("kind") != "band-f.specialist-output":
        continue
    result = (payload.get("value") or {}).get("toolResult")
    if result:
        counts[str(result)] += 1
print(json.dumps(dict(sorted(counts.items()))))
COUNTS
  else
    "$PYTHON" - "$HARNESS_DIR" "$(state baseline "$rundir")" <<'COUNTS'
import collections, json, os, sys
sys.path.insert(0, os.path.join(sys.argv[1], "lib"))
import tool_results
document = tool_results.newest(sys.argv[2])
counts = collections.Counter(tool_results.tool_results(document) if document else [])
print(json.dumps(dict(sorted(counts.items()))))
COUNTS
  fi
}

# How many times the baseline's specialists actually ran the tool of a turn, read from the agent
# session the framework checkpointed: each tool result is a `tool` role message in the shared
# conversation, so a re-delivery shows up as a second one.
baseline_tool_results() {
  "$PYTHON" - "$(state baseline "$1")" <<'PY'
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(sys.argv[0])))
directory = sys.argv[1]
index = os.path.join(directory, "index.jsonl")
order = []
try:
    for line in open(index, encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        entry = json.loads(line)
        info = entry.get("checkpointInfo") or {}
        if info.get("checkpointId") and entry.get("fileName"):
            order.append(os.path.join(directory, entry["fileName"]))
except (OSError, ValueError):
    order = []
if not order:
    print(0); raise SystemExit(0)
documents = []
for path in order:
    try:
        documents.append(json.load(open(path, encoding="utf-8")))
    except (OSError, ValueError):
        pass
if not documents:
    print(0); raise SystemExit(0)
newest = max(documents, key=lambda d: d.get("stepNumber", 0))
for key, value in (newest.get("stateData") or {}).items():
    if not key.endswith("SharedState"):
        continue
    history = (((value or {}).get("value") or {}).get("conversation") or {}).get("history") or []
    results = []
    for message in history:
        for content in message.get("contents") or []:
            if content.get("$type") == "functionResult" and content.get("result") is not None:
                results.append(str(content.get("result")))
    print(len(results))
    raise SystemExit(0)
print(0)
PY
}

# One field of a JSON document, by dotted path.
json_get() {
  "$PYTHON" -c "$(cat <<'INNER'
import json, sys
try:
    value = json.load(open(sys.argv[1]))
except Exception:
    print("")
    raise SystemExit(1)
for part in sys.argv[2].split("."):
    if not part:
        continue
    try:
        value = value[int(part)] if isinstance(value, list) else value.get(part)
    except Exception:
        value = None
    if value is None:
        print("")
        raise SystemExit(0)
print("" if value is None else value)
INNER
)" "$1" "$2"
}

# The last few lines of a log the harness kept, for a result's evidence.
tail_log() {
  local path="$1" lines="${2:-12}"
  [ -f "$path" ] || { echo ""; return 0; }
  tail -"$lines" "$path" | sed "s|$SCRATCH_ROOT|<scratch>|g"
}

# ── recording ─────────────────────────────────────────────────────────────

write_result() {
  local variant="$1" fault="$2" json="$3"
  mkdir -p "$RESULTS_DIR/$variant"
  printf '%s\n' "$json" > "$RESULTS_DIR/$variant/$fault.json"
  log "harness: wrote faults/results/$variant/$fault.json"
}

# The one way a result is recorded. `holder` is the post-recovery controlling agent — the band's
# assertion — and `source` says which committed state it was read out of. `detail` says what was
# observed; `evidence` carries the raw observations the claim comes from.
record_result() {
  local variant="$1" fault="$2" outcome="$3" holder="$4" phase="$5" detail="$6" evidence="${7:-}"
  local observations="${8:-}" holder_at_fault="${9:-}" holder_at_end="${10:-}"
  local json
  json="$("$PYTHON" - "$variant" "$fault" "$outcome" "$holder" "$phase" "$detail" "$evidence" "$observations" "$holder_at_fault" "$holder_at_end" <<'PY'
import datetime, json, os, sys
variant, fault, outcome, holder, phase, detail, evidence, observations, holder_at_fault, holder_at_end = sys.argv[1:11]
try:
    observations = json.loads(observations) if observations else None
except ValueError:
    observations = {"raw": observations}
document = {
    "variant": variant,
    "fault": fault,
    "outcome": outcome,
    "holder": holder or None,
    "holderAtFault": holder_at_fault or None,
    "holderAtEnd": holder_at_end or None,
    "phase": phase,
    "detail": detail,
    "evidence": evidence,
    "harness": {
        "dotnet": os.environ.get("FAULTS_DOTNET_VERSION", os.environ.get("DOTNET", "dotnet")),
        "endpoint": os.environ.get("OPENAI_BASE_URL", ""),
        "model": os.environ.get("OPENAI_MODEL", "") if variant == "baseline" else "harness default (scripted routing)",
        "command": os.environ.get("FAULT_COMMAND", ""),
        "commit": os.environ.get("FAULTS_COMMIT", ""),
        "apiKeyFrom": os.environ.get("FAULTS_KEY_SOURCE", ""),
    },
    "recordedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
}
if observations is not None:
    document["observations"] = observations
print(json.dumps(document, indent=2, ensure_ascii=False))
PY
)"
  write_result "$variant" "$fault" "$json"
}

record_not_exercised() {
  local variant="$1" fault="$2" phase="$3" reason="$4"
  record_result "$variant" "$fault" "not exercised" "" "$phase" "$reason" ""
}

new_run_dir() {
  local variant="$1" fault="$2"
  local dir="$SCRATCH_ROOT/$variant/$fault"
  rm -rf "$dir"
  mkdir -p "$dir"
  echo "$dir"
}

# A complete reading of the variant's state, from `holder.py snapshot`. This is the one call a fault
# makes before and after injecting, so the two readings are directly comparable.
snapshot() {
  local path
  if [ "$1" = "durable" ]; then path="$(state durable "$2")/store.jsonl"; else path="$(state baseline "$2")"; fi
  "$PYTHON" "$HARNESS_DIR/lib/holder.py" snapshot "$1" "$path" 2>/dev/null || echo '{}'
}

# One top-level field of a snapshot, as a string. Empty when the key is absent.
snap() {
  "$PYTHON" -c '
import json, sys
try:
    document = json.loads(sys.argv[1])
except ValueError:
    print("")
    raise SystemExit(0)
value = document.get(sys.argv[2])
if value is None:
    print("")
elif isinstance(value, (dict, list)):
    print(json.dumps(value, sort_keys=True))
else:
    print(value)' "$1" "$2"
}

# The control-transfer line the baseline's port prints about its own resume ("resumed with X holding
# control"). Recorded, never used as the answer: on a kill at a specialist it names a different agent
# from the one that then acts, and that disagreement is part of what this band reports.
port_reported_resume() {
  local rundir="$1"
  local log
  for log in "$rundir"/.resume.resume*.log; do
    [ -f "$log" ] || continue
    grep -o 'resumed with [a-z_]* holding control' "$log" 2>/dev/null | head -1 | cut -d' ' -f3
    return 0
  done
  echo ""
}

# Whether a snapshot moved: the fingerprint covers the sequence number (durable) or the checkpoint
# count and conversation length (baseline), so a commit of any kind changes it.
snapshot_moved() {
  local before="$1" after="$2"
  [ "$(snap "$before" fingerprint)" != "$(snap "$after" fingerprint)" ]
}

# The post-recovery holder, as every fault reports it.
#
# `holder` is the agent the recovered process gave control to: the holder the state named when the
# process opened it, which is the only thing the durable variant consults and the thing the baseline's
# resume reads back. `acted` is the first agent that actually committed something in the recovered
# process, derived independently, so the claim is corroborated rather than asserted. When the two
# differ the outcome names both, and says which way.
#
# This is deliberately not where the run ended. A run that completes hands control back to the
# coordinator, so "the run ended with triage_agent" is true of nearly every successful recovery and
# answers nothing about who took over after the fault.
describe_recovery() {
  local holder="$1" acted="${2:-}"
  if [ -z "$holder" ]; then
    echo "nothing resumed"
  elif [ -z "$acted" ] || [ "$acted" = "$holder" ]; then
    echo "resumed by $holder"
  elif [ "$holder" = "triage_agent" ]; then
    echo "resumed by $holder, which routed the turn on to $acted"
  else
    echo "resumed at $holder, where $acted acted first"
  fi
}

# ── the shape every fault script shares ───────────────────────────────────

# Parses `--variant` and `--fault-not-injected`, publishes the variant, and prints the result name
# (`<fault>` or `<fault>-control`). A script that cannot run the variant at all records
# `not exercised` with the reason and exits 0: a recorded absence, never a missing file.
begin_fault() {
  local fault="$1"
  shift
  VARIANT=""
  NOT_INJECTED=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --variant) VARIANT="${2:-}"; shift 2 ;;
      --variant=*) VARIANT="${1#--variant=}"; shift ;;
      --fault-not-injected) NOT_INJECTED=1; shift ;;
      -h|--help) echo "usage: $0 --variant baseline|durable [--fault-not-injected]"; exit 0 ;;
      *) log "$fault: unknown argument '$1'"; exit 2 ;;
    esac
  done
  case "$VARIANT" in
    baseline|durable) ;;
    *) log "$fault: --variant baseline|durable is required"; exit 2 ;;
  esac

  FAULT="$fault"
  RESULT_NAME="$fault"
  [ "$NOT_INJECTED" = "1" ] && RESULT_NAME="$fault-control"
  FAULT_COMMAND="$0 --variant $VARIANT$([ "$NOT_INJECTED" = "1" ] && echo ' --fault-not-injected')"
  export FAULT_COMMAND

  require_tools || exit 1
  prepare_variant "$VARIANT" "$SCRATCH_ROOT/build/$VARIANT" || exit 1

  if [ "$VARIANT" = "baseline" ]; then
    local reason
    reason="$(baseline_unrunnable_reason)" || {
      record_not_exercised baseline "$RESULT_NAME" "the baseline could not be started" "$reason"
      echo "not exercised: $reason" >&2
      exit 0
    }
  fi
  return 0
}

# One run directory per fault per variant per control, with the variant's own state beneath it.
new_fault_dir() {
  new_run_dir "$VARIANT" "$RESULT_NAME"
}

# The variant's `verify`, when it has one. The durable variant's checks are the band's store-level
# claims; the baseline has no verifier, so its rows say so rather than inventing one.
verify_store() {
  local rundir="$1"
  [ "$VARIANT" = "durable" ] || { echo "(the baseline has no verifier)"; return 0; }
  BAND_F_RUN_DIR="$(state durable "$rundir")" "$DOTNET" "$DURABLE_DIR/bin/Debug/net10.0/BandFDurable.dll" verify
}

# ── cleanup ───────────────────────────────────────────────────────────────

CLEANUP_VARIANTS=()
CLEANUP_DIRS=()
cleanup_all() {
  local status=$?
  local index dir
  for index in "${!CLEANUP_DIRS[@]}"; do
    dir="${CLEANUP_DIRS[$index]}"
    reap "${CLEANUP_VARIANTS[$index]}" "$dir"
    # chmod back before removing: a run whose fault left a directory read-only must still be
    # removable, or a failed fault would leave a scratch directory nothing can clear.
    chmod -R u+rwX "$dir" 2>/dev/null || true
    if [ "${KEEP:-0}" != "1" ]; then
      rm -rf "$dir"
    fi
  done
  return $status
}
trap cleanup_all EXIT INT TERM

track_dir() {
  CLEANUP_VARIANTS+=("$1")
  CLEANUP_DIRS+=("$2")
}

# The commit every result in this run was produced at, so a checked-in result names its producer.
FAULTS_COMMIT="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || true)"
export FAULTS_COMMIT
FAULTS_DOTNET_VERSION="$("$DOTNET" --version 2>/dev/null || true)"
export FAULTS_DOTNET_VERSION
