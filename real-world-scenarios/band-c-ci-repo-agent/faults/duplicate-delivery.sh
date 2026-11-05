#!/usr/bin/env bash
# Fault: duplicate-delivery. The same triage run is delivered twice.
#
#   ./faults/duplicate-delivery.sh --variant baseline|durable [--fault-not-injected]
#
# The duplicate is a real duplicate on the path a scheduler produces: two processes are started for the
# same repository, at the same clock. They are the same delivery in the sense that matters — the same
# fixture, the same repository, the same scheduled instant — and each one is a separate process, so
# nothing is shared in memory and neither knows the other exists unless the *state* tells it.
#
#   baseline — both read `memory/pr-triage` in Phase 1, both find it empty (a cold start), both triage
#              and both post all thirteen comments. The first `update-ref` moves the branch; the second
#              fails and the run exits 1. The comments are already written: a duplicate delivery costs
#              thirteen duplicated comments, and the losing process's failure is the only sign.
#   durable  — the second process is refused at the store open (exit 3): a persistent Durable store holds
#              an exclusive lock on its file, so a second writer cannot start reading. Nothing is
#              duplicated, and the refusal is immediate and loud.
#
#   --fault-not-injected delivers it once. That is the control: one process, one comment per PR. The
#   difference between the pair is what the second delivery cost.
#
# The same-process rerun (a completed run followed by a second run inside the cooldown) is a *different*
# fault — `cooldown-desync` and `restart-after-checkpoint` cover it. This one is two live writers.
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
FAULT_COMMAND="./faults/duplicate-delivery.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

if [ "$VARIANT" = "baseline" ]; then STATE_HINT="$RUN_DIR/repo.git"; else STATE_HINT="$RUN_DIR/store.jsonl"; fi
if [ "$INJECT" = "1" ]; then
  PHASE="the same triage run delivered twice: two live processes over one repository"
else
  PHASE="the same triage run delivered once (the control)"
fi

# ── The first delivery ────────────────────────────────────────────────────────────────────────────
# The second delivery is started while the first is live and has not committed, which is the overlap
# the fault is about: the read-and-decide window of both runs is open at once. The first is *held* with
# SIGSTOP for exactly as long as it takes to start the second, and for a reason the harness can point
# at: its stdout is block-buffered, so no log line is a reliable "it is still in phase 6" — but its
# Phase 7 report on disk is a fact. Holding there means the first has read its state, posted its
# comments and written its report, and has not committed; the second then reads the same state and
# duplicates it. Without the hold the harness would be racing the two Androids' schedulers, and a
# second delivery that happened to arrive after the first committed would be a different fault.
#
# Durable needs no hold: the lock is taken at the open, before the first line, and the second is
# refused on it.
HELD=0
if [ "$VARIANT" = "baseline" ]; then
  start_baseline "$RUN_DIR" "$STATE_HINT" first
else
  start_durable "$RUN_DIR" "$STATE_HINT" first
fi
FIRST_PID="$STARTED_PID"
track_pid "$FIRST_PID"

if [ "$INJECT" = "1" ]; then
  if [ "$VARIANT" = "baseline" ]; then
    wait_for_file "$RUN_DIR/outputs/first/triage-report.md" 120 || {
      log "duplicate-delivery: the first run never reached Phase 7"
      exit 1
    }
    kill -0 "$FIRST_PID" 2>/dev/null || { log "duplicate-delivery: the first run finished first"; exit 1; }
    kill -STOP "$FIRST_PID" || { log "duplicate-delivery: could not hold the first run"; exit 1; }
    HELD=1
  else
    wait_for_file "$STATE_HINT.lock" 120 || {
      log "duplicate-delivery: the first run never took the store lock"
      exit 1
    }
  fi
  if [ "$VARIANT" = "baseline" ]; then
    start_baseline "$RUN_DIR" "$STATE_HINT" second
  else
    start_durable "$RUN_DIR" "$STATE_HINT" second
  fi
  SECOND_PID="$STARTED_PID"
  track_pid "$SECOND_PID"
  wait_for_exit "$SECOND_PID" 240 || { log "duplicate-delivery: the second delivery did not finish"; exit 1; }
  SECOND_STATUS="$WAIT_STATUS"
  if [ "$HELD" = "1" ]; then
    kill -CONT "$FIRST_PID" || { log "duplicate-delivery: could not release the first run"; exit 1; }
  fi
else
  SECOND_STATUS=""
fi

wait_for_exit "$FIRST_PID" 240 || { log "duplicate-delivery: the first delivery did not finish"; tail -20 "$RUN_DIR/first.log" >&2; exit 1; }
FIRST_STATUS="$WAIT_STATUS"

FIRST_WROTE="$(wrote_count "$RUN_DIR/first.log")"
SECOND_WROTE=0
[ -n "$SECOND_STATUS" ] && SECOND_WROTE="$(wrote_count "$RUN_DIR/second.log")"
REFUSED=0
[ -n "$SECOND_STATUS" ] && grep -q "cannot open the store" "$RUN_DIR/second.log" 2>/dev/null && REFUSED=1
GATE=0
[ -n "$SECOND_STATUS" ] && grep -q "run gate" "$RUN_DIR/second.log" 2>/dev/null && GATE=1
UPDATEREF_FAILED=0
grep -q "git update-ref" "$RUN_DIR/second.log" 2>/dev/null && UPDATEREF_FAILED=1
STATE_OK="$(state_field "$VARIANT" "$STATE_HINT" phase)"
RUNS="$(run_count "$VARIANT" "$STATE_HINT")"
TRIAGED="$(run_ids "$VARIANT" "$STATE_HINT")"

if [ "$INJECT" = "1" ]; then
  if [ "$VARIANT" = "baseline" ]; then
    OUTCOME="doubled"
    if [ "$UPDATEREF_FAILED" = "1" ]; then
      OUTCOME_NOTE="The second write's compare-and-swap failed and that process exited $SECOND_STATUS."
    else
      OUTCOME_NOTE="Both processes exited successfully ($FIRST_STATUS and $SECOND_STATUS) and memory/pr-triage now carries $RUNS commit(s): the second write used the parent it read at Phase 8, not at Phase 1, so the compare-and-swap never saw a conflict and neither run reports an error. The duplicate is entirely silent."
    fi
    DETAIL="Two live processes over one repository each read it as a cold start and each posted all thirteen comments: $FIRST_WROTE from the first and $SECOND_WROTE from the second, so thirteen PRs got two comments each. $OUTCOME_NOTE Nothing in the baseline has an identity for a comment or a run, so the only record of the duplicate is the branch's history, which no next run reads."
  elif [ "$REFUSED" = "1" ]; then
    OUTCOME="refused, not doubled"
    DETAIL="The first process took the store's exclusive lock; the second was refused at the open (exit $SECOND_STATUS, 'cannot open the store') before it read the fixture, so it posted $SECOND_WROTE comment(s) and wrote no state. $FIRST_WROTE comment(s) exist, one per PR, from the one process that proceeded. The duplicate was not deduplicated after the fact — it was prevented. What that costs is also in the record: a refused run does nothing and does not queue, so a delivery that means to wait has to retry, and the refusal is per store file rather than per repository."
  elif [ "$GATE" = "1" ]; then
    OUTCOME="deduplicated at the cooldown"
    DETAIL="The first process finished before the second read its state, so the second found the record and stopped at the run gate ($SECOND_STATUS): $SECOND_WROTE comment(s) by the second delivery, the first's thirteen standing. Nothing doubled, but the duplicate was refused by the run-level cooldown rather than at the store — the two were not in fact concurrent, which the start order in the harness did not prevent this time."
  else
    OUTCOME="doubled"
    DETAIL="Two processes each posted comments ($FIRST_WROTE and $SECOND_WROTE) and the outcome was not a store refusal or a cooldown stop; the second exited $SECOND_STATUS. The duplicate reached the effect."
  fi
else
  OUTCOME="control: single delivery"
  DETAIL="Control: one process over the repository, same fixture, same clock. It posted $FIRST_WROTE comment(s) — one per PR — and left the state at '$STATE_OK' ($RUNS commit(s) / generation). The duplicate run's second process posted $SECOND_WROTE more; the difference between the pair is what a second delivery costs."
fi

EVIDENCE="deliveries=$([ "$INJECT" = "1" ] && echo 2 || echo 1); first exit=$FIRST_STATUS posted=$FIRST_WROTE; second exit=${SECOND_STATUS:-n/a} posted=$SECOND_WROTE refused_at_store=$REFUSED gated_by_cooldown=$GATE update_ref_failed=$UPDATEREF_FAILED; state=${STATE_OK:-none}; commit(s)/generation=$RUNS; triaged in the committed state=[$TRIAGED]"

record_result "$VARIANT" "$FAULT" "$OUTCOME" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
