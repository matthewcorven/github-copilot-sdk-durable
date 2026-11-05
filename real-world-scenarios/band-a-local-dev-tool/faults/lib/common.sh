#!/usr/bin/env bash
# The shared machinery every fault script uses: publish once, start a variant, drive it, fault it,
# observe it, record it.
#
# A fault script is a caller of this file, not a copy of it. Four things live here:
#
#   - a *run directory*: scratch with a store and a log, recreated per run so nothing is inherited;
#   - a *variant*: how to publish it and how to start it;
#   - a *driver*: the browser that creates an enquiry through the page, which is the only entry point
#     the baseline has;
#   - a *result*: one JSON file per fault per variant, written even when the fault could not be
#     exercised, so "not exercised" is a recorded outcome rather than a missing file.
#
# The rule this file exists to keep: a fault is injected against a real process, by the operating
# system, at a moment the harness can point at. Nothing here fakes a crash in-process, and the cleanup
# trap is what makes the whole thing safe to re-run.

set -uo pipefail

HARNESS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BAND_DIR="$(cd "$HARNESS_DIR/.." && pwd)"
REPO_ROOT="$(cd "$BAND_DIR/../.." && pwd)"
# Where results land. The checked-in directory is the evidence, so a run only writes there when it is
# told to promote; a plain run writes into a directory of its own and prints the command that would
# promote it. That is what stops a machine without Playwright from silently replacing five measured
# baseline rows with "not exercised".
RESULTS_DIR="${FAULTS_RESULTS_DIR:-$HARNESS_DIR/results}"
CHECKED_IN_RESULTS="$HARNESS_DIR/results"
SCRATCH_ROOT="${FAULTS_SCRATCH:-$REPO_ROOT/.scratch/band-a-faults}"
COMMIT="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
export FAULTS_COMMIT="$COMMIT"

DOTNET="${DOTNET:-/opt/homebrew/bin/dotnet}"
PYTHON="${PYTHON:-python3}"
RUNTIME="${FAULTS_RUNTIME:-$REPO_ROOT/tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime}"
FIXTURES="$BAND_DIR/durable/fixtures"

log() { printf '%s\n' "$*" >&2; }

require_tools() {
  local missing=0
  if [ ! -x "$DOTNET" ]; then log "harness: $DOTNET is not executable; .NET 10 is required"; missing=1; fi
  if [ ! -x "$RUNTIME" ]; then
    log "harness: the recorded runtime is not built: $RUNTIME"
    log "harness: $DOTNET build $REPO_ROOT/tools/ReplayRuntime/ReplayRuntime.csproj"
    missing=1
  fi
  command -v "$PYTHON" >/dev/null 2>&1 || { log "harness: python3 is required"; missing=1; }
  command -v node >/dev/null 2>&1 || { log "harness: node is required for the browser driver"; missing=1; }
  return $missing
}

# One port per fault per variant: two runs never fight over a socket, and a stale listener is found by
# asking for the port rather than by reading a log.
port_for() {
  case "$1:$2" in
    baseline:kill-mid-turn) echo 5211 ;;
    baseline:restart-after-checkpoint) echo 5212 ;;
    baseline:duplicate-delivery) echo 5213 ;;
    baseline:concurrent-clients) echo 5214 ;;
    baseline:store-outage) echo 5215 ;;
    baseline:kill-mid-turn-control) echo 5216 ;;
    baseline:restart-after-checkpoint-control) echo 5217 ;;
    baseline:duplicate-delivery-control) echo 5218 ;;
    baseline:concurrent-clients-not-injected) echo 5219 ;;
    baseline:store-outage-control) echo 5220 ;;
    durable:kill-mid-turn) echo 5221 ;;
    durable:restart-after-checkpoint) echo 5222 ;;
    durable:duplicate-delivery) echo 5223 ;;
    durable:concurrent-clients) echo 5224 ;;
    durable:store-outage) echo 5225 ;;
    durable:kill-mid-turn-control) echo 5226 ;;
    durable:restart-after-checkpoint-control) echo 5227 ;;
    durable:duplicate-delivery-control) echo 5228 ;;
    durable:concurrent-clients-not-injected) echo 5229 ;;
    durable:store-outage-control) echo 5230 ;;
    *) echo 5298 ;;
  esac
}

variant_project() {
  case "$1" in
    baseline) echo "$BAND_DIR/baseline/AgentOrchestrator.csproj" ;;
    durable) echo "$BAND_DIR/durable/BandADurable.csproj" ;;
  esac
}

variant_assembly() {
  case "$1" in
    baseline) echo BandABaseline.dll ;;
    durable) echo BandADurable.dll ;;
  esac
}

# Publishes a variant once per run. The publish is the slow step, so a script that restarts the app
# reuses the output rather than paying for it twice.
prepare_variant() {
  local variant="$1" out="$2"
  local project assembly
  project="$(variant_project "$variant")"
  assembly="$(variant_assembly "$variant")"
  [ -f "$out/$assembly" ] && return 0
  log "harness: publishing $variant"
  "$DOTNET" publish "$project" -c Release -o "$out" --nologo -v quiet 2>&1 |
    grep -v 'NU1903\|^warn: Microsoft.EntityFrameworkCore\|^ *$' >&2
  [ -f "$out/$assembly" ] || { log "harness: publish of $variant produced no $assembly"; return 1; }
}

# The gate in front of the recorded runtime, which is where a fault script's injection point lives.
write_gate() {
  local path="$1" fixture="$2" logfile="$3"
  cat > "$path" <<GATE
#!/usr/bin/env bash
exec "$PYTHON" "$HARNESS_DIR/lib/runtime_gate.py" "$RUNTIME" "$fixture" "$logfile"
GATE
  chmod +x "$path"
}

# Starts a variant and records its PID in <rundir>/pid. Extra arguments go to the application; the
# Durable variant uses them for `--resume` and `--enquiry`, the baseline is never given any because it
# has no command line.
start_variant() {
  local variant="$1" rundir="$2" port="$3" fixture="$4"
  shift 4
  local extra=("$@")
  local out="$rundir/app"
  local store="$rundir/store.jsonl"
  local assembly
  assembly="$(variant_assembly "$variant")"
  write_gate "$rundir/copilot" "$FIXTURES/$fixture" "$rundir/runtime.log"

  if [ "$variant" = "baseline" ]; then
    # Started by this shell, not in a subshell: `wait` must be able to report this process's status, and
    # `kill -9` must reach the application rather than a shell that has already exited.
    pushd "$out" >/dev/null || return 1
    COPILOT_CLI_PATH="$rundir/copilot" \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS="http://127.0.0.1:$port" \
    FAULTS_PAUSE_ON="${FAULTS_PAUSE_ON:-}" \
    FAULTS_MARKER="$rundir" \
    REPLAY_MARKER_PATH="$rundir/replay.log" \
    Logging__LogLevel__Default=Warning \
      "$DOTNET" "$assembly" >"$rundir/server.log" 2>&1 &
    echo $! > "$rundir/pid"
    popd >/dev/null || true
  else
    pushd "$out" >/dev/null || return 1
    BAND_A_STORE="$store" \
    BAND_A_FIXTURE="$FIXTURES/$fixture" \
    DURABLE_RUNTIME_PATH="$rundir/copilot" \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS="http://127.0.0.1:$port" \
    FAULTS_PAUSE_ON="${FAULTS_PAUSE_ON:-}" \
    FAULTS_MARKER="$rundir" \
    REPLAY_MARKER_PATH="$rundir/replay.log" \
    Logging__LogLevel__Default=Warning \
      "$DOTNET" "$assembly" "${extra[@]+"${extra[@]}"}" >"$rundir/server.log" 2>&1 &
    echo $! > "$rundir/pid"
    popd >/dev/null || true
  fi
  STARTED_PID="$(cat "$rundir/pid")"
  echo "harness: started $variant as pid $STARTED_PID"
}

# Starts the Durable variant in its command-line mode, and leaves the PID in STARTED_PID.
#
# Call it as a statement, not as `PID=$(start_headless ...)`: a command substitution runs the function in
# a subshell, so the background job would belong to that subshell and `wait` in the caller would report 127
# instead of the application's status. STARTED_PID exists for exactly that reason.
start_headless() {
  local rundir="$1" fixture="$2"
  shift 2
  local out="$rundir/app"
  local log="$rundir/server.log"
  write_gate "$rundir/copilot" "$FIXTURES/$fixture" "$rundir/runtime.log"
  # The application is started by *this* shell, not inside a subshell: `wait` can only report the exit
  # status of a job the shell owns, and `kill -9 "$!"` must reach the application rather than a wrapper
  # that has already exited. The directory change is undone immediately afterwards.
  pushd "$out" >/dev/null || return 1
  BAND_A_STORE="${FAULTS_STORE:-$rundir/store.jsonl}" \
  BAND_A_FIXTURE="$FIXTURES/$fixture" \
  DURABLE_RUNTIME_PATH="$rundir/copilot" \
  FAULTS_PAUSE_ON="${FAULTS_PAUSE_ON:-}" \
  FAULTS_MARKER="$rundir" \
  REPLAY_MARKER_PATH="$rundir/replay.log" \
  Logging__LogLevel__Default=Warning \
    "$DOTNET" BandADurable.dll "$@" >"$log" 2>&1 &
  echo $! > "$rundir/pid"
  popd >/dev/null || true
  STARTED_PID="$(cat "$rundir/pid")"
  echo "harness: started headless as pid $STARTED_PID"
}

# Waits until the headless driver exits, then reports the application's *own* exit status.
#   0  the process exited on its own — whatever the status was, the caller reads it from $HEADLESS_STATUS
#   1  the process was still running when the timeout expired (it was stopped, and the status is then
#      meaningless)
# The status is left in HEADLESS_STATUS as well, because a caller that wants to distinguish "finished
# cleanly" from "crashed after the fault" must not confuse it with this function's return value.
HEADLESS_STATUS=""
wait_headless() {
  local rundir="$1" seconds="${2:-180}"
  local pid deadline status
  HEADLESS_STATUS=""
  pid="$(cat "$rundir/pid" 2>/dev/null || true)"
  [ -n "$pid" ] || { HEADLESS_STATUS="no-process"; return 0; }
  deadline=$((SECONDS + seconds))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.2; done
  if kill -0 "$pid" 2>/dev/null; then return 1; fi
  # `wait` is what surfaces the exit code; a process started with `&` in a subshell would report 127.
  wait "$pid" 2>/dev/null
  status=$?
  HEADLESS_STATUS="$status"
  return 0
}

# The exit status left by the most recent `wait_headless`, or "not-observed" when there is none.
headless_status() { echo "${HEADLESS_STATUS:-not-observed}"; }

wait_for_port() {
  local port="$1" attempts="${2:-160}" i=0
  while [ "$i" -lt "$attempts" ]; do
    if curl -fsS -o /dev/null --max-time 2 "http://127.0.0.1:$port/" 2>/dev/null; then return 0; fi
    sleep 0.5
    i=$((i + 1))
  done
  return 1
}

# Kills a PID and its children. The application owns a gate and a runtime, so killing the PID alone
# leaves two orphans; both are reaped here, and anything else whose command line names the run's
# scratch directory follows.
kill_tree() {
  local pid="${1:-}"
  [ -n "$pid" ] || return 0
  local child
  for child in $(pgrep -P "$pid" 2>/dev/null || true); do kill_tree "$child"; done
  kill -9 "$pid" 2>/dev/null || true
  local deadline=$((SECONDS + 10))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.1; done
}

# Stops a variant but leaves its scratch directory: a script that means to resume the same store must not
# have the store removed underneath it.
#
# Only the process tree is reaped. Matching on the run's path would also match a reviewer tailing the
# log, an editor holding the file, or a second invocation of the same fault — a diagnostic that kills
# someone else's process is worse than a stray one. `FAULTS_REAP_PATH=1` restores the path sweep for a
# caller who wants it, and `KEEP=1` makes the trap leave everything alone.
stop_variant_keep() {
  local rundir="$1" pid helper
  pid="$(cat "$rundir/pid" 2>/dev/null || true)"
  # The gate records itself and its runtime so they can be reaped by PID. They matter because `kill -9` on
  # the application reparents them: the tree walk cannot find a child whose parent is already gone.
  if [ -f "$rundir/pids" ]; then
    while read -r helper; do
      [ -n "$helper" ] && kill -9 "$helper" 2>/dev/null || true
    done < "$rundir/pids"
    rm -f "$rundir/pids"
  fi
  kill_tree "$pid"
  if [ "${FAULTS_REAP_PATH:-0}" = "1" ]; then
    local stray
    for stray in $(pgrep -f "$rundir" 2>/dev/null || true); do
      [ "$stray" = "$$" ] && continue
      kill -9 "$stray" 2>/dev/null || true
    done
  fi
  rm -f "$rundir/pid" "$rundir/inflight" "$rundir/release"
}

# Stops a variant and clears the artefacts of a run that will not be continued: the store, its lock
# sidecars, and the gate's markers. Used by the cleanup trap, never between a stop and a restart.
stop_variant_and_clear() {
  stop_variant_keep "$1"
  rm -rf "$1/store.jsonl" "$1/store.jsonl.lock" "$1/store.jsonl.lock.owner" "$1/inflight" "$1/release"
}

# Stops a variant with SIGTERM and reports what actually ended it.
#
#   STOP_SIGNAL  the signal sent, or "none" when there was no process
#   STOP_EXIT    the status `wait` reported (128+N when the process died of signal N, so 143 is SIGTERM)
#   STOP_HOW     "died of signal 15", "no shutdown hook to observe, shell reported 0", "exited N",
#                "survived SIGTERM and was killed", or "no process"
#
# Nothing here claims the stop was orderly. The application registers no handler, so SIGTERM ends it the
# way SIGKILL would minus the reaping; what makes the store complete is the per-commit fsync, not the
# signal. Reporting the distinction is the point: a caller may say "the store was complete" only if it
# checked, and may never say "the process shut down cleanly" unless STOP_EXIT says so.
stop_cleanly() {
  local rundir="$1" pid
  STOP_SIGNAL="none"
  STOP_EXIT=""
  STOP_HOW="no process"
  pid="$(cat "$rundir/pid" 2>/dev/null || true)"
  [ -n "$pid" ] || return 0
  kill -TERM "$pid" 2>/dev/null || true
  STOP_SIGNAL="SIGTERM"
  local deadline=$((SECONDS + 20))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.2; done
  if kill -0 "$pid" 2>/dev/null; then
    STOP_HOW="survived SIGTERM and was killed"
    kill_tree "$pid"
    STOP_EXIT="not-observed"
  else
    wait "$pid" 2>/dev/null
    STOP_EXIT=$?
    if [ -z "$STOP_EXIT" ]; then
      STOP_EXIT="not-observed"
    elif [ "$STOP_EXIT" -gt 128 ] 2>/dev/null; then
      STOP_HOW="died of signal $((STOP_EXIT - 128)) (SIGTERM is 15); no shutdown hook ran"
    elif [ "$STOP_EXIT" = "0" ]; then
      # 0 does not mean "a clean shutdown": it is what a shell reports for a job it has already reaped, and
      # what a process that was killed after signalling exits with. The honest statement is what was
      # observed, not a shutdown the application never performs.
      STOP_HOW="status 0 reported by the shell, which is not evidence of an orderly stop (the application registers no handler)"
    else
      STOP_HOW="exited $STOP_EXIT"
    fi
  fi
  stop_variant_keep "$rundir"
}

# The deterministic injection point: pause a named JSON-RPC method in the gate. A script waits for the
# marker and then knows the application is blocked inside that request — not sleeping and hoping.
#
# Usage: pause_at <json-rpc-method> [occurrence]
# `occurrence` is 1-based; 0 means "never hold", which is how a restart after the fault is left alone.
pause_at() {
  FAULTS_PAUSE_ON="$1"
  FAULTS_PAUSE_COUNT="${2:-1}"
  export FAULTS_PAUSE_ON FAULTS_PAUSE_COUNT
}

# Never hold: used for the observing process of every fault, so what it does unaided is the observation.
unpause() {
  FAULTS_PAUSE_ON=""
  FAULTS_PAUSE_COUNT=1
  export FAULTS_PAUSE_ON FAULTS_PAUSE_COUNT
}

# Where the *control* stop lands and where the *fault* stop lands, as a pair of occurrence counts. Each
# script names the two places; the control runs the same code with the earlier one, so the difference
# between the two results is the phase the fault was injected after, not a different script.
phase_split() { :; }

wait_for_marker() {
  local rundir="$1" name="$2" seconds="${3:-60}"
  local deadline=$((SECONDS + seconds))
  while [ ! -f "$rundir/$name" ]; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 0.1
  done
  return 0
}

# Lets a paused gate handshake finish, so the process can be stopped tidily after the observation.
release_gate() {
  local rundir="$1"
  touch "$rundir/release"
}

driver_unavailable_reason() {
  local package
  package="$("$PYTHON" "$HARNESS_DIR/lib/discover_tool.py" 2>/dev/null |
    "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin)["playwright"] or "")' 2>/dev/null || true)"
  if [ -z "$package" ]; then
    echo "no Playwright install found on this machine (BAND_A_PLAYWRIGHT unset and no global package), so the page the baseline requires cannot be driven"
  fi
}

prepare_driver_spec() {
  local rundir="$1" port="$2" spec="$3"
  local package
  package="$("$PYTHON" "$HARNESS_DIR/lib/discover_tool.py" 2>/dev/null |
    "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin)["playwright"] or "")' 2>/dev/null || true)"
  [ -n "$package" ] || return 3
  "$PYTHON" "$HARNESS_DIR/lib/merge_spec.py" "$package" "$port" "$spec" "$rundir/spec.json"
}

drive() {
  local rundir="$1" port="$2" spec="$3" result="$4"
  prepare_driver_spec "$rundir" "$port" "$spec" || return 3
  node "$HARNESS_DIR/lib/driver.js" "$rundir/spec.json" "$result"
}

# Starts the browser driver without waiting for it, so the caller can fault the server while the page is
# mid-flight. The PID goes to a file rather than to stdout: a command substitution would wait for the
# background process to close the pipe, which is exactly the wait the caller is trying to avoid.
start_driver() {
  local rundir="$1" port="$2" spec="$3" result="$4"
  prepare_driver_spec "$rundir" "$port" "$spec" || return 3
  node "$HARNESS_DIR/lib/driver.js" "$rundir/spec.json" "$result" >"$rundir/driver.log" 2>&1 &
  echo $! > "$rundir/driver.pid"
  return 0
}

driver_pid() { cat "$1/driver.pid" 2>/dev/null || true; }

# One field of a JSON file, by dotted path. The program is passed with -c so stdin stays free: a
# caller's own redirection must not collide with the interpreter reading its program from stdin.
json_get() {
  local file="$1" expr="$2"
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
)" "$file" "$expr"
}

# One field of the last committed value of a document kind, read straight from the JSONL: the phase a
# kill left behind, the task status it left behind, and so on. Empty when the store, the document or
# the field is missing, so a caller can test it without quoting gymnastics.
store_get() {
  local store="$1" kind="$2" field="$3"
  "$PYTHON" -c "$(cat <<'INNER'
import json, sys
path, kind, field = sys.argv[1], sys.argv[2], sys.argv[3]
# The store writes the pipeline phase as its enum ordinal; the harness reports the name the rest of the
# evidence uses.
PHASES = {0: "queued", 1: "validating", 2: "searching", 3: "writing_report",
          4: "rejected_garbage", 5: "rejected_no_matches", 6: "done"}
STATUSES = {0: "pending", 1: "running", 2: "waiting", 3: "sleeping", 4: "terminal"}
last = None
try:
    handle = open(path, encoding="utf-8")
except OSError:
    print("")
    raise SystemExit(0)
for line in handle:
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    payload = record.get("payload") or {}
    if kind == "task":
        if record.get("type") == "task":
            last = payload
    elif record.get("type") == "document-version" and payload.get("kind") == kind:
        last = payload.get("value") or {}
value = last
for part in field.split("."):
    if not part or value is None:
        continue
    try:
        value = value[int(part)] if isinstance(value, list) else value.get(part)
    except Exception:
        value = None
if isinstance(value, int) and field == "phase":
    value = PHASES.get(value, value)
elif isinstance(value, int) and field == "status" and kind == "task":
    value = STATUSES.get(value, value)
print("" if value is None else value)
INNER
)" "$store" "$kind" "$field"
}

# Counts records in a durable store by reading the JSONL, not by trusting what a process printed.
#   store_count <path> <document-kind>[ @entries | @admitted ] | task
store_count() {
  local store="$1" kind="$2"
  "$PYTHON" - "$store" "$kind" <<'PY'
import json, sys
path, kind = sys.argv[1], sys.argv[2]
wanted, _, field = kind.partition("@")
count = 0
try:
    handle = open(path, encoding="utf-8")
except OSError:
    print(0)
    raise SystemExit(0)
for line in handle:
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    payload = record.get("payload") or {}
    if wanted == "task":
        if record.get("type") == "task":
            count += 1
        continue
    if record.get("type") != "document-version" or payload.get("kind") != wanted:
        continue
    value = payload.get("value") or {}
    if not field:
        count += 1
    elif field == "entries":
        count = len(value.get("entries") or [])
    elif field == "admitted":
        count += 1 if value.get("state") == "admitted" else 0
print(count)
PY
}

# The last committed value of a document kind, straight from the file, so an observation does not
# depend on a process still being alive.
store_last() {
  local store="$1" kind="$2"
  "$PYTHON" - "$store" "$kind" <<'PY'
import json, sys
last = None
try:
    handle = open(sys.argv[1], encoding="utf-8")
except OSError:
    print("null")
    raise SystemExit(0)
for line in handle:
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    payload = record.get("payload") or {}
    if sys.argv[2] == "task":
        if record.get("type") == "task":
            last = payload
    elif record.get("type") == "document-version" and payload.get("kind") == sys.argv[2]:
        last = payload.get("value")
print(json.dumps(last))
PY
}

# One field of a store record, for a shell test to branch on.
store_field() {
  local file="$1" expr="$2"
  json_get "$file" "$expr"
}

# The transcript of the last committed enquiry, condensed: each entry as kind and text, so a duplication
# is visible as two identical lines rather than inferred from a total.
store_transcript() {
  local store="$1"
  "$PYTHON" -c "$(cat <<'INNER'
import json, sys
last = None
try:
    handle = open(sys.argv[1], encoding="utf-8")
except OSError:
    print("")
    raise SystemExit(0)
for line in handle:
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    payload = record.get("payload") or {}
    if record.get("type") == "document-version" and payload.get("kind") == "band-a.enquiry":
        last = payload.get("value") or {}
entries = (last or {}).get("entries") or []
print("; ".join("%s%s" % (e.get("kind"), (":" + (e.get("text") or "")[:24]) if e.get("text") else "") for e in entries))
INNER
)" "$store"
}

# How many transcript entries repeat a (kind, text) pair that already appeared. The number is the measured
# form of "the run repeated itself": a resumed pass that re-sends an interrupted turn commits that turn's
# intents and tool results a second time, and this counts them rather than calling the transcript exact.
store_repeats() {
  local store="$1"
  "$PYTHON" -c "$(cat <<'INNER'
import collections, json, sys
last = None
try:
    handle = open(sys.argv[1], encoding="utf-8")
except OSError:
    print(0)
    raise SystemExit(0)
for line in handle:
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    payload = record.get("payload") or {}
    if record.get("type") == "document-version" and payload.get("kind") == "band-a.enquiry":
        last = payload.get("value") or {}
entries = (last or {}).get("entries") or []
seen = collections.Counter((e.get("kind"), (e.get("text") or "")[:40]) for e in entries)
print(sum(count - 1 for count in seen.values() if count > 1))
INNER
)" "$store"
}

# Writes one result. A result may not silently downgrade a measured row: replacing a non-"not exercised"
# outcome with "not exercised" is refused unless FAULTS_ALLOW_DOWNGRADE=1, because that is what a run on a
# machine that lacks a capability would otherwise do to the checked-in evidence.
write_result() {
  local variant="$1" fault="$2" json="$3"
  local path="$RESULTS_DIR/$variant/$fault.json"
  mkdir -p "$RESULTS_DIR/$variant"
  if [ -f "$path" ] && [ "${FAULTS_ALLOW_DOWNGRADE:-0}" != "1" ]; then
    local before after
    before="$("$PYTHON" -c 'import json,sys; print((json.load(open(sys.argv[1])) or {}).get("outcome",""))' "$path" 2>/dev/null || true)"
    after="$("$PYTHON" -c 'import json,sys; print((json.loads(sys.argv[1]) or {}).get("outcome",""))' "$json" 2>/dev/null || true)"
    if [ "$after" = "not exercised" ] && [ -n "$before" ] && [ "$before" != "not exercised" ]; then
      log "harness: refusing to replace a measured '$before' result at $path with 'not exercised'"
      log "harness: set FAULTS_ALLOW_DOWNGRADE=1 to record the loss of capability deliberately"
      return 1
    fi
  fi
  printf '%s\n' "$json" > "$path"
  log "harness: wrote $path"
}

# The one way a result is recorded. `detail` says what was observed; `evidence` carries the raw
# observations the claim comes from, so a reader can check the sentence against them.
record_result() {
  local variant="$1" fault="$2" outcome="$3" phase="$4" detail="$5" evidence="${6:-}"
  local json
  json="$("$PYTHON" - "$variant" "$fault" "$outcome" "$phase" "$detail" "$evidence" <<'PY'
import datetime, json, os, sys
variant, fault, outcome, phase, detail, evidence = sys.argv[1:7]
print(json.dumps({
    "variant": variant,
    "fault": fault,
    "outcome": outcome,
    "phase": phase,
    "detail": detail,
    "evidence": evidence,
    "harness": {
        "dotnet": os.environ.get("DOTNET", "dotnet"),
        "runtime": os.environ.get("FAULTS_RUNTIME", "tools/ReplayRuntime"),
        "command": os.environ.get("FAULT_COMMAND", ""),
        "script": os.environ.get("FAULT_SCRIPT", ""),
        "commit": os.environ.get("FAULTS_COMMIT", "unknown"),
    },
    "recordedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
}, indent=2, ensure_ascii=False))
PY
)"
  write_result "$variant" "$fault" "$json"
}

# "Not exercised" still writes a result — the absence is a finding — but it takes the downgrade guard with
# it, so a machine that lacks Playwright cannot replace a measured baseline row by accident.
record_not_exercised_result() {
  local variant="$1" fault="$2" phase="$3" reason="$4"
  record_not_exercised "$variant" "$fault" "$phase" "$reason"
}

record_not_exercised() {
  local variant="$1" fault="$2" phase="$3" reason="$4"
  record_result "$variant" "$fault" "not exercised" "$phase" "$reason" ""
}

new_run_dir() {
  local variant="$1" fault="$2"
  local dir="$SCRATCH_ROOT/$variant/$fault"
  rm -rf "$dir"
  mkdir -p "$dir"
  echo "$dir"
}

# Every fault script installs this. It stops what the run started and removes the run's scratch, so a
# re-run starts from nothing and a failed run leaves nothing behind. KEEP=1 keeps the directories,
# which is the only way to look at a failure afterwards.
CLEANUP_DIRS=()
CLEANUP_PIDS=()
cleanup_all() {
  local status=$?
  local pid dir
  for pid in ${CLEANUP_PIDS[@]+"${CLEANUP_PIDS[@]}"}; do kill_tree "$pid"; done
  for dir in ${CLEANUP_DIRS[@]+"${CLEANUP_DIRS[@]}"}; do
    if [ "${KEEP:-0}" = "1" ]; then
      # KEEP is the promise that a failure can be inspected afterwards: the store is the thing worth
      # inspecting, so the process is stopped and nothing else is touched.
      [ -f "$dir/pid" ] && stop_variant_keep "$dir"
      log "harness: kept $dir"
    else
      [ -f "$dir/pid" ] && stop_variant_and_clear "$dir"
      rm -rf "$dir"
    fi
  done
  return $status
}
trap cleanup_all EXIT INT TERM

track_pid() { CLEANUP_PIDS+=("$1"); }
track_dir() { CLEANUP_DIRS+=("$1"); }
