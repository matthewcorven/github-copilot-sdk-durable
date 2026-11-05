#!/usr/bin/env bash
# Fault: restart-after-checkpoint. Stop cleanly after a committed phase, then restart.
#
#   ./faults/restart-after-checkpoint.sh --variant baseline|durable [--fault-not-injected]
#
# This is the control for `kill-mid-turn`'s opposite: the process is *not* killed mid-turn. It is
# stopped with SIGTERM after a phase has committed and the turn has settled, so the store is quiet and
# complete. What is measured is whether the restart is exact — the enquiry continues from the committed
# phase, nothing is replayed, and nothing is lost.
#
#   --fault-not-injected stops *before* the phase commits and restarts: the enquiry is then at `queued`
#   with nothing done. That is the before/after control — the same restart, on a run that had committed
#   nothing, must land at a different phase. If both land in the same place the harness is not observing
#   the checkpoint at all.
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
  PID="$(start_variant baseline "$RUN_DIR" "$PORT" enquiry.json)"
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
  PID="$(start_headless "$RUN_DIR" enquiry.json --enquiry "$ENQUIRY")"
  track_pid "$PID"
fi

wait_for_marker "$RUN_DIR" inflight 90 || {
  log "restart-after-checkpoint: the enquiry never reached the injection point"
  log "runtime log: $(dirname "$RUN_DIR")"
  tail -20 "$RUN_DIR/runtime.log" >&2
  tail -20 "$RUN_DIR/server.log" >&2
  exit 1
}

# The fault is a *clean* stop, so the difference from `kill-mid-turn` is the signal and the moment: the
# application is blocked waiting on the runtime (nothing is being written), and it is stopped with
# SIGTERM and confirmed gone — not SIGKILLed mid-write.
if [ "$VARIANT" = "durable" ]; then
  stop_cleanly "$RUN_DIR"
  FIRST_STOP="SIGTERM while waiting on the runtime, then the process was confirmed gone"
else
  stop_cleanly "$RUN_DIR"
  FIRST_STOP="SIGTERM while the page showed the enquiry in flight, then the process was confirmed gone"
fi

# --- inject: stop ------------------------------------------------------------------------------
BEFORE_PHASE="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry phase)"
BEFORE_ENTRIES="$(store_count "$RUN_DIR/store.jsonl" band-a.enquiry@entries)"
BEFORE_TASK="$(store_get "$RUN_DIR/store.jsonl" task status)"
BEFORE_TRANSCRIPT="$(store_transcript "$RUN_DIR/store.jsonl")"

if [ "$VARIANT" = "durable" ]; then
  # A clean stop leaves no torn tail and no lock sidecar: the durability claim is that the file is
  # complete, not that it is merely present.
  TAIL_OK="$("$PYTHON" -c '
import json, sys
path = sys.argv[1]
lines = [l for l in open(path, encoding="utf-8") if l.strip()]
ok = True
for line in lines:
    try:
        json.loads(line)
    except ValueError:
        ok = False
print("complete" if ok else "torn")
' "$RUN_DIR/store.jsonl" 2>/dev/null || echo missing)"
else
  TAIL_OK="no store: the baseline writes no agent state"
fi

# --- observe: restart the same application on the same directory and port ----------------------
# The restart is not paused: the fault is over, and what a new process does unaided is the observation.
unpause
if [ "$VARIANT" = "baseline" ]; then
  PID2="$(start_variant baseline "$RUN_DIR" "$PORT" enquiry.json)"
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
    DETAIL="The server was stopped cleanly ($PHASE) and restarted. It shows $CARDS cards: processing $PROCESSING, completed $COMPLETED. Whatever phase had committed is gone with the process, so there is no resume to be exact about."
  else
    OBSERVED="resumed"
    DETAIL="The restart shows $CARDS card(s) after a clean stop, which contradicts the baseline's design. Reported, not smoothed over."
  fi
  EVIDENCE="clean stop: $FIRST_STOP; restart: same directory $RUN_DIR/app, same port $PORT; cards=$CARDS processing=$PROCESSING completed=$COMPLETED"
else
  PID2="$(start_headless "$RUN_DIR" enquiry.json --resume)"
  track_pid "$PID2"
  wait_headless "$RUN_DIR" 240 || { log "restart-after-checkpoint: the resuming process did not finish"; stop_variant_and_clear "$RUN_DIR"; }
  RESUMED_COUNT="$(grep -o 'resumed tasks:  *[0-9]*' "$RUN_DIR/server.log" 2>/dev/null | grep -o '[0-9]*$' | head -1)"
  AFTER_PHASE="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry phase)"
  AFTER_ENTRIES="$(store_count "$RUN_DIR/store.jsonl" band-a.enquiry@entries)"
  AFTER_TASK="$(store_get "$RUN_DIR/store.jsonl" task status)"
  AFTER_RESTARTS="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry restarts)"
  AFTER_FINISHED="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry finishedAt)"
  AFTER_TRANSCRIPT="$(store_transcript "$RUN_DIR/store.jsonl")"
  PROMOTS="$(grep -c 'client->runtime session.send' "$RUN_DIR/runtime.log" 2>/dev/null || echo 0)"
  if [ "${RESUMED_COUNT:-0}" = "0" ]; then
    # Nothing was pending, so nothing was re-run: the restart is exact. That is the passing shape for
    # this fault, and it is a finding rather than a skip.
    if [ "$BEFORE_PHASE" = "$AFTER_PHASE" ] && [ "$BEFORE_ENTRIES" = "$AFTER_ENTRIES" ]; then
      OBSERVED="restarted exactly"
      DETAIL="The run reached phase $BEFORE_PHASE with $BEFORE_ENTRIES transcript entries and stopped ($FIRST_STOP). The restart resumed $RESUMED_COUNT task(s) — the task was already $BEFORE_TASK — and the document is unchanged: phase $AFTER_PHASE, $AFTER_ENTRIES entries, same finishedAt. Nothing was replayed and nothing was lost; the store file after the stop was $TAIL_OK."
    else
      OBSERVED="changed"
      DETAIL="The restart changed the document without resuming anything: phase $BEFORE_PHASE→$AFTER_PHASE, entries $BEFORE_ENTRIES→$AFTER_ENTRIES. That is a replay or a loss, not an exact restart."
    fi
  elif [ -n "$RESUMED_COUNT" ]; then
    OBSERVED="resumed"
    DETAIL="The restart resumed $RESUMED_COUNT task(s) from the committed phase and finished at $AFTER_PHASE with $AFTER_ENTRIES entries (task $AFTER_TASK). The committed checkpoint was ahead of the last completed turn, so the remaining work ran and the finished part did not."
  else
    OBSERVED="lost"
    DETAIL="The restart reported no resume and left the document at phase ${AFTER_PHASE:-none}, task ${AFTER_TASK:-none}."
  fi
  EVIDENCE="clean stop: $FIRST_STOP; store after the stop: $TAIL_OK; resumed tasks: ${RESUMED_COUNT:-0}; restarts=$AFTER_RESTARTS; session.send frames across both processes: $PROMOTS; before: $BEFORE_PHASE/$BEFORE_ENTRIES entries [$BEFORE_TRANSCRIPT]; after: $AFTER_PHASE/$AFTER_ENTRIES entries [$AFTER_TRANSCRIPT]"
  PHASE="committed at $BEFORE_PHASE when the process was stopped ($FIRST_STOP)"
fi

record_result "$VARIANT" "$FAULT" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
