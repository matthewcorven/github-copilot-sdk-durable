#!/usr/bin/env bash
# Fault: restart-after-checkpoint. Stop abruptly after a committed phase, then restart.
#
#   ./faults/restart-after-checkpoint.sh --variant baseline|durable [--fault-not-injected]
#
# What is measured is whether the committed checkpoint is where the restart resumes — not whether the
# restart is byte-identical to an uninterrupted run. It is not, and the result says so: the turn that was
# in flight when the process stopped is sent again by the new process, so its intent is committed twice.
# `durable/evidence/durable-restart-and-concurrency.md` already documents the same effect ("the earlier
# turn is sent and committed again"), and this script measures it rather than contradicting it.
#
# The stop is SIGTERM, and it is *not* described as a clean shutdown: the application registers no signal
# handler, so it dies the way SIGKILL would minus the reaping. The store stays complete because every
# commit is fsynced before it is acknowledged — that is the durability property under test — and
# `stop_cleanly` reports the status `wait` actually saw, so a reader can tell the two apart.
#
#   --fault-not-injected stops before any phase has committed and restarts: the enquiry is then at
#   `queued` with nothing done. That is the before/after control — the same restart, on a run that had
#   committed nothing, must land at a different phase. If both land in the same place the harness is not
#   observing the checkpoint at all.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

VARIANT=""
INJECT=1
while [ $# -gt 0 ]; do
  case "$1" in
    --variant) VARIANT="${2:-}"; shift 2 ;;
    --fault-not-injected) INJECT=0; shift ;;
    *) log "restart-after-checkpoint: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "restart-after-checkpoint: --variant baseline|durable is required"; exit 2; }

FAULT="restart-after-checkpoint"
[ "$INJECT" = "1" ] || FAULT="restart-after-checkpoint-control"
PORT="$(port_for "$VARIANT" "$FAULT")"
FAULT_COMMAND="./faults/restart-after-checkpoint.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND
# The script that owns this result, so summarise.py can tell a result written by this code from a stale
# or hand-edited file.
FAULT_SCRIPT="$(basename "${BASH_SOURCE[0]}")"
export FAULT_SCRIPT

require_tools || exit 1
REASON="$(driver_unavailable_reason)"
if [ "$VARIANT" = "baseline" ] && [ -n "$REASON" ]; then
  record_not_exercised "baseline" "$FAULT" "a committed phase" "$REASON"
  exit 0
fi

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

ENQUIRY="Looking for a 3-bed family home near good schools in the suburbs"
if [ "$INJECT" = "1" ]; then
  # The second tool result of the turn. By then `set_current_phase validating` has committed, so the
  # enquiry is past `queued`; the application is waiting on the runtime, not writing, so a SIGTERM here
  # leaves a complete store rather than a torn one. That is "stopped cleanly after a committed phase".
  PHASE="a committed phase, process stopped while waiting on the runtime"
  pause_at session.tools.handlePendingToolCall 2
else
  # The control: the very first prompt, before any tool call has committed anything. `queued` is the
  # only phase the store holds, so the same restart lands somewhere else — which is what proves the
  # fault's restart is exact *at a committed phase* rather than exact about an empty store.
  PHASE="before any phase has progressed (queued only)"
  pause_at session.send 1
fi

if [ "$VARIANT" = "baseline" ]; then
  start_variant baseline "$RUN_DIR" "$PORT" enquiry.json
  PID="${STARTED_PID}"
  PID="${STARTED_PID}"
  track_pid "$PID"
  wait_for_port "$PORT" || { log "restart-after-checkpoint: baseline never served"; exit 1; }
  cat > "$RUN_DIR/spec.json" <<JSON
{"actions": [
  {"kind": "submit", "text": "$ENQUIRY"},
  {"kind": "wait-for-card", "text": "3-bed family home", "count": 1, "timeoutMs": 30000}
]}
JSON
  start_driver "$RUN_DIR" "$PORT" "$RUN_DIR/spec.json" "$RUN_DIR/drive.json" || exit 1
  track_pid "$(driver_pid "$RUN_DIR")"
else
  start_headless "$RUN_DIR" enquiry.json --enquiry "$ENQUIRY"
  PID="${STARTED_PID}"
  PID="${STARTED_PID}"
  track_pid "$PID"
fi

wait_for_marker "$RUN_DIR" inflight 90 || {
  log "restart-after-checkpoint: the enquiry never reached the injection point"
  log "runtime log: $(dirname "$RUN_DIR")"
  tail -20 "$RUN_DIR/runtime.log" >&2
  tail -20 "$RUN_DIR/server.log" >&2
  exit 1
}

# The difference from `kill-mid-turn` is the signal: SIGTERM rather than SIGKILL, sent while the
# application is blocked on the runtime rather than mid-write. What that does to the *process* is reported
# from `wait`, not asserted: `STOP_HOW` says which signal ended it.
stop_cleanly "$RUN_DIR"
if [ "$VARIANT" = "durable" ]; then
  FIRST_STOP="SIGTERM while waiting on the runtime; wait reported: ${STOP_HOW} (exit ${STOP_EXIT})"
else
  FIRST_STOP="SIGTERM while the page showed the enquiry in flight; wait reported: ${STOP_HOW} (exit ${STOP_EXIT})"
fi

# --- inject: stop ------------------------------------------------------------------------------
BEFORE_PHASE="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry phase)"
BEFORE_ENTRIES="$(store_count "$RUN_DIR/store.jsonl" band-a.enquiry@entries)"
BEFORE_TASK="$(store_get "$RUN_DIR/store.jsonl" task status)"
BEFORE_TRANSCRIPT="$(store_transcript "$RUN_DIR/store.jsonl")"

if [ "$VARIANT" = "durable" ]; then
  # "Complete" here means every record parses, which is the property an fsynced append gives even when the
  # process was killed. The lock sidecar is reported separately because a signal that skips disposal leaves
  # it behind; that is a fact about the stop, not about the store's contents.
  TAIL_OK="$("$PYTHON" -c '
import json, sys
lines = [l for l in open(sys.argv[1], encoding="utf-8") if l.strip()]
ok = True
for line in lines:
    try:
        json.loads(line)
    except ValueError:
        ok = False
print("complete" if ok else "torn")
' "$RUN_DIR/store.jsonl" 2>/dev/null || echo missing)"
  LOCK_AFTER_STOP="$([ -f "$RUN_DIR/store.jsonl.lock" ] && echo present || echo absent)"
else
  TAIL_OK="no store: the baseline writes no agent state"
  LOCK_AFTER_STOP="n/a"
fi

# --- observe: restart the same application on the same directory and port ----------------------
# The restart is not paused: the fault is over, and what a new process does unaided is the observation.
unpause
if [ "$VARIANT" = "baseline" ]; then
  start_variant baseline "$RUN_DIR" "$PORT" enquiry.json
  PID2="${STARTED_PID}"
  PID2="${STARTED_PID}"
  track_pid "$PID2"
  wait_for_port "$PORT" || { log "restart-after-checkpoint: the restarted baseline never served"; exit 1; }
  cat > "$RUN_DIR/spec-observe.json" <<'JSON'
{"actions": [{"kind": "snapshot", "label": "after the restart"}]}
JSON
  drive "$RUN_DIR" "$PORT" "$RUN_DIR/spec-observe.json" "$RUN_DIR/observe.json" || {
    log "restart-after-checkpoint: the restarted baseline could not be observed"
    exit 1
  }
  CARDS="$("$PYTHON" -c '
import json, sys
final = (json.load(open(sys.argv[1])) or {}).get("final") or {}
print(len(final.get("cards") or []))
' "$RUN_DIR/observe.json")"
  COMPLETED="$(json_get "$RUN_DIR/observe.json" final.dashboard.completed)"
  PROCESSING="$(json_get "$RUN_DIR/observe.json" final.dashboard.processing)"
  if [ "$CARDS" = "0" ]; then
    OBSERVED="lost"
    DETAIL="The server was stopped with SIGTERM ($PHASE; ${STOP_HOW}) and restarted. It shows $CARDS cards: processing $PROCESSING, completed $COMPLETED. Whatever phase had committed is gone with the process, so there is no checkpoint for a restart to resume from."
  else
    OBSERVED="resumed"
    DETAIL="The restart shows $CARDS card(s) after the stop, which contradicts the baseline's design. Reported, not smoothed over."
  fi
  EVIDENCE="stop: $FIRST_STOP; restart: same directory $RUN_DIR/app, same port $PORT; cards=$CARDS processing=$PROCESSING completed=$COMPLETED"
  PHASE="committed at nothing the restart can read (the baseline writes no state)"
else
  start_headless "$RUN_DIR" enquiry.json --resume
  PID2="${STARTED_PID}"
  PID2="${STARTED_PID}"
  track_pid "$PID2"
  # A resume that does not finish is a harness error, not a fault outcome: the observation is what the new
  # process did, and a process that hangs did nothing that can be classified.
  if ! wait_headless "$RUN_DIR" 240; then
    log "restart-after-checkpoint: the resuming process did not finish within 240s; that is a harness error"
    stop_variant_and_clear "$RUN_DIR"
    exit 1
  fi
  RESUME_EXIT="$(headless_status)"
  RESUMED_COUNT="$(grep -o 'resumed tasks:  *[0-9]*' "$RUN_DIR/server.log" 2>/dev/null | grep -o '[0-9]*$' | head -1)"
  FINISHED_LINE="$(grep -c '^disposed\.' "$RUN_DIR/server.log" 2>/dev/null || true)"
  AFTER_PHASE="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry phase)"
  AFTER_ENTRIES="$(store_count "$RUN_DIR/store.jsonl" band-a.enquiry@entries)"
  AFTER_TASK="$(store_get "$RUN_DIR/store.jsonl" task status)"
  AFTER_RESTARTS="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry restarts)"
  AFTER_FINISHED="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry finishedAt)"
  AFTER_TRANSCRIPT="$(store_transcript "$RUN_DIR/store.jsonl")"
  PROMOTS="$(grep -c 'client->runtime session.send' "$RUN_DIR/runtime.log" 2>/dev/null || true)"
  # A resumed pass that re-sends a turn leaves the same text in the transcript twice. That is counted, not
  # inferred: it is the difference between "the checkpoint was honoured" and "the run repeated itself".
  REPEATED="$(store_repeats "$RUN_DIR/store.jsonl")"
  if [ "${FINISHED_LINE:-0}" = "0" ]; then
    log "restart-after-checkpoint: the resuming process never reached its disposal line; that is a harness error"
    exit 1
  fi
  if [ "${RESUMED_COUNT:-0}" = "0" ]; then
    if [ "$BEFORE_PHASE" = "$AFTER_PHASE" ] && [ "$BEFORE_ENTRIES" = "$AFTER_ENTRIES" ]; then
      OBSERVED="no resume needed"
      DETAIL="The run reached phase $BEFORE_PHASE with $BEFORE_ENTRIES transcript entries and stopped ($FIRST_STOP). The task was already $BEFORE_TASK, so the restart resumed $RESUMED_COUNT tasks and the document is unchanged: phase $AFTER_PHASE, $AFTER_ENTRIES entries, same finishedAt. Nothing was lost. This is the shape of a stop after a terminal commit, not a demonstration that a mid-enquiry resume is exact."
    else
      OBSERVED="changed"
      DETAIL="The restart changed the document without resuming anything: phase $BEFORE_PHASE→$AFTER_PHASE, entries $BEFORE_ENTRIES→$AFTER_ENTRIES. That is unexplained mutation, not a restart."
    fi
  elif [ -n "$RESUMED_COUNT" ]; then
    OBSERVED="resumed"
    DETAIL="The restart resumed $RESUMED_COUNT task(s) from the committed checkpoint ($BEFORE_PHASE, $BEFORE_ENTRIES entries) and finished at $AFTER_PHASE with $AFTER_ENTRIES entries (task $AFTER_TASK, exit $RESUME_EXIT). The checkpoint was honoured — the finished part was not re-run from the start — but the pass that was cut off was sent again, so the transcript carries ${REPEATED:-0} repeated entr(y/ies) relative to the control. Replay of the interrupted turn is not eliminated by the checkpoint; only work before the checkpoint is."
  else
    OBSERVED="lost"
    DETAIL="The restart reported no resume and left the document at phase ${AFTER_PHASE:-none}, task ${AFTER_TASK:-none}."
  fi
  EVIDENCE="stop: $FIRST_STOP; store after the stop: $TAIL_OK; lock sidecar after the stop: $LOCK_AFTER_STOP; resumed tasks: ${RESUMED_COUNT:-0}; resume exit: ${RESUME_EXIT:-n/a}; restarts=$AFTER_RESTARTS; session.send frames across both processes: $PROMOTS; repeated entries after the restart: ${REPEATED:-0}; before: $BEFORE_PHASE/$BEFORE_ENTRIES entries [$BEFORE_TRANSCRIPT]; after: $AFTER_PHASE/$AFTER_ENTRIES entries [$AFTER_TRANSCRIPT]"
  PHASE="committed at $BEFORE_PHASE when the process was stopped ($FIRST_STOP)"
fi

record_result "$VARIANT" "$FAULT" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
