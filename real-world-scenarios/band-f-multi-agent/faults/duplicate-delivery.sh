#!/usr/bin/env bash
# `duplicate-delivery` — deliver the same request to the specialist twice and count the work.
#
#   ./duplicate-delivery.sh --variant durable
#   ./duplicate-delivery.sh --variant baseline --fault-not-injected
#
# Both variants are frozen at the same point: the closing request has been handed to the specialist
# and nothing has been produced for it. Then it is delivered again through the second process the
# variant supports. The observation is the multiset of specialist tool results before and after: a
# result held once and then twice is the same work done twice, whatever else the run did.
#
# The control delivers once: the same run, one process, nothing killed.
#
# The baseline cannot complete this fault and the row says so. Its only re-entry point is a resume,
# which re-sends the outstanding request — that part happens — but every resumed workflow with work
# left aborts inside the framework, and the port will not start a fresh run over a directory that
# already holds one. So the second delivery is attempted and observed, and the row is `not exercised`
# with the reason rather than a count from an aborted run.

set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

begin_fault duplicate-delivery "$@"
RUNDIR="$(new_fault_dir)"; track_dir "$VARIANT" "$RUNDIR"

# ── the control: one delivery, one process ────────────────────────────────

if [ "$NOT_INJECTED" = "1" ]; then
  start_run "$VARIANT" "$RUNDIR"
  if ! await_app_pid "$VARIANT" "$RUNDIR" 180 >/dev/null; then
    record_not_exercised "$VARIANT" "$RESULT_NAME" "the variant did not start" \
      "the application never opened its store: $(tail_log "$RUNDIR/.starter.log" 4 | tr '\n' ' ')"
    exit 0
  fi
  wait_wrapper "$RUNDIR" wrapper 420 || true
  AFTER="$(holder_json "$VARIANT" "$RUNDIR")"
  AFTER_HOLDER="$(printf '%s' "$AFTER" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin).get("holder") or "")')"
  SNAP="$(snapshot "$VARIANT" "$RUNDIR")"
  CONTROL_HOLDER="$(snap "$SNAP" holder)"
  COUNTS="$(snap "$SNAP" counts)"
  if [ "$VARIANT" = "durable" ]; then
    TURNS="$(durable_count "$(state durable "$RUNDIR")/store.jsonl" turns)"
    DETAIL="Control: the closing request delivered once, in one process, with nothing killed. The store holds one specialist output per turn — $COUNTS — across $TURNS turn(s), and the run ended with $AFTER_HOLDER holding control. The fault run is compared to exactly this multiset, so a result it holds twice is that specialist's tool run a second time and nothing else."
  else
    TURNS="$(baseline_log_count "$RUNDIR" turn_end)"
    DETAIL="Control: the closing request delivered once, in one process, with nothing killed. The checkpointed conversation holds the specialist tool results $COUNTS across $TURNS completed turn(s), and the run ended with $AFTER_HOLDER holding control. The fault run is compared to exactly this multiset, so a result it holds twice is that specialist's tool run a second time and nothing else."
  fi
  record_result "$VARIANT" "$RESULT_NAME" "control: delivered once" "$AFTER_HOLDER" \
    "the closing request was delivered once and nothing was killed (the control)" "$DETAIL" \
    "tool results: $COUNTS; completed turns: $TURNS; holder after: $AFTER_HOLDER" \
    "{\"toolResults\":$COUNTS,\"completedTurns\":$TURNS}" "$CONTROL_HOLDER" "$AFTER_HOLDER"
  echo "control: $VARIANT delivered once (holder $AFTER_HOLDER)" >&2
  exit 0
fi

# ── the fault: freeze the delivery in flight ──────────────────────────────

if [ "$VARIANT" = "durable" ]; then
  # The variant's own `--kill-in-turn` seam is where the state is guaranteed: the specialist's turn is
  # entered, the request is committed, nothing has been produced.
  start_run "$VARIANT" "$RUNDIR" --kill-in-turn 3
  if ! seam_pid="$(await_seam_pid "$RUNDIR" 420)"; then
    record_not_exercised "$VARIANT" "$RESULT_NAME" "the first delivery never reached the specialist" \
      "the run never announced the in-turn seam: $(tail_log "$RUNDIR/.starter.log" 6 | tr '\n' ' ')"
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
  if ! await_holder "$VARIANT" "$RUNDIR" "return_agent" 420; then
    record_not_exercised "$VARIANT" "$RESULT_NAME" "the first delivery never reached the specialist" \
      "the run never reached a committed state naming return_agent: $(tail_log "$RUNDIR/.starter.log" 6 | tr '\n' ' ')"
    exit 0
  fi
fi

BEFORE="$(holder_json "$VARIANT" "$RUNDIR")"
BEFORE_HOLDER="$(printf '%s' "$BEFORE" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin).get("holder") or "")')"
if [ "$VARIANT" = "durable" ]; then
  TURNS_BEFORE="$(durable_count "$(state durable "$RUNDIR")/store.jsonl" turns)"
else
  TURNS_BEFORE="$(baseline_log_count "$RUNDIR" turn_end)"
fi

if ! killed="$(kill_and_confirm "$VARIANT" "$RUNDIR")"; then
  record_not_exercised "$VARIANT" "$RESULT_NAME" "the first delivery could not be frozen" \
    "the harness could not confirm the application gone, so no duplicate could be delivered"
  exit 0
fi

# The state the fault froze, read with the process confirmed gone so nothing moves underneath it.
FROZEN="$(snapshot "$VARIANT" "$RUNDIR")"
FROZEN_HOLDER="$(snap "$FROZEN" holder)"
COUNTS_BEFORE="$(snap "$FROZEN" counts)"

# ── the second delivery ───────────────────────────────────────────────────

recover_variant "$VARIANT" "$RUNDIR" 2
DELIVERY_STATUS=$?
AFTER="$(holder_json "$VARIANT" "$RUNDIR")"
AFTER_HOLDER="$(printf '%s' "$AFTER" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin).get("holder") or "")')"

if [ "$VARIANT" = "baseline" ]; then
  RESENT="$(baseline_log_count "$RUNDIR" turn_end)"
  DETAIL="The request the kill interrupted was delivered a second time: the port's resume re-sent it from the turn its own log had not recorded as complete, which is the duplicate delivery happening. The delivery could not be completed, however — ${RECOVERY_ERRORS:-every resume aborted inside the framework} — and the port refuses a fresh run over a directory that already holds one, so there is no completed run to compare counts in. The run's log recorded $RESENT turn end(s) across $RECOVERY_ATTEMPTS process(es) and the newest framework checkpoint names ${AFTER_HOLDER:-nothing}. The row is recorded as not exercised rather than as a comparison: the second delivery was attempted and observed, but what it would have done to the specialist's output is unobservable in this variant. It is not a durability win for the baseline — its recovery is broken, not safe."
  record_result "$VARIANT" "$RESULT_NAME" "not exercised" "" \
    "the request was re-delivered and the port aborted before it could complete" "$DETAIL" \
    "the port's resume re-sent the request; ${RECOVERY_ERRORS:-no error recorded}; holder after: ${AFTER_HOLDER:-nothing}" \
    "{\"resumeExit\":$DELIVERY_STATUS,\"completedTurns\":$RESENT,\"processes\":$RECOVERY_ATTEMPTS,\"secondDelivery\":\"attempted\"}"
  echo "baseline / $RESULT_NAME: not exercised (the port's resume aborts)" >&2
  exit 0
fi

SNAP_AFTER="$(snapshot durable "$RUNDIR")"
COUNTS_AFTER="$(snap "$SNAP_AFTER" counts)"
TURNS_AFTER="$(durable_count "$(state durable "$RUNDIR")/store.jsonl" turns)"
OUTPUT_VERSIONS="$(durable_count "$(state durable "$RUNDIR")/store.jsonl" output-versions 3)"
REUSED="$(durable_count "$(state durable "$RUNDIR")/store.jsonl" reused 3)"
PHASE="$(printf '%s' "$AFTER" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin)["detail"].get("phase") or "")')"
HOLDERS="$(printf '%s' "$AFTER" | "$PYTHON" -c 'import json,sys; d=json.load(sys.stdin)["detail"]; print(" -> ".join(d.get("reportedHolders") or []) or "(none reported)")')"
verify_store "$RUNDIR" >/dev/null
VERIFY_NOTE="verify exited $?"

# The doubling is in the multiset of results, not in a total: a result the control holds once and the
# fault holds twice is that specialist's tool run twice, whatever else the run did.
DOUBLED="$(printf '%s\n%s\n' "$COUNTS_BEFORE" "$COUNTS_AFTER" | "$PYTHON" -c '
import json, sys
lines = [line for line in sys.stdin.read().splitlines() if line.strip()]
before, after = json.loads(lines[0]), json.loads(lines[1])
print(json.dumps({k: after[k] - before.get(k, 0) for k in after if after[k] > before.get(k, 0)}))')"
DOUBLED_COUNT="$("$PYTHON" -c 'import json,sys; print(sum(json.loads(sys.argv[1]).values()))' "$DOUBLED")"
TURNS_DELTA=$(( ${TURNS_AFTER:-0} - ${TURNS_BEFORE:-0} ))
if [ "$DOUBLED_COUNT" -gt 0 ]; then OUTCOME="doubled"; else OUTCOME="not doubled"; fi

if [ "$DOUBLED_COUNT" -gt 0 ]; then
  MECHANISM="The second process found the delegation committed, its own checkpoint still naming $AFTER_HOLDER, and no output for the turn — so it produced a second time. This variant has no identity for a delivery that a second process could deduplicate against."
else
  MECHANISM="The second process produced nothing: the first had already committed the turn and the store answered for it (${OUTPUT_VERSIONS:-0} output version(s), ${REUSED:-0} reuse record(s)), so the request was delivered twice and the tool ran once."
fi

DETAIL="The closing request was delivered twice. The first process was killed with SIGKILL while $BEFORE_HOLDER held turn 3 with nothing produced; the second delivery was one resume of the state the kill left (exit $DELIVERY_STATUS). The specialist's tool results went from $COUNTS_BEFORE to $COUNTS_AFTER, so $DOUBLED_COUNT more specialist tool run(s) happened ($DOUBLED), and the run served $TURNS_DELTA more turn(s). $MECHANISM $VERIFY_NOTE. Control after recovery was $AFTER_HOLDER."

EVIDENCE="frozen after the first delivery (pid $killed, SIGKILL, confirmed gone) with holder $BEFORE_HOLDER and $TURNS_BEFORE completed turn(s); tool results before: $COUNTS_BEFORE; after: $COUNTS_AFTER; repeated: $DOUBLED; completed turns: $TURNS_BEFORE then $TURNS_AFTER; second delivery exit: $DELIVERY_STATUS; holder after: $AFTER_HOLDER; $VERIFY_NOTE; $OUTPUT_VERSIONS output version(s) for turn 3; $REUSED reuse record(s); holders across the run: $HOLDERS"

record_result "$VARIANT" "$RESULT_NAME" "$OUTCOME" "$AFTER_HOLDER" "$PHASE" "$DETAIL" "$EVIDENCE" \
  "{\"toolResultsBefore\":$COUNTS_BEFORE,\"toolResultsAfter\":$COUNTS_AFTER,\"repeated\":$DOUBLED,\"completedTurnsBefore\":$TURNS_BEFORE,\"completedTurnsAfter\":$TURNS_AFTER,\"secondDelivery\":\"resume\"}" "$FROZEN_HOLDER" "$AFTER_HOLDER"
echo "$VARIANT / $RESULT_NAME: $OUTCOME (holder $AFTER_HOLDER)" >&2
