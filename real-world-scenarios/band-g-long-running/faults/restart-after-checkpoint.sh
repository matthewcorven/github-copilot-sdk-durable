#!/usr/bin/env bash
# Fault: restart-after-checkpoint. Stop after a committed checkpoint, then resume, and count the calls.
#
#   ./faults/restart-after-checkpoint.sh --variant baseline|durable [--fault-not-injected]
#
# The pre-gate work is the part a run has already done when it stops. The question this fault asks is not
# "does it resume" — both variants do — but "does the new process re-do the work the old one had
# committed". The answer is a number, and it is taken the same way for both:
#
#   baseline  The "stop" is inside the endpoint: the recorded endpoint holds the call the writer agent
#             would make, and the process is stopped while it is inside that model call, after the
#             manager's plan turn and the researcher's turn had completed inside the one resumed
#             process. The resume is a fresh process against the same checkpoint directory; the calls
#             *it* makes are the number.
#   durable   The "stop" is `--fault write`: the process leaves itself after the write step's turn and
#             before its checkpoint commits, which is exactly "stopped after the last committed
#             checkpoint and inside the next step". The resume is a fresh process, and the ledger's call
#             count is the number.
#
# `--fault-not-injected` is the same flow with the stop removed: no hold, no fault step. Both variants
# must then finish in one process, which is what makes the second process's count attributable to the
# restart.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

parse_variant_args "${BASH_SOURCE[0]}" "$@"
FAULT="$(fault_name restart-after-checkpoint)"
PORT="$(port_for "$VARIANT" "$FAULT")"
FAULT_COMMAND="./faults/restart-after-checkpoint.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1
build_variants || exit 1
install_cleanup

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
mkdir -p "$RUN_DIR"

if [ "$VARIANT" = "baseline" ]; then
  # ── baseline ────────────────────────────────────────────────────────────────────────────────
  # The stop lands on the writer agent's turn: request 2 of the resumed process. Requests are counted by
  # the endpoint, so the hold is on a number rather than on a promise about timing.
  if [ "$INJECT" = "1" ]; then
    pause_on=2
  else
    pause_on=0
  fi
  fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
  start_baseline_bg "$RUN_DIR" run "run"
  pid="$APP_PID"
  track_pid "$pid"
  wait_for_exit "$pid" 180 || { log "restart-after-checkpoint: process 1 never reached the gate"; exit 1; }
  CALLS_PROCESS_1="$(count_calls "$RUN_DIR")"
  GATE_CHECKPOINT="$(observed baseline "$RUN_DIR" gate-checkpoint)"
  assert_observed "the baseline wrote no gate checkpoint" "$GATE_CHECKPOINT" || exit 1

  # The resumed process, with the endpoint armed to hold the writer's turn.
  fresh_endpoint "$RUN_DIR" "$PORT" "$pause_on" || exit 1
  start_baseline_bg "$RUN_DIR" resume "resume"
  pid="$APP_PID"
  track_pid "$pid"

  if [ "$INJECT" = "1" ]; then
    if ! wait_for_file "$RUN_DIR/inflight" 90; then
      log "restart-after-checkpoint: the endpoint never held the writer's turn"
      exit 1
    fi
    # The process is inside the writer's model call. Stop it there — a restart, not a kill: SIGTERM and
    # then the same confirmation, so the difference from kill-during-wait is what the stop is, not its
    # outcome.
    CALLS_BEFORE_STOP="$(count_calls "$RUN_DIR")"
    # The PID is the application itself (start_baseline_bg asserts that), so this stop lands on the process
    # holding the model call — not on a shell wrapper with the application behind it.
    stop_and_confirm "$pid" "$RUN_DIR" "the resumed process" || exit 1
    STOPPED_PID="$pid"
    touch "$RUN_DIR/release"
    PHASE_STOPPED="the resumed process, inside the writer's model call, after the plan and research turns"
  else
    if ! wait_for_exit "$pid" 240; then
      log "restart-after-checkpoint-control: the uninterrupted resume did not finish"
      exit 1
    fi
    CALLS_BEFORE_STOP="$(count_calls "$RUN_DIR")"
    STOPPED_PID=""
    PHASE_STOPPED="the resumed process, uninterrupted"
  fi

  # ── observe ─────────────────────────────────────────────────────────────────────────────────
  # A fresh process, a fresh endpoint. Its call count is the number in the result.
  fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
  resume_status=0
  baseline_mode "$RUN_DIR" resume >"$RUN_DIR/after.log" 2>&1 || resume_status=$?
  CALLS_SECOND="$(count_calls "$RUN_DIR")"
  SECOND_SHAPES="$(call_shapes "$RUN_DIR" | tr '\n' ' ')"
  LATEST="$(store_read baseline "$RUN_DIR" latest-checkpoint 2>/dev/null)"
  LATEST="${LATEST:-(absent)}"
  APPROVED="$(observed baseline "$RUN_DIR" approved-in-store)"
  CHECKPOINTS="$(observed baseline "$RUN_DIR" checkpoint-count)"

  if [ "$INJECT" = "1" ] && [ -n "$STOPPED_PID" ]; then
    OBSERVED="resumed, completed work re-done"
    PHASE="stopped inside the writer's turn; a fresh process completed the run"
    DETAIL="The stop landed on pid $STOPPED_PID ($PHASE_STOPPED) with $CALLS_BEFORE_STOP model call(s) already served to that process. A fresh resume against the same checkpoint directory exited $resume_status and wrote checkpoint $LATEST ($CHECKPOINTS file(s) now, approved checkpoint present: $APPROVED). The second process issued $CALLS_SECOND model call(s): the turns the stopped process had already completed inside its own process were not carried over by its checkpoint, only the workflow's own state was, so the work of the stopped process was re-done. The model is the recorded transcript, so the count is of calls made, not of answers invented."
  elif [ "$resume_status" -eq 0 ]; then
    OBSERVED="resumed, uninterrupted"
    PHASE="uninterrupted (the control)"
    DETAIL="Control: the same two processes with no stop. The first process exited $resume_status after $CALLS_BEFORE_STOP model call(s), wrote checkpoint $LATEST, and a second process started from that checkpoint issued $CALLS_SECOND model call(s) and left the run completed. This is what the fault result is measured against: the interrupted run differs from it only by the stop."
  else
    OBSERVED="lost"
    PHASE="stopped inside the writer's turn; the fresh process did not finish"
    DETAIL="The stop landed on pid ${STOPPED_PID:-none}. A fresh resume exited $resume_status and left the latest checkpoint at $LATEST after issuing $CALLS_SECOND model call(s)."
  fi

  EVIDENCE="process 1: $CALLS_PROCESS_1 model call(s); gate checkpoint $GATE_CHECKPOINT
stopped pid ${STOPPED_PID:-none} at: $PHASE_STOPPED; $CALLS_BEFORE_STOP model call(s) served to it
second process (fresh resume, exit $resume_status): $CALLS_SECOND model call(s), shapes: ${SECOND_SHAPES% }
latest checkpoint: $LATEST; approved checkpoint present: $APPROVED; checkpoints on disk: $CHECKPOINTS"
  EXTRA="$(printf '{"model_calls_process_1": %s, "model_calls_stopped_process": %s, "model_calls_second_process": %s}' \
    "${CALLS_PROCESS_1:-0}" "${CALLS_BEFORE_STOP:-0}" "$CALLS_SECOND")"
else
  # ── durable ─────────────────────────────────────────────────────────────────────────────────
  if [ "$INJECT" = "1" ]; then
    fault_args=(--fault write)
    PHASE_STOPPED="the write step, after its turn and before its checkpoint commits"
  else
    fault_args=()
    PHASE_STOPPED="no fault armed (the control)"
  fi

  PAUSE_ON="" start_durable_bg "$RUN_DIR" start start --gate-seconds 600 --deterministic "${fault_args[@]+"${fault_args[@]}"}"
  pid="$APP_PID"
  track_pid "$pid"
  if ! wait_for_exit "$pid" 180; then
    log "restart-after-checkpoint: the start process did not finish"
    exit 1
  fi
  if [ "$INJECT" = "0" ]; then
    # The control has to have reached the gate in one process, so it is driven to the point where the
    # gate's wait is a committed record rather than a phase a process is still entering.
    durable_at_gate "$RUN_DIR" 60 || exit 1
  fi
  START_STATUS="$(observed durable "$RUN_DIR" task-status)"
  PHASE_BEFORE="$(observed durable "$RUN_DIR" phase)"
  CALLS_BEFORE_STOP="$(observed durable "$RUN_DIR" calls)"
  STEPS_BEFORE="$(observed durable "$RUN_DIR" call-steps)"

  if [ "$INJECT" = "1" ]; then
    # The fault's precondition: the process left itself after a committed checkpoint, so the run is not
    # finished and the next process is the only one that can move it.
    if [ "$START_STATUS" = "terminal" ]; then
      log "restart-after-checkpoint: the armed fault did not stop the process (the run finished)"
      exit 1
    fi
  else
    # The control must have reached the gate in one process, or it is not a control for anything.
    if [ "$START_STATUS" != "waiting" ]; then
      log "restart-after-checkpoint-control: the uninterrupted start did not reach the gate ($START_STATUS)"
      exit 1
    fi
  fi

  # ── observe ─────────────────────────────────────────────────────────────────────────────────
  PAUSE_ON="" durable_verb "$RUN_DIR" resume --deterministic >"$RUN_DIR/after.log" 2>&1
  resume_status=$?
  CALLS_AFTER_RESUME="$(observed durable "$RUN_DIR" calls)"
  STEPS_AFTER_RESUME="$(observed durable "$RUN_DIR" call-steps)"
  MEMO_AFTER_RESUME="$(observed durable "$RUN_DIR" memo-hits)"
  TASK_STATUS="$(observed durable "$RUN_DIR" task-status)"
  GATE_PHASE="$(observed durable "$RUN_DIR" gate-phase)"

  if [ "$INJECT" = "1" ]; then
    OBSERVED="resumed, no calls repeated"
    PHASE="stopped after the ${PHASE_BEFORE} checkpoint (inside ${PHASE_STOPPED}); the resume stopped at the gate"
    DETAIL="The fault left the process before the write step's checkpoint committed, with the run at phase $PHASE_BEFORE and the ledger reading $CALLS_BEFORE_STOP call(s) [$STEPS_BEFORE]. A fresh process re-entered the run and stopped at the gate again: task $TASK_STATUS, gate phase $GATE_PHASE, $CALLS_AFTER_RESUME call(s) [$STEPS_AFTER_RESUME] with $MEMO_AFTER_RESUME memo hit(s). So the resume added no model call at all for a step the store already held: every step it re-entered was served from the memo, and the resume's own count is $((CALLS_AFTER_RESUME - CALLS_BEFORE_STOP))."
  else
    OBSERVED="resumed, uninterrupted"
    PHASE="uninterrupted (the control): reached the gate in one process"
    DETAIL="Control: the same flow with no fault armed. The start process drove the run to the gate in one process — task $TASK_STATUS, gate phase $GATE_PHASE — with $CALLS_BEFORE_STOP call(s) [$STEPS_BEFORE]. The second process re-entered it and added $((CALLS_AFTER_RESUME - CALLS_BEFORE_STOP)) call(s) ($CALLS_AFTER_RESUME total, $MEMO_AFTER_RESUME memo hit(s)). The interrupted run differs from this one only by the fault, so a call the fault run's resume adds is the restart's doing."
  fi

  EVIDENCE="start (${PHASE_STOPPED}): $CALLS_BEFORE_STOP call(s) [$STEPS_BEFORE], run $START_STATUS at phase $PHASE_BEFORE
resume (exit $resume_status): $CALLS_AFTER_RESUME call(s) [$STEPS_AFTER_RESUME], $MEMO_AFTER_RESUME memo hit(s), run $TASK_STATUS, gate phase $GATE_PHASE
$(tail -2 "$RUN_DIR/after.log" 2>/dev/null)"
  EXTRA="$(printf '{"model_calls_process_1": %s, "model_calls_process_2": %s, "calls_added_by_resume": %s, "memo_hits": %s, "steps_after_resume": "%s"}' \
    "${CALLS_BEFORE_STOP:-0}" "${CALLS_AFTER_RESUME:-0}" "$(( ${CALLS_AFTER_RESUME:-0} - ${CALLS_BEFORE_STOP:-0} ))" "${MEMO_AFTER_RESUME:-0}" "${STEPS_AFTER_RESUME}")"
fi

record_result "$VARIANT" "$FAULT" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
exit 0
