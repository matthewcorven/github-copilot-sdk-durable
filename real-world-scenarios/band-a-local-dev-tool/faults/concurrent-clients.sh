#!/usr/bin/env bash
# Fault: concurrent-clients. Two enquiries for two customers mutate state at the same time.
#
#   ./faults/concurrent-clients.sh --variant baseline|durable [--fault-not-injected]
#
# "At the same time" is arranged, not hoped for: the first enquiry is driven to a tool call and held
# there by the gate, and the second is submitted while the first is blocked. Both are then in flight
# together, and the gate releases them. The observation is whether either enquiry's state survived — for
# the baseline, whether both agents are on the page and both reached `Done`; for the Durable variant,
# whether each conversation's transcript contains only its own entries and both tasks finished.
#
#   --fault-not-injected submits the two enquiries one after the other, with the first allowed to finish
#   first. That is the control: the same two enquiries, not concurrent, must produce the same *content*
#   and the counts are compared against each other.
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
    *) log "concurrent-clients: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "concurrent-clients: --variant baseline|durable is required"; exit 2; }

FAULT="concurrent-clients"
[ "$INJECT" = "1" ] || FAULT="concurrent-clients-not-injected"
PORT="$(port_for "$VARIANT" "$FAULT")"
FAULT_COMMAND="./faults/concurrent-clients.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1
REASON="$(driver_unavailable_reason)"
if [ "$VARIANT" = "baseline" ] && [ -n "$REASON" ]; then
  record_not_exercised "baseline" "$FAULT" "two enquiries in flight" "$REASON"
  exit 0
fi

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

STORE="$RUN_DIR/store.jsonl"
FIRST="Looking for a 3-bed family home near good schools in the suburbs"
SECOND="Need a downtown condo with parking, 1-2 bedrooms, under \$350k"
if [ "$INJECT" = "1" ]; then PHASE="two enquiries in flight together"; else PHASE="two enquiries submitted one after the other (the control)"; fi

if [ "$VARIANT" = "baseline" ]; then
  # The gate's pause must be in the environment before the server starts, or the server's gate is a plain
  # relay. The first enquiry is held inside a tool call, so the second click lands while the first agent
  # is mid-turn — two run tasks inside the same dictionary at the same moment, which is the race.
  if [ "$INJECT" = "1" ]; then pause_at session.tools.handlePendingToolCall; fi
  PID="$(start_variant baseline "$RUN_DIR" "$PORT" concurrent.json)"
  track_pid "$PID"
  wait_for_port "$PORT" || { log "concurrent-clients: baseline never served"; exit 1; }
  if [ "$INJECT" = "1" ]; then
    cat > "$RUN_DIR/spec.json" <<JSON
{"actions": [
  {"kind": "submit", "text": "$FIRST"},
  {"kind": "wait-for-card", "text": "3-bed family home", "count": 1, "timeoutMs": 30000},
  {"kind": "submit", "text": "$SECOND"},
  {"kind": "wait-dashboard", "expect": {"completed": 2}, "timeoutMs": 120000},
  {"kind": "snapshot", "label": "both finished"}
]}
JSON
  else
    cat > "$RUN_DIR/spec.json" <<JSON
{"actions": [
  {"kind": "submit", "text": "$FIRST"},
  {"kind": "wait-dashboard", "expect": {"completed": 1}, "timeoutMs": 120000},
  {"kind": "submit", "text": "$SECOND"},
  {"kind": "wait-dashboard", "expect": {"completed": 2}, "timeoutMs": 120000},
  {"kind": "snapshot", "label": "both finished"}
]}
JSON
  fi
  if [ "$INJECT" = "1" ]; then
    start_driver "$RUN_DIR" "$PORT" "$RUN_DIR/spec.json" "$RUN_DIR/observe.json" || {
      log "concurrent-clients: the baseline page could not be driven"
      exit 1
    }
    if ! wait_for_marker "$RUN_DIR" inflight 90; then
      log "concurrent-clients: the first enquiry never reached a tool call"
      exit 1
    fi
    release_gate "$RUN_DIR"
    wait "$(driver_pid "$RUN_DIR")"
    DRIVE_STATUS=$?
  else
    drive "$RUN_DIR" "$PORT" "$RUN_DIR/spec.json" "$RUN_DIR/observe.json"
    DRIVE_STATUS=$?
  fi
  read -r CARDS COMPLETED PROCESSING REJECTED <<<"$("$PYTHON" -c '
import json, sys
final = (json.load(open(sys.argv[1])) or {}).get("final") or {}
dash = final.get("dashboard") or {}
print(len(final.get("cards") or []), dash.get("completed"), dash.get("processing"), dash.get("rejected"))
' "$RUN_DIR/observe.json" 2>/dev/null || echo "0 0 0 0")"
  # A Dictionary mutated from two tasks at once does not fail quietly: it throws. The server's own log is
  # where that would appear, so it is read rather than assumed absent.
  FAULTS_IN_LOG="$(grep -c 'InvalidOperationException\|Collection was modified\|ObjectDisposedException\|Error running agent' "$RUN_DIR/server.log" 2>/dev/null || true)"
  SENT="$(grep -c 'client->runtime session.send' "$RUN_DIR/runtime.log" 2>/dev/null || true)"
  if [ "$DRIVE_STATUS" != "0" ]; then
    if [ "${FAULTS_IN_LOG:-0}" -gt 0 ]; then
      OBSERVED="corrupted"
      DETAIL="The two concurrent enquiries did not both settle: the driver failed and the server logged $FAULTS_IN_LOG exception/error line(s). That is the baseline's unprotected Dictionary<string, Agent> failing under concurrent mutation, observed rather than argued."
    else
      OBSERVED="incomplete"
      DETAIL="The two concurrent enquiries did not both settle within the window: cards=$CARDS completed=$COMPLETED processing=$PROCESSING. No exception was logged, so the failure is a stall rather than a detected race."
    fi
  elif [ "${CARDS:-0}" = "2" ] && [ "${COMPLETED:-0}" = "2" ]; then
    OBSERVED="not corrupted"
    if [ "$INJECT" = "1" ]; then
      DETAIL="Both enquiries completed with the first held inside a tool call when the second was submitted: $CARDS agents on the page, completed=$COMPLETED, rejected=$REJECTED, $SENT model turn(s) reached the runtime. In this sample the dictionary race did not manifest — the defect is present in the code (unlocked Dictionary<string, Agent> mutated from every run task), but one concurrent pair is not enough to make it throw. This row is a tie on behaviour and a difference only in the code."
    else
      DETAIL="Control: the same two enquiries, submitted one after the other. $CARDS agents, completed=$COMPLETED, rejected=$REJECTED, $SENT model turn(s). Same content as the concurrent run; a sequential pair cannot show the race, which is why the concurrent run exists."
    fi
  else
    OBSERVED="lost"
    DETAIL="Two enquiries were submitted together and the page shows $CARDS agents (completed=$COMPLETED, processing=$PROCESSING, rejected=$REJECTED): at least one enquiry's state did not survive."
  fi
  EVIDENCE="cards=$CARDS completed=$COMPLETED processing=$PROCESSING rejected=$REJECTED; session.send frames=$SENT; driver status=$DRIVE_STATUS; exception lines in the server log=$FAULTS_IN_LOG"
else
  # The headless driver's own shape is the fault: every enquiry is submitted before any is awaited, which
  # is the same fire-and-forget shape the baseline had. `--contend` is deliberately not used here; that
  # races writers at one document, which is the Durable variant's own stage 5, not this fault.
  if [ "$INJECT" = "1" ]; then
    # The first enquiry is held inside a tool call, so the second is submitted while the first is blocked
    # in a turn rather than merely queued behind it. Both are then released together.
    pause_at session.tools.handlePendingToolCall
    PID="$(start_headless "$RUN_DIR" concurrent.json --enquiry "$FIRST" --enquiry "$SECOND")"
  else
    # The control: sequential submission, one enquiry at a time, by running the driver twice.
    PID="$(start_headless "$RUN_DIR" concurrent.json --enquiry "$FIRST")"
  fi
  track_pid "$PID"
  if [ "$INJECT" = "1" ]; then
    wait_for_marker "$RUN_DIR" inflight 90 || { log "concurrent-clients: the first enquiry never reached a tool call"; tail -10 "$RUN_DIR/runtime.log" >&2; exit 1; }
    log "concurrent-clients: the first enquiry is blocked inside a tool call; releasing it"
    release_gate "$RUN_DIR"
  fi
  wait_headless "$RUN_DIR" 300 || { log "concurrent-clients: the run did not finish"; stop_variant_and_clear "$RUN_DIR"; }
  if [ "$INJECT" = "0" ]; then
    PID="$(start_headless "$RUN_DIR" concurrent.json --enquiry "$SECOND")"
    track_pid "$PID"
    wait_headless "$RUN_DIR" 300 || { log "concurrent-clients-control: the second run did not finish"; stop_variant_and_clear "$RUN_DIR"; }
  fi
  read -r DOCS TASKS TERMINAL ENTRIES CROSSED <<<"$("$PYTHON" -c '
import json, sys
docs, tasks, terminal, entries, crossed = set(), {}, set(), 0, 0
owners = {}
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
        tasks[payload.get("id")] = payload.get("status")
    if record.get("type") == "document-version" and payload.get("kind") == "band-a.enquiry":
        owner = payload.get("ownerId")
        docs.add(owner)
        owners[owner] = payload.get("value") or {}
    if record.get("type") == "entry-record":
        pass
for owner, value in owners.items():
    items = value.get("entries") or []
    entries += len(items)
    if all(item.get("entryId") for item in items):
        terminal.add(owner)
# Cross-talk check: every transcript entry of a document must belong to that document. The store writes
# each entry under its conversation id, so an entry that names another conversation in the same
# document is cross-talk.
named = {}
for line in open(sys.argv[1], encoding="utf-8"):
    line = line.strip()
    if not line:
        continue
    try:
        record = json.loads(line)
    except ValueError:
        continue
    payload = record.get("payload") or {}
    if record.get("type") == "entry-record":
        named[payload.get("id")] = payload.get("conversationId")
for owner, value in owners.items():
    for item in value.get("entries") or []:
        if named.get(item.get("entryId")) not in (None, owner):
            crossed += 1
print(len(docs), len(tasks), sum(1 for s in tasks.values() if s == 4), entries, crossed)
' "$STORE")"
  SEARCHES="$("$PYTHON" -c '
import json, sys
searches = 0
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
        searches += sum(1 for e in ((payload.get("value") or {}).get("entries") or []) if e.get("kind") == "tool")
print(searches)
' "$STORE")"
  if [ "$DOCS" = "2" ] && [ "${CROSSED:-1}" = "0" ] && [ "$TERMINAL" = "$TASKS" ]; then
    OBSERVED="not corrupted"
    DETAIL="$DOCS enquiry documents and $TASKS task records, all $TERMINAL terminal, $ENTRIES transcript entries and 0 entries crossing documents. The commit gate serialized the two writers, so neither document saw the other's entries."
  elif [ "${CROSSED:-0}" -gt 0 ]; then
    OBSERVED="corrupted"
    DETAIL="$DOCS documents held $CROSSED transcript entry/entries belonging to another conversation. That is cross-talk, and it is reported rather than explained away."
  else
    OBSERVED="lost"
    DETAIL="$DOCS documents, $TASKS tasks ($TERMINAL terminal), $ENTRIES entries. At least one enquiry did not finish as a separate document."
  fi
  EVIDENCE="documents=$DOCS; tasks=$TASKS; terminal=$TERMINAL; transcript entries=$ENTRIES; entries belonging to another conversation=$CROSSED; search tool calls=$SEARCHES"
fi

record_result "$VARIANT" "$FAULT" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
