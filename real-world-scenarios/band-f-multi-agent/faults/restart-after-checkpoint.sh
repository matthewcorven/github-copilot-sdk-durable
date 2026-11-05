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
  # The baseline announces nothing, so the stop point is found by polling. A poll has a window between
  # "the state says turn 1 is with the specialist" and the harness acting, and the run can consume a
  # whole turn inside it. The window is closed by holding the process the moment the state is right and
  # re-reading it while it is held: a stopped process cannot advance underneath the check.
  reached=0
  for _ in $(seq 1 6000); do
    HOLDER_NOW="$(holder_now "$VARIANT" "$RUNDIR")"
    TURNS_DONE="$(baseline_log_count "$RUNDIR" turn_end)"
    if [ "${TURNS_DONE:-0}" -eq 1 ] && [ "$HOLDER_NOW" = "$WANT_HOLDER" ]; then
      if hold_and_confirm "$VARIANT" "$RUNDIR" >/dev/null; then
        STILL="$(holder_now "$VARIANT" "$RUNDIR")"
        STILL_TURNS="$(baseline_log_count "$RUNDIR" turn_end)"
        if [ "$STILL" = "$WANT_HOLDER" ] && [ "${STILL_TURNS:-0}" -eq 1 ]; then
          reached=1
          break
        fi
        kill -CONT "$(cat "$RUNDIR/pid")" 2>/dev/null || true
      fi
    fi
    if [ -f "$RUNDIR/wrapper.pid" ] && ! kill -0 "$(cat "$RUNDIR/wrapper.pid")" 2>/dev/null; then break; fi
    sleep 0.2
  done
  if [ "$reached" != "1" ]; then
    record_not_exercised "$VARIANT" "$RESULT_NAME" "the stop point was not reached" \
      "the run never reached '$STOP_LABEL' (last holder: ${HOLDER_NOW:-none}, completed turns: ${TURNS_DONE:-0})"
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

if [ "$RESUME_STATUS" = "0" ]; then
  OUTCOME="$(describe_recovery "$FIRST_AGENT" "$FIRST_AGENT")"
else
  OUTCOME="restart failed; control stops at ${FIRST_AGENT:-${END_HOLDER:-nothing}}"
fi

EVIDENCE="stopped cleanly (pid $stopped, SIGTERM, confirmed gone) at $STOP_LABEL; frozen state: holder $AT_STOP_HOLDER, fingerprint $(snap "$FROZEN" fingerprint), tool results $FROZEN_COUNTS; first action after the restart: $FIRST_DESC; $REPLAY_NOTE; $VERIFY_NOTE; recovery needed $RECOVERY_ATTEMPTS process(es)${RECOVERY_ERRORS:+ ($RECOVERY_ERRORS)}; port-reported resume: ${REPORTED:-(none)}${HOLDERS:+; holders across the run: $HOLDERS}"

if [ "$VARIANT" = "durable" ]; then
  DETAIL="The process was stopped with SIGTERM at the commit where $AT_STOP_HOLDER held turn 1 and had produced nothing. The restarted process read that checkpoint and gave control to $FIRST_AGENT — the agent the checkpoint named, not the coordinator — and its first committed action was $FIRST_DESC. $REPLAY_NOTE; $VERIFY_NOTE. Control after the restart was $(describe_recovery "$FIRST_AGENT" "$FIRST_AGENT"); the run then finished in $RECOVERY_ATTEMPTS process(es) and ended with $END_HOLDER holding control, which is the settle step rather than the answer."
else
  if [ "$RESUME_STATUS" = "0" ]; then
    COMPLETED="The restart completed in $RECOVERY_ATTEMPTS process(es)."
  else
    COMPLETED="The restart did not complete: $RECOVERY_ERRORS — the framework aborts every resume of a workflow that still has work, and the port's resume has no way past it."
  fi
  DETAIL="The process was stopped with SIGTERM at $STOP_LABEL. The restarted process gave control to ${FIRST_AGENT:-nothing} — the first agent to act in a checkpoint it wrote afterwards — and its newest checkpoint then names ${END_HOLDER:-nothing}. $REPLAY_NOTE. $COMPLETED"
  if [ -n "$REPORTED" ] && [ "$REPORTED" != "$FIRST_AGENT" ]; then
    DETAIL="$DETAIL The port's own resume line named $REPORTED; the state it moved is what says $FIRST_AGENT acted."
  fi
fi

record_result "$VARIANT" "$RESULT_NAME" "$OUTCOME" "${FIRST_AGENT:-$END_HOLDER}" "$PHASE" "$DETAIL" "$EVIDENCE" \
  "{\"processes\":$RECOVERY_ATTEMPTS,\"holderAtStop\":\"${AT_STOP_HOLDER:-}\",\"actedFirst\":\"${FIRST_AGENT:-}\",\"portReportedResume\":\"${REPORTED:-}\"}" \
  "$AT_STOP_HOLDER" "$END_HOLDER"
echo "$VARIANT / $RESULT_NAME: $OUTCOME (holder ${FIRST_AGENT:-$END_HOLDER})" >&2
