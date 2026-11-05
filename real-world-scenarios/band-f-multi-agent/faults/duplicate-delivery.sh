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
    CONTROL_VERSIONS="$(durable_count "$(state durable "$RUNDIR")/store.jsonl" output-versions 3)"
    DETAIL="Control: the closing request delivered once, in one process, with nothing killed. The store holds $CONTROL_VERSIONS output version(s) for turn 3 and the specialist results $COUNTS across $TURNS turn(s); the run ended with $AFTER_HOLDER holding control. Turn 3's output version count is what the fault row is measured against, because the whole-run multiset cannot answer the question: return_agent answers turn 0 and turn 3, so its result legitimately appears twice in any run that serves both turns."
  else
    TURNS="$(baseline_log_count "$RUNDIR" turn_end)"
    DETAIL="Control: the closing request delivered once, in one process, with nothing killed. The checkpointed conversation holds the specialist tool results $COUNTS across $TURNS completed turn(s), and the run ended with $AFTER_HOLDER holding control. The fault run is compared to exactly this multiset, so a result it holds twice is that specialist's tool run a second time and nothing else."
  fi
  record_result "$VARIANT" "$RESULT_NAME" "control: delivered once" "$AFTER_HOLDER" \
    "the closing request was delivered once and nothing was killed (the control)" "$DETAIL" \
    "tool results: $COUNTS; completed turns: $TURNS; holder after: $AFTER_HOLDER" \
    "{\"toolResults\":$COUNTS,\"completedTurns\":$TURNS,\"outputVersionsForTurn3\":${CONTROL_VERSIONS:-0}}" "$CONTROL_HOLDER" "$AFTER_HOLDER"
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

# The request is delivered a **second** time after the first delivery completed. This is the half the
# issue is really about: a re-delivery only tests deduplication if the first delivery was answered.
# For the durable variant the second resume finds the turn's output committed; for the baseline the
# port's resume skips the turns its log records as complete. Either may also no-op on a finished run,
# which is a deduplication of its own and is reported as such.
REDELIVERY_STATUS=""
REDELIVERY_STATUS="$(run_second_process "$VARIANT" "$RUNDIR" "$RUNDIR/.redelivery.log")"
REDELIVERY_NOTE="$(grep -m1 -E 'already finished|nothing to resume|skipping [0-9]+ completed' "$RUNDIR/.redelivery.log" 2>/dev/null | sed 's/^[[:space:]]*//' || true)"
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

# What "doubled" means here, and why the whole-run multiset cannot decide it.
#
# return_agent answers turn 0 (the return the customer asked for) and turn 3 (the confirm-still-open
# follow-up), so its tool result legitimately appears **twice** in any run that serves both turns —
# including the no-fault control, whose multiset is identical to this run's "after". A delta against
# the frozen state therefore cannot distinguish "the re-delivered turn ran twice" from "the run served
# two turns": for `return_agent` the two are the same number. The whole-run total is recorded for the
# reader, but the assertion is per turn: the re-delivered request is turn 3, so turn 3 must have more
# than one committed output, or a reuse record, for the tool to have run a second time for it.
SNAP_AFTER_COUNTS="$(snap "$SNAP_AFTER" counts)"
VERSIONS_FOR_TURN="$("$PYTHON" - "$(state durable "$RUNDIR")/store.jsonl" <<'PY'
import json, sys
versions = 0
for line in open(sys.argv[1], encoding="utf-8"):
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    if record.get("type") != "document-version":
        continue
    payload = record.get("payload") or {}
    if payload.get("kind") != "band-f.specialist-output":
        continue
    if (payload.get("value") or {}).get("turn") == 3:
        versions += 1
print(versions)
PY
)"
DOUBLED="$(printf '%s\n%s\n' "$COUNTS_BEFORE" "$SNAP_AFTER_COUNTS" | "$PYTHON" -c '
import json, sys
lines = [line for line in sys.stdin.read().splitlines() if line.strip()]
before, after = json.loads(lines[0]), json.loads(lines[1])
print(json.dumps({k: after[k] - before.get(k, 0) for k in after if after[k] > before.get(k, 0)}))')"
TURNS_DELTA=$(( ${TURNS_AFTER:-0} - ${TURNS_BEFORE:-0} ))

# The assertion: did the re-delivered turn produce a second output, or was the first reused?
if [ "${VERSIONS_FOR_TURN:-0}" -gt 1 ] || [ "${REUSED:-0}" -gt 0 ]; then
  OUTCOME="doubled"
  DOUBLED_VERDICT="$VERSIONS_FOR_TURN output version(s) for the re-delivered turn ${TURNS_AFTER:+and $REUSED reuse record(s)}"
else
  OUTCOME="not doubled"
  DOUBLED_VERDICT="one output version for the re-delivered turn and no reuse record — the request arrived twice and the tool ran once"
fi

if [ "$OUTCOME" = "doubled" ]; then
  MECHANISM="The second process found the delegation committed, its own checkpoint still naming $AFTER_HOLDER, and no output for the turn — so it produced. That is not a durability win: the variant has no memo for an output a process has already consumed, only one for a routing decision it has not yet acted on, so a second process after a kill produces again."
else
  MECHANISM="The second process produced nothing for the turn: the first had already committed it and the store answered for it, so the request was delivered twice and the tool ran once. What stopped the second run was the committed output, not an identity for the request — a delivery the variant has no record of would not have been deduplicated."
fi

DETAIL="The first process was killed with SIGKILL while $BEFORE_HOLDER held turn 3 with nothing produced, and a second process resumed the same store (exit $DELIVERY_STATUS), so the request was delivered twice. On the per-turn count — how many outputs turn 3 committed, which is the finding — $DOUBLED_VERDICT. Control after recovery was $AFTER_HOLDER. $MECHANISM A third delivery to the now-finished run (exit ${REDELIVERY_STATUS:-?}) is recorded only as the separate fact that ${REDELIVERY_NOTE:-it was handled}; it is not part of this claim, because a finished run being a no-op says nothing about whether the variant could deduplicate a request. $VERIFY_NOTE."

EVIDENCE="frozen after the first delivery (pid $killed, SIGKILL, confirmed gone) with holder $BEFORE_HOLDER and $TURNS_BEFORE completed turn(s); output versions for the re-delivered turn 3: 0 before, $VERSIONS_FOR_TURN after, $REUSED reuse record(s); third delivery exit ${REDELIVERY_STATUS:-?}: ${REDELIVERY_NOTE:-no refusal line}; whole-run specialist results before: $COUNTS_BEFORE, after: $SNAP_AFTER_COUNTS (delta $DOUBLED — reported, not decisive: return_agent answers turn 0 too, so its result appears twice in a no-fault run); completed turns: $TURNS_BEFORE then $TURNS_AFTER; second delivery exit: $DELIVERY_STATUS; holder after: $AFTER_HOLDER; $VERIFY_NOTE; holders across the run: $HOLDERS"

record_result "$VARIANT" "$RESULT_NAME" "$OUTCOME" "$AFTER_HOLDER" "$PHASE" "$DETAIL" "$EVIDENCE" \
  "{\"outputVersionsForTurn3\":${VERSIONS_FOR_TURN:-0},\"reuseRecordsForTurn3\":${REUSED:-0},\"toolResultsBefore\":$COUNTS_BEFORE,\"toolResultsAfter\":$SNAP_AFTER_COUNTS,\"toolResultsDelta\":$DOUBLED,\"completedTurnsBefore\":$TURNS_BEFORE,\"completedTurnsAfter\":$TURNS_AFTER,\"secondDelivery\":\"resume\",\"thirdDeliveryExit\":${REDELIVERY_STATUS:-null}}" "$FROZEN_HOLDER" "$AFTER_HOLDER"
echo "$VARIANT / $RESULT_NAME: $OUTCOME (holder $AFTER_HOLDER)" >&2
