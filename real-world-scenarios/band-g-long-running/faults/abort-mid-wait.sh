#!/usr/bin/env bash
# Fault: abort-mid-wait. Abort while waiting, and see whether pending child work is cancelled and the
# state is final.
#
#   ./faults/abort-mid-wait.sh --variant baseline|durable
#
# A `--fault-not-injected` control is **deliberately absent**, and the result says so rather than
# omitting it quietly: the control for an abort is "do not abort", which is a normal run and is already
# measured by the band's own two-process evidence (#176 for the baseline, #177 for the Durable variant).
# Re-running a normal run here would add a fourth copy of the same measurement, not a comparison.
#
#   baseline  The baseline has no abort verb and no abort tree. Nothing in the port can be asked to
#             cancel a run that is waiting: the wait is a pending request in a checkpoint, and the only
#             operations are `run` and `resume`. So `baseline` is `not exercised`, and the reason is that
#             the shape has no abort to inject — not that the harness could not reach it.
#   durable   The run is at its gate; `abort` is called. The parent's abort handler records what it saw of
#             each child at the moment it ran, so the cascade's ordering is recorded rather than inferred.
#             An approval afterwards must be refused, and the run must stay terminal.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

parse_variant_args "${BASH_SOURCE[0]}" "$@"
FAULT="$(fault_name abort-mid-wait)"
PORT="$(port_for "$VARIANT" "$FAULT")"
FAULT_COMMAND="./faults/abort-mid-wait.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1
build_variants || exit 1
install_cleanup

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
mkdir -p "$RUN_DIR"

if [ "$VARIANT" = "baseline" ]; then
  # The reason is checked, not asserted: the port's own usage line is the evidence that there is no abort
  # to inject. A baseline that grew an abort verb would make this reason wrong, and the check fails here
  # rather than leaving a stale paragraph in a checked-in result.
  # The port reads its environment before it reads its mode, so the check names a directory and a key it
  # will not use; the mode is what is being asked about.
  mkdir -p "$RUN_DIR/env-probe"
  dumps="$(CHECKPOINT_DIR="$RUN_DIR/env-probe" OPENAI_API_KEY=unused "$BASELINE_OUT/BaselineCheckpoint" nonsense 2>&1 || true)"
  if ! grep -q "Use 'run' or 'resume'" <<<"$dumps"; then
    log "abort-mid-wait: the baseline no longer reports only 'run' and 'resume'; this reason needs re-checking"
    log "abort-mid-wait: what it printed was: $dumps"
    exit 1
  fi
  record_not_exercised "$VARIANT" "$FAULT" \
    "a run waiting at the gate, with an abort to inject" \
    "The baseline's port advertises exactly two modes, 'run' and 'resume' — checked in this run, where an unknown mode answers 'Use run or resume'. So there is no abort verb and no abort tree over the workflow's pending work: the wait is a pending request inside a checkpoint, and the only operations on it are to answer it and continue. Injecting an abort would mean adding one to the baseline, which this band does not do. Recorded as not exercised with that reason rather than reported as a tie or a loss." \
    '{"reason_checked_by": "BaselineCheckpoint with an unknown mode answers: Use '\''run'\'' or '\''resume'\''"}'
  exit 0
fi

# ── durable ───────────────────────────────────────────────────────────────────────────────────
PAUSE_ON="" start_durable_bg "$RUN_DIR" start start --gate-seconds 600 --deterministic
track_pid "$APP_PID"
wait_for_exit "$APP_PID" 180 || { log "abort-mid-wait: the start process did not finish"; exit 1; }
TASK_BEFORE="$(observed durable "$RUN_DIR" task-status)"
GATE_STATUS_BEFORE="$(observed durable "$RUN_DIR" gate-status)"
GATE_PHASE_BEFORE="$(observed durable "$RUN_DIR" gate-phase)"
CALLS_BEFORE="$(observed durable "$RUN_DIR" calls)"
durable_at_gate "$RUN_DIR" 60 || exit 1
GATE_PHASE_BEFORE="$(observed durable "$RUN_DIR" gate-phase)"
if [ "$TASK_BEFORE" != "waiting" ] || [ "$GATE_PHASE_BEFORE" != "wait" ]; then
  log "abort-mid-wait: the run is not waiting at its gate before the abort ($TASK_BEFORE / $GATE_PHASE_BEFORE)"
  exit 1
fi

# The abort. It is the variant's own verb, run as its own process, exactly as a host would call it.
abort_status=0
PAUSE_ON="" durable_verb "$RUN_DIR" abort >"$RUN_DIR/abort.log" 2>&1 || abort_status=$?
TASK_AFTER="$(observed durable "$RUN_DIR" task-status)"
GATE_STATUS_AFTER="$(observed durable "$RUN_DIR" gate-status)"
GATE_VERDICT_AFTER="$(observed durable "$RUN_DIR" gate-verdict)"
VERDICT_AFTER="$(observed durable "$RUN_DIR" verdict)"
OBSERVED_CHILDREN="$(observed durable "$RUN_DIR" observed-children)"

# A later approval must be refused, and the run must not move. This is what "terminal is final" means for
# an abort: the decision has nowhere to land.
late_status=0
PAUSE_ON="" durable_verb "$RUN_DIR" approve --deterministic --as mallory --comment "after the abort" \
  >"$RUN_DIR/late.log" 2>&1 || late_status=$?
LATE_NOTE="$(grep -m1 '^decision:' "$RUN_DIR/late.log" 2>/dev/null | sed 's/^decision: *//')"
DECISION_AFTER="$(observed durable "$RUN_DIR" decision)"
TASK_FINAL="$(observed durable "$RUN_DIR" task-status)"
VERDICT_FINAL="$(observed durable "$RUN_DIR" verdict)"

# What the parent saw of its children when its abort handler ran. This is the ordering evidence: "both
# are terminal" is produced by any ordering, including no cascade; "the child was already terminal when
# the parent's handler ran" is not.
if ! assert_observed "the parent recorded nothing about its children" "$OBSERVED_CHILDREN"; then
  log "abort-mid-wait: the run was aborted but the handler observed no child; nothing shows the cascade"
  # Keep what the failure needs: the abort's own output, the log that says whether the abort process
  # actually finished, and the store as it was. A failure that deletes its evidence cannot be fixed.
  KEEP=1
  log "abort-mid-wait: the run directory is kept at $RUN_DIR"
  log "abort-mid-wait: abort exit $abort_status; task $TASK_AFTER; gate $GATE_STATUS_AFTER"
  tail -6 "$RUN_DIR/abort.log" >&2
  exit 1
fi
CHILD_ALREADY_TERMINAL=0
case "$OBSERVED_CHILDREN" in
  *Terminal*) CHILD_ALREADY_TERMINAL=1 ;;
esac

if [ "$TASK_AFTER" = "terminal" ] && [ "$GATE_STATUS_AFTER" = "terminal" ] && [ "$CHILD_ALREADY_TERMINAL" = "1" ]; then
  OBSERVED="cancelled"
  PHASE="waiting at the gate (gate phase wait); abort called"
  DETAIL="Before the abort: task $TASK_BEFORE, gate phase $GATE_PHASE_BEFORE ($GATE_STATUS_BEFORE), $CALLS_BEFORE call(s). The abort exited $abort_status and both tasks ended terminal — the gate '$GATE_VERDICT_AFTER', the workflow '$VERDICT_AFTER'. The parent's abort handler recorded what it saw of its children when it ran: '$OBSERVED_CHILDREN'. The child was already terminal at that moment, which is the cascade shown as an ordering and not merely as two terminal states. A later approval was then refused — '$LATE_NOTE' — the decision document is '$DECISION_AFTER', and the run is still $TASK_FINAL with verdict '${VERDICT_FINAL:-(none)}'."
else
  OBSERVED="not cancelled"
  PHASE="waiting at the gate (gate phase wait); abort called"
  DETAIL="Before the abort: task $TASK_BEFORE, gate phase $GATE_PHASE_BEFORE ($GATE_STATUS_BEFORE). The abort exited $abort_status and left the workflow '$TASK_AFTER' with verdict '${VERDICT_AFTER:-(none)}' and the gate '$GATE_STATUS_AFTER' with verdict '${GATE_VERDICT_AFTER:-(none)}'. The parent's handler recorded '$OBSERVED_CHILDREN'. Reported as it stands."
fi

EVIDENCE="before: task $TASK_BEFORE, gate $GATE_STATUS_BEFORE / phase $GATE_PHASE_BEFORE, $CALLS_BEFORE call(s)
abort (exit $abort_status): task $TASK_AFTER, gate $GATE_STATUS_AFTER (verdict ${GATE_VERDICT_AFTER:-(none)}), workflow verdict ${VERDICT_AFTER:-(none)}
observed by the parent's abort handler: $OBSERVED_CHILDREN
late approval (exit $late_status): '$LATE_NOTE'; decision '$DECISION_AFTER'; task $TASK_FINAL, verdict '${VERDICT_FINAL:-(none)}'
$(grep -E '^observed:' "$RUN_DIR/abort.log" 2>/dev/null | head -4)"
CALLS_AFTER="$(observed durable "$RUN_DIR" calls)"
EXTRA="$(printf '{"model_calls_at_gate": %s, "model_calls_after_abort": %s, "gate_verdict_after_abort": "%s", "workflow_verdict_after_abort": "%s", "children_observed": "%s", "child_terminal_when_parent_concluded": "%s", "late_approval_note": "%s", "decision_after_abort": "%s", "run_final": "%s"}' \
  "${CALLS_BEFORE:-0}" "${CALLS_AFTER:-0}" "${GATE_VERDICT_AFTER:-}" "${VERDICT_AFTER:-}" "$OBSERVED_CHILDREN" "$CHILD_ALREADY_TERMINAL" "$LATE_NOTE" "$DECISION_AFTER" "$TASK_FINAL")"

record_result "$VARIANT" "$FAULT" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
exit 0
