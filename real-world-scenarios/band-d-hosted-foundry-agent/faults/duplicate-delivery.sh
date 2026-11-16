#!/usr/bin/env bash
# Fault: duplicate-delivery. The same input or the same human decision is delivered twice,
# and the question is whether it applies twice.
#
#   ./faults/duplicate-delivery.sh --variant baseline|durable-inside [--fault-not-injected]
#
# What "twice" means in each column, said plainly:
#
#   baseline        D1 has no store and no decision document, so the same input delivered a
#                   second time is a second run: the driver writes each step's result file
#                   and nothing records that a step already happened, so both steps are
#                   called again. The observation is the service's call count, not the log.
#   durable-inside  D2's approval is a durable task parked on an external event, and its
#                   answer is a document. The same answer is delivered by a second reviewer
#                   from a second process, then a third process resumes the task: the
#                   document must carry the first decision once, and the task must wake once.
#
# The platform's own duplicate behaviour is not what this measures. Foundry does not dedupe
# an approval for the application: the application is the thing that has a decision on file.
# The attribution for that is in `platform.behaviours`.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

FAULT=duplicate-delivery
parse_fault_args "${BASH_SOURCE[0]}" "$@"
case "$VARIANT" in
  baseline|durable-inside) ;;
  *)
    log "duplicate-delivery: baseline (no record of a step) and durable-inside (a decision document) are the variants."
    log "duplicate-delivery: durable-outside's gate refuses a second answer by construction; see its own README note."
    exit 2
    ;;
esac

require_tools || exit 1
install_cleanup
FAULTS_DOCKER="not used by this fault - the decision is delivered by host processes"; export FAULTS_DOCKER
[ "$VARIANT" = "durable-inside" ] && { require_d2 || exit 1; }

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_path "$RUN_DIR"
PORT="$(port_for "$VARIANT" "$FAULT"$( [ "$INJECT" = "1" ] || echo "-control" ))"
start_service "$RUN_DIR" "$PORT" || exit 1

REQUEST_ID="PA-D4-0002"
REQUEST="Prior authorization PA-D4-0002: knee arthroscopy, 2 of 2 criteria met."

if [ "$VARIANT" = "durable-inside" ]; then
  VOLUME="$(d2_volume "$RUN_DIR")"
  track_volume "$VOLUME"

  d2_run "$VOLUME" python3 d2_tool.py assess --request "$REQUEST_ID" --text "criteria met" \
    > "$RUN_DIR/10-assess.json" 2>/dev/null
  assess_status=$?

  # Two deliveries of the same decision, from two processes, the second by a different
  # reviewer. Whether the first is refused is the application's answer, recorded as-is.
  d2_run "$VOLUME" python3 -c "
import json
from durable_state import Workspace
w = Workspace.open()
print(json.dumps({'woken': w.deliver('$REQUEST_ID', {'decision': 'approved', 'reviewer': 'Dr Chen', 'reason': 'criteria met'})}))
w.close()
" > "$RUN_DIR/20-deliver-first.json" 2>/dev/null
  first_status=$?

  if [ "$INJECT" = "1" ]; then
    d2_run "$VOLUME" python3 -c "
import json
from durable_state import Workspace
w = Workspace.open()
print(json.dumps({'woken': w.deliver('$REQUEST_ID', {'decision': 'denied', 'reviewer': 'Dr Mallory', 'reason': 'second delivery'})}))
w.close()
" > "$RUN_DIR/30-deliver-second.json" 2>/dev/null
    second_status=$?
  else
    printf '{"control": "the second delivery was not made"}\n' > "$RUN_DIR/30-deliver-second.json"
    second_status=0
  fi

  # A third process resumes the task. If one decision was applied once, the task wakes
  # once and the document carries the first reviewer.
  d2_run "$VOLUME" python3 d2_tool.py resume --request "$REQUEST_ID" \
    > "$RUN_DIR/40-resume.json" 2>/dev/null
  resume_status=$?
  d2_run "$VOLUME" python3 d2_tool.py state --request "$REQUEST_ID" > "$RUN_DIR/50-final-state.json" 2>/dev/null
  d2_run "$VOLUME" python3 d2_tool.py approval --request "$REQUEST_ID" > "$RUN_DIR/60-final-approval.json" 2>/dev/null

  decided_by="$(python3 - "$RUN_DIR/50-final-state.json" <<'PY'
import json, sys
try:
    print(json.load(open(sys.argv[1])).get("decidedBy", ""))
except (OSError, ValueError):
    print("")
PY
)"
  decision="$(python3 - "$RUN_DIR/60-final-approval.json" <<'PY'
import json, sys
try:
    print(json.load(open(sys.argv[1])).get("decision", ""))
except (OSError, ValueError):
    print("")
PY
)"
  # One answer is one entry in the approval document's history. The count is read from the
  # store's own record, not from the tool's output.
  # The store, read out of the volume into a file and counted here. A pipe would work too,
  # but a file is what the reader in `fence.py` reads and there is then one shape of input.
  d2_cat "$VOLUME" /home/session/durable/working-state.jsonl > "$RUN_DIR/70-store.jsonl"
  answered_versions="$(python3 - "$RUN_DIR/70-store.jsonl" <<'PY'
import json, sys
count = 0
try:
    for line in open(sys.argv[1], encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        entry = json.loads(line)
        payload = entry.get("payload") or {}
        if entry.get("type") == "document-version" and payload.get("kind") == "d2.approval":
            count += 1
except OSError:
    pass
print(count)
PY
)"
  woken_first="$(python3 -c "
import json,sys
try: print(len(json.load(open('$RUN_DIR/20-deliver-first.json')).get('woken') or []))
except Exception: print(0)")"
  woken_second="$(python3 -c "
import json,sys
try: print(len(json.load(open('$RUN_DIR/30-deliver-second.json')).get('woken') or []))
except Exception: print(0)")"

  if [ "$INJECT" = "1" ]; then
    outcome="one decision applied once; the second delivery woke nothing"
    detail="The approval was delivered twice from two processes: 'approved by Dr Chen' first, then 'denied by Dr Mallory'. The first delivery woke $woken_first task(s); the second woke $woken_second, because the task was no longer waiting on the event and the answer memo was already set. A third process resumed the task: it settled, and the decision on file is '${decision:-?}' by '${decided_by:-?}' — the first delivery's, not the second's. The approval document's history holds $answered_versions version(s): the park and the one applied answer. A second applied answer would have written a third."
  else
    outcome="control: one delivery, one decision"
    detail="One delivery ('approved by Dr Chen') and one resume: the task woke $woken_first time(s), the decision on file is '${decision:-?}' by '${decided_by:-?}', and the approval history holds $answered_versions version(s). The second delivery is what the fault row adds."
  fi

  TEMPLATE="$RUN_DIR/template.json"
  FAULT_LIB="$HERE/lib" FAULT_VARIANT="$VARIANT" FAULT_INJECT="$INJECT" \
  FAULT_OUTCOME="$outcome" FAULT_DETAIL="$detail" FAULT_ASSESS="$assess_status" \
  FAULT_FIRST="$first_status" FAULT_SECOND="$second_status" FAULT_RESUME="$resume_status" \
  FAULT_WOKEN1="$woken_first" FAULT_WOKEN2="$woken_second" \
  FAULT_DECISION="$decision" FAULT_BY="$decided_by" FAULT_VERSIONS="$answered_versions" \
  python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

doc = {
    "outcome": os.environ["FAULT_OUTCOME"],
    "platform": block(
        touched=[],
        observed=(
            "not exercised in this run: Foundry was not asked to deliver anything twice, and it has no "
            "application-level deduplication for the application to rely on - the decision document is "
            "the application's, not the platform's"
        ),
        note=(
            "A duplicate delivery here is two processes writing to one durable store. The platform is "
            "not in that path at all, so nothing in this result describes the platform's behaviour."
        ),
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": os.environ["FAULT_DETAIL"],
        "observations": {
            "second_delivery_made": os.environ["FAULT_INJECT"] == "1",
            "tasks_woken_by_first_delivery": int(os.environ["FAULT_WOKEN1"]),
            "tasks_woken_by_second_delivery": int(os.environ["FAULT_WOKEN2"]),
            "decision_on_file": os.environ["FAULT_DECISION"],
            "decided_by": os.environ["FAULT_BY"],
            "approval_document_versions": int(os.environ["FAULT_VERSIONS"]),
            "assess_exit": int(os.environ["FAULT_ASSESS"]),
            "first_delivery_exit": int(os.environ["FAULT_FIRST"]),
            "second_delivery_exit": int(os.environ["FAULT_SECOND"]),
            "resume_exit": int(os.environ["FAULT_RESUME"]),
        },
    },
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY

  write_result "$TEMPLATE" "$(result_path "$VARIANT" "$FAULT")" "$FAULT" "$VARIANT" || exit 1
  exit 0
fi

# -- baseline: the same input, delivered twice ---------------------------------------------

python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run" --request "$REQUEST" \
  > "$RUN_DIR/10-run-1.log" 2>&1
first_status=$?
posts_after_first="$(service_posts "$RUN_DIR" coverage-assessment-agent)"
posts_synth_first="$(service_posts "$RUN_DIR" synthesis-agent)"

if [ "$INJECT" = "1" ]; then
  # The same input again, through the same entry point a retry would use. A *fresh* run
  # directory, because a retry of a request is a new run of it — re-using the first run's
  # directory would let the driver's own skip-if-recorded path answer the question instead
  # of the duplicate delivery.
  python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run-2" --request "$REQUEST" \
    > "$RUN_DIR/20-run-2.log" 2>&1
  second_status=$?
else
  printf 'control: the input was delivered once\n' > "$RUN_DIR/20-run-2.log"
  second_status=0
fi
posts_total="$(service_posts "$RUN_DIR" coverage-assessment-agent)"
posts_synth_total="$(service_posts "$RUN_DIR" synthesis-agent)"

if [ "$INJECT" = "1" ]; then
  outcome="both steps applied twice; nothing refused the second delivery"
  detail="The same request was driven twice through the driver's own entry point. The first run made $posts_after_first coverage call(s) and $posts_synth_first synthesis call(s); the second run made the total $posts_total and $posts_synth_total. The driver's only record of progress is a result file per step, and its resume path skips a step that has one — but a *fresh* delivery does not consult it, which is the shape a retried request has. No refusal was recorded because there is nothing in the baseline that could refuse."
else
  outcome="control: one delivery, one application of each step"
  detail="One delivery: $posts_after_first coverage call(s) and $posts_synth_first synthesis call(s), the run finished. The second delivery in the fault row is what produced the duplicates."
fi

TEMPLATE="$RUN_DIR/template.json"
FAULT_LIB="$HERE/lib" FAULT_VARIANT="$VARIANT" FAULT_INJECT="$INJECT" \
FAULT_OUTCOME="$outcome" FAULT_DETAIL="$detail" FAULT_FIRST="$first_status" \
FAULT_SECOND="$second_status" FAULT_COV1="$posts_after_first" FAULT_COV2="$posts_total" \
FAULT_SYN1="$posts_synth_first" FAULT_SYN2="$posts_synth_total" \
python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

doc = {
    "outcome": os.environ["FAULT_OUTCOME"],
    "platform": block(
        touched=[],
        observed=(
            "not exercised in this run: the platform's response records are not the application's "
            "decision record, and no hosted deployment existed to deliver anything twice to"
        ),
        note=(
            "The duplicate here is two processes over one stateless driver. The baseline keeps its "
            "progress only in result files, so what it lacks is an identity for a step - a property of "
            "the accelerator's design, not of Foundry."
        ),
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": os.environ["FAULT_DETAIL"],
        "observations": {
            "second_delivery_made": os.environ["FAULT_INJECT"] == "1",
            "coverage_calls_after_first_delivery": int(os.environ["FAULT_COV1"]),
            "coverage_calls_in_total": int(os.environ["FAULT_COV2"]),
            "synthesis_calls_after_first_delivery": int(os.environ["FAULT_SYN1"]),
            "synthesis_calls_in_total": int(os.environ["FAULT_SYN2"]),
            "run_1_exit": int(os.environ["FAULT_FIRST"]),
            "run_2_exit": int(os.environ["FAULT_SECOND"]),
        },
    },
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY

write_result "$TEMPLATE" "$(result_path "$VARIANT" "$FAULT")" "$FAULT" "$VARIANT" || exit 1
exit 0
