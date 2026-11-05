#!/usr/bin/env bash
# Fault: store-outage. Make the store unavailable while the gate's decision is being committed, and see
# whether a half-committed decision is left behind.
#
#   ./faults/store-outage.sh --variant baseline|durable [--fault-not-injected]
#
# The outage is real: the file the store commits into is made unwritable by the operating system
# (`chflags uchg`), while the process that would write it is running. No flag is read by the application
# and nothing in either variant knows the outage exists. The flag is removed on every exit path, including
# failure, so a run never leaves an immutable file behind.
#
#   baseline  The baseline's checkpoint directory is its store. The directory is made immutable while a
#             resume is mid-flight, so the framework cannot write the checkpoint it is about to write. What
#             is observed afterwards, by a fresh process, is whether the run can still be resumed and
#             whether a half-written checkpoint was left.
#   durable   The gate reads a decision document and the workflow commits its checkpoint in one store. The
#             store *file* is made immutable before the run drives the decision, so the commit that would
#             record the approval cannot append. What is observed afterwards is whether the decision and
#             the run's state agree, or whether one moved without the other.
#
# Two mechanisms on purpose: an immutable directory is the baseline's store (one file per checkpoint), and
# an immutable file is the Durable variant's (one append-only file). Both are the same outage — "the store
# cannot be written" — expressed in the shape each store actually has.
#
# `--fault-not-injected` runs the same steps and makes nothing immutable. Both variants must then finish.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

parse_variant_args "${BASH_SOURCE[0]}" "$@"
FAULT="$(fault_name store-outage)"
PORT="$(port_for "$VARIANT" "$FAULT")"
FAULT_COMMAND="./faults/store-outage.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1
build_variants || exit 1
install_cleanup

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
mkdir -p "$RUN_DIR"

# chflags is macOS. The check is explicit so a Linux run says it lacks the capability instead of silently
# recording a "the store was fine" result.
if ! command -v chflags >/dev/null 2>&1; then
  record_not_exercised "$VARIANT" "$FAULT" "a store write that the operating system refuses" \
    "This harness makes a store unwritable with chflags(1), which is a macOS tool; on this machine it is absent, so the outage could not be injected. Recording not exercised with the reason rather than reporting an unaffected run as the fault's outcome."
  exit 0
fi

if [ "$VARIANT" = "baseline" ]; then
  # ── baseline ────────────────────────────────────────────────────────────────────────────────
  fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
  start_baseline_bg "$RUN_DIR" run "run"
  track_pid "$APP_PID"
  wait_for_exit "$APP_PID" 180 || { log "store-outage: process 1 never reached the gate"; exit 1; }
  GATE_CHECKPOINT="$(observed baseline "$RUN_DIR" gate-checkpoint)"
  assert_observed "the baseline wrote no gate checkpoint" "$GATE_CHECKPOINT" || exit 1
  CHECKPOINTS_AT_GATE="$(observed baseline "$RUN_DIR" checkpoint-count)"

  # The store is the checkpoint directory: one JSON file per checkpoint, written whole by the framework.
  STORE="$RUN_DIR/checkpoints"
  if [ "$INJECT" = "1" ]; then
    chflags uchg "$STORE" || { log "store-outage: could not make $STORE immutable"; exit 1; }
    track_restore "chflags nouchg '$STORE'"
    log "store-outage: $STORE is now immutable (chflags uchg)"
  fi

  fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
  drive_status=0
  baseline_mode "$RUN_DIR" resume >"$RUN_DIR/drive.log" 2>&1 || drive_status=$?
  # The flag is removed before the observation, so the observing process is not itself the outage.
  if [ "$INJECT" = "1" ]; then
    chflags nouchg "$STORE"
  fi
  CALLS_DRIVE="$(count_calls "$RUN_DIR")"

  # Observe from a fresh process, with the store writable again: what did the outage leave behind?
  fresh_endpoint "$RUN_DIR" "$PORT" 0 || exit 1
  after_status=0
  baseline_mode "$RUN_DIR" resume >"$RUN_DIR/after.log" 2>&1 || after_status=$?
  CHECKPOINTS_AFTER="$(observed baseline "$RUN_DIR" checkpoint-count)"
  LATEST_AFTER="$(observed baseline "$RUN_DIR" latest-checkpoint)"
  APPROVED_AFTER="$(observed baseline "$RUN_DIR" approved-in-store)"
  PENDING_AFTER="$(observed baseline "$RUN_DIR" pending-requests)"
  LATE_APPROVALS="$(grep -c 'Approving plan and continuing to completion' "$RUN_DIR/after.log" 2>/dev/null)"
  LATE_APPROVALS="${LATE_APPROVALS:-0}"

  if [ "$INJECT" = "1" ]; then
    if [ "$drive_status" -ne 0 ]; then
      OBSERVED="failed safely, gate still answerable"
      PHASE="the checkpoint directory made immutable while a resume was committing"
      DETAIL="The store — the checkpoint directory itself — was made immutable with chflags uchg while a resume process was writing its post-approval checkpoint. That process exited $drive_status and its own output says why: the framework could not create a checkpoint. So the commit it was making did not happen and no partial checkpoint was left — the directory held $CHECKPOINTS_AT_GATE checkpoint file(s) at the gate. What is left is the gate checkpoint, still holding the plan-review request ($PENDING_AFTER): with the flag removed, a fresh resume exited $after_status, answered the request again ($LATE_APPROVALS re-approvals), wrote checkpoint $LATEST_AFTER, and left a post-approval checkpoint: $APPROVED_AFTER. That is a clean failure and also a limit: the baseline's gate has no way to say it has already been answered, so the same approval is still there to be applied a second time."
    else
      OBSERVED="no effect"
      PHASE="the checkpoint directory made immutable while a resume was committing"
      DETAIL="The directory was made immutable, and the resume exited 0 anyway after $CALLS_DRIVE model call(s). A store that refuses writes but whose writer still reports success is a stronger finding than a clean failure, and it is reported rather than smoothed over: the run's commit did not reach the store the flag protected."
    fi
  else
    OBSERVED="completed"
    PHASE="no outage (the control)"
    DETAIL="Control: the same steps with nothing made immutable. The resume exited $drive_status after $CALLS_DRIVE model call(s), the run finished, and a second resume exited $after_status with $CHECKPOINTS_AFTER checkpoint file(s) and a post-approval checkpoint present: $APPROVED_AFTER. This is what the outage run is measured against."
  fi

  EVIDENCE="gate checkpoint: $GATE_CHECKPOINT; checkpoints at the gate: $CHECKPOINTS_AT_GATE
drive (exit $drive_status): $CALLS_DRIVE model call(s)
after the flag was removed, fresh resume (exit $after_status): $CHECKPOINTS_AFTER file(s), latest $LATEST_AFTER, post-approval checkpoint: $APPROVED_AFTER, pending requests: ${PENDING_AFTER:-(none)}
$(grep -m2 -E 'Unhandled exception|System\.' "$RUN_DIR/drive.log" 2>/dev/null)"
  EXTRA="$(printf '{"drive_exit": %s, "model_calls_drive": %s, "checkpoints_at_gate": "%s", "checkpoints_after": "%s", "post_approval_checkpoint_after": "%s", "gate_reapproved_after_outage": "%s"}' \
    "$drive_status" "${CALLS_DRIVE:-0}" "$CHECKPOINTS_AT_GATE" "$CHECKPOINTS_AFTER" "$APPROVED_AFTER" "$LATE_APPROVALS")"
else
  # ── durable ─────────────────────────────────────────────────────────────────────────────────
  PAUSE_ON="" start_durable_bg "$RUN_DIR" start start --gate-seconds 600 --deterministic
  track_pid "$APP_PID"
  wait_for_exit "$APP_PID" 180 || { log "store-outage: the start process did not finish"; exit 1; }
  durable_at_gate "$RUN_DIR" 60 || exit 1
  TASK_AT_GATE="$(observed durable "$RUN_DIR" task-status)"
  CALLS_AT_GATE="$(observed durable "$RUN_DIR" calls)"
  if [ "$TASK_AT_GATE" != "waiting" ]; then
    log "store-outage: the run is '$TASK_AT_GATE' after start, not waiting at the gate"
    exit 1
  fi

  # A decision is recorded first; that is the write the outage will prevent from being applied. The
  # document is written by its own process, so the outage lands on the process that drives the run, not
  # on the one that recorded the approval — which is the case that shows what "half committed" would mean.
  PAUSE_ON="" durable_verb "$RUN_DIR" approve --deterministic --as alice --comment "approved before the outage" \
    >"$RUN_DIR/approve.log" 2>&1 || true
  DECISION_BEFORE="$(observed durable "$RUN_DIR" decision)"
  CALLS_AFTER_APPROVE="$(observed durable "$RUN_DIR" calls)"
  STORE="$RUN_DIR/store.jsonl"
  SIZE_BEFORE="$(wc -c < "$STORE" | tr -d ' ')"

  if [ "$INJECT" = "1" ]; then
    chflags uchg "$STORE" || { log "store-outage: could not make $STORE immutable"; exit 1; }
    track_restore "chflags nouchg '$STORE'"
    log "store-outage: $STORE is now immutable (chflags uchg)"
  fi

  drive_status=0
  PAUSE_ON="" durable_verb "$RUN_DIR" resume --deterministic >"$RUN_DIR/drive.log" 2>&1 || drive_status=$?
  if [ "$INJECT" = "1" ]; then
    chflags nouchg "$STORE"
  fi

  # Observe with the store writable again. The claim is about what the outage left: the decision and the
  # run must agree, and the store must still read.
  SIZE_AFTER_OUTAGE="$(wc -c < "$STORE" | tr -d ' ')"
  TASK_AFTER_OUTAGE="$(observed durable "$RUN_DIR" task-status)"
  DECISION_AFTER_OUTAGE="$(observed durable "$RUN_DIR" decision)"
  VERDICT_AFTER_OUTAGE="$(observed durable "$RUN_DIR" verdict)"
  CALLS_AFTER_OUTAGE="$(observed durable "$RUN_DIR" calls)"

  # A fresh process must still be able to open and read the store: a torn commit would show up here.
  open_status=0
  PAUSE_ON="" durable_verb "$RUN_DIR" inspect >"$RUN_DIR/after.log" 2>&1 || open_status=$?

  if [ "$INJECT" = "1" ]; then
    # "No half-committed decision" means: the decision on file is the one written before the outage, the
    # run did not become terminal on a commit that never landed, and the store still reads coherently.
    if [ "$DECISION_AFTER_OUTAGE" = "$DECISION_BEFORE" ] \
       && [ "$TASK_AFTER_OUTAGE" != "terminal" ] \
       && [ "$open_status" -eq 0 ] \
       && [ "$drive_status" -ne 0 ]; then
      OBSERVED="failed safely"
      PHASE="the store made immutable while the run drove a recorded decision"
      DETAIL="The decision was on file before the outage ('$DECISION_BEFORE') and the store file was made immutable with chflags uchg. The resume that would have applied it exited $drive_status, so the commit that records the approved run did not happen. With the flag removed, the store still reads: the decision is still '$DECISION_AFTER_OUTAGE', the run is '$TASK_AFTER_OUTAGE' with verdict '${VERDICT_AFTER_OUTAGE:-(none)}', the ledger is unchanged at $CALLS_AFTER_OUTAGE call(s), the file is $SIZE_BEFORE bytes before and $SIZE_AFTER_OUTAGE after, and a fresh 'inspect' exited $open_status. The decision and the run agree, and the run did not advance on a write that failed."
    elif [ "$drive_status" -eq 0 ] && [ "$TASK_AFTER_OUTAGE" = "terminal" ]; then
      OBSERVED="failed safely, with an unrefused write"
      PHASE="the store made immutable while the run drove a recorded decision"
      DETAIL="The store was made immutable, and the resume exited 0 and completed the run anyway (verdict '${VERDICT_AFTER_OUTAGE:-(none)}', $CALLS_AFTER_OUTAGE call(s), $SIZE_AFTER_OUTAGE bytes). The outage did not prevent the commit the flag was there to prevent — a store that refuses writes while its writer reports success. That is a stronger finding than a clean failure and it is recorded rather than smoothed over."
    else
      OBSERVED="half committed"
      PHASE="the store made immutable while the run drove a recorded decision"
      DETAIL="The resume exited $drive_status and the store afterwards reads: decision '$DECISION_AFTER_OUTAGE' (before: '$DECISION_BEFORE'), run '$TASK_AFTER_OUTAGE' with verdict '${VERDICT_AFTER_OUTAGE:-(none)}', $SIZE_AFTER_OUTAGE bytes (before: $SIZE_BEFORE), fresh 'inspect' exit $open_status. The decision and the run do not agree, or a fresh process could not read the store."
    fi
    OUTCOME_STATUS="$TASK_AFTER_OUTAGE"
  else
    OBSERVED="completed"
    PHASE="no outage (the control)"
    DETAIL="Control: the same steps with nothing made immutable. The resume exited $drive_status and the run ended '$TASK_AFTER_OUTAGE' with verdict '${VERDICT_AFTER_OUTAGE:-(none)}'; the decision on file is '$DECISION_AFTER_OUTAGE', the ledger reads $CALLS_AFTER_OUTAGE call(s), and a fresh 'inspect' exited $open_status. This is what the outage run is measured against: the same decision, applied."
    OUTCOME_STATUS="$TASK_AFTER_OUTAGE"
  fi

  EVIDENCE="at the gate: $TASK_AT_GATE, $CALLS_AT_GATE call(s)
decision before the outage: $DECISION_BEFORE ($CALLS_AFTER_APPROVE call(s)); store $SIZE_BEFORE bytes
drive under the outage (exit $drive_status): store $SIZE_AFTER_OUTAGE bytes
after the flag was removed: run $TASK_AFTER_OUTAGE, verdict ${VERDICT_AFTER_OUTAGE:-(none)}, decision $DECISION_AFTER_OUTAGE, $CALLS_AFTER_OUTAGE call(s), fresh inspect exit $open_status
$(grep -m2 -E 'band-g-durable:|UnauthorizedAccess|Operation not permitted' "$RUN_DIR/drive.log" 2>/dev/null)"
  CALLS_AT_GATE_D="$(observed durable "$RUN_DIR" calls)"
  EXTRA="$(printf '{"model_calls_at_gate": %s, "model_calls_after_outage": %s, "drive_exit": %s, "store_bytes_before": "%s", "store_bytes_after": "%s", "run_after_outage": "%s", "verdict_after_outage": "%s", "decision_after_outage": "%s", "fresh_inspect_exit": %s}' \
    "${CALLS_AT_GATE_D:-0}" "${CALLS_AFTER_OUTAGE:-0}" "$drive_status" "$SIZE_BEFORE" "$SIZE_AFTER_OUTAGE" "$OUTCOME_STATUS" "${VERDICT_AFTER_OUTAGE:-}" "$DECISION_AFTER_OUTAGE" "$open_status")"
fi

record_result "$VARIANT" "$FAULT" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
exit 0
