#!/usr/bin/env bash
# Fault: late-approval. Approve after the deadline, and see whether the terminal state is overwritten.
#
#   ./faults/late-approval.sh --variant baseline|durable [--fault-not-injected]
#
# The approval arrives after the gate has already settled. What "settled" means differs, and the fault
# reports each as it is:
#
#   baseline  There is no timer and no deadline in the workflow. The gate settles only when a resume
#             answers it, so a "late" approval is a *second* resume after the first one finished the run:
#             it arrives after the gate checkpoint has been consumed. What is observed is whether it can
#             still move the run.
#   durable   The gate has a durable deadline. A run created with a 3-second window, left alone, is
#             resumed by a process that arrives after the window has passed: the gate wakes at once, reads
#             no decision, and records a timeout. The late approval then arrives and must be refused as
#             late — by the terminal guard, which is a different refusal from the duplicate's document rule.
#
# `--fault-not-injected` approves inside the window. The run then ends approved. A late-approval result
# is only meaningful against that: the same approval, on time, does take effect.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

parse_variant_args "${BASH_SOURCE[0]}" "$@"
FAULT="$(fault_name late-approval)"
PORT="$(port_for "$VARIANT" "$FAULT")"
FAULT_COMMAND="./faults/late-approval.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1
build_variants || exit 1
install_cleanup

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
mkdir -p "$RUN_DIR"

if [ "$VARIANT" = "baseline" ]; then
  # The baseline has no deadline, so "late" is measured against the run's own end: the approval arrives
  # only after the first resume has already applied one and finished the run.
  fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
  start_baseline_bg "$RUN_DIR" run "run"
  track_pid "$APP_PID"
  wait_for_exit "$APP_PID" 180 || { log "late-approval: process 1 never reached the gate"; exit 1; }
  GATE_CHECKPOINT="$(observed baseline "$RUN_DIR" gate-checkpoint)"
  assert_observed "the baseline wrote no gate checkpoint" "$GATE_CHECKPOINT" || exit 1

  fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
  ontime_status=0
  baseline_mode "$RUN_DIR" resume >"$RUN_DIR/ontime.log" 2>&1 || ontime_status=$?
  CALLS_ON_TIME="$(count_calls "$RUN_DIR")"
  FINISHED_CHECKPOINT="$(observed baseline "$RUN_DIR" latest-checkpoint)"
  FINISHED_APPROVED="$(observed baseline "$RUN_DIR" approved-in-store)"
  CHECKPOINTS_DONE="$(observed baseline "$RUN_DIR" checkpoint-count)"

  if [ "$INJECT" = "1" ]; then
    fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
    late_status=0
    baseline_mode "$RUN_DIR" resume >"$RUN_DIR/late.log" 2>&1 || late_status=$?
    CALLS_LATE="$(count_calls "$RUN_DIR")"
    AFTER_LATE="$(observed baseline "$RUN_DIR" latest-checkpoint)"
    LATE_RAN="$(grep -c 'Approving plan and continuing to completion' "$RUN_DIR/late.log" 2>/dev/null)"
    LATE_RAN="${LATE_RAN:-0}"

    if [ "$LATE_RAN" = "0" ]; then
      OBSERVED="not overwritten"
      PHASE="approved after the run had already finished"
      DETAIL="The run finished from the gate checkpoint on the first resume (exit $ontime_status, checkpoint $FINISHED_CHECKPOINT, $CHECKPOINTS_DONE file(s), $CALLS_ON_TIME model call(s)). The later resume — the same approval, arriving after the fact — found no plan-review request to answer: it exited $late_status without printing 'Approving plan', issued $CALLS_LATE model call(s) and left the latest checkpoint at $AFTER_LATE."
    else
      OBSERVED="applied again"
      PHASE="approved after the run had already finished"
      DETAIL="The run finished on the first resume (checkpoint $FINISHED_CHECKPOINT, $CALLS_ON_TIME model call(s)). The later resume — the same approval, in a new process against the same checkpoint directory — approved again: it printed 'Approving plan', issued $CALLS_LATE model call(s) and wrote checkpoint $AFTER_LATE. There is no terminal state to overwrite, because the baseline has no terminal record and no guard: the gate checkpoint stays answerable and a late approval re-runs the workflow from it. What the baseline also does not have is any record that this was late — no decision document and nothing that refused."
    fi
  else
    OBSERVED="approved in time"
    PHASE="approved inside the run (the control)"
    DETAIL="Control: the approval delivered on time. The resume exited $ontime_status and the run finished — checkpoint $FINISHED_CHECKPOINT, $CHECKPOINTS_DONE checkpoint file(s), $CALLS_ON_TIME model call(s), a post-approval checkpoint present: $FINISHED_APPROVED. This is what the late approval is measured against: the same approval, on time, is what finishes the run."
  fi

  EVIDENCE="gate checkpoint: $GATE_CHECKPOINT
on-time resume (exit $ontime_status): latest $FINISHED_CHECKPOINT, $CHECKPOINTS_DONE file(s), $CALLS_ON_TIME model call(s), post-approval checkpoint: $FINISHED_APPROVED
late resume (exit ${late_status:-n/a}): latest ${AFTER_LATE:-(n/a)}, ${CALLS_LATE:-(n/a)} model call(s), 'Approving plan' printed ${LATE_RAN:-n/a} time(s)"
  EXTRA="$(printf '{"ontime_exit": %s, "model_calls_ontime": %s, "checkpoints_after_ontime": "%s", "late_exit": %s, "model_calls_late": %s, "late_approved_again": "%s"}' \
    "$ontime_status" "${CALLS_ON_TIME:-0}" "$CHECKPOINTS_DONE" "${late_status:-not-run}" "${CALLS_LATE:-0}" "${LATE_RAN:-not-run}")"
else
  # ── durable ─────────────────────────────────────────────────────────────────────────────────
  # A three-second window. Nothing answers it, so the gate is left to reach its own deadline; the process
  # that arrives after it is a different process from the one that recorded the deadline, which is the
  # only way to show that the deadline is durable rather than a timer in the creating process.
  if [ "$INJECT" = "1" ]; then
    window=3
  else
    window=600
  fi
  PAUSE_ON="" start_durable_bg "$RUN_DIR" start start --gate-seconds "$window" --deterministic
  track_pid "$APP_PID"
  wait_for_exit "$APP_PID" 180 || { log "late-approval: the start process did not finish"; exit 1; }
  TASK_AT_GATE="$(observed durable "$RUN_DIR" task-status)"
  DEADLINE="$(observed durable "$RUN_DIR" gate-deadline)"
  CALLS_AT_GATE="$(observed durable "$RUN_DIR" calls)"

  if [ "$INJECT" = "1" ]; then
    # Wait on the wall clock for the recorded deadline to pass. The wait is on the deadline the store
    # records, not on a fixed sleep: what is being tested is that the record is what governs.
    deadline_epoch="$("$PYTHON" - "$DEADLINE" <<'PY'
import datetime, sys
print(int(datetime.datetime.fromisoformat(sys.argv[1]).timestamp()) + 2)
PY
)"
    while [ "$(date +%s)" -lt "$deadline_epoch" ]; do sleep 0.2; done
    PAUSE_ON="" durable_verb "$RUN_DIR" resume --deterministic >"$RUN_DIR/expire.log" 2>&1
    expire_status=$?
    TASK_AFTER_DEADLINE="$(observed durable "$RUN_DIR" task-status)"
    GATE_VERDICT="$(observed durable "$RUN_DIR" gate-verdict)"
    CALLS_AFTER_DEADLINE="$(observed durable "$RUN_DIR" calls)"

    # The late approval. It has to be refused, and the refusal has to be the terminal one.
    late_status=0
    PAUSE_ON="" durable_verb "$RUN_DIR" approve --deterministic --as mallory --comment "approved too late" \
      >"$RUN_DIR/late.log" 2>&1 || late_status=$?
    LATE_NOTE="$(grep -m1 '^decision:' "$RUN_DIR/late.log" 2>/dev/null | sed 's/^decision: *//')"
    DECISION_AFTER_LATE="$(observed durable "$RUN_DIR" decision)"
    TASK_AFTER_LATE="$(observed durable "$RUN_DIR" task-status)"
    OUTCOME_AFTER_LATE="$(observed durable "$RUN_DIR" outcome-status)"
    ERROR_AFTER_LATE="$(observed durable "$RUN_DIR" outcome-error)"

    if [ "$DECISION_AFTER_LATE" = "(absent)" ] \
       && [ "$LATE_NOTE" = "refused: the workflow is Terminal, so this decision is late" ] \
       && [ "$TASK_AFTER_LATE" = "terminal" ] \
       && [ "$OUTCOME_AFTER_LATE" = "failed" ]; then
      OBSERVED="terminal state held"
      PHASE="the gate's recorded 3s deadline passed in a process that did not set it"
      DETAIL="The run was created with a 3-second window and its gate recorded deadline $DEADLINE. A process started after that deadline re-entered the run: the gate woke at once and recorded '$GATE_VERDICT', and the workflow concluded on it — task $TASK_AFTER_LATE, outcome $OUTCOME_AFTER_LATE, '$ERROR_AFTER_LATE'. The late approval was then refused — '$LATE_NOTE' — and the decision document is still absent. The terminal state was not overwritten, and the refusal names the terminal record rather than the document rule, which is the different guard a late approval meets."
    else
      OBSERVED="overwritten"
      PHASE="the gate's recorded deadline passed in a process that did not set it"
      DETAIL="The gate recorded deadline $DEADLINE. After it passed the run read: gate '$GATE_VERDICT', task $TASK_AFTER_DEADLINE, outcome '${OUTCOME_AFTER_LATE:-none}'. The late approval produced '$LATE_NOTE', the decision document reads '$DECISION_AFTER_LATE' and the task is $TASK_AFTER_LATE. Reported as it stands: the late approval was not refused by the terminal guard."
    fi
    EXTRA="$(printf '{"gate_window_seconds": %s, "model_calls_at_gate": %s, "model_calls_after_deadline": %s, "gate_verdict_after_deadline": "%s", "late_approval_note": "%s", "decision_after_late": "%s", "run_after_late": "%s", "outcome_after_late": "%s"}' \
      "$window" "${CALLS_AT_GATE:-0}" "${CALLS_AFTER_DEADLINE:-0}" "$GATE_VERDICT" "$LATE_NOTE" "$DECISION_AFTER_LATE" "$TASK_AFTER_LATE" "${OUTCOME_AFTER_LATE:-}")"
    EVIDENCE="run at the gate: $TASK_AT_GATE, $CALLS_AT_GATE call(s), gate deadline $DEADLINE
resume after the deadline (exit $expire_status): task $TASK_AFTER_DEADLINE, gate verdict '$GATE_VERDICT', $CALLS_AFTER_DEADLINE call(s)
workflow concluded: $OUTCOME_AFTER_LATE — $ERROR_AFTER_LATE
late approval (exit $late_status): '$LATE_NOTE'; decision '$DECISION_AFTER_LATE'; task $TASK_AFTER_LATE"
  else
    # Control: the approval lands inside a long window, so it is not late and does take effect.
    PAUSE_ON="" durable_verb "$RUN_DIR" approve --deterministic --as alice --comment "approved in time" \
      >"$RUN_DIR/ontime.log" 2>&1
    ontime_status=$?
    PAUSE_ON="" durable_verb "$RUN_DIR" resume --deterministic >"$RUN_DIR/ontime-resume.log" 2>&1
    ontime_resume_status=$?
    DECISION_ON_TIME="$(observed durable "$RUN_DIR" decision)"
    TASK_END="$(observed durable "$RUN_DIR" task-status)"
    VERDICT_END="$(observed durable "$RUN_DIR" verdict)"
    CALLS_END="$(observed durable "$RUN_DIR" calls)"

    OBSERVED="approved in time"
    PHASE="approved inside a ${window}s window (the control)"
    DETAIL="Control: the same flow with the approval delivered inside the window. The approval was recorded (exit $ontime_status, decision '$DECISION_ON_TIME'), the run was driven (exit $ontime_resume_status) and ended $TASK_END with verdict '${VERDICT_END:-(none)}' after $CALLS_END call(s). This is what the late approval is measured against: an approval that arrives on time is applied, so a refusal in the fault run is the lateness and not the approval."
    EXTRA="$(printf '{"gate_window_seconds": %s, "model_calls_at_gate": %s, "decision_ontime": "%s", "run_after_ontime": "%s", "verdict_ontime": "%s", "model_calls_end": %s}' \
      "$window" "${CALLS_AT_GATE:-0}" "$DECISION_ON_TIME" "${TASK_END:-unknown}" "${VERDICT_END:-}" "${CALLS_END:-0}")"
    EVIDENCE="run at the gate: $TASK_AT_GATE, $CALLS_AT_GATE call(s), gate deadline $DEADLINE
on-time approval (exit $ontime_status): decision '$DECISION_ON_TIME'
driven (exit $ontime_resume_status): $TASK_END, verdict '${VERDICT_END:-(none)}', $CALLS_END call(s)"
  fi
fi

record_result "$VARIANT" "$FAULT" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
exit 0
