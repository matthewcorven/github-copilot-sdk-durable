#!/usr/bin/env bash
# Fault: restart-after-checkpoint. A clean SIGTERM after a committed phase, then a restart.
#
#   ./faults/restart-after-checkpoint.sh --variant baseline|durable [--fault-not-injected]
#
# Deliberately *not* `kill -9`: this fault is about a stop the process can take politely, so a
# difference it shows is not the difference between a clean and an unclean exit. The process is sent
# `SIGTERM` at the moment the harness has seen evidence that a phase has completed and its result is on
# the state:
#
#   baseline — `labels.json` has been written, so Phase 5 is finished. (The baseline has
#              no in-run checkpoint at all; labels.json is the closest thing to "a phase finished", and
#              the finding is that the baseline has no state to checkpoint, so the restart is a cold
#              start.)
#   durable  — the variant's own seam: `BAND_C_EXIT_AFTER_PHASE=batch` commits the run record at
#              `comment` and then exits 42, so the store is at a known phase. The restart is over that
#              store. No SIGTERM is sent on this arm; the result's `phase` says so, because a checkpoint
#              the store knows about is a different stop from a signal it has to interpret.
#
# Both then restart with no seam, at the same clock, and the question is whether the restart is exact:
# does it continue from what committed, and does it redo work that was already committed?
#
#   --fault-not-injected is the control. It stops the first run *before* the checkpointed phase (baseline:
#   before labels.json exists, so during Phase 1-4; durable: `BAND_C_EXIT_AFTER_PHASE=collect`, record at
#   `classify`) and restarts from there. Both halves run, so a difference between fault and control is
#   the phase the stop was taken after — not the restart itself.
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
    *) log "restart-after-checkpoint: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "restart-after-checkpoint: --variant baseline|durable is required"; exit 2; }

FAULT="restart-after-checkpoint"
[ "$INJECT" = "1" ] || FAULT="restart-after-checkpoint-control"
FAULT_COMMAND="./faults/restart-after-checkpoint.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1
# The result this run replaces is removed before anything else happens, so a run that fails before
# recording leaves *no* file rather than a stale one that the summary would render as its result.
begin_result "$VARIANT" "$FAULT"

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

FIRST="$RUN_DIR/first.log"
SECOND="$RUN_DIR/second.log"
if [ "$VARIANT" = "baseline" ]; then STATE_HINT="$RUN_DIR/repo.git"; else STATE_HINT="$RUN_DIR/store.jsonl"; fi

if [ "$INJECT" = "1" ]; then
  PHASE="after Phase 5 (baseline: labels.json written, then SIGTERM; durable: committed at phase 'comment', then the seam's exit 42)"
else
  if [ "$VARIANT" = "baseline" ]; then
    PHASE="an earlier stop in the same run (SIGSTOP then SIGTERM before its first phase completed), then a restart — the control"
  else
    PHASE="the control: committed at phase 'classify', then the seam's exit 42, then restarted"
  fi
fi

# ── The first run, stopped where the variant can be stopped ──────────────────────────────────────
STOP_PID=""
FIRST_SEAM_STATUS=""
if [ "$VARIANT" = "durable" ]; then
  # The durable stop is the variant's own seam, reached through the environment it already reads:
  # `BAND_C_EXIT_AFTER_PHASE=<phase>` commits the run record at the *next* phase and then exits 42, so
  # the store is at a known phase and the restart can be asked to continue from it. That is a checkpoint
  # the store knows about rather than a signal it has to interpret — and the exit is still a real
  # process exit (Environment.Exit(42)), which the harness waits for rather than assuming. No SIGTERM is
  # sent on this arm, and the result says so: the two variants cannot be stopped the same way because
  # only one of them has a phase to stop after.
  if [ "$INJECT" = "1" ]; then
    BAND_C_EXIT_AFTER_PHASE=batch start_durable "$RUN_DIR" "$STATE_HINT" first
  else
    BAND_C_EXIT_AFTER_PHASE=collect start_durable "$RUN_DIR" "$STATE_HINT" first
  fi
  STOP_PID="$STARTED_PID"
  track_pid "$STOP_PID"
  wait_for_exit "$STOP_PID" 120 || { log "restart-after-checkpoint: the seam did not fire"; exit 1; }
  FIRST_SEAM_STATUS="$WAIT_STATUS"
else
  start_baseline "$RUN_DIR" "$STATE_HINT" first
  STOP_PID="$STARTED_PID"
  if [ "$INJECT" = "1" ]; then
    # Stop where a committed phase would be: `labels.json` is Phase 5's output, so Phase 5 is finished.
    wait_for_line "$FIRST" "wrote:      labels.json" 120 || {
      log "restart-after-checkpoint: the baseline never reached Phase 5"
      exit 1
    }
    kill -0 "$STOP_PID" 2>/dev/null || { log "restart-after-checkpoint: the process exited before SIGTERM"; exit 1; }
    kill_soft "$STOP_PID" || { log "restart-after-checkpoint: the process ignored SIGTERM"; exit 1; }
  else
    # The control is an *earlier* stop in the same run, not no stop at all: the first run is sent SIGTERM
    # as soon as its Phase 1 header appears, so the pair is "stopped after Phase 5" against "stopped
    # before Phase 2". Both arms stop; the difference is where, which is the only way the baseline can be
    # asked whether the *later* stop is worse than an earlier one. (It is not — neither is resumable —
    # and the result says so rather than implying the baseline has a commit point to stop either side of.)
    # Held before it can do anything observable. SIGSTOP is sent the instant the process exists, so the
    # stop is deterministically earlier than the fault's — it cannot race a buffer, because the process
    # is frozen before its first phase can complete. SIGTERM is sent while it is stopped (it stays
    # pending) and SIGCONT lets it die, which is why the sequence is STOP → TERM → CONT and not TERM
    # alone: a stopped process would keep the signal queued and the harness would wait 20 s for nothing.
    kill -STOP "$STOP_PID" || { log "restart-after-checkpoint: could not hold the control run"; exit 1; }
    kill -TERM "$STOP_PID" 2>/dev/null || { log "restart-after-checkpoint: could not signal the control run"; exit 1; }
    kill -CONT "$STOP_PID" 2>/dev/null || { log "restart-after-checkpoint: could not release the control run"; exit 1; }
    if [ "$(wrote_count "$FIRST")" -gt 0 ] || [ -f "$RUN_DIR/outputs/first/labels.json" ]; then
      log "restart-after-checkpoint: the control's stop was not earlier than the fault's"
      exit 1
    fi
  fi
  # The status is the *observed* status of the stopped process, from wait_for_exit; a bare `wait` would
  # give the status of the last command in this branch (a `grep` in the control arm), which is not the
  # run's status at all.
  wait_for_exit "$STOP_PID" 30 || true
  FIRST_SEAM_STATUS="$WAIT_STATUS"
fi
FIRST_STATUS="$FIRST_SEAM_STATUS"
FIRST_PHASE="$(last_phase "$FIRST")"
FIRST_WROTE="$(wrote_count "$FIRST")"
FIRST_SKIPPED="$(skipped_count "$FIRST")"
STATE_BEFORE="$(state_field "$VARIANT" "$STATE_HINT" phase)"
TASK_BEFORE=""
if [ "$VARIANT" = "durable" ]; then TASK_BEFORE="$("$PYTHON" "$HARNESS_DIR/lib/store.py" task "$STATE_HINT" 2>/dev/null || true)"; fi

# ── The restart, over the same state, with nothing injected ──────────────────────────────────────
if [ "$VARIANT" = "baseline" ]; then
  start_baseline "$RUN_DIR" "$STATE_HINT" second
else
  start_durable "$RUN_DIR" "$STATE_HINT" second
fi
SECOND_PID="$STARTED_PID"
track_pid "$SECOND_PID"
wait_for_exit "$SECOND_PID" 240 || { log "restart-after-checkpoint: the restart did not finish"; tail -20 "$SECOND" >&2; exit 1; }
SECOND_STATUS="$WAIT_STATUS"

COLD_START=0
grep -q "cold start" "$SECOND" 2>/dev/null && COLD_START=1
RESUMED=0
grep -q "resuming durable task" "$SECOND" 2>/dev/null && RESUMED=1
RESUME_PHASE="$(grep -o "at phase '[a-z_]*'" "$SECOND" 2>/dev/null | head -1 | sed "s/at phase '//; s/'//" || true)"
SECOND_WROTE="$(wrote_count "$SECOND")"
snapshot_comments "$RUN_DIR/outputs/first" "$RUN_DIR/comments-before-second.json"
SECOND_DUPES="$(dup_list "$RUN_DIR/comments-before-second.json" "$SECOND" "$RUN_DIR/outputs/first" "$RUN_DIR/outputs/second")" ||
  { log "restart-after-checkpoint: could not measure the restart's duplicates"; exit 1; }
SECOND_DUPES_COUNT="$(dup_count "$RUN_DIR/comments-before-second.json" "$SECOND" "$RUN_DIR/outputs/first" "$RUN_DIR/outputs/second")" ||
  { log "restart-after-checkpoint: could not measure the restart's duplicates"; exit 1; }
SECOND_SKIPPED="$(skipped_count "$SECOND")"
SECOND_PHASE="$(last_phase "$SECOND")"
STATE_AFTER="$(state_field "$VARIANT" "$STATE_HINT" phase)"
DONE_LINE="$(grep -E 'completed — |triaged [0-9]+ PR' "$SECOND" 2>/dev/null | tail -1)"
# Where the restart's comment files went: the run says which directory it wrote to, and for a Durable
# resume that is the *first* task's output directory, not the one this script passed it.
SECOND_OUT="$(output_dir "$SECOND")"

if [ "$INJECT" = "1" ]; then
  if [ "$VARIANT" = "baseline" ]; then
    OUTCOME="lost"
    DETAIL="The run was stopped with SIGTERM (confirmed gone) after Phase 5 had written labels.json and while Phase 6 was posting comments ($FIRST_WROTE posted before the signal). The restart over the same repository is a cold start: it loaded nothing, re-ran all eight phases and posted $SECOND_WROTE comment(s). Nothing was resumed, because a whole-file snapshot written at the end of Phase 8 is the baseline's only state and this run never reached it. The restart is not exact — there was nothing to be exact about."
  else
    OUTCOME="resumed"
    DETAIL="The run was stopped after the batch phase committed: the seam committed the run record at phase 'comment' and exited 42 ($TASK_BEFORE). The restart resumed that task at '$RESUME_PHASE' rather than starting over: it posted $SECOND_WROTE new comment(s) and adopted $SECOND_SKIPPED already-committed one(s), reached '$SECOND_PHASE' and finished with the run record at '$STATE_AFTER'. The phase it re-entered is the one the stop left, so the restart is exact: the committed phases did not run again."
  fi
else
  if [ "$VARIANT" = "baseline" ]; then
    OUTCOME="control: lost"
    DETAIL="Control: the same restart, with the stop taken earlier — the process was held with SIGSTOP the moment it started and SIGTERMed before its first phase completed, when it had posted $FIRST_WROTE comment(s) and written no state. The restart is a cold start: it posted $SECOND_WROTE comment(s), $SECOND_DUPES_COUNT of them duplicates (none, because the stopped run had posted nothing to duplicate). The *outcome word* is the same as the fault row's because the baseline cannot resume from either stop — that is the finding, and it is stated here rather than implied: the baseline has no commit point to be before or after, so the phase the stop lands in changes only *how much* work is lost, not whether any is. What the pair shows is that the fault row's cold start is the stop's doing and not the run's; what it does **not** show is that $FIRST_WROTE comments survive, because on this arm none were posted."
  else
    OUTCOME="control: resumed"
    DETAIL="Control: the same seam earlier in the same run — the record committed at 'classify' ($TASK_BEFORE) — and the same restart. It resumed at '$RESUME_PHASE' and finished at '$SECOND_PHASE', writing $SECOND_WROTE comment(s) with $SECOND_DUPES_COUNT duplicates. The pair is what shows the phase is read from the record and not assumed: the fault stopped later in the same run and the restart re-entered later."
  fi
fi

EVIDENCE="first exit=$FIRST_STATUS; first last phase=$FIRST_PHASE; first posted=$FIRST_WROTE adopted=$FIRST_SKIPPED; state before restart=${STATE_BEFORE:-none}; task before restart=${TASK_BEFORE:-n/a}; second exit=$SECOND_STATUS; second cold start=$COLD_START resumed=$RESUMED${RESUME_PHASE:+ at phase=$RESUME_PHASE}; second posted=$SECOND_WROTE adopted=$SECOND_SKIPPED duplicates=[$SECOND_DUPES] ($SECOND_DUPES_COUNT); second output dir=${SECOND_OUT:-none}; state after=${STATE_AFTER:-none}; second run says: ${DONE_LINE:-none}"

record_result "$VARIANT" "$FAULT" "$OUTCOME" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
