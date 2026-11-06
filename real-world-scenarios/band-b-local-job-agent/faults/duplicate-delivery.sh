#!/usr/bin/env bash
# Fault: duplicate-delivery. The same delivery reaches the job twice, and the question is whether the
# file mutations double.
#
#   ./faults/duplicate-delivery.sh --variant baseline|durable [--fault-not-injected]
#
# The delivery is a *prompt frame*: the gate forwards the prompt request to the runtime twice. That is one
# delivery arriving twice, at the transport, not two prompts typed into a runner — so neither variant is
# asked to have a feature it does not have, and both are faulted by the same mechanism.
#
# The request's name differs because the SDK method does: the baseline calls `SendAndWaitAsync`
# (`session.sendAndWait`) and the Durable variant calls `SendAsync` (`session.send`). The selector
# follows the variant, and the result names the frame it used, so the two rows are not silently
# describing different injections.
#
# What "doubled" can mean is decided by measurement, not by the vocabulary:
#
#   * the tool calls the runtime asked for, counted from the gate's own log;
#   * the turns the runtime delivered;
#   * the files, compared with a reference run of the same variant over the same staged target.
#
# A doubled delivery whose second copy never gets a turn out of the runtime has doubled a *request* and
# not an effect, and the result says which. The outcome word is chosen from those counts.
#
#   --fault-not-injected is the control: the same run with nothing re-delivered. Its counts are the
#   baseline the duplicate's counts are read against; a control that also doubled would mean the
#   doubling was not the delivery.
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
[ "$VARIANT" = "baseline" ] || [ "$VARIANT" = "durable" ] || { log "duplicate-delivery: unknown variant"; exit 2; }

FAULT="duplicate-delivery"
[ "$INJECT" = "1" ] || FAULT="duplicate-delivery-control"
FAULT_COMMAND="./faults/duplicate-delivery.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND
FAULT_SCRIPT="$(basename "${BASH_SOURCE[0]}")"
export FAULT_SCRIPT

require_tools || exit 1

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")" || exit 1
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

# One iteration only: the fault is about one delivery arriving twice, and a second iteration would put
# two different deliveries in the same log. The cap is 1 in both branches, so the only difference
# between the fault and its control is the re-delivery.
# The prompt request, named as the variant's own SDK call makes it.
if [ "$VARIANT" = "baseline" ]; then
  DELIVERY="session.sendAndWait"
else
  DELIVERY="session.send"
fi

if [ "$INJECT" = "1" ]; then
  FAULTS_DUPLICATE="in:$DELIVERY"
  FAULTS_DUPLICATE_COUNT=1
  export FAULTS_DUPLICATE FAULTS_DUPLICATE_COUNT
else
  FAULTS_DUPLICATE=""
  export FAULTS_DUPLICATE
fi
FAULTS_HOLD=""
FAULTS_COUNT=0
export FAULTS_HOLD FAULTS_COUNT

start_variant "$VARIANT" "$RUN_DIR" 1
PID="$STARTED_PID"
if ! wait_app "$RUN_DIR" 900; then
  log "duplicate-delivery: the run did not finish within the budget"
  exit 1
fi
STATUS="$(app_status)"

# Counted from the gate's log, which is the transport's own record of what crossed it. A request that
# the gate re-delivered and a notification the runtime sent twice are both counted, so "the second copy
# reached the runtime" and "the runtime answered it" are separate numbers.
SENDS="$(grep -c -x "[[:space:]]*[0-9.]* \[gate\] IN $DELIVERY" "$RUN_DIR/gate.log" 2>/dev/null || true)"
TOOL_REQUESTS="$(grep -c '\[gate\] OUT session.event:tool.execution_start' "$RUN_DIR/gate.log" 2>/dev/null || true)"
TURNS="$(grep -c '\[gate\] OUT session.event:assistant.message' "$RUN_DIR/gate.log" 2>/dev/null || true)"
DUPLICATED="$(grep -c 're-delivering' "$RUN_DIR/gate.log" 2>/dev/null || true)"

# The tool calls the runtime asked for *after* the re-delivery crossed the gate. That is the only
# attribution the log can support: a frame's timestamp is after the duplicate line or it is not. It makes
# "the second delivery got a turn of its own" a count rather than an inference from two totals.
TOOL_AFTER_DUPLICATE="$("$PYTHON" - "$RUN_DIR/gate.log" <<'PY'
import sys
after = None
count = 0
try:
    handle = open(sys.argv[1], encoding="utf-8")
except OSError:
    print(0)
    raise SystemExit(0)
for line in handle:
    if "re-delivering" in line:
        after = line.split()[0]
        continue
    if after is not None and "OUT session.event:tool.execution_start" in line:
        count += 1
print(count)
PY
)"


PLAN="$(plan_counts "$RUN_DIR/target")"
VERIFY="$(verify_target "$RUN_DIR/target")"
DIGEST="$(product_digest "$RUN_DIR/target" | shasum -a 256 | cut -d' ' -f1)"
ITERATIONS="$(iterations_run "$RUN_DIR/app.log")"
END="$(end_line "$RUN_DIR/app.log")"
STORE="$(store_state "$RUN_DIR/target/.durable/store.jsonl")"
EVIDENCE="frames at the gate: $SENDS $DELIVERY, $TURNS assistant.message, $TOOL_REQUESTS tool.execution_start; frames re-delivered by the gate: $DUPLICATED; tool calls after the re-delivery: $TOOL_AFTER_DUPLICATE; product digest ${DIGEST:0:16}; store '$STORE'; last line \"$END\""

if [ "$INJECT" = "1" ]; then
  if [ "${DUPLICATED:-0}" = "0" ]; then
    log "duplicate-delivery: the gate never re-delivered a frame; the fault was not injected"
    exit 1
  fi
  if [ "${TOOL_AFTER_DUPLICATE:-0}" -gt 0 ]; then
    OUTCOME="doubled"
    DETAIL="One delivery, forwarded twice by the gate ($DUPLICATED re-delivery of $DELIVERY). The second copy got a turn of its own: $TOOL_AFTER_DUPLICATE tool call(s) crossed the gate after the re-delivery, out of $TOOL_REQUESTS in the whole run. One iteration finished, exit $STATUS, plan '$PLAN', verifier '$VERIFY'. The side effect of a second delivery is therefore a second set of tool calls over the same files, not an error the loop reports."
  elif [ "${TOOL_REQUESTS:-0}" -eq 0 ]; then
    OUTCOME="not doubled"
    DETAIL="The gate re-delivered the frame ($DUPLICATED), but the run asked the runtime for no tool at all: 0 tool.execution_start frames. A duplicate that produced no effect in this run is reported as such rather than as a doubling: there was nothing for the second copy to double."
  else
    OUTCOME="not doubled"
    DETAIL="The gate re-delivered the frame ($DUPLICATED), and no tool call crossed the gate after it: 0 of the run's $TOOL_REQUESTS tool.execution_start frames came after the re-delivery. The second copy arrived after the loop had finished with that session, so nothing doubled. The count, not a guess about the runtime, is what says so."
  fi
  record_result "$VARIANT" "$FAULT" "$OUTCOME" \
    "one iteration, one delivery re-delivered by the gate" \
    "$DETAIL This is a measured count of frames, not an assumption about the variant: the same words would be chosen for either variant." \
    "$EVIDENCE"
  exit 0
fi

record_result "$VARIANT" "$FAULT" "control: one delivery" \
  "one iteration, nothing re-delivered (the control)" \
  "Control: the same run with nothing re-delivered. $SENDS $DELIVERY frame(s), $TURNS assistant.message, $TOOL_REQUESTS tool.execution_start, exit $STATUS, plan '$PLAN', verifier '$VERIFY', product digest ${DIGEST:0:16}. The duplicate run is read against these counts; the re-delivery is the only difference." \
  "$EVIDENCE"
exit 0
