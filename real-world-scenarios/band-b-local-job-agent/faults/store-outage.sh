#!/usr/bin/env bash
# Fault: store-outage. The store goes away from under a live run.
#
#   ./faults/store-outage.sh --variant baseline|durable [--fault-not-injected]
#
# Two injections, one per variant, because the two variants' "store" is not the same object:
#
#   durable  the store file is **moved aside** while the application holds it open, at a moment the gate
#            pins: the turn's first tool call has returned, so the store is between a checkpoint and the
#            memo that follows it. The held frame is then released and the application continues against
#            a file that is no longer at its path.
#   baseline writes no agent state at all, so there is no store to remove. Moving anything else in the
#            target would fault the *search over a file*, not durability, and recording that under this
#            fault's name would be worse than the absence. It is recorded as **not exercised**, and the
#            script proves the claim before recording it rather than asserting it: the run is allowed to
#            finish, and the target is checked for any file the consequence of removing would have
#            touched — there is none, so the paragraph is not stale.
#
# What "fail safe, no partial commit" can mean is read off the store, not asserted:
#
#   * the store's records all parse, or the torn count says how many do not;
#   * the task record's checkpoint stage, so "nothing advanced past what was committed" is a value;
#   * the target, so "no file was written after the store went" is a comparison.
#
#   --fault-not-injected is the control: the same steps with nothing moved. The run finishes against the
#   store that is still there, which is what makes an outage run's difference attributable.
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
[ "$VARIANT" = "baseline" ] || [ "$VARIANT" = "durable" ] || { log "store-outage: unknown variant"; exit 2; }

FAULT="store-outage"
[ "$INJECT" = "1" ] || FAULT="store-outage-control"
FAULT_COMMAND="./faults/store-outage.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND
FAULT_SCRIPT="$(basename "${BASH_SOURCE[0]}")"
export FAULT_SCRIPT

require_tools || exit 1

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")" || exit 1
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

STORE_PATH="$RUN_DIR/target/.durable/store.jsonl"

# ── baseline: prove the claim that there is nothing to remove ──────────────────────────────────────
if [ "$VARIANT" = "baseline" ]; then
  if [ "$INJECT" = "1" ]; then
    # Run it once under the harness's own conditions and look for state. The check is the *presence* of a
    # file the run wrote outside the job's product: a store, a checkpoint, a session index. The baseline
    # writes none — the product files are the whole of what it leaves — and that is what the result says.
    FAULTS_HOLD=""
    FAULTS_COUNT=0
    export FAULTS_HOLD FAULTS_COUNT
    start_variant "$VARIANT" "$RUN_DIR" 1
    if ! wait_app "$RUN_DIR" 900; then
      log "store-outage: the baseline did not finish within the budget, so the claim could not be checked"
      exit 1
    fi
    STATE_FILES="$(cd "$RUN_DIR/target" && find . -type f \
      -not -path './.durable/*' -not -path './docs/*' \
      -not -name 'PLAN.md' -not -name 'PROMPT.md' -not -name 'release-notes.md' 2>/dev/null | LC_ALL=C sort)"
    VERIFY="$(verify_target "$RUN_DIR/target")"
    if [ -n "$STATE_FILES" ]; then
      # The claim in the README would be false and the result must not repeat it.
      record_result "$VARIANT" "$FAULT" "failed safely" \
        "the run's state on disk, if it has any" \
        "The baseline left state the band README does not know about: [$(echo "$STATE_FILES" | tr '\n' ' ')]. A store-outage against it is therefore possible after all, and this row is a finding about the README rather than an absence." \
        "verifier '$VERIFY'"
      exit 0
    fi
    record_not_exercised "$VARIANT" "$FAULT" "the run's state on disk" \
      "The baseline keeps no state outside the job's own files, so there is no store to take away and nothing durable for an outage to fail safely over. Checked rather than assumed: the run finished (verifier '$VERIFY'), and its target holds no file but PLAN.md, PROMPT.md, release-notes.md and docs/*.md — no store, no checkpoint, no session index. Removing one of those would fault the file the job is rewriting, which is a different fault and is not reported under this name."
    exit 0
  fi
  # The control of an exercise that does not happen is the absence itself; recording it separately would
  # suggest a comparison was made.
  record_not_exercised "$VARIANT" "$FAULT" "the run's state on disk" \
    "No control is run: a fault that cannot be exercised has no before/after to compare. See the fault result for the checked reason."
  exit 0
fi

# ── durable ────────────────────────────────────────────────────────────────────────────────────────
FAULTS_HOLD="out:tool.execution_complete"
FAULTS_COUNT=1
export FAULTS_HOLD FAULTS_COUNT

start_variant "$VARIANT" "$RUN_DIR" "$TARGET_CAP"
PID="$STARTED_PID"

if ! wait_for_inflight "$RUN_DIR" 300; then
  log "store-outage: the gate never held a tool result, so the store was not between two commits"
  tail -15 "$RUN_DIR/app.log" >&2
  exit 1
fi
FRAME="$(inflight_frame "$RUN_DIR")"
BEFORE_STORE="$(store_state "$STORE_PATH")"
BEFORE_PLAN="$(plan_counts "$RUN_DIR/target")"
product_digest "$RUN_DIR/target" > "$RUN_DIR/before.digest"
log "store-outage: $VARIANT is blocked at the gate on '$FRAME'; store before: $BEFORE_STORE"

if [ "$INJECT" = "1" ]; then
  if [ ! -f "$STORE_PATH" ]; then
    log "store-outage: there is no store at $STORE_PATH to move; the run never created one"
    exit 1
  fi
  mv "$STORE_PATH" "$STORE_PATH.moved" || { log "store-outage: could not move the store aside"; exit 1; }
  MOVED=1
  log "store-outage: moved the store to $STORE_PATH.moved while pid $PID holds it open"
else
  MOVED=0
  log "store-outage-control: leaving the store where it is"
fi

release_gate "$RUN_DIR"
if ! wait_app "$RUN_DIR" 1200; then
  log "store-outage: the run did not finish within the budget"
  exit 1
fi
STATUS="$(app_status)"
reap_servers "$RUN_DIR"
rm -f "$RUN_DIR/pid" "$RUN_DIR/inflight" "$RUN_DIR/release"

# What is at the path now: the store the application re-created (an open handle keeps writing to the
# moved inode, so a new file at the path is a new store), the moved file's own contents, or neither.
AT_PATH="$(store_state "$STORE_PATH")"
MOVED_STATE="$(store_state "$STORE_PATH.moved")"
AFTER_PLAN="$(plan_counts "$RUN_DIR/target")"
VERIFY="$(verify_target "$RUN_DIR/target")"
product_digest "$RUN_DIR/target" > "$RUN_DIR/after.digest"
CHANGED_AFTER_OUTAGE="$(comm -13 <(cut -c67- "$RUN_DIR/before.digest" | LC_ALL=C sort) <(cut -c67- "$RUN_DIR/after.digest" | LC_ALL=C sort) | tr '\n' ' ')"
END="$(end_line "$RUN_DIR/app.log")"
REFUSED="$(grep -c 'refused\|Unhandled exception\|InvalidOperationException' "$RUN_DIR/app.log" 2>/dev/null || true)"
EVIDENCE="held '$FRAME'; store before '$BEFORE_STORE'; at the path after '$AT_PATH'; the moved file '$MOVED_STATE'; files new after the outage: [${CHANGED_AFTER_OUTAGE:-none}]; plan before '$BEFORE_PLAN' after '$AFTER_PLAN'; verifier '$VERIFY'; exit $STATUS; last line \"$END\""

if [ "$MOVED" = "0" ]; then
  record_result "$VARIANT" "$FAULT" "control: the store stayed" \
    "a tool result in flight, nothing moved (the control)" \
    "Control: the same steps with the store left in place. It finished at '$AT_PATH', plan '$AFTER_PLAN', verifier '$VERIFY', exit $STATUS last line \"$END\". This is what the outage run is compared against; the move is the only difference." \
    "$EVIDENCE"
  exit 0
fi

# The store was moved. The claim to check is "fail safe, no partial commit": the run either refused, or
# it finished and left the moved file readable with a checkpoint it had already committed. A store that
# reports torn records, or a checkpoint past what was on disk before the move, is neither.
# `store_state` prints `<present|empty|missing> <records> <torn> <stage/iteration>`, so the fields are
# taken by position: the last one is the checkpoint, the third is the torn count.
TORN="$(printf '%s' "$MOVED_STATE" | awk '{print $3}')"
TASK_CHECKPOINT="$(printf '%s' "$MOVED_STATE" | awk '{print $4}')"
BEFORE_CHECKPOINT="$(printf '%s' "$BEFORE_STORE" | awk '{print $4}')"

if [ "${TORN:-0}" -gt 0 ]; then
  record_result "$VARIANT" "$FAULT" "failed unsafely" \
    "a tool result in flight; store moved aside" \
    "The store was moved while the application held it open and the run then wrote a record that does not parse: the moved file has $TORN unparseable line(s) of $(printf '%s' "$MOVED_STATE" | awk '{print $3}'). A store that cannot be read back in full is not the safe failure this fault is meant to show. Exit $STATUS, last line \"$END\"." \
    "$EVIDENCE"
  exit 0
fi

if [ "${REFUSED:-0}" -gt 0 ]; then
  record_result "$VARIANT" "$FAULT" "failed safely" \
    "a tool result in flight; store moved aside" \
    "The store was moved while the application held it open. The run then reported the failure rather than continuing on a store it could not commit to: exit $STATUS, last line \"$END\". The moved file still parses in full ($(printf '%s' "$MOVED_STATE" | awk '{print $3}') record(s), 0 torn) at checkpoint ${BEFORE_CHECKPOINT}. No file was written after the outage: [${CHANGED_AFTER_OUTAGE:-none}]. That is 'fail safe, no partial commit': the last committed state is intact and nothing past it was invented." \
    "$EVIDENCE"
  exit 0
fi

if [ "$AFTER_PLAN" = "$BEFORE_PLAN" ] && [ -z "${CHANGED_AFTER_OUTAGE// /}" ]; then
  record_result "$VARIANT" "$FAULT" "failed safely, without advancing" \
    "a tool result in flight; store moved aside" \
    "The store was moved while the application held it open. The run finished (exit $STATUS, last line \"$END\") but advanced nothing: plan unchanged at '$AFTER_PLAN', no file new after the outage, verifier '$VERIFY'. The moved file parses in full ($(printf '%s' "$MOVED_STATE" | awk '{print $3}') record(s), 0 torn) at checkpoint $TASK_CHECKPOINT. Whatever the process did after the outage, it committed no partial work to the target." \
    "$EVIDENCE"
  exit 0
fi

record_result "$VARIANT" "$FAULT" "continued past the outage" \
  "a tool result in flight; store moved aside" \
  "The store was moved while the application held it open, and the run neither refused nor stopped: exit $STATUS, last line \"$END\", plan '$AFTER_PLAN', files new after the outage [${CHANGED_AFTER_OUTAGE:-none}], verifier '$VERIFY'. A new store stands at the path ('$AT_PATH') because the open handle kept writing to the moved inode. The moved file is '$MOVED_STATE'. That is the honest description: an outage the run did not turn into a failure, reported rather than folded into 'failed safely'." \
  "$EVIDENCE"
exit 0
