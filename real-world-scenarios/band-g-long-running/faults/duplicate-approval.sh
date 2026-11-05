#!/usr/bin/env bash
# Fault: duplicate-approval. Deliver the approval twice, and see whether the decision is applied once.
#
#   ./faults/duplicate-approval.sh --variant baseline|durable [--fault-not-injected]
#
# The second delivery is the same approval, with a different reviewer's name so the result can say which
# one took effect. What is compared is what the run did with it:
#
#   baseline  The double delivery is two `resume` processes against one gate checkpoint. That is the only
#             shape the baseline has: the approval is the resume. Both are run, and the second is observed
#             rather than assumed — it either finds the run finished or starts again from the gate.
#   durable   The double delivery is two `approve` verbs, each its own process, against a live gate. The
#             second must be refused, by the decision document's first-writer-wins rule rather than by the
#             terminal guard. Only then is the run driven, so the refusal is the document's doing.
#
# `--fault-not-injected` delivers the approval once. That is what a "doubled" outcome is measured
# against: the same flow, one delivery.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

parse_variant_args "${BASH_SOURCE[0]}" "$@"
FAULT="$(fault_name duplicate-approval)"
PORT="$(port_for "$VARIANT" "$FAULT")"
FAULT_COMMAND="./faults/duplicate-approval.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1
build_variants || exit 1
install_cleanup

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
mkdir -p "$RUN_DIR"

if [ "$VARIANT" = "baseline" ]; then
  fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
  start_baseline_bg "$RUN_DIR" run "run"
  track_pid "$APP_PID"
  wait_for_exit "$APP_PID" 180 || { log "duplicate-approval: process 1 never reached the gate"; exit 1; }
  CALLS_GATE="$(count_calls "$RUN_DIR")"
  GATE_CHECKPOINT="$(observed baseline "$RUN_DIR" gate-checkpoint)"
  assert_observed "the baseline wrote no gate checkpoint" "$GATE_CHECKPOINT" || exit 1

  # First delivery.
  fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
  first_status=0
  baseline_mode "$RUN_DIR" resume >"$RUN_DIR/first.log" 2>&1 || first_status=$?
  CALLS_FIRST="$(count_calls "$RUN_DIR")"
  FIRST_CHECKPOINT="$(observed baseline "$RUN_DIR" latest-checkpoint)"
  CHECKPOINTS_AFTER_FIRST="$(observed baseline "$RUN_DIR" checkpoint-count)"
  APPROVED_AFTER_FIRST="$(observed baseline "$RUN_DIR" approved-in-store)"

  if [ "$INJECT" = "1" ]; then
    # Second delivery: the same approval, in its own process, against the same checkpoint directory.
    fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
    second_status=0
    baseline_mode "$RUN_DIR" resume >"$RUN_DIR/second.log" 2>&1 || second_status=$?
    CALLS_SECOND="$(count_calls "$RUN_DIR")"
    SECOND_CHECKPOINT="$(observed baseline "$RUN_DIR" latest-checkpoint)"
    CHECKPOINTS_AFTER_SECOND="$(observed baseline "$RUN_DIR" checkpoint-count)"
    SECOND_RAN="$(grep -c 'Approving plan and continuing to completion' "$RUN_DIR/second.log" 2>/dev/null)"
    SECOND_RAN="${SECOND_RAN:-0}"

    # The claim: a second delivery of one approval does not apply a second time. The measure is that the
    # second process either did not approve at all, or approved and produced no further work — and the
    # call count says which.
    if [ "$SECOND_RAN" = "0" ]; then
      OBSERVED="applied once"
      PHASE="two resume processes against one gate checkpoint"
      DETAIL="The first resume approved the plan and exited $first_status, writing checkpoint $FIRST_CHECKPOINT ($CHECKPOINTS_AFTER_FIRST checkpoint file(s), $CALLS_FIRST model call(s)). The second resume — the same approval, a different process — found no plan-review request to answer: it exited $second_status without printing 'Approving plan', issued $CALLS_SECOND model call(s) and wrote no checkpoint ($CHECKPOINTS_AFTER_SECOND file(s), latest still $SECOND_CHECKPOINT). The baseline has no decision record to deduplicate against, so the second delivery is not refused — it is simply not a request the resumed checkpoint still holds."
    else
      OBSERVED="doubled"
      PHASE="two resume processes against one gate checkpoint"
      DETAIL="The first resume approved and exited $first_status ($CALLS_FIRST model call(s)). The second resume approved the re-emitted request again — it printed 'Approving plan' — and exited $second_status after $CALLS_SECOND model call(s), leaving the latest checkpoint at $SECOND_CHECKPOINT ($CHECKPOINTS_AFTER_SECOND file(s)). The approval applied twice."
    fi
  else
    OBSERVED="applied once"
    PHASE="one resume process against the gate checkpoint (the control)"
    DETAIL="Control: the approval delivered once. The resume exited $first_status, approved the plan, and wrote checkpoint $FIRST_CHECKPOINT ($CHECKPOINTS_AFTER_FIRST checkpoint file(s), $CALLS_FIRST model call(s)). This is what the double delivery is measured against: one approval, one run."
  fi

  EVIDENCE="gate checkpoint: $GATE_CHECKPOINT; calls before the first delivery: $CALLS_GATE
first delivery (exit $first_status): checkpoint $FIRST_CHECKPOINT, $CHECKPOINTS_AFTER_FIRST file(s), $CALLS_FIRST model call(s), approved checkpoint present: $APPROVED_AFTER_FIRST
second delivery: ${SECOND_CHECKPOINT:-(not run)}"
  if [ "$INJECT" = "1" ]; then
    EVIDENCE="$EVIDENCE
second delivery (exit ${second_status:-n/a}): checkpoint ${SECOND_CHECKPOINT:-(n/a)}, ${CHECKPOINTS_AFTER_SECOND:-(n/a)} file(s), ${CALLS_SECOND:-(n/a)} model call(s), 'Approving plan' printed ${SECOND_RAN:-n/a} time(s)"
  fi
  EXTRA="$(printf '{"model_calls_at_gate": %s, "checkpoints_after_first": "%s", "model_calls_first": %s, "second_delivery_ran": "%s", "model_calls_second": %s, "checkpoints_after_second": "%s"}' \
    "${CALLS_GATE:-0}" "$CHECKPOINTS_AFTER_FIRST" "${CALLS_FIRST:-0}" "${SECOND_RAN:-not-run}" "${CALLS_SECOND:-0}" "${CHECKPOINTS_AFTER_SECOND:-not-run}")"
else
  # ── durable ─────────────────────────────────────────────────────────────────────────────────
  PAUSE_ON="" start_durable_bg "$RUN_DIR" start start --gate-seconds 600 --deterministic
  track_pid "$APP_PID"
  wait_for_exit "$APP_PID" 180 || { log "duplicate-approval: the start process did not finish"; exit 1; }
  durable_at_gate "$RUN_DIR" 60 || exit 1
  TASK_BEFORE="$(observed durable "$RUN_DIR" task-status)"
  GATE_PHASE="$(observed durable "$RUN_DIR" gate-phase)"
  CALLS_GATE="$(observed durable "$RUN_DIR" calls)"

  # First delivery. Recorded only; the run is not driven yet, so the decision is what is being tested.
  first_status=0
  PAUSE_ON="" durable_verb "$RUN_DIR" approve --deterministic --as alice --comment "first approval" \
    >"$RUN_DIR/first.log" 2>&1 || first_status=$?
  DECISION_AFTER_FIRST="$(observed durable "$RUN_DIR" decision)"

  if [ "$INJECT" = "1" ]; then
    # Second delivery, while the run is still live at its gate: this is the half that proves a duplicate
    # cannot overwrite a decision, as opposed to being refused for being late.
    second_status=0
    PAUSE_ON="" durable_verb "$RUN_DIR" approve --deterministic --as mallory --comment "second approval" \
      >"$RUN_DIR/second.log" 2>&1 || second_status=$?
    DECISION_AFTER_SECOND="$(observed durable "$RUN_DIR" decision)"
    TASK_AFTER_SECOND="$(observed durable "$RUN_DIR" task-status)"
    SECOND_NOTE="$(grep -m1 '^decision:' "$RUN_DIR/second.log" 2>/dev/null | sed 's/^decision: *//')"
    SECOND_TERMINAL="$(grep -c 'the workflow is Terminal' "$RUN_DIR/second.log" 2>/dev/null)"
    SECOND_TERMINAL="${SECOND_TERMINAL:-0}"

    # The claim has two halves and both are needed: the second write did not reach the document, and its
    # refusal was the document's rule rather than the terminal guard.
    if [ "$DECISION_AFTER_SECOND" = "alice approved" ] && [ "$SECOND_TERMINAL" = "0" ]; then
      OBSERVED="second delivery refused"
      PHASE="two approve processes against a live gate"
      DETAIL="alice's approval was recorded (exit $first_status). mallory's approval of the same live gate was refused — '$SECOND_NOTE' — and the decision on file was still '$DECISION_AFTER_SECOND'. The refusal did not come from the terminal guard (the run was still '$TASK_AFTER_SECOND'), so it came from the decision document's first-writer-wins rule: the second delivery found a value already on file and returned it."
    else
      OBSERVED="doubled"
      PHASE="two approve processes against a live gate"
      DETAIL="The decision on file after the second approval is '$DECISION_AFTER_SECOND' (alice's was '$DECISION_AFTER_FIRST'), and the second process's terminal-guard hits: $SECOND_TERMINAL. The duplicate was not refused by the document rule. Reported as it stands."
    fi

    # Then drive, so the record also shows the run finishing on the first decision.
    finish_status=0
    PAUSE_ON="" durable_verb "$RUN_DIR" resume --deterministic >"$RUN_DIR/finish.log" 2>&1 || finish_status=$?
    TASK_END="$(observed durable "$RUN_DIR" task-status)"
    VERDICT="$(observed durable "$RUN_DIR" verdict)"
    DECISION_END="$(observed durable "$RUN_DIR" decision)"
    CALLS_END="$(observed durable "$RUN_DIR" calls)"
  else
    OBSERVED="one delivery, nothing to refuse"
    PHASE="one approve process against a live gate (the control)"
    DETAIL="Control: the approval delivered once. alice's approval was recorded (exit $first_status), the decision on file is '$DECISION_AFTER_FIRST', and the gate is still waiting for the run to be driven. This is what the double delivery is measured against."
    finish_status=0
    PAUSE_ON="" durable_verb "$RUN_DIR" resume --deterministic >"$RUN_DIR/finish.log" 2>&1 || finish_status=$?
    TASK_END="$(observed durable "$RUN_DIR" task-status)"
    VERDICT="$(observed durable "$RUN_DIR" verdict)"
    DECISION_END="$(observed durable "$RUN_DIR" decision)"
    CALLS_END="$(observed durable "$RUN_DIR" calls)"
    SECOND_NOTE="(not delivered)"
    SECOND_TERMINAL=0
    DECISION_AFTER_SECOND="(not delivered)"
    TASK_AFTER_SECOND="$TASK_BEFORE"
  fi

  EVIDENCE="run before the deliveries: $TASK_BEFORE, gate phase $GATE_PHASE, $CALLS_GATE call(s)
first delivery (exit $first_status): decision on file '$DECISION_AFTER_FIRST'
second delivery (exit ${second_status:-n/a}): note '$SECOND_NOTE', decision on file '$DECISION_AFTER_SECOND', run still '$TASK_AFTER_SECOND', terminal-guard hits $SECOND_TERMINAL
driven (exit $finish_status): $TASK_END, verdict ${VERDICT:-(none)}, decision '$DECISION_END', $CALLS_END call(s)"
  EXTRA="$(printf '{"model_calls_at_gate": %s, "decision_after_first": "%s", "decision_after_second": "%s", "second_refused_by_terminal_guard": "%s", "run_after_finish": "%s", "verdict": "%s", "model_calls_end": %s}' \
    "${CALLS_GATE:-0}" "$DECISION_AFTER_FIRST" "$DECISION_AFTER_SECOND" "$SECOND_TERMINAL" "${TASK_END:-unknown}" "${VERDICT:-}" "${CALLS_END:-0}")"
fi

record_result "$VARIANT" "$FAULT" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
exit 0
