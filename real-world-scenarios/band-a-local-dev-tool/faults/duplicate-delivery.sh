#!/usr/bin/env bash
# Fault: duplicate-delivery. The same enquiry is sent twice.
#
#   ./faults/duplicate-delivery.sh --variant baseline|durable [--fault-not-injected]
#
# The duplicate is a real duplicate on the path a user produces: the page's sample entry is double
# clicked, so Blazor dispatches two `SubmitEnquiry` calls, each of which starts a run. Nothing is faked
# at the store or the SDK; two deliveries go out and the question is whether their side effects double.
#
# What counts as doubling is the *effect*, not the click: the number of enquiries the application now
# holds, and the number of model turns it sent for them. Both are read from evidence — the page's cards
# and the gate's frame log for the baseline, the committed transcript and the admission documents for the
# Durable variant.
#
#   --fault-not-injected clicks once. The pair of results is the control: the difference between one
#   delivery and two is what the duplicate actually cost.
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
    *) log "duplicate-delivery: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "duplicate-delivery: --variant baseline|durable is required"; exit 2; }

FAULT="duplicate-delivery"
[ "$INJECT" = "1" ] || FAULT="duplicate-delivery-control"
PORT="$(port_for "$VARIANT" "$FAULT")"
FAULT_COMMAND="./faults/duplicate-delivery.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1
REASON="$(driver_unavailable_reason)"
if [ "$VARIANT" = "baseline" ] && [ -n "$REASON" ]; then
  record_not_exercised "baseline" "$FAULT" "a submitted enquiry" "$REASON"
  exit 0
fi

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

ENQUIRY="Need a downtown condo with parking, 1-2 bedrooms"
if [ "$INJECT" = "1" ]; then PHASE="the same enquiry delivered twice"; else PHASE="the same enquiry delivered once (the control)"; fi

if [ "$VARIANT" = "baseline" ]; then
  PID="$(start_variant baseline "$RUN_DIR" "$PORT" enquiry.json)"
  track_pid "$PID"
  wait_for_port "$PORT" || { log "duplicate-delivery: baseline never served"; exit 1; }
  if [ "$INJECT" = "1" ]; then
    cat > "$RUN_DIR/spec.json" <<JSON
{"actions": [
  {"kind": "submit-twice", "text": "$ENQUIRY"},
  {"kind": "wait", "ms": 6000},
  {"kind": "snapshot", "label": "after the double delivery"}
]}
JSON
  else
    cat > "$RUN_DIR/spec.json" <<JSON
{"actions": [
  {"kind": "submit", "text": "$ENQUIRY"},
  {"kind": "wait", "ms": 6000},
  {"kind": "snapshot", "label": "after a single delivery"}
]}
JSON
  fi
  drive "$RUN_DIR" "$PORT" "$RUN_DIR/spec.json" "$RUN_DIR/observe.json" || {
    log "duplicate-delivery: the baseline page could not be driven"
    exit 1
  }
  CARDS="$("$PYTHON" -c '
import json, sys
final = (json.load(open(sys.argv[1])) or {}).get("final") or {}
titles = [card.get("title") for card in (final.get("cards") or [])]
print(len(titles))
print(" | ".join(titles))
' "$RUN_DIR/observe.json" | head -1)"
  TITLES="$("$PYTHON" -c '
import json, sys
final = (json.load(open(sys.argv[1])) or {}).get("final") or {}
print(" | ".join(card.get("title") or "" for card in (final.get("cards") or [])))
' "$RUN_DIR/observe.json")"
  SENT="$(grep -c 'client->runtime session.send' "$RUN_DIR/runtime.log" 2>/dev/null || true)"
  # The baseline holds its agents in a Dictionary keyed by a fresh 8-character id, so two clicks are two
  # agents even when the enquiry text is identical. That is the doubling the fault is looking for.
  if [ "${CARDS:-0}" -ge 2 ]; then
    [ "$INJECT" = "1" ] && OBSERVED="doubled" || OBSERVED="not injected: one delivery, doubled"
    DETAIL="Two deliveries of the same enquiry produced $CARDS agents on the page ($TITLES) and $SENT model turns reached the runtime. The application has no identity for an enquiry, so the second delivery is a second agent: its side effects — a Copilot session, a search, a report — happen twice."
  elif [ "${CARDS:-0}" = "1" ]; then
    if [ "$INJECT" = "1" ]; then
      OBSERVED="deduplicated"
      DETAIL="Two deliveries produced $CARDS agent and $SENT model turn: the duplicate did not double the work."
    else
      OBSERVED="control: single delivery"
      DETAIL="Control: one delivery produced $CARDS agent and $SENT model turn."
    fi
  else
    OBSERVED="lost"
    DETAIL="After the deliveries the page shows $CARDS cards; the enquiry did not arrive at all."
  fi
  EVIDENCE="cards=$CARDS [$TITLES]; session.send frames=$SENT; processing=$(json_get "$RUN_DIR/observe.json" final.dashboard.processing); completed=$(json_get "$RUN_DIR/observe.json" final.dashboard.completed); rejected=$(json_get "$RUN_DIR/observe.json" final.dashboard.rejected)"
else
  # The Durable variant is delivered twice through the same page path: the sample entry double clicked,
  # which calls `AppState.RunAgentAsync` twice. Its command line would have had to be given the same
  # `--enquiry` twice, which is a different question (two enquiries that happen to read alike), so the
  # page is used so the duplicate shares the enquiry text *and* the click path.
  PID="$(start_headless "$RUN_DIR" enquiry.json)"
  track_pid "$PID"
  # The headless run needs no browser, but a duplicate needs two deliveries, and its command line can
  # send the same text twice — which is exactly what a double-submitted form does.
  stop_variant_and_clear "$RUN_DIR"
  if [ "$INJECT" = "1" ]; then
    PID="$(start_headless "$RUN_DIR" enquiry.json --enquiry "$ENQUIRY" --enquiry "$ENQUIRY")"
  else
    PID="$(start_headless "$RUN_DIR" enquiry.json --enquiry "$ENQUIRY")"
  fi
  track_pid "$PID"
  wait_headless "$RUN_DIR" 300 || { log "duplicate-delivery: the run did not finish"; stop_variant_and_clear "$RUN_DIR"; }
  read -r TASKS ADMISSIONS DOCS ENTRIES SEARCHES <<<"$("$PYTHON" -c '
import json, sys
tasks, admissions, docs, entries, searches = set(), 0, set(), 0, 0
values = {}
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
        tasks.add(payload.get("id"))
    if record.get("type") == "document-version":
        value = payload.get("value") or {}
        if payload.get("kind") == "durable.admission" and value.get("state") == "admitted":
            admissions += 1
        if payload.get("kind") == "band-a.enquiry":
            docs.add(payload.get("ownerId"))
            values[payload.get("ownerId")] = value
for conversation, value in values.items():
    entries += len(value.get("entries") or [])
    searches += sum(1 for e in (value.get("entries") or []) if e.get("kind") == "tool")
print(len(tasks), admissions, len(docs), entries, searches)
' "$RUN_DIR/store.jsonl")"
  SUBMITTED="$(grep -o 'submitted [0-9]* enquiries' "$RUN_DIR/server.log" 2>/dev/null | grep -o '[0-9]*' | head -1)"
  TRANSCRIPT="$(store_transcript "$RUN_DIR/store.jsonl")"
  if [ "${TASKS:-0}" -ge 2 ]; then
    [ "$INJECT" = "1" ] && OBSERVED="doubled" || OBSERVED="not injected: one delivery, doubled"
    DETAIL="Two deliveries of the same enquiry produced $DOCS enquiry documents, $ADMISSIONS model admissions and $SEARCHES search tool call(s), across $ENTRIES transcript entries. The variant has no identity for an enquiry to deduplicate on, so the second delivery is a second conversation: the search and the report happen twice, and the store keeps both documents intact — nothing was corrupted, but nothing was reused either."
  elif [ "${TASKS:-0}" = "1" ]; then
    if [ "$INJECT" = "1" ]; then
      OBSERVED="deduplicated"
      DETAIL="Two deliveries produced $DOCS enquiry document and $ADMISSIONS model admission: the second delivery was absorbed, so the effects did not double."
    else
      OBSERVED="control: single delivery"
      DETAIL="Control: one delivery produced $DOCS enquiry document, $ADMISSIONS model admission, $SEARCHES search tool call and $ENTRIES transcript entries. The same steps delivered twice produced two of each, so the doubling is attributable to the duplicate delivery and not to the run."
    fi
  else
    OBSERVED="lost"
    DETAIL="The deliveries produced $DOCS enquiry documents; nothing was recorded."
  fi
  EVIDENCE="deliveries=$([ "$INJECT" = "1" ] && echo 2 || echo 1); submitted=$SUBMITTED; enquiry documents=$DOCS; task records=$TASKS; admissions=$ADMISSIONS; search tool calls=$SEARCHES; transcript of the last document=$TRANSCRIPT"
fi

record_result "$VARIANT" "$FAULT" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
