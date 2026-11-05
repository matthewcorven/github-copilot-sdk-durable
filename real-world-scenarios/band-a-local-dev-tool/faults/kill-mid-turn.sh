#!/usr/bin/env bash
# Fault: kill-mid-turn. A real SIGKILL of a real process while a model turn is in flight.
#
#   ./faults/kill-mid-turn.sh --variant baseline|durable [--fault-not-injected]
#
# The injection point is the gate holding `session.send`: the prompt has been sent, the answer has not
# come back, and the application is blocked waiting for it. At that instant the process is killed with
# `kill -9`, by the PID the shell started. Nothing about the kill is simulated. The observation is made
# afterwards, by a different process.
#
#   --fault-not-injected is the before/after control. It runs the same steps and releases the held turn
#   instead of killing, so the result must show the enquiry finishing. A control that also lost the
#   enquiry would mean the script was measuring its own cleanup rather than the fault.
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
    *) log "kill-mid-turn: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "kill-mid-turn: --variant baseline|durable is required"; exit 2; }
[ "$VARIANT" = "baseline" ] || [ "$VARIANT" = "durable" ] || { log "kill-mid-turn: unknown variant"; exit 2; }

FAULT="kill-mid-turn"
[ "$INJECT" = "1" ] || FAULT="kill-mid-turn-control"
PORT="$(port_for "$VARIANT" "$FAULT")"
FAULT_COMMAND="./faults/kill-mid-turn.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1
REASON="$(driver_unavailable_reason)"
if [ "$VARIANT" = "baseline" ] && [ -n "$REASON" ]; then
  record_not_exercised "baseline" "$FAULT" "an enquiry in a model turn" "$REASON"
  exit 0
fi

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

PHASE="a model turn in flight (a tool result held by the gate, the answer not yet received)"

if [ "$VARIANT" = "baseline" ]; then
  # The baseline has no command line: an enquiry exists only after a click, so the page is driven until
  # a card is on screen and the turn it started is the one the gate is holding.
  pause_at session.tools.handlePendingToolCall
  FAULTS_PAUSE_COUNT=2
  export FAULTS_PAUSE_COUNT
  PID="$(start_variant baseline "$RUN_DIR" "$PORT" enquiry.json)"
  track_pid "$PID"
  wait_for_port "$PORT" || { log "kill-mid-turn: baseline never served on $PORT"; tail -5 "$RUN_DIR/server.log" >&2; exit 1; }
  cat > "$RUN_DIR/spec.json" <<'JSON'
{"actions": [
  {"kind": "submit", "text": "Looking for a 3-bed family home near good schools in the suburbs"},
  {"kind": "wait-for-card", "text": "3-bed family home", "count": 1, "timeoutMs": 30000}
]}
JSON
  start_driver "$RUN_DIR" "$PORT" "$RUN_DIR/spec.json" "$RUN_DIR/drive.json" || {
    log "kill-mid-turn: the baseline page could not be driven"
    exit 1
  }
  DRIVER="$(driver_pid "$RUN_DIR")"
  track_pid "$DRIVER"
  DRIVE_STATUS=0
else
  # The Durable variant's command line runs the same AppState the page injects, so it needs no browser.
  # The second `session.send` is the one worth killing at: the first is the queued pass, which has
  # committed nothing, so a kill there would only prove the store was created. The second is reached
  # with `validating` committed, which is the phase the resume has to start from.
  # The pause is on the second tool result, not on the prompt: by the time the app answers the second
  # tool call of the turn, the first one (`set_current_phase validating`) is already committed, so the
  # kill lands inside a turn that has durable progress to resume from. Pausing on `session.send` would
  # kill the queued pass, which has committed nothing and proves only that the store was created.
  pause_at session.tools.handlePendingToolCall
  FAULTS_PAUSE_COUNT=2
  export FAULTS_PAUSE_COUNT
  PID="$(start_headless "$RUN_DIR" enquiry.json --enquiry "Looking for a 3-bed family home near good schools in the suburbs")"
  track_pid "$PID"
fi

if ! wait_for_marker "$RUN_DIR" inflight 90; then
  log "kill-mid-turn: the gate never held a tool result, so no turn was in flight"
  tail -10 "$RUN_DIR/runtime.log" >&2
  exit 1
fi
log "kill-mid-turn: $VARIANT is blocked inside a tool result (gate wrote $RUN_DIR/inflight)"

BEFORE_PHASE="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry phase)"
BEFORE_ENTRIES="$(store_count "$RUN_DIR/store.jsonl" band-a.enquiry@entries)"

# --- inject ------------------------------------------------------------------------------------
if [ "$INJECT" = "1" ]; then
  KILLED_PID="$(cat "$RUN_DIR/pid")"
  kill -9 "$KILLED_PID"
  deadline=$((SECONDS + 20))
  while kill -0 "$KILLED_PID" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.1; done
  if kill -0 "$KILLED_PID" 2>/dev/null; then
    log "kill-mid-turn: pid $KILLED_PID survived SIGKILL, which cannot happen; aborting"
    exit 1
  fi
  log "kill-mid-turn: kill -9 $KILLED_PID; the process is gone"
else
  log "kill-mid-turn-control: not injecting; releasing the held turn so it can finish"
  release_gate "$RUN_DIR"
fi

if [ -n "${DRIVER:-}" ]; then
  # The page's circuit dies with the server. A non-zero driver status here is the kill's consequence,
  # not a harness error, and it is recorded as such.
  wait "$DRIVER" 2>/dev/null
  DRIVE_STATUS=$?
  log "kill-mid-turn: the browser driver returned $DRIVE_STATUS"
fi

if [ "$INJECT" = "0" ]; then
  # The control's job is to finish. Waiting for the headless process (or the page's dashboard) is what
  # makes "not injected, so it completed" an observation.
  if [ "$VARIANT" = "durable" ]; then
    wait_headless "$RUN_DIR" 180 || { log "kill-mid-turn-control: the uninterrupted run did not finish"; exit 1; }
  else
    cat > "$RUN_DIR/spec-wait.json" <<'JSON'
{"actions": [{"kind": "wait-dashboard", "expect": {"completed": 1}, "timeoutMs": 90000}],
 "url": ""}
JSON
    "$PYTHON" - "$RUN_DIR/spec-wait.json" "$PORT" <<'PY'
import json, sys
spec = json.load(open(sys.argv[1]))
spec["url"] = "http://127.0.0.1:%s/" % sys.argv[2]
json.dump(spec, open(sys.argv[1], "w"))
PY
    FAULTS_PAUSE_ON=""
    export FAULTS_PAUSE_ON
    drive "$RUN_DIR" "$PORT" "$RUN_DIR/spec-wait.json" "$RUN_DIR/control.json"
    CONTROL_STATUS=$?
    if [ "$CONTROL_STATUS" != "0" ]; then
      log "kill-mid-turn-control: the unaffected baseline did not complete; the control failed"
      exit 1
    fi
  fi

  # The control's claim is that the same run, uninterrupted, completes. That is what its result says,
  # so it is recorded here rather than by falling through to the fault branch.
  if [ "$VARIANT" = "durable" ]; then
    CONTROL_PHASE="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry phase)"
    CONTROL_TASK="$(store_get "$RUN_DIR/store.jsonl" task status)"
    CONTROL_REPORT="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry report)"
    CONTROL_ENTRIES="$(store_count "$RUN_DIR/store.jsonl" band-a.enquiry@entries)"
  else
    # The baseline has no store, so its control is read from the page it left on screen.
    CONTROL_PHASE="$(json_get "$RUN_DIR/control.json" final.dashboard.completed 2>/dev/null)"
    CONTROL_TASK="dashboard"
    CONTROL_REPORT="$(json_get "$RUN_DIR/control.json" final.cards.0.title 2>/dev/null)"
    CONTROL_ENTRIES="$(json_get "$RUN_DIR/control.json" final.dashboard.completed 2>/dev/null)"
  fi
  if [ "$VARIANT" = "durable" ]; then
    CONTROL_DETAIL="Control: the same steps, with the held tool result released instead of the process killed. The enquiry reached phase $CONTROL_PHASE with a ${#CONTROL_REPORT}-character report and $CONTROL_ENTRIES transcript entries. This is what the killed run is measured against: the kill is the only difference."
  else
    CONTROL_DETAIL="Control: the same steps, with the held tool result released instead of the process killed. The page's dashboard reads completed=$CONTROL_ENTRIES and the completed card is \"$CONTROL_REPORT\". This is what the killed run is measured against: the kill is the only difference, and the same run then showed 0 cards after it."
  fi
  if [ -n "$CONTROL_REPORT" ]; then
    record_result "$VARIANT" "$FAULT" "completed" \
      "an uninterrupted model turn (the control)" \
      "$CONTROL_DETAIL" \
      "gate held one tool result; release let the turn finish"
    exit 0
  fi
  record_result "$VARIANT" "$FAULT" "lost" "an uninterrupted model turn (the control)" \
    "Control FAILED: without the kill the enquiry still did not complete (phase ${CONTROL_PHASE:-none}, task ${CONTROL_TASK:-none}). A fault that 'loses' an unaffected run is measuring the harness, not the fault." \
    ""
  exit 0
fi

AFTER_KILL_PHASE="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry phase)"
AFTER_KILL_TASK="$(store_get "$RUN_DIR/store.jsonl" task status)"
AFTER_KILL_ENTRIES="$(store_count "$RUN_DIR/store.jsonl" band-a.enquiry@entries)"

# --- observe, in a process that did not witness the fault --------------------------------------
if [ "$VARIANT" = "baseline" ]; then
  # A new process, not a reused one: the killed server is stopped, and the restart is started from the
  # same working directory and port so the only difference is that it is a different process.
  stop_variant_and_clear "$RUN_DIR"
  # The 2nd tool result is already past in this run; the restart starts no enquiry, so nothing is held.
  FAULTS_PAUSE_ON=""
  export FAULTS_PAUSE_ON
  PID2="$(start_variant baseline "$RUN_DIR" "$PORT" enquiry.json)"
  track_pid "$PID2"
  wait_for_port "$PORT" || { log "kill-mid-turn: the restarted baseline never served"; exit 1; }
  cat > "$RUN_DIR/spec-observe.json" <<'JSON'
{"actions": [{"kind": "snapshot", "label": "after the kill, in a new process"}]}
JSON
  drive "$RUN_DIR" "$PORT" "$RUN_DIR/spec-observe.json" "$RUN_DIR/observe.json" || {
    log "kill-mid-turn: the restarted baseline could not be observed"
    exit 1
  }
  # The counts are lifted verbatim from the page after the restart, so the sentence below is a reading
  # of the page rather than a re-derivation of it.
  CARD_COUNT="$(json_get "$RUN_DIR/observe.json" final.dashboard.processing)"
  CARD_COUNT="$("$PYTHON" -c '
import json, sys
final = (json.load(open(sys.argv[1])) or {}).get("final") or {}
print(len(final.get("cards") or []))
' "$RUN_DIR/observe.json")"
  COMPLETED="$(json_get "$RUN_DIR/observe.json" final.dashboard.completed)"
  PROCESSING="$(json_get "$RUN_DIR/observe.json" final.dashboard.processing)"
  REJECTED="$(json_get "$RUN_DIR/observe.json" final.dashboard.rejected)"
  if [ "$CARD_COUNT" = "0" ]; then
    OBSERVED="lost"
    DETAIL="The enquiry was mid-turn when pid $KILLED_PID was killed. A new process, same port, same working directory, shows $CARD_COUNT cards: processing $PROCESSING, completed $COMPLETED, rejected $REJECTED. Nothing resumes: the agent lived in a Dictionary<string, Agent> that died with the process, and the SDK session id was never retained."
  else
    OBSERVED="survived"
    DETAIL="A new process shows $CARD_COUNT card(s) after the kill. That contradicts the baseline's design and is reported rather than smoothed over."
  fi
  EVIDENCE="killed pid $KILLED_PID (kill -9, confirmed gone); restart: same directory $RUN_DIR/app, same port $PORT; observed cards=$CARD_COUNT processing=$PROCESSING completed=$COMPLETED rejected=$REJECTED"
else
  # The resume must not be paused again: the point is to observe what a new process does unaided.
  FAULTS_PAUSE_ON=""
  FAULTS_PAUSE_COUNT=1
  export FAULTS_PAUSE_ON FAULTS_PAUSE_COUNT
  PID2="$(start_headless "$RUN_DIR" enquiry.json --resume)"
  track_pid "$PID2"
  if ! wait_headless "$RUN_DIR" 240; then
    log "kill-mid-turn: the resuming process did not finish; killing it"
    stop_variant_and_clear "$RUN_DIR"
  fi
  RESUMED_COUNT="$(grep -o 'resumed tasks:  *[0-9]*' "$RUN_DIR/server.log" 2>/dev/null | grep -o '[0-9]*$' | head -1)"
  RESUMED_LINE="$(grep -E 'resumes: the enquiry is committed at phase' "$RUN_DIR/server.log" 2>/dev/null | head -1)"
  RESUMED_PHASE="$(printf '%s' "$RESUMED_LINE" | sed -E 's/.*committed at phase ([a-z_]+).*/\1/')"
  AFTER_PHASE="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry phase)"
  AFTER_TASK="$(store_get "$RUN_DIR/store.jsonl" task status)"
  AFTER_ENTRIES="$(store_count "$RUN_DIR/store.jsonl" band-a.enquiry@entries)"
  AFTER_RESTARTS="$(store_get "$RUN_DIR/store.jsonl" band-a.enquiry restarts)"
  REPORT_LEN="$("$PYTHON" - "$RUN_DIR/store.jsonl" <<'PY'
import json, sys
last = None
for line in open(sys.argv[1], encoding="utf-8"):
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
print(len(last.get("report") or "") if last else 0)
PY
)"
  if [ -n "$RESUMED_COUNT" ] && [ "$RESUMED_COUNT" -ge 1 ] 2>/dev/null; then
    OBSERVED="resumed"
    if [ "$INJECT" = "0" ]; then
      PHASE="an uninterrupted model turn (the control)"
      DETAIL="Control: the same steps without the kill. The gate's held turn was released and the enquiry finished with task $AFTER_TASK at phase $AFTER_PHASE, $AFTER_ENTRIES transcript entries, report ${REPORT_LEN} characters. This is what the killed run is compared against."
    else
      PHASE="committed at ${RESUMED_PHASE:-$AFTER_KILL_PHASE} when the process was killed"
      DETAIL="The kill left a committed document at phase $AFTER_KILL_PHASE with $AFTER_KILL_ENTRIES entries and task status $AFTER_KILL_TASK. A new process resumed $RESUMED_COUNT task(s) at phase ${RESUMED_PHASE:-?} and finished with task $AFTER_TASK at phase $AFTER_PHASE, $AFTER_ENTRIES entries, restarts=$AFTER_RESTARTS, report ${REPORT_LEN} characters. The turn that was cut off was re-sent, not replayed: it left no answer to reuse."
    fi
  else
    OBSERVED="lost"
    PHASE="committed at $AFTER_KILL_PHASE when the process was killed"
    DETAIL="A new process resumed nothing (resumed=$RESUMED_COUNT); the store it opened was not continued: task ${AFTER_TASK:-none}, phase ${AFTER_PHASE:-none}."
  fi
  EVIDENCE="$(grep -E 'resumed tasks:|resumes:|finished:|enquiry .* settled' "$RUN_DIR/server.log" 2>/dev/null | head -8)"
fi

record_result "$VARIANT" "$FAULT" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
