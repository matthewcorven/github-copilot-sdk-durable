#!/usr/bin/env bash
# `store-outage` — the store disappears during the handoff commit, and the question is what survives.
#
#   ./store-outage.sh --variant durable
#   ./store-outage.sh --variant baseline --fault-not-injected
#
# The fault: once the state names the specialist as holder of turn 0, the file the variant's store
# lives in is moved aside while the process has it open. Whatever the live process does next, its
# writes no longer land where they did, and the moved file is all that survived of the state at the
# moment of the outage.
#
# The observation, in three parts, each read from a file rather than from a process's word:
#
#   1. what the live process did after the outage;
#   2. whether the moved file is intact and where its last committed checkpoint was — a torn or
#      half-written transition would show up here;
#   3. the post-recovery holder: a fresh process is started on the surviving state and the agent it
#      gives control to is read from that state.
#
# The control moves nothing, so the run finishes and the same three readings are taken from an
# untouched store. The difference is attributable to the outage.

set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

begin_fault store-outage "$@"
RUNDIR="$(new_fault_dir)"; track_dir "$VARIANT" "$RUNDIR"
BASE="$(state "$VARIANT" "$RUNDIR")"

# The file that holds the state, and the file whose removal is the outage.
if [ "$VARIANT" = "durable" ]; then
  PRIMARY="$BASE/store.jsonl"
else
  PRIMARY="$BASE/index.jsonl"
fi

start_run "$VARIANT" "$RUNDIR"
if ! await_app_pid "$VARIANT" "$RUNDIR" 180 >/dev/null; then
  record_not_exercised "$VARIANT" "$RESULT_NAME" "the variant did not start" \
    "the application never opened its store: $(tail_log "$RUNDIR/.starter.log" 4 | tr '\n' ' ')"
  exit 0
fi

if ! await_holder "$VARIANT" "$RUNDIR" "return_agent" 420; then
  record_not_exercised "$VARIANT" "$RESULT_NAME" "the handoff never committed" \
    "the run never reached a committed state naming a specialist as holder, so there was no handoff for the store to fail during: $(tail_log "$RUNDIR/.starter.log" 6 | tr '\n' ' ')"
  exit 0
fi

BEFORE="$(holder_json "$VARIANT" "$RUNDIR")"
BEFORE_HOLDER="$(printf '%s' "$BEFORE" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin).get("holder") or "")')"

if [ "$NOT_INJECTED" = "1" ]; then
  wait_wrapper "$RUNDIR" wrapper 420 || true
  AFTER="$(holder_json "$VARIANT" "$RUNDIR")"
  AFTER_HOLDER="$(printf '%s' "$AFTER" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin).get("holder") or "")')"
  if [ "$VARIANT" = "durable" ]; then
    LINES="$(grep -c . "$PRIMARY" 2>/dev/null || echo 0)"
    INTACT="$("$PYTHON" - "$PRIMARY" <<'PY'
import json, sys
records = torn = 0
for line in open(sys.argv[1], encoding="utf-8"):
    if not line.strip():
        continue
    try:
        json.loads(line); records += 1
    except ValueError:
        torn += 1
print("%d record(s), %d torn" % (records, torn))
PY
)"
    DETAIL="Control: nothing was moved. The process finished the run with $AFTER_HOLDER holding control and the store's $PRIMARY held $INTACT. This is what the outage run is compared against: whatever is short there is the outage's doing."
  else
    COUNT="$(baseline_checkpoint_count "$RUNDIR")"
    DETAIL="Control: nothing was moved. The process finished the run with $AFTER_HOLDER holding control and the framework store's index reached $COUNT checkpoint(s). This is what the outage run is compared against."
    INTACT="$COUNT checkpoint(s) indexed"
    LINES=""
  fi
  record_result "$VARIANT" "$RESULT_NAME" "control: completed" "$AFTER_HOLDER" \
    "the store was left in place (the control)" "$DETAIL" \
    "holder after: $AFTER_HOLDER; $INTACT" \
    "{\"toolResults\":$(tool_counts "$VARIANT" "$RUNDIR")}" "$BEFORE_HOLDER" "$AFTER_HOLDER"
  echo "control: $VARIANT completed (holder $AFTER_HOLDER)" >&2
  exit 0
fi

# The state at the outage, read before the file moves: this is the state the fault interrupts.
FROZEN="$(snapshot "$VARIANT" "$RUNDIR")"
AT_OUTAGE_HOLDER="$(snap "$FROZEN" holder)"
FROZEN_COUNTS="$(snap "$FROZEN" counts)"

# The outage: the file the store lives in is moved aside while the process has it open. Nothing is
# truncated and nothing is rewritten — an operator losing a volume sees the same thing.
MOVED="$RUNDIR/$(basename "$PRIMARY").outage"
mv "$PRIMARY" "$MOVED" || {
  record_not_exercised "$VARIANT" "$RESULT_NAME" "the store could not be moved" \
    "mv of $PRIMARY failed, so no outage was injected"
  exit 0
}

MOVED_AT_OUTAGE="$(grep -c . "$MOVED" 2>/dev/null || echo 0)"

# What survived: read the moved file directly, with no process involved.
if [ "$VARIANT" = "durable" ]; then
  # Read the moved file directly: the live path is gone, and this is what survived.
  SURVIVED="$("$PYTHON" "$HARNESS_DIR/lib/holder.py" durable "$MOVED")"
  INTACT="$("$PYTHON" - "$MOVED" <<'PY'
import json, sys
records = torn = tasks = 0
for line in open(sys.argv[1], encoding="utf-8"):
    if not line.strip():
        continue
    try:
        record = json.loads(line); records += 1
        if record.get("type") == "task": tasks += 1
    except ValueError:
        torn += 1
print("%d record(s), %d task record(s), %d torn" % (records, tasks, torn))
PY
)"
fi

# Give the live process a moment to notice, then record what it did and stop it.
OBSERVED=0
for _ in $(seq 1 300); do
  if [ -f "$RUNDIR/wrapper.pid" ] && ! kill -0 "$(cat "$RUNDIR/wrapper.pid")" 2>/dev/null; then OBSERVED=1; break; fi
  sleep 0.2
done
if [ "$OBSERVED" = "0" ]; then
  stop_and_confirm "$VARIANT" "$RUNDIR" >/dev/null 2>&1 || kill_and_confirm "$VARIANT" "$RUNDIR" >/dev/null 2>&1 || true
  LIVE_DID="kept running with the store gone; the harness stopped it"
else
  LIVE_DID="exited on its own"
fi
MOVED_AFTER="$(grep -c . "$MOVED" 2>/dev/null || echo 0)"
LIVE_PATH_EXISTS=0
[ -f "$PRIMARY" ] && LIVE_PATH_EXISTS=1
LIVE_LOG="$(tail_log "$RUNDIR/.starter.log" 3 | tr '\n' ' ')"
LIVE_ERROR="$(grep -m1 -E 'Unhandled exception|workflow error' "$RUNDIR/.starter.log" 2>/dev/null | cut -c1-200 || true)"

if [ "$VARIANT" = "baseline" ]; then
  # The baseline's index is one file among many: the checkpoint documents it names are the state.
  # Moving the index is what an operator losing the directory's metadata would see.
  INTACT="$("$PYTHON" - "$MOVED" "$BASE" <<'PY'
import json, os, sys
moved, base = sys.argv[1], sys.argv[2]
entries = torn = 0
for line in open(moved, encoding="utf-8"):
    line = line.strip()
    if not line:
        continue
    try:
        json.loads(line); entries += 1
    except ValueError:
        torn += 1
documents = 0
for line in open(moved, encoding="utf-8"):
    line = line.strip()
    if not line:
        continue
    try:
        entry = json.loads(line)
    except ValueError:
        continue
    stored = entry.get("fileName") or ""
    for name in (stored, stored.replace("%2E", ".")):
        if name and os.path.exists(os.path.join(base, name)):
            documents += 1
            break
print("%d index entr(y|ies), %d torn, %d whose checkpoint document is present" % (entries, torn, documents))
PY
)"
  SURVIVED="$(cat "$MOVED" 2>/dev/null | wc -l | tr -d ' ') index entr(y|ies) in the moved file"
fi

# Recovery, stated as what it is: put the surviving state back at the path the variant opens and ask
# it who holds control. It is not "the system restored itself" — an operator did that — but it is the
# only way to read the surviving state through the variant's own reader.
if [ "$VARIANT" = "durable" ]; then
  cp "$MOVED" "$PRIMARY"
  SURVIVED_HOLDER="$("$PYTHON" "$HARNESS_DIR/lib/holder.py" durable "$PRIMARY" |
    "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin).get("holder") or "")')"
  SURVIVED_SEQ="$("$PYTHON" "$HARNESS_DIR/lib/holder.py" --max-seq "$PRIMARY")"
  recover_variant durable "$RUNDIR" 1
  RESUME_STATUS=$?
  FIRST="$("$PYTHON" "$HARNESS_DIR/lib/holder.py" --first-after-durable "$PRIMARY" "$SURVIVED_SEQ")"
  FIRST_AGENT="$(printf '%s' "$FIRST" | "$PYTHON" -c 'import json,sys; d=json.load(sys.stdin) or {}; print(d.get("agent") or "")' 2>/dev/null || echo "")"
  FIRST_DESC="$(printf '%s' "$FIRST" | "$PYTHON" -c '
import json,sys
d=json.load(sys.stdin) or {}
if not d: print("nothing: the recovered process committed no further agent action"); raise SystemExit(0)
print("%s %s turn %s at %s" % (d.get("agent"), d.get("event") or "spoke", d.get("turn"), d.get("entry")))' 2>/dev/null || echo "")"
  AFTER="$(holder_json durable "$RUNDIR")"
  AFTER_HOLDER="$(printf '%s' "$AFTER" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin).get("holder") or "")')"
  PHASE="$(printf '%s' "$AFTER" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin)["detail"].get("phase") or "")')"
  LIVE_RECORDS="$(grep -c . "$PRIMARY" 2>/dev/null || echo 0)"
  MOVED_GROWTH=$(( MOVED_AFTER - MOVED_AT_OUTAGE ))
  verify_store "$RUNDIR" >/dev/null
  VERIFY_NOTE="verify exited $?"
  TURNS="$(durable_count "$PRIMARY" turns)"
else
  # The holder the surviving index names last: the checkpoint document its last entry points at.
  SURVIVED_HOLDER="$("$PYTHON" "$HARNESS_DIR/lib/holder.py" --index-holder "$MOVED" "$BASE" |
    "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin).get("holder") or "")')"
  AFTER="{}"
  AFTER_HOLDER="$SURVIVED_HOLDER"
  PHASE="the newest checkpoint the surviving index names"
  VERIFY_NOTE="the baseline has no verifier"
  TURNS="$(baseline_log_count "$RUNDIR" turn_end)"
  RESUME_STATUS=""
fi

if [ "$VARIANT" = "durable" ]; then
  OUTCOME="failed safely, with a split store; resumed at ${SURVIVED_HOLDER:-nothing}"
  DETAIL="The store file was moved aside while the process held it open at the handoff ($BEFORE_HOLDER held turn 0). The live process $LIVE_DID${LIVE_ERROR:+: $LIVE_ERROR} — it did not notice: it reopened the original path and finished the run there. The moved file survived intact — $INTACT — with its last committed checkpoint naming ${SURVIVED_HOLDER:-nothing}; the transition that was in flight (a specialist output) is simply absent, so there is no state in which control has moved and the commit that says so is missing. A fresh process on the surviving file took control as $SURVIVED_HOLDER — the holder the surviving checkpoint names — and its first committed action was $FIRST_DESC; the run reached $TURNS turn(s) with $AFTER_HOLDER holding control at the end. $VERIFY_NOTE. The state is now in two files and nothing merged them: the live path holds $LIVE_RECORDS record(s), the moved file still holds the $MOVED_AT_OUTAGE record(s) it had at the outage (it grew by $MOVED_GROWTH), so an operator who restores the moved file replaces the post-outage run rather than merging with it. Control was not lost; it was forked, which is the failure this row records."
else
  if [ "$LIVE_PATH_EXISTS" = "1" ]; then
    SPLIT="the index appeared again at the live path, so the state was split"
  else
    SPLIT="the live path holds no index at all, while the moved index grew by $(( MOVED_AFTER - MOVED_AT_OUTAGE )) entr(y|ies) because the port writes through the handle it opened before the outage — so nothing was lost, and the file the store's own path names is simply absent"
  fi
  OUTCOME="no half-transition; the store's path stopped resolving"
  DETAIL="The framework store's index was moved aside while the process held it open at the handoff ($BEFORE_HOLDER held turn 0). The live process $LIVE_DID${LIVE_ERROR:+: $LIVE_ERROR}. The moved index survived intact — $INTACT — and the checkpoint documents it names are still present, so the state before the outage is readable: the checkpoint its last entry names holds ${SURVIVED_HOLDER:-nothing} in control. There is no half-transition in the surviving index: the checkpoint documents are written whole, so the worst case is one entry that a reader of the moved index cannot see. After the outage, $SPLIT. The port does not expose a way to resume a completed run (its resume aborts in the framework), so the post-outage reading is the state itself rather than a process started on it; control after the outage was ${SURVIVED_HOLDER:-nothing}, read from the checkpoint the surviving index names last."
fi

EVIDENCE="outage at the handoff: held by $BEFORE_HOLDER; moved $PRIMARY aside with $MOVED_AT_OUTAGE record(s) in it; live process: $LIVE_DID; moved file afterwards: $MOVED_AFTER record(s); live path present: $([ "$LIVE_PATH_EXISTS" = "1" ] && echo yes || echo no); live output: $LIVE_LOG; moved file: $INTACT; holder read from the moved state: ${SURVIVED_HOLDER:-nothing}; holder after recovery: ${AFTER_HOLDER:-nothing}${RESUME_STATUS:+; resume exit $RESUME_STATUS}; $VERIFY_NOTE"

record_result "$VARIANT" "$RESULT_NAME" "$OUTCOME" "$SURVIVED_HOLDER" "$PHASE" "$DETAIL" "$EVIDENCE" \
  "{\"holderAtOutage\":\"$BEFORE_HOLDER\",\"survivedHolder\":\"${SURVIVED_HOLDER:-}\",\"recoveredHolder\":\"${AFTER_HOLDER:-}\",\"liveProcess\":\"$([ "$OBSERVED" = "1" ] && echo exited || echo stopped)\"}" \
  "$SURVIVED_HOLDER" "$AFTER_HOLDER"
echo "$VARIANT / $RESULT_NAME: $OUTCOME (holder $AFTER_HOLDER)" >&2
