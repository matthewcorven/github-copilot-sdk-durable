#!/usr/bin/env bash
# `restart-after-checkpoint` — stop the process once the handoff has committed, then restart it.
#
#   ./restart-after-checkpoint.sh --variant durable
#   ./restart-after-checkpoint.sh --variant baseline --fault-not-injected
#
# The fault: stop the process at the commit where control has moved to the specialist and nothing for
# that turn has been produced, with SIGTERM, confirmed gone. A clean stop, deliberately: what this
# fault asks is whether the state the checkpoint describes is enough to restart from, not whether a
# kill tears a write.
#
# The observation: which agent the restarted process gave control to, and whether it replayed work the
# checkpoint already recorded. For the durable variant `verify` answers the second half.
#
# The control: the same run stopped at the same turn one commit earlier, where the checkpoint names
# the coordinator and no delegation has moved control. The pair is what makes "exact" a claim: the
# difference between the two rows is the commit, not the program.
#
# How the stop point is found differs by variant, and only because the variants differ in what they
# expose:
#
#   durable    its own seam (`--kill-after-routing` / `--kill-after-delegating`, F1's addition for
#              exactly this) announces the pid once the named commit has returned. The harness sends
#              SIGTERM to that pid. The seam is part of the variant; the stop is still the harness's.
#   baseline   no seam exists, so the harness polls the framework checkpoint for the holder the stop
#              is about and sends SIGTERM once it is there.

set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

begin_fault restart-after-checkpoint "$@"
RUNDIR="$(new_fault_dir)"; track_dir "$VARIANT" "$RUNDIR"

if [ "$NOT_INJECTED" = "1" ]; then
  STOP_LABEL="turn 1 under the coordinator, no delegation committed"
  WANT_HOLDER=triage_agent
  SEAM="routing"
else
  STOP_LABEL="turn 1 delegated to refund_agent and nothing produced"
  WANT_HOLDER=refund_agent
  SEAM="delegating"
fi

if [ "$VARIANT" = "durable" ]; then
  start_run "$VARIANT" "$RUNDIR" "--kill-after-$SEAM" 1
  if ! seam_pid="$(await_seam_pid "$RUNDIR" 420)"; then
    record_not_exercised "$VARIANT" "$RESULT_NAME" "the stop point was not reached" \
      "the run never announced the '$STOP_LABEL' seam: $(tail_log "$RUNDIR/.starter.log" 8 | tr '\n' ' ')"
    exit 0
  fi
  echo "$seam_pid" > "$RUNDIR/pid"
else
  start_run "$VARIANT" "$RUNDIR"
  if ! await_app_pid "$VARIANT" "$RUNDIR" 180 >/dev/null; then
    record_not_exercised "$VARIANT" "$RESULT_NAME" "the variant did not start" \
      "the application never opened its store: $(tail_log "$RUNDIR/.starter.log" 4 | tr '\n' ' ')"
    exit 0
  fi
  # The baseline announces nothing, so the stop point is found by polling: one turn must finish and
  # the newest checkpoint must name the specialist the stop is about. Under load that can take a while
  # (a resumed model call plus checkpoint persistence), so the wait is generous — a timeout here
  # would record `not exercised` for a fault that is merely slow, which would be a false absence.
  # The baseline announces nothing, so the stop point is found by polling — but a poll read from a
  # running process is a race: the run can consume the whole turn between the read and the signal,
  # which is what made this row record `not exercised` for a fault that was merely too quick.
  #
  # The race is closed by stopping the process first and reading while it cannot move. If the state is
  # right the process stays held and is stopped with SIGTERM next; if not, it is resumed and polled
  # again. What the row reports is the state read *inside* the process that was stopped, so it is the
  # state the stop actually landed on rather than the state a poll happened to see.
  reached=0
  for _ in $(seq 1 3000); do
    pid_now="$(cat "$RUNDIR/pid" 2>/dev/null || true)"
    if [ -z "$pid_now" ]; then
      pid_now="$(app_pid "$VARIANT" "$RUNDIR")"
      [ -n "$pid_now" ] && echo "$pid_now" > "$RUNDIR/pid"
    fi
    if [ -z "$pid_now" ]; then
      if [ -f "$RUNDIR/wrapper.pid" ] && ! kill -0 "$(cat "$RUNDIR/wrapper.pid")" 2>/dev/null; then break; fi
      sleep 0.05
      continue
    fi
    if ! kill -STOP "$pid_now" 2>/dev/null; then
      if [ -f "$RUNDIR/wrapper.pid" ] && ! kill -0 "$(cat "$RUNDIR/wrapper.pid")" 2>/dev/null; then break; fi
      sleep 0.05
      continue
    fi
    # Confirm the stop took effect before reading; a stopped process cannot advance underneath it.
    stopped_state=""
    for _ in $(seq 1 200); do
      stopped_state="$(ps -o state= -p "$pid_now" 2>/dev/null | tr -d ' ')"
      case "$stopped_state" in T*|t*|P*) break ;; esac
      sleep 0.01
    done
    HELD_HOLDER="$(holder_now "$VARIANT" "$RUNDIR")"
    HELD_TURNS="$(baseline_log_count "$RUNDIR" turn_end)"
    case "$stopped_state" in
      T*|t*|P*)
        if [ "$HELD_HOLDER" = "$WANT_HOLDER" ] && [ "${HELD_TURNS:-0}" -eq 1 ]; then
          reached=1
          break
        fi
        kill -CONT "$pid_now" 2>/dev/null || true
        ;;
      *)
        # It exited while being stopped; nothing was held.
        if ! kill -0 "$pid_now" 2>/dev/null; then
          if [ -f "$RUNDIR/wrapper.pid" ] && ! kill -0 "$(cat "$RUNDIR/wrapper.pid")" 2>/dev/null; then break; fi
          continue
        fi
        kill -CONT "$pid_now" 2>/dev/null || true
        ;;
    esac
    sleep 0.05
  done
  if [ "$reached" != "1" ]; then
    record_not_exercised "$VARIANT" "$RESULT_NAME" "the stop point was not reached" \
      "the run never reached '$STOP_LABEL' while held (last holder: ${HELD_HOLDER:-none}, completed turns: ${HELD_TURNS:-0}): $(tail_log "$RUNDIR/.starter.log" 6 | tr '\n' ' ')"
    exit 0
  fi
fi

if ! stopped="$(stop_and_confirm "$VARIANT" "$RUNDIR")"; then
  record_not_exercised "$VARIANT" "$RESULT_NAME" "the process could not be stopped" \
    "the harness could not confirm the application gone after SIGTERM, so no restart result is claimed"
  exit 0
fi

# The frozen state, read with the process confirmed gone.
FROZEN="$(snapshot "$VARIANT" "$RUNDIR")"
AT_STOP_HOLDER="$(snap "$FROZEN" holder)"
FROZEN_COUNTS="$(snap "$FROZEN" counts)"
FROZEN_CHECKPOINTS="$(snap "$FROZEN" checkpointIndex)"

recover_variant "$VARIANT" "$RUNDIR" 2
RESUME_STATUS=$?
AFTER="$(snapshot "$VARIANT" "$RUNDIR")"
END_HOLDER="$(snap "$AFTER" holder)"
AFTER_COUNTS="$(snap "$AFTER" counts)"
REPORTED="$(port_reported_resume "$RUNDIR")"

if [ "$VARIANT" = "durable" ]; then
  FIRST="$("$PYTHON" "$HARNESS_DIR/lib/holder.py" --first-after-durable "$(state durable "$RUNDIR")/store.jsonl" "$(snap "$FROZEN" maxSeq)")"
  PHASE="$("$PYTHON" -c 'import json,sys; print(json.loads(sys.argv[1])["detail"].get("phase") or "")' "$AFTER")"
  HOLDERS="$("$PYTHON" -c 'import json,sys; d=json.loads(sys.argv[1])["detail"]; print(" -> ".join(d.get("reportedHolders") or []) or "(none reported)")' "$AFTER")"
  verify_store "$RUNDIR" >/dev/null
  VERIFY_STATUS=$?
  if [ "$VERIFY_STATUS" = "0" ]; then
    VERIFY_NOTE="verify held every claim, so nothing the checkpoint recorded was written twice"
  else
    VERIFY_NOTE="verify failed (exit $VERIFY_STATUS)"
  fi
  REPLAY_NOTE="the specialist tool results went from $FROZEN_COUNTS at the stop to $AFTER_COUNTS after the restart"
else
  FIRST="$("$PYTHON" "$HARNESS_DIR/lib/holder.py" --first-after-baseline "$(state baseline "$RUNDIR")" \
    "$(( ${FROZEN_CHECKPOINTS:-1} - 1 ))" "$(snap "$FROZEN" authors)")"
  PHASE="$("$PYTHON" -c 'import json,sys; print(json.loads(sys.argv[1])["detail"].get("basis") or "")' "$AFTER")"
  HOLDERS=""
  VERIFY_NOTE="the baseline has no verifier, so a replay is read off the conversation its checkpoints hold"
  REPLAY_NOTE="the specialist tool results the checkpointed conversation held went from $FROZEN_COUNTS at the stop to $AFTER_COUNTS after the restart"
fi

FIRST_AGENT="$(printf '%s' "$FIRST" | "$PYTHON" -c 'import json,sys; d=json.load(sys.stdin) or {}; print(d.get("agent") or "")' 2>/dev/null || echo "")"
FIRST_DESC="$(printf '%s' "$FIRST" | "$PYTHON" -c '
import json,sys
d=json.load(sys.stdin) or {}
if not d:
    print("nothing: the restarted process committed no further agent action")
    raise SystemExit(0)
print("%s %s turn %s at %s" % (d.get("agent"), d.get("event") or "spoke in checkpoint", d.get("turn", d.get("index")), d.get("entry") or d.get("checkpoint")))' 2>/dev/null || echo "")"

# The answer is the agent the restarted process gave control to: the first agent to act in a checkpoint
# it wrote, which is a state reading independent of the holder field. It is deliberately not
# `END_HOLDER` — that is where the run ended once it had served every remaining turn, and on both
# variants it is a different agent from the one that resumed. An earlier version phrased this from one
# value and passed that same value twice, which could only ever print "resumed by X" and never
# consulted the state at all; that is why the control's sentence contradicted its own checkpoint.
POST_HOLDER="${FIRST_AGENT:-$END_HOLDER}"
if [ "$RESUME_STATUS" = "0" ]; then
  OUTCOME="$(describe_recovery "$POST_HOLDER" "$FIRST_AGENT")"
else
  OUTCOME="restart failed; control stops at ${POST_HOLDER:-nothing}"
fi

EVIDENCE="stopped cleanly (pid $stopped, SIGTERM, confirmed gone) at $STOP_LABEL; frozen state: holder $AT_STOP_HOLDER, fingerprint $(snap "$FROZEN" fingerprint), tool results $FROZEN_COUNTS; first action after the restart: $FIRST_DESC; $REPLAY_NOTE; $VERIFY_NOTE; recovery needed $RECOVERY_ATTEMPTS process(es)${RECOVERY_ERRORS:+ ($RECOVERY_ERRORS)}; port-reported resume: ${REPORTED:-(none)}${HOLDERS:+; holders across the run: $HOLDERS}"

if [ "$VARIANT" = "durable" ]; then
  DETAIL="The process was stopped with SIGTERM at the commit where $AT_STOP_HOLDER held turn 1 and had produced nothing. The restarted process read that checkpoint, took control as $(describe_recovery "$POST_HOLDER" "$FIRST_AGENT"), and its first committed action was $FIRST_DESC. $REPLAY_NOTE; $VERIFY_NOTE. Control after the restart was $POST_HOLDER, and the run finished in $RECOVERY_ATTEMPTS process(es)."
else
  if [ "$RESUME_STATUS" = "0" ]; then
    COMPLETED="The restart completed in $RECOVERY_ATTEMPTS process(es)."
  else
    COMPLETED="The restart did not complete: $RECOVERY_ERRORS — the framework aborts every resume of a workflow that still has work, and the port's resume has no way past it."
  fi
  DETAIL="The process was stopped with SIGTERM at $STOP_LABEL, where the framework checkpoint named $AT_STOP_HOLDER. The restarted process gave control to $POST_HOLDER ($(describe_recovery "$POST_HOLDER" "$FIRST_AGENT")); its newest checkpoint then names ${END_HOLDER:-nothing} and the first agent to act in a checkpoint it wrote was ${FIRST_AGENT:-none}. $REPLAY_NOTE. $COMPLETED"
  if [ -n "$REPORTED" ] && [ "$REPORTED" != "$FIRST_AGENT" ]; then
    DETAIL="$DETAIL The port's own resume line named $REPORTED; the state it moved is what says $FIRST_AGENT acted."
  fi
fi

record_result "$VARIANT" "$RESULT_NAME" "$OUTCOME" "$POST_HOLDER" "$PHASE" "$DETAIL" "$EVIDENCE" \
  "{\"processes\":$RECOVERY_ATTEMPTS,\"holderAtStop\":\"${AT_STOP_HOLDER:-}\",\"actedFirst\":\"${FIRST_AGENT:-}\",\"portReportedResume\":\"${REPORTED:-}\"}" \
  "$AT_STOP_HOLDER" "$END_HOLDER"
echo "$VARIANT / $RESULT_NAME: $OUTCOME (holder $POST_HOLDER)" >&2
