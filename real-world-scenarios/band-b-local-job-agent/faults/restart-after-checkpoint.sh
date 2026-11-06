#!/usr/bin/env bash
# Fault: restart-after-checkpoint. The process is stopped between two iterations, and a second process
# is asked to continue.
#
#   ./faults/restart-after-checkpoint.sh --variant baseline|durable [--fault-not-injected]
#
# The injection point is the gate holding the *second* `session.send`: iteration 1's turn has returned
# and its work is on disk, iteration 2's prompt has not reached the runtime. That is the boundary the
# fault is about, and the gate writes `inflight` when it is there, so the stop is not a sleep.
#
# What each variant does with the second process is the whole comparison:
#
#   baseline  its position is a local variable. A new process re-reads `PLAN.md` and starts at
#             iteration 1 of its own cap. Nothing is resumed because nothing was recorded.
#   durable   its position is a checkpoint in the store. A new process reads it and continues at the
#             iteration the checkpoint names — and because the iteration that was in flight committed
#             `turn-started` and nothing else, this is the case the variant deliberately refuses: it
#             reports what the store holds and fails rather than re-running a turn that may have
#             written files. The result says which of the two things happened, from the store.
#
#   --fault-not-injected is the control: the same steps, the same second process, and no stop. Without
#   it "the second process did not finish the job" could be a property of the harness.
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
[ "$VARIANT" = "baseline" ] || [ "$VARIANT" = "durable" ] || { log "restart-after-checkpoint: unknown variant"; exit 2; }

FAULT="restart-after-checkpoint"
[ "$INJECT" = "1" ] || FAULT="restart-after-checkpoint-control"
FAULT_COMMAND="./faults/restart-after-checkpoint.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND
FAULT_SCRIPT="$(basename "${BASH_SOURCE[0]}")"
export FAULT_SCRIPT

require_tools || exit 1

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")" || exit 1
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

# The seam, chosen per variant and *checked* rather than assumed.
#
#   baseline   it has no between-iterations seam on this machine: one pass finishes the whole plan, so
#              there is never a second iteration to stop between. Its reachable equivalent is a stop after
#              a tool call has *returned* inside the pass, and the second process is what shows that
#              nothing recorded where the stop landed.
#   durable    the seam between two iterations is a *new conversation being created for the next one*, and
#              the store says what state the run is in at that instant. Which state that is, is measured
#              here rather than assumed — see the branch below, which records `not exercised` if the
#              frame is always inside an issued turn.
if [ "$VARIANT" = "durable" ]; then
  FAULTS_HOLD="in:session.create"
  FAULTS_COUNT=3
else
  FAULTS_HOLD="out:tool.execution_complete"
  FAULTS_COUNT=1
fi
FAULTS_REPEAT=0
export FAULTS_HOLD FAULTS_COUNT FAULTS_REPEAT

start_variant "$VARIANT" "$RUN_DIR" "$TARGET_CAP"
FIRST_PID="$STARTED_PID"

if ! wait_for_inflight "$RUN_DIR" 600; then
  log "restart-after-checkpoint: the gate never held $FAULTS_HOLD, so no iteration boundary was reached"
  tail -15 "$RUN_DIR/app.log" >&2
  exit 1
fi
FRAME="$(inflight_frame "$RUN_DIR")"
log "restart-after-checkpoint: $VARIANT is blocked at the gate on '$FRAME'"

if [ "$VARIANT" = "durable" ]; then
  # What the store says *at the seam*. `turn-started/<n>` means iteration n+1's turn has been issued and
  # has not returned: the run is inside an iteration, not between two, and a second process finds the
  # in-flight turn — the state `kill-mid-turn` records, not a restart from a boundary. That is measured
  # here, and the result below says so instead of claiming a restart this variant cannot be given from
  # outside its own process.
  SEAM_STATE="$(store_state "$RUN_DIR/target/.durable/store.jsonl")"
  SEAM_CHECKPOINT="$(printf '%s' "$SEAM_STATE" | awk '{print $4}')"
  SETTLED="$(grep -c '^iteration [0-9]* complete\.$' "$RUN_DIR/app.log" 2>/dev/null || true)"
  case "$SEAM_CHECKPOINT" in
    turn-started/*)
      log "restart-after-checkpoint: the seam '$FRAME' is inside iteration $(( ${SEAM_CHECKPOINT#*/} + 1 )) (checkpoint '$SEAM_CHECKPOINT'); releasing the frame so the run is not left held"
      if [ "$INJECT" = "1" ]; then
        record_not_exercised "$VARIANT" "$FAULT" \
          "a boundary between two iterations, seen from outside the process" \
          "Not reachable, and checked rather than assumed. The only frame a process outside the run can stop at between iterations is the creation of the next iteration's conversation. At that frame the store already says '$SEAM_CHECKPOINT' — iteration $(( ${SEAM_CHECKPOINT#*/} + 1 ))'s turn was issued and has not returned — because the checkpoint that marks a turn started is committed before the conversation is created and the settle that follows is inside the same iteration. ${SETTLED} iteration(s) had completed by then. So a process stopped here leaves the state kill-mid-turn records: a turn that was issued and never finished, which the next process refuses rather than repeats. A restart from a *settled* boundary exists only inside the process, and a fault injected from outside cannot land on it. The control below is the same run uninterrupted, which does finish."
        release_gate "$RUN_DIR"
        if ! wait_app "$RUN_DIR" 1800; then
          log "restart-after-checkpoint: the released run did not finish, so the control's run is unusable"
          exit 1
        fi
        exit 0
      fi
      ;;
  esac
fi

BEFORE_PLAN="$(plan_counts "$RUN_DIR/target")"
BEFORE_DIGEST="$(product_digest "$RUN_DIR/target" | shasum -a 256 | cut -d' ' -f1)"
BEFORE_STORE="$(store_state "$RUN_DIR/target/.durable/store.jsonl")"
BEFORE_ITERATIONS="$(iterations_run "$RUN_DIR/app.log")"

if [ "$INJECT" = "1" ]; then
  # SIGTERM, and what ended the process is reported as the signal `wait` saw — never as "a clean stop".
  kill -TERM "$FIRST_PID" 2>/dev/null || true
  STOP_SIGNAL="SIGTERM"
  deadline=$((SECONDS + 30))
  while kill -0 "$FIRST_PID" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.2; done
  if kill -0 "$FIRST_PID" 2>/dev/null; then
    log "restart-after-checkpoint: the process survived SIGTERM; killing it so the run can continue"
    kill_and_confirm "$FIRST_PID" application || exit 1
    STOP_HOW="survived SIGTERM, then killed"
  else
    wait "$FIRST_PID" 2>/dev/null
    FIRST_STATUS=$?
    # A shell reports 128+N for a process that died of signal N, so 143 is SIGTERM and not "128+43".
    # Nothing here calls the stop clean: the application registers no handler of its own.
    if [ "$FIRST_STATUS" -gt 128 ] 2>/dev/null; then
      STOP_HOW="wait reported $FIRST_STATUS, i.e. it died of signal $((FIRST_STATUS - 128)) (SIGTERM is 15)"
    else
      STOP_HOW="wait reported $FIRST_STATUS"
    fi
  fi
  reap_servers "$RUN_DIR"
  rm -f "$RUN_DIR/pid" "$RUN_DIR/inflight" "$RUN_DIR/release"
  log "restart-after-checkpoint: stopped pid $FIRST_PID: $STOP_HOW"
else
  # The control releases the held frame and lets the first process run on. Its result is what the
  # stopped run is compared against, so it is recorded from the same steps and nothing else.
  release_gate "$RUN_DIR"
  if ! wait_app "$RUN_DIR" 1200; then
    log "restart-after-checkpoint-control: the uninterrupted run did not finish within the budget"
    exit 1
  fi
  CONTROL_STATUS="$(app_status)"
  CONTROL_PLAN="$(plan_counts "$RUN_DIR/target")"
  CONTROL_DIGEST="$(product_digest "$RUN_DIR/target" | shasum -a 256 | cut -d' ' -f1)"
  CONTROL_VERIFY="$(verify_target "$RUN_DIR/target")"
  CONTROL_ITERATIONS="$(iterations_run "$RUN_DIR/app.log")"
  if [ "$CONTROL_VERIFY" = "pass" ]; then
    record_result "$VARIANT" "$FAULT" "completed" \
      "an uninterrupted run to the end (the control)" \
      "Control: the same steps without the stop. $CONTROL_ITERATIONS iteration(s), exit $CONTROL_STATUS, plan '$CONTROL_PLAN', product digest ${CONTROL_DIGEST:0:16}, verifier: pass. This is what the stopped run is measured against; the stop is the only difference." \
      "gate held '$FRAME' for iteration 2's prompt and released it; the run then reached its own end"
    exit 0
  fi
  record_result "$VARIANT" "$FAULT" "control did not finish" \
    "an uninterrupted run to the end (the control)" \
    "Control FAILED: without any stop the same run did not produce a target that matches the plan ($CONTROL_VERIFY), exit $CONTROL_STATUS, plan '$CONTROL_PLAN'. A fault cannot be blamed for a run the harness could not finish." \
    ""
  exit 0
fi

# ── the second process ─────────────────────────────────────────────────────────────────────────────
#
# Nothing is staged again and nothing is cleared: the second process finds exactly what the first left.
FAULTS_HOLD=""
FAULTS_COUNT=0
FAULTS_REPEAT=0
export FAULTS_HOLD FAULTS_COUNT FAULTS_REPEAT
start_variant "$VARIANT" "$RUN_DIR" "$TARGET_CAP"
SECOND_PID="$STARTED_PID"
if ! wait_app "$RUN_DIR" 1200; then
  log "restart-after-checkpoint: the second process did not finish within the budget"
  exit 1
fi
SECOND_STATUS="$(app_status)"

AFTER_PLAN="$(plan_counts "$RUN_DIR/target")"
AFTER_VERIFY="$(verify_target "$RUN_DIR/target")"
AFTER_ITERATIONS="$(iterations_run "$RUN_DIR/app.log")"
AFTER_STORE="$(store_state "$RUN_DIR/target/.durable/store.jsonl")"
END="$(end_line "$RUN_DIR/app.log")"

if [ "$VARIANT" = "baseline" ]; then
  # `=== Iteration n/N` appears twice per pass in this log — once from the first process, once from the
  # second — so the second process's own count is what it printed after it started. Nothing is resumed
  # because nothing was written down: the position was a local variable.
  record_result "$VARIANT" "$FAULT" "restarted from the plan" \
    "stopped after a tool call returned, inside the pass" \
    "Before the stop: plan '$BEFORE_PLAN' (${BEFORE_ITERATIONS} iteration line(s) printed), seam '$FRAME'. Stopped pid $FIRST_PID: $STOP_HOW. A second process then ran with the same command and no resume capability: $AFTER_ITERATIONS iteration line(s) in the log in total (its own are appended), exit $SECOND_STATUS, plan '$AFTER_PLAN', verifier '$AFTER_VERIFY', last line: \"$END\". The baseline keeps its position in a local variable, so the second process re-read the plan and began again at its own iteration 1; the iteration the first process committed is not known to it." \
    "product digest before the stop ${BEFORE_DIGEST:0:16}; no store exists for the baseline (plan only); frames at the gate: $(gate_frames "$RUN_DIR" IN) in, $(gate_frames "$RUN_DIR" OUT) out"
  exit 0
fi

# Durable. The store is the record, so the claim comes from it, not from the transcript.
RECORDED_BEFORE="${BEFORE_STORE##* }"
case "$AFTER_STORE" in
  missing*) STORE_NOTE="the store disappeared" ;;
  *) STORE_NOTE="store $AFTER_STORE" ;;
esac

if grep -q 'was in flight' "$RUN_DIR/app.log" 2>/dev/null; then
  record_result "$VARIANT" "$FAULT" "refused to resume the in-flight iteration" \
    "stopped inside an iteration's turn, which is the in-flight limit rather than a boundary" \
    "Before the stop the store held '$BEFORE_STORE'. Stopped pid $FIRST_PID: $STOP_HOW. The second process read the checkpoint ($RECORDED_BEFORE) and refused: the iteration that was in flight had committed 'turn-started' and no outcome, so re-running the turn could duplicate whatever it had already written. Its own accounting: \"$END\". Exit $SECOND_STATUS, plan '$AFTER_PLAN', verifier '$AFTER_VERIFY'. $STORE_NOTE. This is the variant's stated limit rather than a crash: the layer records a turn when it returns and nothing if it does not." \
    "gate frame held: '$FRAME'; checkpoint before: $RECORDED_BEFORE; checkpoint after: $AFTER_STORE"
  exit 0
fi

if grep -q 'resuming at iteration' "$RUN_DIR/app.log" 2>/dev/null; then
  RESUMED_AT="$(grep -m1 'resuming at iteration' "$RUN_DIR/app.log" | sed -E 's/.*iteration ([0-9]+).*/\1/')"
  if [ "$AFTER_VERIFY" = "pass" ]; then
    record_result "$VARIANT" "$FAULT" "resumed and finished" \
      "stopped at a settled iteration boundary" \
      "Before the stop the store held '$BEFORE_STORE'. Stopped pid $FIRST_PID: $STOP_HOW. A second process resumed at iteration $RESUMED_AT and finished: exit $SECOND_STATUS, plan '$AFTER_PLAN', verifier: pass, $AFTER_ITERATIONS iteration line(s) in total, last line \"$END\". $STORE_NOTE." \
      "checkpoint before: $RECORDED_BEFORE; after: $AFTER_STORE"
    exit 0
  fi
  record_result "$VARIANT" "$FAULT" "resumed but did not finish" \
    "stopped at a settled iteration boundary" \
    "The second process resumed at iteration $RESUMED_AT but did not reach a target that matches the plan ($AFTER_VERIFY); exit $SECOND_STATUS, plan '$AFTER_PLAN', last line \"$END\". The resume happened; the job did not complete, and both are reported." \
    "checkpoint before: $RECORDED_BEFORE; after: $AFTER_STORE"
  exit 0
fi

record_result "$VARIANT" "$FAULT" "lost its position" \
  "stopped at a settled iteration boundary" \
  "Before the stop the store held '$BEFORE_STORE'; after the second process it holds '$AFTER_STORE'. The second process neither resumed nor refused: exit $SECOND_STATUS, plan '$AFTER_PLAN', last line \"$END\". Nothing continued the run." \
  "checkpoint before: $RECORDED_BEFORE"
exit 0
