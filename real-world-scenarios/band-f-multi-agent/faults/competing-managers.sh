#!/usr/bin/env bash
# `competing-managers` — two coordinators route the same request at the same time.
#
#   ./competing-managers.sh --variant durable
#   ./competing-managers.sh --variant baseline --fault-not-injected
#
# The fault: while the first process is serving turn 0, a second process is started on the same
# state, with the same instruction to continue the conversation. Both are coordinators; whichever one
# wins determines who holds control. The observation is whether the second is refused, and whether
# the first's control of the turn survives — read from committed state either way.
#
# Each variant answers with its own mechanism, and both are reported rather than one being fitted to
# the other:
#
#   durable    the store's single-writer lock. The second process is refused at open, before it can
#              commit anything.
#   baseline   the framework's checkpoint store lock. The second process is refused at open too.
#
# The control starts only one process. Without it, "the second was refused" could be a property of
# the harness rather than of the variant.

set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

begin_fault competing-managers "$@"
RUNDIR="$(new_fault_dir)"; track_dir "$VARIANT" "$RUNDIR"

start_run "$VARIANT" "$RUNDIR"
if ! first_pid="$(await_app_pid "$VARIANT" "$RUNDIR" 180)"; then
  record_not_exercised "$VARIANT" "$RESULT_NAME" "the variant did not start" \
    "the application never opened its store: $(tail_log "$RUNDIR/.starter.log" 4 | tr '\n' ' ')"
  exit 0
fi

if ! await_holder "$VARIANT" "$RUNDIR" "return_agent" 420; then
  record_not_exercised "$VARIANT" "$RESULT_NAME" "the first manager never took control" \
    "the run never reached a committed state with a specialist in control: $(tail_log "$RUNDIR/.starter.log" 6 | tr '\n' ' ')"
  exit 0
fi

BEFORE="$(holder_json "$VARIANT" "$RUNDIR")"
BEFORE_HOLDER="$(printf '%s' "$BEFORE" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin).get("holder") or "")')"
SNAP_BEFORE="$(snapshot "$VARIANT" "$RUNDIR")"
COUNTS_BEFORE="$(snap "$SNAP_BEFORE" counts)"

if [ "$NOT_INJECTED" = "1" ]; then
  # The state at the point the fault would have struck: the first manager was serving turn 0 with a
  # specialist in control. That is the `holderAtFault` the fault row is compared against.
  wait_wrapper "$RUNDIR" wrapper 420 || true
  AFTER="$(snapshot "$VARIANT" "$RUNDIR")"
  AFTER_HOLDER="$(snap "$AFTER" holder)"
  COUNTS_AFTER="$(snap "$AFTER" counts)"
  DETAIL="Control: one coordinator. The process serving turn 0 finished the run alone and control ended with $AFTER_HOLDER; the specialist tool results went from $COUNTS_BEFORE to $COUNTS_AFTER. Nothing was competing for the state, so anything the fault row observes that differs from this belongs to the second manager."
  record_result "$VARIANT" "$RESULT_NAME" "control: one manager completed" "$AFTER_HOLDER" \
    "one coordinator served the run (the control)" "$DETAIL" \
    "tool results: $COUNTS_BEFORE then $COUNTS_AFTER; holder after: $AFTER_HOLDER" \
    "{\"toolResultsBefore\":$COUNTS_BEFORE,\"toolResultsAfter\":$COUNTS_AFTER,\"processes\":1}" "$BEFORE_HOLDER" "$AFTER_HOLDER"
  echo "control: $VARIANT one manager completed (holder $AFTER_HOLDER)" >&2
  exit 0
fi

# The second coordinator: the same entry point as the first, on the same state, started while the
# first is still serving. It is given a bounded time to finish or fail; a refusal is the fastest
# outcome and the expected one.
SECOND_LOG="$RUNDIR/second-manager.log"
SECOND_START=$SECONDS
SECOND_STATUS="$(run_second_process "$VARIANT" "$RUNDIR" "$SECOND_LOG")"
SECOND_MILLIS=$(( (SECONDS - SECOND_START) * 1000 ))

REFUSAL=""
if [ "$VARIANT" = "durable" ]; then
  REFUSAL="$(grep -m1 'already open in another process' "$SECOND_LOG" 2>/dev/null || true)"
  REFUSAL_KIND="the store's single-writer lock"
else
  REFUSAL="$(grep -m1 'is already in use by another process' "$SECOND_LOG" 2>/dev/null || true)"
  REFUSAL_KIND="the framework checkpoint store's lock"
fi

# The first manager is still alive and still the one serving: that is the "defined winner" half.
FIRST_ALIVE=0
kill -0 "$first_pid" 2>/dev/null && FIRST_ALIVE=1
AFTER="$(snapshot "$VARIANT" "$RUNDIR")"
AFTER_HOLDER="$(snap "$AFTER" holder)"
COUNTS_MID="$(snap "$AFTER" counts)"

# Let the first manager finish, so the row states where control ended as well as who was refused.
wait_wrapper "$RUNDIR" wrapper 420 || true
FINAL="$(snapshot "$VARIANT" "$RUNDIR")"
FINAL_HOLDER="$(snap "$FINAL" holder)"
COUNTS_AFTER="$(snap "$FINAL" counts)"

if [ "$VARIANT" = "durable" ]; then
  PHASE="$("$PYTHON" -c 'import json,sys; print(json.loads(sys.argv[1])["detail"].get("phase") or "")' "$FINAL")"
  HOLDERS="$("$PYTHON" -c 'import json,sys; d=json.loads(sys.argv[1])["detail"]; print(" -> ".join(d.get("reportedHolders") or []) or "(none reported)")' "$FINAL")"
  verify_store "$RUNDIR" >/dev/null
  VERIFY_NOTE="verify exited $?"
  TURNS="$(durable_count "$(state durable "$RUNDIR")/store.jsonl" turns)"
  DELEGATIONS="$(durable_count "$(state durable "$RUNDIR")/store.jsonl" delegations)"
else
  PHASE="$("$PYTHON" -c 'import json,sys; print(json.loads(sys.argv[1])["detail"].get("basis") or "")' "$FINAL")"
  HOLDERS=""
  VERIFY_NOTE="the baseline has no verifier"
  TURNS="$(baseline_log_count "$RUNDIR" turn_end)"
  DELEGATIONS="$(baseline_log_count "$RUNDIR" handoffs)"
fi

if [ -n "$REFUSAL" ]; then OUTCOME="refused: the second manager"; else OUTCOME="two managers ran"; fi

if [ -n "$REFUSAL" ]; then
  if [ "$VARIANT" = "durable" ]; then
    WINNER_NOTE="The refusal names the pid that holds the lock, so the outcome is a defined winner rather than first-past-the-post."
  else
    WINNER_NOTE="The refusal names no pid: the framework's store lock says the directory is in use and stops there, so the winner is defined by who holds the lock rather than by anything a reader could look up."
  fi
  DETAIL="A second coordinator was started on the same state while the first was serving turn 0. It was refused by $REFUSAL_KIND at open, after ${SECOND_MILLIS}ms and before it could commit anything: \"$REFUSAL\". The first manager was still running when the refusal was reported and stayed the only writer; $AFTER_HOLDER kept control of the turn it had (specialist tool results at that moment: $COUNTS_MID), and the run went on to $TURNS turn(s) with $DELEGATIONS delegation record(s) and $COUNTS_AFTER specialist tool results, ending with $FINAL_HOLDER holding control. $VERIFY_NOTE. $WINNER_NOTE"
else
  DETAIL="A second coordinator was started on the same state while the first was serving turn 0, and it was NOT refused (exit $SECOND_STATUS after ${SECOND_MILLIS}ms). Both processes ran against one state. Control ended with $FINAL_HOLDER; $TURNS turn(s) and $DELEGATIONS delegation record(s) are in the store, and the specialist tool results went from $COUNTS_BEFORE to $COUNTS_AFTER. $VERIFY_NOTE."
fi

EVIDENCE="first manager pid $first_pid alive when the second was refused: $([ "$FIRST_ALIVE" = "1" ] && echo yes || echo no); second manager exit $SECOND_STATUS after ${SECOND_MILLIS}ms; refusal read from its own output: ${REFUSAL:-none}; holder at refusal: $AFTER_HOLDER; tool results at refusal: $COUNTS_MID; holder at the end: $FINAL_HOLDER; holders across the run: ${HOLDERS:-unavailable}"

record_result "$VARIANT" "$RESULT_NAME" "$OUTCOME" "$AFTER_HOLDER" "$PHASE" "$DETAIL" "$EVIDENCE" \
  "{\"secondManagerExit\":$SECOND_STATUS,\"secondManagerMillis\":$SECOND_MILLIS,\"refused\":$([ -n "$REFUSAL" ] && echo true || echo false),\"firstAliveAtRefusal\":$([ "$FIRST_ALIVE" = "1" ] && echo true || echo false),\"toolResultsBefore\":$COUNTS_BEFORE,\"toolResultsAfter\":$COUNTS_AFTER}" "$AFTER_HOLDER" "$FINAL_HOLDER"
echo "$VARIANT / $RESULT_NAME: $OUTCOME (holder $FINAL_HOLDER)" >&2
