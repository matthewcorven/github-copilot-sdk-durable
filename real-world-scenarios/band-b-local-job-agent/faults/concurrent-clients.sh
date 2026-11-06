#!/usr/bin/env bash
# Fault: concurrent-clients. Two loops over the same folder at once.
#
#   ./faults/concurrent-clients.sh --variant baseline|durable [--fault-not-injected]
#
# The first loop is driven to a real tool call at the gate and left there — mid-turn, with files already
# opened. The second loop is started in the same target directory while the first is blocked. Then the
# first is released and both are allowed to finish.
#
# Two things are measured, and neither is inferred:
#
#   * **overlap** — each application gets its own gate, and the gate logs with a wall clock, so the run's
#     two connection windows are read off the log and their intersection is the overlap. A run whose
#     windows do not intersect cannot claim to have been concurrent, so the script refuses to record a
#     fault it did not inject. The control starts the second loop only after the first has finished.
#   * **the target** — whether the plan still parses, whether every plan line is still one of the two
#     forms the template uses, whether the verifier passes, and which files differ between the two loops'
#     writes and a single-loop reference over the same staged target.
#
# The Durable variant's second loop is expected to refuse: the layer takes an exclusive lock on the store
# for the store's lifetime, and the variant turns that refusal into exit 4 without reading the plan or
# sending a prompt. `lib/Durable.cs` already refuses the writer; the exit code and the "nothing was done"
# wording are the variant's, and this fault is what made them necessary — see the README.
#
# The baseline has no lock and no store, so its two loops write to the same files at the same time. What
# that produces is whatever it produces; the result states it.
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
    *) log "concurrent-clients: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "concurrent-clients: --variant baseline|durable is required"; exit 2; }
[ "$VARIANT" = "baseline" ] || [ "$VARIANT" = "durable" ] || { log "concurrent-clients: unknown variant"; exit 2; }

FAULT="concurrent-clients"
[ "$INJECT" = "1" ] || FAULT="concurrent-clients-not-injected"
FAULT_COMMAND="./faults/concurrent-clients.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND
FAULT_SCRIPT="$(basename "${BASH_SOURCE[0]}")"
export FAULT_SCRIPT

require_tools || exit 1

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")" || exit 1
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

# Both loops need a bounded pass; otherwise the first one finishes the whole job before the second starts
# and there is nothing concurrent about it.
LOOP_CAP=2

# Loop 1 is held at its second tool call, which is reached with the target already being written.
FAULTS_HOLD="out:tool.execution_start"
FAULTS_COUNT=2
export FAULTS_HOLD FAULTS_COUNT
FAULTS_APP_LOG="$RUN_DIR/app1.log"
export FAULTS_APP_LOG
start_variant "$VARIANT" "$RUN_DIR" "$LOOP_CAP"
FIRST_PID="$STARTED_PID"

if ! wait_for_inflight "$RUN_DIR" 300; then
  log "concurrent-clients: the first loop never reached a tool call, so there was nothing to overlap"
  tail -15 "$RUN_DIR/app.log" >&2
  exit 1
fi
FIRST_FRAME="$(inflight_frame "$RUN_DIR")"
FIRST_INFLIGHT_AT="$(tail -5 "$RUN_DIR/gate.log" | grep -o '^[0-9.]*' | tail -1)"
log "concurrent-clients: loop 1 is blocked at the gate on '$FIRST_FRAME'"

# The first loop's own log is kept separate from the moment the second starts, so the two connection
# windows can be read from one file each.

if [ "$INJECT" = "1" ]; then
  # Loop 2 starts while loop 1 is blocked. It gets the same gate script, so it gets a gate and runtime of
  # its own; `servers` records both, which is what the reap uses.
  FAULTS_HOLD=""
  FAULTS_COUNT=0
  export FAULTS_HOLD FAULTS_COUNT
  FAULTS_APP_LOG="$RUN_DIR/app2.log"
  export FAULTS_APP_LOG
  # Count the frames from where the shared log had reached when loop 2 started, so "loop 2 has reached
  # the gate" is a count and not a wait for a log line that could already have been there.
  FRAMES_BEFORE_SECOND="$(wc -l < "$RUN_DIR/gate.log")"
  # Taken immediately before the second process is started: this is the overlap claim, and it is a fact
  # about the two applications rather than about the two gates. A second loop that refuses before it
  # reaches the runtime (the Durable variant does) never opens a gate at all, so a gate-only measure
  # would report "no overlap" for the very case where the refusal *is* the finding.
  if kill -0 "$FIRST_PID" 2>/dev/null && [ -s "$RUN_DIR/inflight" ]; then
    OVERLAPPED="loop 1 (pid $FIRST_PID) was alive and blocked at its gate on '$FIRST_FRAME' when loop 2 started"
  else
    OVERLAPPED=""
  fi
  start_variant "$VARIANT" "$RUN_DIR" "$LOOP_CAP"
  SECOND_PID="$STARTED_PID"
  log "concurrent-clients: loop 2 (pid $SECOND_PID) started while loop 1 (pid $FIRST_PID) is blocked"
  # Let loop 2 reach the gate and send its prompt before loop 1 is allowed to continue: the overlap has to
  # be real, not just two processes alive.
  deadline=$((SECONDS + 120))
  while [ "$(wc -l < "$RUN_DIR/gate.log")" -le "$FRAMES_BEFORE_SECOND" ] && [ "$SECONDS" -lt "$deadline" ]; do
    sleep 0.5
  done
  release_gate "$RUN_DIR"
  log "concurrent-clients: released loop 1; both loops now run"
else
  # The control: loop 1 finishes first, and only then is loop 2 started. Same two processes, same target,
  # no overlap — so any corruption in the fault run belongs to the overlap.
  release_gate "$RUN_DIR"
  if ! wait_app "$RUN_DIR" 900; then
    log "concurrent-clients-not-injected: the first loop did not finish within the budget"
    exit 1
  fi
  FIRST_STATUS="$(app_status)"
  OVERLAPPED="none: loop 1 had already exited ($FIRST_STATUS) when loop 2 started"
  reap_servers "$RUN_DIR"
  rm -f "$RUN_DIR/pid" "$RUN_DIR/inflight" "$RUN_DIR/release"
  FAULTS_HOLD=""
  FAULTS_COUNT=0
  export FAULTS_HOLD FAULTS_COUNT
  FAULTS_APP_LOG="$RUN_DIR/app2.log"
  export FAULTS_APP_LOG
  start_variant "$VARIANT" "$RUN_DIR" "$LOOP_CAP"
  SECOND_PID="$STARTED_PID"
  log "concurrent-clients-not-injected: loop 2 (pid $SECOND_PID) started after loop 1 finished (exit $FIRST_STATUS)"
fi

# Both loops, either way: wait for whatever is still running.
for pid in "$FIRST_PID" "$SECOND_PID"; do
  deadline=$((SECONDS + 1200))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.5; done
done
FIRST_ALIVE="$(kill -0 "$FIRST_PID" 2>/dev/null && echo yes || echo no)"
SECOND_ALIVE="$(kill -0 "$SECOND_PID" 2>/dev/null && echo yes || echo no)"
if [ "$FIRST_ALIVE" = "yes" ] || [ "$SECOND_ALIVE" = "yes" ]; then
  log "concurrent-clients: a loop was still running after the budget; that is a harness error"
  exit 1
fi
wait "$FIRST_PID" 2>/dev/null; FIRST_STATUS=$?
wait "$SECOND_PID" 2>/dev/null; SECOND_STATUS=$?

# ── the overlap, from the two gates' own clocks ─────────────────────────────────────────────────
OVERLAP="$("$PYTHON" "$HARNESS_DIR/lib/overlap.py" "$RUN_DIR/gate.log")"
read -r CONNECTION_SHAPE CONNECTION_COUNT OVERLAP_SECONDS <<< "$OVERLAP"

PLAN="$(plan_counts "$RUN_DIR/target")"
VERIFY="$(verify_target "$RUN_DIR/target")"
ITERATIONS="$(iterations_run "$RUN_DIR/app1.log")+$(iterations_run "$RUN_DIR/app2.log")"
END1="$(end_line "$RUN_DIR/app1.log")"
END2="$(end_line "$RUN_DIR/app2.log")"
STORE="$(store_state "$RUN_DIR/target/.durable/store.jsonl")"

# Whether the plan is still a plan: every checklist line is the template's `- [ ]` or `- [x]`, and the
# status line is one of the two it has. A file written by two processes at once can be neither.
PLAN_SHAPE="$("$PYTHON" "$HARNESS_DIR/lib/plan_shape.py" "$RUN_DIR/target/PLAN.md")"

RECEIVED_BY_SECOND="$(grep -c 'refused:' "$RUN_DIR/app2.log" 2>/dev/null || true)"
EVIDENCE="overlap claim: $OVERLAPPED; gate connection windows: $CONNECTION_SHAPE ($CONNECTION_COUNT connection(s), overlap ${OVERLAP_SECONDS}s); loop 1 exit $FIRST_STATUS, loop 2 exit $SECOND_STATUS; gate frames: $(gate_frames "$RUN_DIR" IN) in / $(gate_frames "$RUN_DIR" OUT) out; plan shape: $PLAN_SHAPE; store '$STORE'"

if [ "$INJECT" = "1" ] && [ -z "$OVERLAPPED" ]; then
  log "concurrent-clients: loop 1 was not alive and blocked when loop 2 started, so the fault was not injected"
  exit 1
fi

if [ "$INJECT" = "1" ]; then
  if [ "${RECEIVED_BY_SECOND:-0}" -gt 0 ]; then
    record_result "$VARIANT" "$FAULT" "refused the second client" \
      "loop 1 mid-turn; loop 2 started against the same target while loop 1 was blocked" \
      "Two loops over one folder overlapped: $OVERLAPPED. Loop 2 exited $SECOND_STATUS and its own first line about the collision was: \"$(grep -m1 'refused:' "$RUN_DIR/app2.log" | cut -c1-200)\". Loop 1 exited $FIRST_STATUS after $ITERATIONS iteration line(s) in total. Plan '$PLAN', shape $PLAN_SHAPE, verifier '$VERIFY'. The second loop did not read the plan, send a prompt or touch a file: the refusal comes from the layer's exclusive lock on the store file, and the variant reports it as a refusal rather than as a failed run." \
      "$EVIDENCE; loop 1 last line \"$END1\"; loop 2 last line \"$END2\""
    exit 0
  fi
  if [ "$PLAN_SHAPE" = "intact" ] && [ "$VERIFY" = "pass" ]; then
    record_result "$VARIANT" "$FAULT" "not corrupted" \
      "loop 1 mid-turn; loop 2 started against the same target while loop 1 was blocked" \
      "Two loops over one folder overlapped — $OVERLAPPED. Neither refused the other, both finished (exit $FIRST_STATUS and $SECOND_STATUS), the plan is still a plan ($PLAN_SHAPE), and the verifier passes. $ITERATIONS iteration line(s) in total, plan '$PLAN'. Nothing serialised them and nothing broke: that is the finding, and it is not the same claim as 'serialised'." \
      "$EVIDENCE"
    exit 0
  fi
  record_result "$VARIANT" "$FAULT" "corrupted" \
    "loop 1 mid-turn; loop 2 started against the same target while loop 1 was blocked" \
    "Two loops over one folder overlapped — $OVERLAPPED. Loop 1 exited $FIRST_STATUS (last line \"$END1\"); loop 2 exited $SECOND_STATUS (last line \"$END2\"). Plan '$PLAN', shape $PLAN_SHAPE, verifier '$VERIFY'. The target the two loops left is not one either loop would have produced alone." \
    "$EVIDENCE"
  exit 0
fi

if [ "$PLAN_SHAPE" = "intact" ] && [ "$VERIFY" = "pass" ]; then
  record_result "$VARIANT" "$FAULT" "control: serialised, not corrupted" \
    "loop 1 to completion, then loop 2 against the same target (the control)" \
    "Control: the same two loops with no overlap — loop 1 exited $FIRST_STATUS before loop 2 started, loop 2 exited $SECOND_STATUS, plan '$PLAN', shape $PLAN_SHAPE, verifier: pass. Two loops one after the other over the same target do not corrupt it; so a corrupt target in the fault run belongs to the overlap, not to running twice." \
    "$EVIDENCE"
  exit 0
fi

record_result "$VARIANT" "$FAULT" "control: corrupted without overlap" \
  "loop 1 to completion, then loop 2 against the same target (the control)" \
  "Control FAILED: two loops run one after the other still left a target that does not match the plan (plan '$PLAN', shape $PLAN_SHAPE, verifier '$VERIFY'). A fault cannot be blamed for damage that happens without it." \
  "$EVIDENCE"
exit 0
