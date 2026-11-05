#!/usr/bin/env bash
# `kill-mid-delegation` — kill the process while the specialist holds control, then recover.
#
#   ./kill-mid-delegation.sh --variant durable
#   ./kill-mid-delegation.sh --variant baseline --fault-not-injected
#
# The fault: wait until the variant's own committed state names a specialist as the holder, then
# `kill -9` the application's own pid and confirm it is gone. Nothing in the process runs afterwards,
# so what the store holds is exactly what its commits wrote.
#
# The observation, and the band's assertion: **which agent held control after recovery**. It is read
# from the state each variant says control lives in — a durable task's `checkpoint.holder`; the
# framework checkpoint the baseline's workflow is reconstructed from — and corroborated by the first
# agent the recovered process committed as. `holderAtFault` and `holderAtEnd` are recorded beside it
# so none of the three can be mistaken for another.
#
# The control: the same run, the same wait, no kill. The run then finishes in one process, which is
# what makes the second process in the fault run attributable to the kill.

set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

begin_fault kill-mid-delegation "$@"
RUNDIR="$(new_fault_dir)"; track_dir "$VARIANT" "$RUNDIR"

start_run "$VARIANT" "$RUNDIR"
if ! await_app_pid "$VARIANT" "$RUNDIR" 180 >/dev/null; then
  record_not_exercised "$VARIANT" "$RESULT_NAME" "the variant did not start" \
    "the application never opened its store, so the fault had nothing to act on: $(tail_log "$RUNDIR/.starter.log" 4 | tr '\n' ' ')"
  exit 0
fi

if ! await_holder "$VARIANT" "$RUNDIR" "return_agent" 420; then
  record_not_exercised "$VARIANT" "$RESULT_NAME" "the delegation never committed" \
    "the run never reached a committed checkpoint naming a specialist as holder, so there was no 'mid-delegation' to kill: $(tail_log "$RUNDIR/.starter.log" 6 | tr '\n' ' ')"
  exit 0
fi

if [ "$NOT_INJECTED" = "1" ]; then
  # The control: the run is left alone. Its own completion is the measurement.
  wait_wrapper "$RUNDIR" wrapper 420 || true
  AFTER="$(holder_json "$VARIANT" "$RUNDIR")"
  END_HOLDER="$(printf '%s' "$AFTER" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin).get("holder") or "")')"
  REPORTED="$(port_reported_resume "$RUNDIR")"
  if [ "$VARIANT" = "durable" ]; then
    HOLDERS="$(printf '%s' "$AFTER" | "$PYTHON" -c 'import json,sys; d=json.load(sys.stdin)["detail"]; print(" -> ".join(d.get("reportedHolders") or []) or "(none reported)")')"
    DETAIL="Control: the same run with no kill. The process was left alone once a specialist held control; it served every turn itself, reported the holders $HOLDERS, and ended with $END_HOLDER holding control. No second process existed, so the recovery in the fault run — and where it landed — is attributable to the kill and not to the shape of the run."
  else
    DETAIL="Control: the same run with no kill. The process was left alone, finished all five turns itself, and ended with $END_HOLDER holding control. No second process existed, so the recovery — and its failure to complete — in the fault run is attributable to the kill."
  fi
  record_result "$VARIANT" "$RESULT_NAME" "control: completed in one process" "$END_HOLDER" \
    "the delegation committed and nothing was killed (the control)" "$DETAIL" \
    "holder when the kill would have struck: $(holder_now "$VARIANT" "$RUNDIR"); holder at the end: $END_HOLDER" \
    "{\"processes\":1}" "$END_HOLDER" "$END_HOLDER"
  echo "control completed: holder $END_HOLDER" >&2
  exit 0
fi

# Kill it, once the state names the specialist.
if ! killed="$(kill_and_confirm "$VARIANT" "$RUNDIR")"; then
  record_not_exercised "$VARIANT" "$RESULT_NAME" "the application's pid could not be killed" \
    "the harness found the application's pid but could not confirm it gone after SIGKILL, so no kill result is claimed"
  exit 0
fi

# The frozen state: read with the process confirmed gone, so nothing moves underneath it.
FROZEN="$(snapshot "$VARIANT" "$RUNDIR")"
AT_KILL_HOLDER="$(snap "$FROZEN" holder)"
FROZEN_COUNTS="$(snap "$FROZEN" counts)"

recover_variant "$VARIANT" "$RUNDIR" 2
RESUME_STATUS=$?

AFTER="$(snapshot "$VARIANT" "$RUNDIR")"
RECOVERED_HOLDER="$(snap "$AFTER" holder)"
END_HOLDER="$(snap "$AFTER" holder)"
RECOVERED_COUNTS="$(snap "$AFTER" counts)"
REPORTED="$(port_reported_resume "$RUNDIR")"
MOVED="no"
if snapshot_moved "$FROZEN" "$AFTER"; then MOVED="yes"; fi

# The first agent the recovered process committed as: derived independently of the holder field, so
# the claim is corroborated rather than restated.
if [ "$VARIANT" = "durable" ]; then
  HOLDERS="$("$PYTHON" -c 'import json,sys; d=json.loads(sys.argv[1])["detail"]; print(" -> ".join(d.get("reportedHolders") or []) or "(none reported)")' "$AFTER")"
else
  HOLDERS=""
fi

# The first action is taken from a dedicated read, because it needs the boundary the frozen state was at.
if [ "$VARIANT" = "durable" ]; then
  FIRST="$("$PYTHON" "$HARNESS_DIR/lib/holder.py" --first-after-durable "$(state durable "$RUNDIR")/store.jsonl" "$(snap "$FROZEN" maxSeq)")"
else
  FIRST="$("$PYTHON" "$HARNESS_DIR/lib/holder.py" --first-after-baseline "$(state baseline "$RUNDIR")" \
    "$(( $(snap "$FROZEN" checkpointIndex) - 1 ))" "$(snap "$FROZEN" authors)")"
fi
FIRST_AGENT="$(printf '%s' "$FIRST" | "$PYTHON" -c 'import json,sys; d=json.load(sys.stdin) or {}; print(d.get("agent") or "")' 2>/dev/null || echo "")"
FIRST_DESC="$(printf '%s' "$FIRST" | "$PYTHON" -c '
import json,sys
d=json.load(sys.stdin) or {}
if not d:
    print("nothing: the recovered process committed no further agent action")
    raise SystemExit(0)
print("%s %s turn %s at %s" % (d.get("agent"), d.get("event") or "spoke in checkpoint", d.get("turn", d.get("index")), d.get("entry") or d.get("checkpoint")))' 2>/dev/null || echo "")"

if [ "$VARIANT" = "durable" ]; then
  verify_store "$RUNDIR" >/dev/null
  VERIFY_NOTE="verify exited $?"
  PHASE="$("$PYTHON" -c 'import json,sys; print(json.loads(sys.argv[1])["detail"].get("phase") or "")' "$AFTER")"
else
  VERIFY_NOTE="the baseline has no verifier; control is read from its checkpoint, not its log"
  PHASE="$("$PYTHON" -c 'import json,sys; print(json.loads(sys.argv[1])["detail"].get("basis") or "")' "$AFTER")"
fi

# The assertion. `RECOVERED_HOLDER` is the holder the recovered process was given; `FIRST_AGENT` is
# the agent that actually committed first. When the port's own resume line disagrees with the state,
# that is reported rather than resolved in the port's favour.
# One expression for the answer, used by the outcome, the detail and the recorded field, so they
# cannot say three different things. The acting agent is the one the recovered process committed as;
# `RECOVERED_HOLDER` is what the state's holder field says, and where they differ that is a finding,
# not a tie to break quietly.
POST_HOLDER="${FIRST_AGENT:-$RECOVERED_HOLDER}"
if [ "$RESUME_STATUS" = "0" ]; then
  OUTCOME="$(describe_recovery "$POST_HOLDER" "$FIRST_AGENT")"
else
  OUTCOME="recovery failed; control stops at ${POST_HOLDER:-nothing}"
fi
EVIDENCE="at the kill (pid $killed, SIGKILL, confirmed gone): holder $AT_KILL_HOLDER, fingerprint $(snap "$FROZEN" fingerprint), specialist tool results $FROZEN_COUNTS; after recovery the state moved: $MOVED; first action after recovery: $FIRST_DESC; recovery ran $RECOVERY_ATTEMPTS process(es)${RECOVERY_ERRORS:+ ($RECOVERY_ERRORS)}; port-reported resume: ${REPORTED:-(none)}; $VERIFY_NOTE${HOLDERS:+; holders across the run: $HOLDERS}"

if [ "$VARIANT" = "durable" ]; then
  DETAIL="The process was killed with SIGKILL while $AT_KILL_HOLDER held control, delegated to with nothing produced. A new process reopened the same store and gave control to $RECOVERED_HOLDER — not back to the coordinator — and its first committed action was $FIRST_DESC. Control after recovery was $RECOVERED_HOLDER. The run then served every turn and ended with $END_HOLDER holding control, which is the settle step handing the turn back and not this fault's answer. $VERIFY_NOTE."
else
  if [ "$RESUME_STATUS" = "0" ]; then
    RECOVERY_NOTE="The recovery completed in $RECOVERY_ATTEMPTS process(es)."
  else
    RECOVERY_NOTE="The recovery did not complete: $RECOVERY_ERRORS — the framework aborts every resume of a workflow that still has work, and the port's resume has no way past it."
  fi
  DETAIL="The process was killed with SIGKILL while $AT_KILL_HOLDER held control; a handoff was queued at that agent's executor. $RECOVERY_NOTE The first agent to act in the recovered process was ${FIRST_AGENT:-none}, so control after recovery was $(describe_recovery "$POST_HOLDER" "$FIRST_AGENT") — read from the framework checkpoint rather than from the port's log. The run's newest checkpoint afterwards names ${END_HOLDER:-nothing}, which is where the port ended rather than what this fault asks."
  if [ -n "$REPORTED" ] && [ "$REPORTED" != "$FIRST_AGENT" ]; then
    DETAIL="$DETAIL The port's own resume line named $REPORTED as holding control; the state it moved shows $FIRST_AGENT ran first, so $FIRST_AGENT is the answer."
  fi
fi

record_result "$VARIANT" "$RESULT_NAME" "$OUTCOME" "$POST_HOLDER" "$PHASE" "$DETAIL" "$EVIDENCE" \
  "{\"processes\":$RECOVERY_ATTEMPTS,\"holderWhenKilled\":\"${AT_KILL_HOLDER:-}\",\"holderAfterRecovery\":\"${RECOVERED_HOLDER:-}\",\"actedFirst\":\"${FIRST_AGENT:-}\",\"stateMoved\":$( [ "$MOVED" = "yes" ] && echo true || echo false ),\"portReportedResume\":\"${REPORTED:-}\"}" \
  "$AT_KILL_HOLDER" "$END_HOLDER"
echo "$VARIANT / $RESULT_NAME: $OUTCOME (holder $POST_HOLDER)" >&2
