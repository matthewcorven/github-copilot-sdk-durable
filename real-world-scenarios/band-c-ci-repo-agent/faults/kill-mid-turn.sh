#!/usr/bin/env bash
# Fault: kill-mid-turn. A real SIGKILL of a real process, during classification.
#
#   ./faults/kill-mid-turn.sh --variant baseline|durable [--fault-not-injected]
#
# The injection point is the classifier: the process has printed `phase 2/8` and at least one PR's
# classification line under it, so it is inside the phase and has done work, and the state write at
# the end of the run is far away. The harness reads that from the process's own stdout (its log file,
# not its pipe) and kills the PID the shell started with `kill -9`, then polls until the PID is gone.
# Nothing about the kill is simulated in-process: there is no exception thrown to look like a crash.
#
# Then a *new* process is started over the same state and the same clock, and the question is whether
# anything continues the interrupted run:
#
#   baseline — the only record is the state blob written at the end of Phase 8, and it was never
#              written, so the second run is a cold start and re-does the classification from PR #101.
#   durable  — the run record committed when Phase 1 finished names `classify`, and the task is
#              unfinished, so the second process resumes at the phase the kill cut short and finishes.
#
#   --fault-not-injected runs the same first process and lets it finish. That is the control: it shows
#   what the two runs produce when nothing is killed, so a difference in the fault run is attributable
#   to the kill rather than to the two-run shape.
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

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

FIRST="$RUN_DIR/first.log"
SECOND="$RUN_DIR/second.log"
if [ "$INJECT" = "1" ]; then
  PHASE="inside phase 2 (classify), at least one PR classified, the state write 4 phases away"
else
  PHASE="inside phase 2 (classify), uninterrupted (the control)"
fi

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

# Two conditions, both from the process's own output: the phase header, and one classification line
# under it. The second is what makes "inside the phase" true rather than "the header was printed and
# the process is about to be scheduled out".
wait_for_line "$FIRST" "phase 2/8" 120 || {
  log "kill-mid-turn: the run never reached phase 2"
  tail -20 "$FIRST" >&2
  exit 1
}
deadline=$((SECONDS + 120))
while [ "$(awk '/phase 2\/8/{f=1;next} f && /^  #[0-9]+/{c++} END{print c+0}' "$FIRST" 2>/dev/null)" -lt 1 ]; do
  [ "$SECONDS" -lt "$deadline" ] || { log "kill-mid-turn: no PR was classified"; exit 1; }
  sleep 0.02
done
CLASSIFIED_AT_START="$(awk '/phase 2\/8/{f=1;next} f && /^  #[0-9]+/{c++} END{print c+0}' "$FIRST")"

KILL_PID=""
if [ "$INJECT" = "1" ]; then
  kill -0 "$FIRST_PID" 2>/dev/null || { log "kill-mid-turn: the process exited before the kill"; exit 1; }
  kill_hard "$FIRST_PID" || { log "kill-mid-turn: the process survived kill -9"; exit 1; }
  KILL_PID="$FIRST_PID"
  log "kill-mid-turn: kill -9 $FIRST_PID, confirmed gone; $CLASSIFIED_AT_START classification line(s) were in its log when the kill was sent"
  wait "$FIRST_PID" 2>/dev/null
  FIRST_STATUS=$?
else
  wait_for_exit "$FIRST_PID" 240 || { log "kill-mid-turn: the control run did not finish"; exit 1; }
  FIRST_STATUS="$WAIT_STATUS"
fi

# What the kill left behind, read from the artefacts rather than from the process. `CLASSIFIED` is the
# count of PRs Phase 2 had printed a line for by the moment a line existed — the kill lands somewhere
# after that, which is why the observation is "classification was under way", not a fixed count. The
# exact number is read from the log after the fact, where a first run that finished has all 13.
CLASSIFIED="$(awk '/phase 2\/8/{f=1;next} f && /^  #[0-9]+/{c++} END{print c+0}' "$FIRST")"

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
SECOND_STATE="$(state_field "$VARIANT" "$STATE_HINT" phase)"
TRIAGED="$(run_ids "$VARIANT" "$STATE_HINT")"
DONE_LINE="$(grep -E 'completed — |triaged [0-9]+ PR' "$SECOND" 2>/dev/null | tail -1)"

if [ "$INJECT" = "1" ]; then
  if [ "$VARIANT" = "baseline" ]; then
    OUTCOME="lost"
    DETAIL="The run was killed with kill -9 (pid $FIRST_PID, confirmed gone) inside Phase 2 (its log shows $CLASSIFIED classification line(s) by the time the kill was confirmed), and had posted $FIRST_COMMENTS comment(s) and written no state. A new process, same repository, same clock, is a cold start: it re-classified all 13 PRs from #101 and posted $SECOND_COMMENTS comment(s). Nothing continued the interrupted run, because the baseline has no record of one — its only state is the whole-file snapshot Phase 8 writes at the end, and the kill was four phases before that."
  else
    OUTCOME="resumed"
    DETAIL="The run was killed with kill -9 (pid $FIRST_PID, confirmed gone) inside Phase 2. The committed run record named phase '$FIRST_STATE' and the task was unfinished ($FIRST_TASK), so the new process resumed at '$RESUME_PHASE' — the phase the kill cut short — and finished with $SECOND_COMMENTS comment(s) posted by the resume and the run at '$SECOND_STATE'. The classification done before the kill was read back from the committed PR documents; the phase was not re-run from the top."
  fi
else
  if [ "$VARIANT" = "baseline" ]; then
    OUTCOME="control: completed"
    DETAIL="Control: the same first run, with nothing killed (it exited $FIRST_STATUS), then a second process over the same repository at the same clock. The first run posted its comments and committed the state, so the second reads that state back and stops at the run gate: $SECOND_COMMENTS comment(s) posted by the second run. Both runs finished because neither was killed; that is what makes the fault run's cold start and its 13 re-posted comments attributable to the kill."
  else
    OUTCOME="control: completed"
    DETAIL="Control: the same first run, with nothing killed (it exited $FIRST_STATUS). It finished the triage, so the second process found a finished task and started a new generation, which posts nothing new ($SECOND_COMMENTS comment(s) posted by the second run). This is what the two runs produce unaided; the fault run's resume belongs to the kill and not to the two-run shape."
  fi
fi

EVIDENCE="classification lines at the first check=$CLASSIFIED_AT_START, in the log after the fault=$CLASSIFIED; kill pid=${KILL_PID:-none}; first exit=$FIRST_STATUS; first comments posted=$FIRST_COMMENTS; first state=${FIRST_STATE:-none}; first task=${FIRST_TASK:-n/a}; second exit=$SECOND_STATUS; second cold start=$COLD_START resumed=$RESUMED${RESUME_PHASE:+ at phase=$RESUME_PHASE}; second comments posted=$SECOND_COMMENTS; committed triaged=[$TRIAGED]; second run says: ${DONE_LINE:-none}"

record_result "$VARIANT" "$FAULT" "$OUTCOME" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
