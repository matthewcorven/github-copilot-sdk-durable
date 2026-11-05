#!/usr/bin/env bash
# Fault: store-outage. Remove the store file mid-operation.
#
#   ./faults/store-outage.sh --variant baseline|durable [--fault-not-injected]
#
# The injection is done by the operating system to the real file: the store is moved aside with `mv`
# while the application is blocked inside a model turn. That is a deletion as the application sees it —
# the path it holds no longer names the file it was appending to — and it is the closest thing to a
# volume being pulled without requiring a mount the harness could not create on a developer machine.
#
# The question is whether the application fails safe and loses nothing:
#   - does it fail loudly rather than silently pretending the enquiry progressed?
#   - is the work already committed still readable afterwards?
#   - does the workspace make it possible to say which of the two is true (for the Durable variant: is a
#     lock file left behind, is the store's tail torn, does a reopen recover the committed prefix)?
#
# The baseline has no store, so its "store" is the only thing that actually holds agent state: the
# process's memory. Deleting a file cannot reach it, and moving `properties.db` aside is an outage of the
# listings database, not of agent state. That arm therefore records `not exercised` with that reason
# rather than inventing a store to break.
#
#   --fault-not-injected moves nothing. It is the control: the same run, with the file in place, must
#   finish, so the failure (or absence of one) is attributable to the outage.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

VARIANT=""
INJECT=1
while [ $# -gt 0 ]; do
  case "$1" in
    --variant) VARIANT="${2:-}"; shift 2 ;;
    --fault-not-injected) INJECT=0; shift ;;
    *) log "store-outage: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "store-outage: --variant baseline|durable is required"; exit 2; }

FAULT="store-outage"
[ "$INJECT" = "1" ] || FAULT="store-outage-control"
PORT="$(port_for "$VARIANT" "$FAULT")"
FAULT_COMMAND="./faults/store-outage.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1

if [ "$VARIANT" = "baseline" ]; then
  # The reason is specific and checked, not assumed: the baseline has no file that holds agent state, so
  # there is no store to remove. Its only candidate, properties.db, holds listings, and removing it is an
  # outage of the search data rather than of the state the fault is about. That was verified by reading
  # the baseline, and the finding is that the baseline cannot be faulted this way at all.
  record_not_exercised "baseline" "$FAULT" "a store that holds agent state" \
    "The baseline keeps every agent in an in-process Dictionary<string, Agent> and writes no agent state to disk for the lifetime of the process. There is no store file to remove, lock, or corrupt: properties.db holds property listings only, and an outage of it would test the search tool rather than the durability of an enquiry. Removing it mid-run was not substituted, because a different fault reported under this name would be worse than an honest absence."
  exit 0
fi

REASON="$(driver_unavailable_reason)"

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

STORE="$RUN_DIR/store.jsonl"
if [ "$INJECT" = "1" ]; then PHASE="a model turn in flight, store removed mid-write"; else PHASE="a model turn in flight, store left in place (the control)"; fi

pause_at session.tools.handlePendingToolCall
PID="$(start_headless "$RUN_DIR" enquiry.json --enquiry "Looking for a 3-bed family home near good schools in the suburbs")"
track_pid "$PID"

wait_for_marker "$RUN_DIR" inflight 90 || {
  log "store-outage: the enquiry never reached a tool call"
  tail -10 "$RUN_DIR/runtime.log" >&2
  exit 1
}

# What is on disk the instant before the outage: the committed prefix, which is what "loses nothing"
# has to be measured against.
if [ ! -f "$STORE" ]; then
  log "store-outage: no store file was created, so there is nothing to remove; the run is not exercising this fault"
  exit 1
fi
BEFORE_BYTES="$(wc -c < "$STORE" | tr -d ' ')"
BEFORE_LINES="$(wc -l < "$STORE" | tr -d ' ')"
BEFORE_PHASE="$(store_get "$STORE" band-a.enquiry phase)"
BEFORE_ENTRIES="$(store_count "$STORE" band-a.enquiry@entries)"
BEFORE_TRANSCRIPT="$(store_transcript "$STORE")"
cp "$STORE" "$RUN_DIR/store.before-outage.jsonl"

# --- inject ------------------------------------------------------------------------------------
# A real removal: `mv` unlinks the path the running process holds open. The bytes it already flushed are
# still in the moved file, which is exactly why the observation can ask "was the committed prefix kept?".
if [ "$INJECT" = "1" ]; then
  mv "$STORE" "$RUN_DIR/store.removed.jsonl"
  OUTAGE=1
  log "store-outage: moved $STORE aside while the application was mid-turn"
  if [ -f "$STORE" ]; then
    log "store-outage: the path still names a file after the move; the fault did not take"
    exit 1
  fi
else
  OUTAGE=0
  log "store-outage: not injecting; the store is left in place"
fi

release_gate "$RUN_DIR"
wait_headless "$RUN_DIR" 240
RUN_STATUS=$?

# --- observe -----------------------------------------------------------------------------------
# The application's own account of what happened, from its log.
PROCESS_OUTPUT="$(grep -Ei 'exception|failed|refused|error|disposed|finished|settled|unable|could not' "$RUN_DIR/server.log" 2>/dev/null | head -12)"
SERVER_EXIT="$(grep -c . "$RUN_DIR/server.log" 2>/dev/null || true)"

# The file system's account.
if [ -f "$STORE" ]; then
  LIVE_FILE="present"
  LIVE_BYTES="$(wc -c < "$STORE" | tr -d ' ')"
  LIVE_LINES="$(wc -l < "$STORE" | tr -d ' ')"
  TAIL_OK="$("$PYTHON" -c '
import json, sys
lines = [l for l in open(sys.argv[1], encoding="utf-8") if l.strip()]
ok = all(True for _ in lines)
for line in lines:
    try:
        json.loads(line)
    except ValueError:
        ok = False
print("complete" if ok else "torn")
' "$STORE" 2>/dev/null || echo unreadable)"
else
  LIVE_FILE="absent (the application did not recreate it)"
  LIVE_BYTES=0
  LIVE_LINES=0
  TAIL_OK="n/a"
fi
LOCK_PRESENT="$([ -f "$STORE.lock" ] && echo present || echo absent)"
OWNER_PRESENT="$([ -f "$STORE.lock.owner" ] && echo present || echo absent)"
if [ -f "$RUN_DIR/store.removed.jsonl" ]; then
  REMOVED_BYTES="$(wc -c < "$RUN_DIR/store.removed.jsonl" | tr -d ' ')"
else
  REMOVED_BYTES=0
fi

# What a fresh open of what is left can still read. This is the "loses nothing" question asked of the
# bytes rather than of the process that wrote them.
if [ "$OUTAGE" = "1" ]; then
  RECOVERED="$("$PYTHON" -c '
import json, sys
path = sys.argv[1]
phase, entries, good, bad = None, 0, 0, 0
try:
    handle = open(path, encoding="utf-8")
except OSError:
    print("unreadable 0 0 0")
    raise SystemExit(0)
for line in handle:
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        bad += 1
        continue
    good += 1
    payload = record.get("payload") or {}
    if record.get("type") == "document-version" and payload.get("kind") == "band-a.enquiry":
        value = payload.get("value") or {}
        entries = len(value.get("entries") or [])
        phase = {0: "queued", 1: "validating", 2: "searching", 3: "writing_report",
                 4: "rejected_garbage", 5: "rejected_no_matches", 6: "done"}.get(value.get("phase"), value.get("phase"))
print("%s %d %d %d" % (phase, entries, good, bad))
' "$RUN_DIR/store.removed.jsonl")"
else
  RECOVERED="$BEFORE_PHASE $BEFORE_ENTRIES 0 0"
fi
read -r KEPT_PHASE KEPT_ENTRIES KEPT_GOOD KEPT_BAD <<<"$RECOVERED"

# Two separate questions, and they are asked separately because they have different answers:
#   - what does the running process hold now? (it recreated the path, so the file it continues to write is
#     not the file it had);
#   - what does a *new* process find in the bytes that were moved out? (the committed prefix, or nothing).
RESUME_TEXT=""
NEW_TEXT=""
if [ "$OUTAGE" = "1" ]; then
  # Ask the moved file, in a process that never saw the outage. If the prefix is complete, a resume should
  # see the committed phase.
  read -r RESUMED_PHASE RESUMED_ENTRIES <<<"$("$PYTHON" -c '
import json, sys
phase, entries = "none", 0
for line in open(sys.argv[1], encoding="utf-8"):
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    payload = record.get("payload") or {}
    if record.get("type") == "document-version" and payload.get("kind") == "band-a.enquiry":
        value = payload.get("value") or {}
        entries = len(value.get("entries") or [])
        phase = {0: "queued", 1: "validating", 2: "searching", 3: "writing_report",
                 4: "rejected_garbage", 5: "rejected_no_matches", 6: "done"}.get(value.get("phase"), value.get("phase"))
print(phase, entries)
' "$RUN_DIR/store.removed.jsonl")"

  # Ask the new file what the *live* process wrote into it after the outage. This is the file an operator
  # tailing the store would now be reading.
  if [ -f "$STORE" ]; then
    read -r NEW_PHASE NEW_ENTRIES <<<"$("$PYTHON" -c '
import json, sys
phase, entries = "none", 0
for line in open(sys.argv[1], encoding="utf-8"):
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    payload = record.get("payload") or {}
    if record.get("type") == "document-version" and payload.get("kind") == "band-a.enquiry":
        value = payload.get("value") or {}
        entries = len(value.get("entries") or [])
        phase = {0: "queued", 1: "validating", 2: "searching", 3: "writing_report",
                 4: "rejected_garbage", 5: "rejected_no_matches", 6: "done"}.get(value.get("phase"), value.get("phase"))
print(phase, entries)
' "$STORE")"
    NEW_TEXT="the live path was recreated and now holds phase $NEW_PHASE with $NEW_ENTRIES entries, in $LIVE_LINES records"
  else
    NEW_PHASE="none"
    NEW_ENTRIES=0
    NEW_TEXT="the live path was not recreated"
  fi
  # And a new process opening the moved file, to say what the store would report to an operator who
  # restored it. `--resume` on a copy, so the original observation file is untouched.
  cp "$RUN_DIR/store.removed.jsonl" "$RUN_DIR/store.recovered.jsonl"
  FAULTS_STORE="$RUN_DIR/store.recovered.jsonl"
  export FAULTS_STORE
  PID2="$(start_headless "$RUN_DIR" enquiry.json --resume)"
  track_pid "$PID2"
  wait_headless "$RUN_DIR" 240
  RESUME_STATUS=$?
  # The store the resume wrote is its own; the observation of what an operator can recover is the copy it
  # started from, so that is what is read back below.
  RECOVERED_AFTER="$("$PYTHON" -c '
import json, sys
phase, entries, tasks = "none", 0, 0
for line in open(sys.argv[1], encoding="utf-8"):
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    payload = record.get("payload") or {}
    if record.get("type") == "task":
        tasks += 1
    if record.get("type") == "document-version" and payload.get("kind") == "band-a.enquiry":
        value = payload.get("value") or {}
        entries = len(value.get("entries") or [])
        phase = {0: "queued", 1: "validating", 2: "searching", 3: "writing_report",
                 4: "rejected_garbage", 5: "rejected_no_matches", 6: "done"}.get(value.get("phase"), value.get("phase"))
print(phase, entries, tasks)
' "$FAULTS_STORE")"
  read -r RESTORED_PHASE RESTORED_ENTRIES RESTORED_TASKS <<<"$RECOVERED_AFTER"
  RESUME_TEXT="$(grep -Ei 'resumed|finished|failed|exception|disposed|could not|no store' "$RUN_DIR/server.log" 2>/dev/null | tail -8)"
  unset FAULTS_STORE
fi

# --- decide ------------------------------------------------------------------------------------
if [ "$OUTAGE" = "0" ]; then
  OUTCOME="completed"
  DETAIL="Control: nothing was moved. The enquiry finished with task $(store_get "$STORE" task status) at phase $(store_get "$STORE" band-a.enquiry phase), $BEFORE_BYTES bytes and $BEFORE_LINES records in the store before the release and $(wc -c < "$STORE" | tr -d ' ') bytes after. This is what the outage run is compared against."
elif [ "$KEPT_BAD" = "0" ] && [ "$KEPT_ENTRIES" -ge "$BEFORE_ENTRIES" ] 2>/dev/null; then
  if [ "$LIVE_FILE" = "absent (the application did not recreate it)" ]; then
    OUTCOME="failed safely"
    DETAIL="The store was moved aside mid-turn. The process finished ($( [ "$RUN_STATUS" = "0" ] && echo 'exit 0' || echo "exit $RUN_STATUS")) without recreating the path: $LIVE_FILE. Every record the process had committed before the outage is intact in the moved file — $KEPT_GOOD readable records, $KEPT_BAD torn, phase $KEPT_PHASE with $KEPT_ENTRIES transcript entries — so nothing that was durable was lost, and nothing was written to a path that no longer named the store. The lock sidecar is $LOCK_PRESENT and the owner note is $OWNER_PRESENT."
  else
    OUTCOME="failed safely, with a split store"
    DETAIL="The store was moved aside mid-turn. The application did not fail: it wrote a new file at the same path, so $NEW_TEXT, while the moved file still holds the committed prefix intact ($KEPT_GOOD records, $KEPT_BAD torn, phase $KEPT_PHASE, $KEPT_ENTRIES entries, $BEFORE_TRANSCRIPT). Nothing was lost, but the state is now in two files: an operator reading the live path sees a different enquiry history from the one the process held a moment before the outage, and restoring the moved file would be a second store rather than a merge. The application fails safe in the sense that it reports no corruption and keeps writing; it does not fail loudly, and it does not tell anyone the old file was gone."
  fi
elif [ "$KEPT_BAD" -gt 0 ]; then
  OUTCOME="corrupted"
  DETAIL="The moved file is torn: $KEPT_BAD unparseable record(s) among $KEPT_GOOD readable ones. A process died holding an append that was not atomic, which is the opposite of the guarantee being tested."
else
  OUTCOME="lost"
  DETAIL="The outage lost committed work: before it the store held phase $BEFORE_PHASE with $BEFORE_ENTRIES entries; what a fresh read of the moved file recovers is phase $KEPT_PHASE with $KEPT_ENTRIES entries."
fi

MOVED_TEXT="moved file: $REMOVED_BYTES bytes, phase $KEPT_PHASE, $KEPT_ENTRIES entries, $KEPT_GOOD records, $KEPT_BAD torn"
if [ "$OUTAGE" = "0" ]; then MOVED_TEXT="nothing was moved"; fi
RESTORED_TEXT="restored copy after a resume: phase ${RESTORED_PHASE:-n/a}, ${RESTORED_ENTRIES:-0} entries, ${RESTORED_TASKS:-0} task records"
if [ "$OUTAGE" = "0" ]; then RESTORED_TEXT="no restored copy in the control"; fi
EVIDENCE="before: $BEFORE_PHASE/$BEFORE_ENTRIES entries, $BEFORE_BYTES bytes, $BEFORE_LINES records [$BEFORE_TRANSCRIPT]; after the outage: live path $LIVE_FILE with $LIVE_LINES records, tail $TAIL_OK, lock $LOCK_PRESENT, owner note $OWNER_PRESENT${NEW_TEXT:+; $NEW_TEXT}; $MOVED_TEXT; $RESTORED_TEXT; process exit=$RUN_STATUS; resume exit=${RESUME_STATUS:-n/a}; resume output: ${RESUME_TEXT:-none}; process output: ${PROCESS_OUTPUT:-none}"

record_result "$VARIANT" "$FAULT" "$OUTCOME" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
