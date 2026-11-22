#!/usr/bin/env bash
# Fault: kill-during-wait. A `kill -9` of a process that is waiting, confirmed gone.
#
#   ./faults/kill-during-wait.sh --variant baseline|durable [--fault-not-injected]
#
# "The wait" is the human's turn to answer. The two variants keep it in different places, and the fault
# says which process is killed and why that process is the one in the wait:
#
#   baseline  The gate is a request inside the workflow, and the workflow's own state is the only record.
#             The process is held inside the manager's first model call — it is waiting for an answer,
#             before any gate checkpoint exists — and killed there. Waiting for an answer inside the run
#             is the only wait the baseline has that a process can be inside of.
#   durable   The wait is a committed child task, so the run reaches its gate before any process has to
#             stay alive. The process that is *in* the wait is the host's `wait` verb, blocked on the
#             verdict. That is the PID the kill lands on.
#
# The two are reported as they are, not as the same test: the baseline has no durable wait to kill, which
# is the finding, not a defect of the harness.
#
# `--fault-not-injected` removes the kill: the held call is released (baseline) and the host's wait is
# allowed to return on its own budget (durable). Both must then reach the gate, so neither result is an
# artefact of the harness.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

parse_variant_args "${BASH_SOURCE[0]}" "$@"
FAULT="$(fault_name kill-during-wait)"
PORT="$(port_for "$VARIANT" "$FAULT")"
FAULT_COMMAND="./faults/kill-during-wait.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1
build_variants || exit 1
install_cleanup

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
mkdir -p "$RUN_DIR"

KILLED_PID=""
WHERE=""

if [ "$VARIANT" = "baseline" ]; then
  # The manager's first turn is the work the baseline does before it can reach the gate. Holding it and
  # killing the process is a kill during a wait, with no checkpoint on disk to continue from.
  fresh_endpoint "$RUN_DIR" "$PORT" 1 || exit 1
  start_baseline_bg "$RUN_DIR" run "run"
  pid="$APP_PID"
  track_pid "$pid"
  WHERE="the process working out the plan, inside its manager turn, before any gate checkpoint exists"

  if ! wait_for_file "$RUN_DIR/inflight" 120; then
    log "kill-during-wait: the endpoint never held the manager's turn"
    exit 1
  fi
  GATE_BEFORE="$(observed baseline "$RUN_DIR" gate-checkpoint)"

  if [ "$INJECT" = "1" ]; then
    KILLED_PID="$pid"
    kill9 "$pid" || exit 1
    CALLS_AT_KILL="$(count_calls "$RUN_DIR")"
    PHASE="the manager's first turn, held; process killed before any gate checkpoint"
  else
    touch "$RUN_DIR/release"
    if ! wait_for_exit "$pid" 240; then
      log "kill-during-wait-control: the released run did not finish"
      exit 1
    fi
    CALLS_AT_KILL="$(count_calls "$RUN_DIR")"
    PHASE="the manager's first turn, released (the control)"
  fi

  # ── observe ─────────────────────────────────────────────────────────────────────────────────
  # The observation is the baseline's own `resume`, in a fresh process, against the directory the killed
  # process left. That is the only continuation entry point the baseline has, and whether it can be
  # entered at all is the question: `resume` reads the `gate.json` the port writes when the gate is
  # reached, so an absent gate file is the baseline saying there is nothing to continue from.
  fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
  after_status=0
  baseline_mode "$RUN_DIR" resume >"$RUN_DIR/after.log" 2>&1 || after_status=$?
  GATE_AFTER="$(observed baseline "$RUN_DIR" gate-checkpoint)"
  CHECKPOINTS="$(observed baseline "$RUN_DIR" checkpoint-count)"
  CALLS_FINAL="$(count_calls "$RUN_DIR")"

  if [ "$INJECT" = "1" ]; then
    if [ "$after_status" -ne 0 ] && [ "$CHECKPOINTS" = "0" ]; then
      OBSERVED="lost"
      DETAIL="The kill landed on pid $KILLED_PID ($WHERE) with $CALLS_AT_KILL model call(s) served to that process and no gate checkpoint on disk. Nothing continues it: the baseline's own continuation entry point — a fresh process running 'resume' against the directory the killed process left — exited $after_status and wrote nothing, because there is no gate record ($CHECKPOINTS checkpoint file(s) on disk). The wait existed only inside the killed process, so killing it cost the whole pre-gate run."
    else
      OBSERVED="survived"
      DETAIL="The kill landed on pid $KILLED_PID, and a fresh continuation still found something: it exited $after_status with gate checkpoint $GATE_AFTER and $CHECKPOINTS checkpoint file(s). That contradicts what this fault expects and is reported rather than smoothed over."
    fi
  else
    OBSERVED="reached-the-gate"
    DETAIL="Control: the same steps with the held turn released instead of the process killed. The process exited $after_status, wrote gate checkpoint $GATE_AFTER, and the run is at its gate ($CHECKPOINTS checkpoint file(s), $CALLS_FINAL model call(s) in that process). The killed run is measured against this: releasing instead of killing is the only difference, and the killed one left no gate record at all."
  fi

  EVIDENCE="killed pid ${KILLED_PID:-none} (kill -9, confirmed gone) at: $WHERE
gate checkpoint before: $GATE_BEFORE
after (fresh 'resume' process, exit $after_status): gate checkpoint $GATE_AFTER, $CHECKPOINTS checkpoint file(s), $CALLS_FINAL model call(s)
$(head -3 "$RUN_DIR/after.log" 2>/dev/null)"
  EXTRA="$(printf '{"model_calls_held_process": %s, "resume_exit": %s, "gate_checkpoint_after": "%s", "checkpoints_after": "%s"}' \
    "${CALLS_AT_KILL:-0}" "$after_status" "$GATE_AFTER" "$CHECKPOINTS")"
else
  # ── durable ─────────────────────────────────────────────────────────────────────────────────
  # Process 1: to the gate. Then a host process waits on the verdict; that is the process the kill lands
  # on, and the run must not notice.
  PAUSE_ON="" start_durable_bg "$RUN_DIR" start start --gate-seconds 600 --deterministic
  pid="$APP_PID"
  track_pid "$pid"
  wait_for_exit "$pid" 180 || { log "kill-during-wait: the start process did not finish"; exit 1; }
  TASK_BEFORE="$(observed durable "$RUN_DIR" task-status)"
  GATE_PHASE_BEFORE="$(observed durable "$RUN_DIR" gate-phase)"
  CALLS_BEFORE="$(observed durable "$RUN_DIR" calls)"
  if [ "$TASK_BEFORE" != "waiting" ]; then
    log "kill-during-wait: the run is '$TASK_BEFORE' after start, not waiting at the gate"
    exit 1
  fi
  durable_at_gate "$RUN_DIR" 60 || exit 1
  GATE_PHASE_BEFORE="$(observed durable "$RUN_DIR" gate-phase)"

  PAUSE_ON="" start_durable_bg "$RUN_DIR" wait wait --timeout 90
  wait_pid="$APP_PID"
  track_pid "$wait_pid"
  WHERE="the host process blocked in the wait for the gate's verdict"
  sleep 1

  if [ "$INJECT" = "1" ]; then
    if ! kill -0 "$wait_pid" 2>/dev/null; then
      log "kill-during-wait: the waiting process exited before the kill could land; the fault was not injected"
      exit 1
    fi
    KILLED_PID="$wait_pid"
    kill9 "$wait_pid" || exit 1
    PHASE="blocked in the wait for the gate's verdict; process killed"
  else
    if ! wait_for_exit "$wait_pid" 180; then
      log "kill-during-wait-control: the waiting process neither returned nor timed out"
      exit 1
    fi
    PHASE="blocked in the wait for the gate's verdict; not killed (the control)"
  fi

  # ── observe ─────────────────────────────────────────────────────────────────────────────────
  # A fresh process re-enters the run and must still find the wait, then be able to apply a decision.
  PAUSE_ON="" durable_verb "$RUN_DIR" resume --deterministic >"$RUN_DIR/after.log" 2>&1
  after_status=$?
  TASK_AFTER="$(observed durable "$RUN_DIR" task-status)"
  GATE_PHASE_AFTER="$(observed durable "$RUN_DIR" gate-phase)"
  CALLS_AFTER="$(observed durable "$RUN_DIR" calls)"

  decide_status=0
  PAUSE_ON="" durable_verb "$RUN_DIR" approve --deterministic --as alice --comment "approved after the wait was killed" \
    >"$RUN_DIR/decide.log" 2>&1 || decide_status=$?
  DECISION="$(observed durable "$RUN_DIR" decision)"

  PAUSE_ON="" durable_verb "$RUN_DIR" resume --deterministic >"$RUN_DIR/finish.log" 2>&1
  finish_status=$?
  TASK_END="$(observed durable "$RUN_DIR" task-status)"
  VERDICT="$(observed durable "$RUN_DIR" verdict)"
  CALLS_END="$(observed durable "$RUN_DIR" calls)"

  if [ "$TASK_AFTER" = "waiting" ] && [ "$GATE_PHASE_AFTER" = "wait" ] && [ "$after_status" -eq 0 ]; then
    if [ "$INJECT" = "1" ]; then
      OBSERVED="wait survived"
      PHASE="blocked in the wait for the gate's verdict; process killed, wait still on record"
      DETAIL="The kill landed on pid $KILLED_PID ($WHERE) with the store reading: task $TASK_BEFORE, gate phase $GATE_PHASE_BEFORE. A fresh process re-entered the run and returned at the gate again — task $TASK_AFTER, gate phase $GATE_PHASE_AFTER, $CALLS_AFTER call(s), unchanged from $CALLS_BEFORE — so the wait survived the death of the only process that was in it. It then accepted a decision ($DECISION) and finished the run: $TASK_END with verdict $VERDICT, $CALLS_END call(s). The wait is a checkpoint the gate committed, not a call the killed process held."
    else
      OBSERVED="no kill, run completed"
      PHASE="blocked in the wait for the gate's verdict; not killed (the control)"
      DETAIL="Control: the same steps with no kill. The host's wait returned on its own budget, a fresh process found the run at its gate ($TASK_AFTER, phase $GATE_PHASE_AFTER), and accepting a decision ($DECISION) finished it: $TASK_END, verdict $VERDICT, $CALLS_END call(s). The fault run is the same flow with one process killed, and it ends in the same place — which is the whole finding: the kill cost a process and nothing else. Nothing here is lost and nothing here is a win."
    fi
    OUTCOME_STATUS="$TASK_END"
  else
    OBSERVED="lost"
    PHASE="the wait was not readable as a wait after the resume"
    DETAIL="Before the kill the run was at its gate ($TASK_BEFORE, gate phase $GATE_PHASE_BEFORE, $CALLS_BEFORE call(s)). A fresh resume exited $after_status and left the run as: task $TASK_AFTER, gate phase $GATE_PHASE_AFTER. The run then ended $TASK_END with verdict ${VERDICT:-(none)}."
    OUTCOME_STATUS="${TASK_END:-unknown}"
  fi

  EVIDENCE="killed pid ${KILLED_PID:-none} (kill -9, confirmed gone) at: $WHERE
before: task $TASK_BEFORE, gate phase $GATE_PHASE_BEFORE, $CALLS_BEFORE call(s)
after resume (exit $after_status): task $TASK_AFTER, gate phase $GATE_PHASE_AFTER, $CALLS_AFTER call(s)
decision (exit $decide_status): $DECISION
after finishing (exit $finish_status): task $TASK_END, verdict ${VERDICT:-(none)}, $CALLS_END call(s)
$(tail -2 "$RUN_DIR/after.log" 2>/dev/null)"
  EXTRA="$(printf '{"model_calls_before_kill": %s, "model_calls_after_resume": %s, "model_calls_after_finish": %s, "run_after_finish": "%s"}' \
    "${CALLS_BEFORE:-0}" "${CALLS_AFTER:-0}" "${CALLS_END:-0}" "$OUTCOME_STATUS")"
fi

record_result "$VARIANT" "$FAULT" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
exit 0
