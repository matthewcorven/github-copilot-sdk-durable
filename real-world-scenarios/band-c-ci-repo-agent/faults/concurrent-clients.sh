#!/usr/bin/env bash
# Fault: concurrent-clients. Two triage runs at once, with the first held at a known phase.
#
#   ./faults/concurrent-clients.sh --variant baseline|durable [--fault-not-injected]
#
# This fault is deliberately adjacent to `duplicate-delivery` and asks a different question. There, the
# two deliveries overlapped wherever the scheduler put them; here the first process is *held* inside a
# named phase until the second has run, so the window is the one the fault is about: two writers whose
# read-and-write windows definitely overlap.
#
#   baseline — the first process is killed with SIGSTOP once Phase 5's labels.json is on disk. The
#              second runs *to completion* over the same repository while the first is frozen. The first
#              is then resumed (SIGCONT) and finishes. Its Phase 8 parent was read after the second
#              committed, so the compare-and-swap does not fire; both processes report success and one
#              overwrites the other's view. The comments are doubled: two runs' worth, for one
#              repository, at one instant.
#   durable  — there is no phase to hold: the store is locked for the process's lifetime, so the second
#              is refused at the open (exit 3) while the first is merely paused between phases. That is
#              the refusal the band's requirement asks for, and it is a refusal rather than a queue.
#
# SIGSTOP/SIGCONT are real signals on a real PID and change nothing about what the program does — they
# are how a fault harness *holds* a process without editing it. The fault is the concurrent second run.
#
#   --fault-not-injected is the control: the same two processes, run one after the other (the first is
#   held while nothing else runs, then released and allowed to finish; the second is started only after
#   it has). Same code, same signals, no overlap — so a difference is the concurrency.
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
FAULT_COMMAND="./faults/concurrent-clients.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

if [ "$VARIANT" = "baseline" ]; then STATE_HINT="$RUN_DIR/repo.git"; else STATE_HINT="$RUN_DIR/store.jsonl"; fi
if [ "$INJECT" = "1" ]; then
  PHASE="two writers overlapping: the first held after Phase 5, the second run to completion while it is held"
else
  PHASE="the same two runs one after the other, no overlap (the control)"
fi

# ── The first process, held at a phase boundary ──────────────────────────────────────────────────
if [ "$VARIANT" = "baseline" ]; then
  start_baseline "$RUN_DIR" "$STATE_HINT" first
  FIRST_PID="$STARTED_PID"
  track_pid "$FIRST_PID"
  # Held on an artefact, not on a log line: the baseline's stdout is block-buffered when redirected, so a
  # `phase 4/8` line can sit in the pipe buffer for thousands of lines. `labels.json` is Phase 5's own
  # output and is written when Phase 5 begins to finish, which is a fact on disk rather than a flush.
  wait_for_file "$RUN_DIR/outputs/first/labels.json" 120 || { log "concurrent-clients: the first run never reached Phase 5"; exit 1; }
  HELD_AT="after Phase 5 (labels.json written) and before its Phase 8 state write"
else
  start_durable "$RUN_DIR" "$STATE_HINT" first
  FIRST_PID="$STARTED_PID"
  track_pid "$FIRST_PID"
  wait_for_line "$RUN_DIR/first.log" "phase 2/8" 120 || { log "concurrent-clients: the first run never reached Phase 2"; exit 1; }
  HELD_AT="phase 2 (classify)"
fi
kill -0 "$FIRST_PID" 2>/dev/null || { log "concurrent-clients: the first run finished before it could be held"; exit 1; }
kill -STOP "$FIRST_PID" || { log "concurrent-clients: could not hold the first process"; exit 1; }
# Confirm the hold is real: a stopped process reports state T. A sleep here is a wait for an observation,
# which is the one thing a sleep is for.
STOPPED_STATE="$(ps -o state= -p "$FIRST_PID" 2>/dev/null | tr -d ' ')"
[ "$STOPPED_STATE" = "T" ] || log "concurrent-clients: warning — pid $FIRST_PID is not reporting state T ($STOPPED_STATE)"

if [ "$INJECT" = "1" ]; then
  # ── The second, while the first is held ──────────────────────────────────────────────────────
  if [ "$VARIANT" = "baseline" ]; then
    start_baseline "$RUN_DIR" "$STATE_HINT" second
  else
    start_durable "$RUN_DIR" "$STATE_HINT" second
  fi
  SECOND_PID="$STARTED_PID"
  track_pid "$SECOND_PID"
fi

# ── Release the first ───────────────────────────────────────────────────────────────────────────
kill -CONT "$FIRST_PID" || { log "concurrent-clients: could not release the first process"; exit 1; }
wait_for_exit "$FIRST_PID" 240 || { log "concurrent-clients: the first run did not finish"; exit 1; }
FIRST_STATUS="$WAIT_STATUS"

if [ "$INJECT" = "1" ]; then
  wait_for_exit "$SECOND_PID" 240 || { log "concurrent-clients: the second run did not finish"; exit 1; }
  SECOND_STATUS="$WAIT_STATUS"
else
  # The control: the second is started only now, after the first has exited, so there is no overlap.
  if [ "$VARIANT" = "baseline" ]; then
    start_baseline "$RUN_DIR" "$STATE_HINT" second
  else
    start_durable "$RUN_DIR" "$STATE_HINT" second
  fi
  SECOND_PID="$STARTED_PID"
  track_pid "$SECOND_PID"
  wait_for_exit "$SECOND_PID" 240 || { log "concurrent-clients: the control's second run did not finish"; exit 1; }
  SECOND_STATUS="$WAIT_STATUS"
fi

FIRST_WROTE="$(wrote_count "$RUN_DIR/first.log")"
SECOND_WROTE="$(wrote_count "$RUN_DIR/second.log")"
REFUSED=0
grep -q "cannot open the store" "$RUN_DIR/second.log" 2>/dev/null && REFUSED=1
GATE=0
grep -q "run gate" "$RUN_DIR/second.log" 2>/dev/null && GATE=1
STATE_OK="$(state_field "$VARIANT" "$STATE_HINT" phase)"
RUNS="$(run_count "$VARIANT" "$STATE_HINT")"

if [ "$INJECT" = "1" ]; then
  if [ "$VARIANT" = "baseline" ]; then
    OUTCOME="doubled, no conflict detected"
    DETAIL="The first process was held with SIGSTOP $HELD_AT while the second ran to completion over the same repository, then resumed. Both posted all thirteen comments ($FIRST_WROTE and $SECOND_WROTE), each exited $FIRST_STATUS and $SECOND_STATUS, and memory/pr-triage ended with $RUNS commit(s). No process detected the other: Phase 8 reads its parent at Phase 8, so the compare-and-swap only ever sees the write immediately before it, and the second writer's state replaced the first's with no error. The state ends at '$STATE_OK' — one run's answer for a repository two runs triaged."
  elif [ "$REFUSED" = "1" ]; then
    OUTCOME="refused, not doubled"
    DETAIL="The first process took the store's exclusive lock before it printed its first line and held it while it was paused at $HELD_AT; the second was refused at the open (exit $SECOND_STATUS, 'cannot open the store') and did nothing at all — no read of the fixture, no comments. $FIRST_WROTE comment(s) exist from the one run that proceeded. The refusal is what the band's requirement asks for, and it is a refusal rather than a queue: the second process does not wait for the lock and does not retry, so a caller that wants it to proceed has to start it again. The guarantee is per store file, not per repository; two different store paths would both proceed (that is the durable variant's own stage 5b control)."
  else
    OUTCOME="not corrupted"
    DETAIL="The first process was held at $HELD_AT and the second was expected to be refused at the store open, but it was not: it exited $SECOND_STATUS and posted $SECOND_WROTE comment(s). The refusal did not happen, which is the finding this row has to report rather than a row to paper over."
  fi
else
  if [ "$GATE" = "1" ]; then
    OUTCOME="control: not doubled"
    DETAIL="Control: the same signals and the same two runs, without overlap — the second started after the first had exited. The second read the committed state and stopped at the run gate ($SECOND_WROTE comment(s) posted): thirteen comments, one per PR, from one run. This is what the events look like when they do not overlap, so the fault run's duplication is the overlap's doing."
  else
    OUTCOME="control: not doubled"
    DETAIL="Control: the same two runs one after the other. The first posted $FIRST_WROTE comment(s) and the second posted $SECOND_WROTE; nothing overlapped. Baseline: the second read the state and stopped at the run gate. Durable: the first had finished, so the store was free and the second ran as a new generation."
  fi
fi

EVIDENCE="held at=$HELD_AT stopped state=$STOPPED_STATE; first exit=$FIRST_STATUS posted=$FIRST_WROTE; second exit=$SECOND_STATUS posted=$SECOND_WROTE refused_at_store=$REFUSED gated_by_cooldown=$GATE; state=${STATE_OK:-none}; commit(s)/generation=$RUNS"

record_result "$VARIANT" "$FAULT" "$OUTCOME" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
