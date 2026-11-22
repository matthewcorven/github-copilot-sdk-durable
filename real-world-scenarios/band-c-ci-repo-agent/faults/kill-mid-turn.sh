#!/usr/bin/env bash
# Fault: kill-mid-turn. A real SIGKILL of a real process, mid-run.
#
#   ./faults/kill-mid-turn.sh --variant baseline|durable [--fault-not-injected]
#
# The injection point is a *comment that has been posted*, not a log line: the harness waits until
# `KILL_AFTER_COMMENTS` (default 7) comment files exist in the run's output directory, confirms the
# process is alive, and sends `kill -9` to the PID the shell started. That is the moment the band is
# about to be measured at: work with an external effect has happened, and the state that would record it
# is a phase away.
#
# Why not "kill during classification", which is what the issue says? Because the baseline's stdout is
# block-buffered and its log is the *only* observable for Phase 2, so no log-line trigger can bound the
# kill to Phase 2. Measured before this change: of 12 kills triggered on the Phase 2 banner, 4 landed
# with `phase 3/8` already printed and 13 classification lines in the log — the trigger was a 4 KB flush,
# not a phase boundary. The probe is in the PR. A kill that lands wherever the buffer flushed is not a
# fault anybody can point at, so the injection was moved to an artefact the harness *sets*: the comment
# count, which is exact for both variants (12/12 runs caught at 7 files).
#
# Classification is still where the interrupted work sits for the durable variant — its seam-free
# resume enters at whatever phase the record names — but the fault's question, "does a resumed run
# re-comment?", is the one worth asking and the one this injection can answer exactly.
#
# Then a *new* process is started over the same state and the same clock, and the question is whether
# anything continues the interrupted run without re-posting what it had already posted:
#
#   baseline — the only record is the state blob written at the end of Phase 8, and it was never
#              written, so the second run is a cold start and posts every comment again.
#   durable  — the comment memos and ledger record the ones already posted, so the resume adopts them
#              and posts only the remainder.
#
#   --fault-not-injected lets the same first process finish. That is the control: it shows what the two
#   runs produce when nothing is killed, so a difference in the fault run is attributable to the kill
#   rather than to the two-run shape.
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
    *) log "kill-mid-turn: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "kill-mid-turn: --variant baseline|durable is required"; exit 2; }

FAULT="kill-mid-turn"
[ "$INJECT" = "1" ] || FAULT="kill-mid-turn-control"
FAULT_COMMAND="./faults/kill-mid-turn.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
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
# The injection point is "at least this many comment files exist". The *observed* count is recorded in
# `phase` and `evidence` after the run: the poll loop can see one or two more files than the threshold
# by the time it acts, because the writer does not pause for the harness. The threshold is a floor, not
# an equality, and the result says which it got.
KILL_AFTER_COMMENTS="${KILL_AFTER_COMMENTS:-7}"

# ── The interrupted (or, for the control, uninterrupted) run ─────────────────────────────────────
if [ "$VARIANT" = "baseline" ]; then
  STATE_HINT="$RUN_DIR/repo.git"
  start_baseline "$RUN_DIR" "$STATE_HINT" first
else
  STATE_HINT="$RUN_DIR/store.jsonl"
  start_durable "$RUN_DIR" "$STATE_HINT" first
fi
FIRST_PID="$STARTED_PID"
track_pid "$FIRST_PID"

# The comment count is an artefact, not a log line: both variants write `comments/pr-NNN.md` as the
# Phase 6 effect, the file appears when the write happens, and the count cannot be inflated by a later
# phase. 12/12 probe runs were caught at exactly the requested count.
FIRST_COMMENT_DIR="$RUN_DIR/outputs/first"
deadline=$((SECONDS + 120))
while [ "$(comments_in "$RUN_DIR" first)" -lt "$KILL_AFTER_COMMENTS" ]; do
  [ "$SECONDS" -lt "$deadline" ] || { log "kill-mid-turn: fewer than $KILL_AFTER_COMMENTS comments were posted"; exit 1; }
  kill -0 "$FIRST_PID" 2>/dev/null || { log "kill-mid-turn: the process finished before the kill point"; exit 1; }
  sleep 0.005
done
POSTED_AT_KILL="$(comments_in "$RUN_DIR" first)"

KILL_PID=""
if [ "$INJECT" = "1" ]; then
  kill -0 "$FIRST_PID" 2>/dev/null || { log "kill-mid-turn: the process exited before the kill"; exit 1; }
  kill_hard "$FIRST_PID" || { log "kill-mid-turn: the process survived kill -9"; exit 1; }
  KILL_PID="$FIRST_PID"
  log "kill-mid-turn: kill -9 $FIRST_PID, confirmed gone; $POSTED_AT_KILL comment file(s) existed when the kill was sent"
  # Reads the observed status of the kill, not the status of the last command before it.
  wait_for_exit "$FIRST_PID" 30 || true
  FIRST_STATUS="$WAIT_STATUS"
else
  wait_for_exit "$FIRST_PID" 240 || { log "kill-mid-turn: the control run did not finish"; exit 1; }
  FIRST_STATUS="$WAIT_STATUS"
fi

CLASSIFIED="$(grep -c 'risk=' "$FIRST" 2>/dev/null || true)"

# The bytes on disk before the second process runs — *both* output directories, so a baseline whose two
# runs write to two directories is judged as one repository. `dup_list` then answers the fault's
# question byte for byte: which comments did the second run post that were already there?
# Only the first run's directory: the second run's is cleared by the starter, and a cold-start baseline
# writes *the same bytes* for the same PRs, so comparing against its own re-write would report every
# comment as a duplicate. The fault's question is what the repository already held when the second
# process started.
snapshot_comments "$RUN_DIR/outputs/first" "$RUN_DIR/comments-before-second.json"
FIRST_STATE="$(state_field "$VARIANT" "$STATE_HINT" phase)"
FIRST_COMMENTS="$(wrote_count "$FIRST")"
FIRST_TASK=""
if [ "$VARIANT" = "durable" ]; then FIRST_TASK="$("$PYTHON" "$HARNESS_DIR/lib/store.py" task "$STATE_HINT" 2>/dev/null || true)"; fi

# ── The second process, over the same state, at the same clock ───────────────────────────────────
if [ "$VARIANT" = "baseline" ]; then
  start_baseline "$RUN_DIR" "$STATE_HINT" second
else
  start_durable "$RUN_DIR" "$STATE_HINT" second
fi
SECOND_PID="$STARTED_PID"
track_pid "$SECOND_PID"
printf 'second pid %s\n' "$SECOND_PID" >&2
wait_for_exit "$SECOND_PID" 240 || { log "kill-mid-turn: the second run did not finish"; tail -20 "$SECOND" >&2; exit 1; }
SECOND_STATUS="$WAIT_STATUS"

COLD_START=0
grep -q "cold start" "$SECOND" 2>/dev/null && COLD_START=1
RESUMED=0
grep -q "resuming durable task" "$SECOND" 2>/dev/null && RESUMED=1
RESUME_PHASE="$(grep -o "resuming durable task t[0-9]* at phase '[a-z_]*'" "$SECOND" 2>/dev/null | sed "s/.*at phase '//; s/'//" || true)"
SECOND_COMMENTS="$(wrote_count "$SECOND")"
SECOND_DUPES="$(dup_list "$RUN_DIR/comments-before-second.json" "$SECOND" "$RUN_DIR/outputs/first" "$RUN_DIR/outputs/second")" ||
  { log "kill-mid-turn: could not measure the second run's duplicates"; exit 1; }
SECOND_DUPES_COUNT="$(dup_count "$RUN_DIR/comments-before-second.json" "$SECOND" "$RUN_DIR/outputs/first" "$RUN_DIR/outputs/second")" ||
  { log "kill-mid-turn: could not measure the second run's duplicates"; exit 1; }
# Where the second run's comments landed: the Durable resume continues the first task, whose input names
# the *first* run's output directory, so its adopted comments are rewrites there. The count above is
# therefore over both directories, and the line below says which directory the run itself wrote into.
SECOND_OUT="$(output_dir "$SECOND")"
SECOND_STATE="$(state_field "$VARIANT" "$STATE_HINT" phase)"
TRIAGED="$(run_ids "$VARIANT" "$STATE_HINT")"
DONE_LINE="$(grep -E 'completed — |triaged [0-9]+ PR' "$SECOND" 2>/dev/null | tail -1)"

if [ "$INJECT" = "1" ]; then
  if [ "$SECOND_DUPES_COUNT" -gt 0 ] 2>/dev/null; then
    if [ "$VARIANT" = "baseline" ]; then
      OUTCOME="lost, and re-commented"
      DETAIL="The run was killed with kill -9 (pid $FIRST_PID, confirmed gone) with $POSTED_AT_KILL comment(s) on disk and no state written — Phase 8 had not run. A new process, same repository, same clock, is a cold start: it re-classified every PR and posted all thirteen comments again, of which $SECOND_DUPES_COUNT were byte-for-byte what was already there ($SECOND_DUPES). Nothing continued the interrupted run: the baseline's only state is the whole-file snapshot Phase 8 writes at the end, so there was no record of the comments to adopt."
    else
      OUTCOME="resumed, with duplicates"
      DETAIL="The run was killed with kill -9 (pid $FIRST_PID, confirmed gone) with $POSTED_AT_KILL comment(s) on disk. The committed run record named phase '$FIRST_STATE' and the new process resumed at '$RESUME_PHASE', but $SECOND_DUPES_COUNT of the $SECOND_COMMENTS comment(s) it wrote were already on disk ($SECOND_DUPES): the resume did not prevent every duplicate."
    fi
  else
    if [ "$VARIANT" = "durable" ]; then
      OUTCOME="resumed"
      DETAIL="The run was killed with kill -9 (pid $FIRST_PID, confirmed gone) with $POSTED_AT_KILL comment(s) on disk. The committed run record named phase '$FIRST_STATE' ($FIRST_TASK), so the new process resumed at '$RESUME_PHASE' — the phase the kill cut short — and finished with the run at '$SECOND_STATE'. It wrote $SECOND_COMMENTS comment(s) of which $SECOND_DUPES_COUNT were already on disk: the $POSTED_AT_KILL posted before the kill were adopted from their memos, not re-posted, and the remaining ones were posted for the first time."
    else
      OUTCOME="lost"
      DETAIL="The run was killed with kill -9 (pid $FIRST_PID, confirmed gone) with $POSTED_AT_KILL comment(s) on disk and no state written. The new process is a cold start, but its $SECOND_COMMENTS comment(s) were not duplicates of the killed run's — the observation is reported as measured rather than assumed."
    fi
  fi
else
  if [ "$VARIANT" = "baseline" ]; then
    OUTCOME="control: completed"
    DETAIL="Control: the same first run, with nothing killed (it exited $FIRST_STATUS), then a second process over the same repository at the same clock. The first run posted its comments and committed the state, so the second reads that state back and stops at the run gate: $SECOND_COMMENTS comment(s) posted by the second run, $SECOND_DUPES_COUNT of them duplicates. Both runs finished because neither was killed; that is what makes the fault run's cold start and its re-posted comments attributable to the kill."
  else
    OUTCOME="control: completed"
    DETAIL="Control: the same first run, with nothing killed (it exited $FIRST_STATUS). It finished the triage, so the second process found a finished task and started a new generation: it wrote $SECOND_COMMENTS comment(s) with $SECOND_DUPES_COUNT duplicates and adopted the rest from the ledger. This is what the two runs produce unaided; the fault run's resume belongs to the kill and not to the two-run shape."
  fi
fi

if [ "$INJECT" = "1" ]; then
  PHASE="$POSTED_AT_KILL of 13 comment file(s) on disk when the kill was sent (threshold $KILL_AFTER_COMMENTS), the run record not yet committed"
else
  PHASE="a clean run to completion, threshold $KILL_AFTER_COMMENTS not used (the control)"
fi

EVIDENCE="kill point: $POSTED_AT_KILL comment file(s) on disk (threshold $KILL_AFTER_COMMENTS); classification lines in the log=$CLASSIFIED; kill pid=${KILL_PID:-none}; first exit=$FIRST_STATUS; first comments posted=$FIRST_COMMENTS; first state=${FIRST_STATE:-none}; first task=${FIRST_TASK:-n/a}; second exit=$SECOND_STATUS; second cold start=$COLD_START resumed=$RESUMED${RESUME_PHASE:+ at phase=$RESUME_PHASE}; second comments posted=$SECOND_COMMENTS; second duplicates=[$SECOND_DUPES] ($SECOND_DUPES_COUNT); second output dir=${SECOND_OUT:-n/a}; committed triaged=[$TRIAGED]; second run says: ${DONE_LINE:-none}"

record_result "$VARIANT" "$FAULT" "$OUTCOME" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
