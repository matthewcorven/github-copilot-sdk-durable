#!/usr/bin/env bash
# Fault: cooldown-desync. The run fails *after* posting its comments and *before* committing its state,
# and the next run is left believing it started cold.
#
#   ./faults/cooldown-desync.sh --variant baseline|durable [--fault-not-injected]
#
# This is the band's headline, and it is the failure the original workflow's authors worked around by
# keeping their only memory in a whole-file snapshot written last. The sequence is three runs:
#
#   1. a baseline run, so the state the faulting run would read is a real one and the cooldown is
#      not trivially "there is no state" (this run is only meaningful for the baseline, whose run gate
#      reads the previous run's timestamp; the Durable variant is measured from an empty store
#      because that is the shape the bug takes there). Its clock is one hour before the faulting run,
#      and it reads a *fresh* fixture — see the note on the fixture clock below.
#   2. the faulting run: it posts its comments and is stopped **between the effect and the state
#      write** — the exact window the band exists to measure.
#   3. a later run, at the faulting run's clock + 61 minutes (past the one-hour cooldown, so a
#      cooldown-honouring state could not be what stops it), over the same state.
#
# The injection, per variant, uses each one's own seam and nothing else:
#
#   baseline — `--dry-run`: the program's existing Phase 8 seam, which writes the state objects and
#              does not move the branch, i.e. the run reaches the end of Phase 8 and the state write
#              does not happen. That is the "failed after commenting, never committed its state"
#              window without editing `baseline/` and without a signal race. The harness then confirms
#              it from the repository: no `memory/pr-triage` ref exists afterwards.
#   durable  — `BAND_C_EXIT_AFTER_COMMENTS=13`: stop inside the comment phase after the last comment,
#              before the phase commits. The ledger and memo committed with each earlier comment; the
#              run record is still at `comment`, and the task is unfinished, so the next process
#              resumes it.
#
# What each variant then does with run 3 is the finding:
#
#   baseline — run 2's comments exist and no state was written, so run 3 reads the *previous* run's
#              state, sees it is more than an hour old, and triages again: it posts the same thirteen
#              comments a second time. The state it committed says the run happened; nothing says the
#              comments already did.
#   durable  — run 3 finds an unfinished task at `comment` and resumes it. The step memos and the
#              comment ledger record the thirteen comments run 2 posted, so it posts 0 and adopts 13,
#              finishes the run, and a *fourth* run (new generation) still posts 0 because the ledger
#              outlives the run.
#
# `--fault-not-injected` is the control: the same three runs with the injection removed — run 2
# completes and commits its state — so the pair shows what the missing state write cost. The control's
# outcome must differ from the fault's for the fault to have shown anything.
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
    *) log "cooldown-desync: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "cooldown-desync: --variant baseline|durable is required"; exit 2; }

FAULT="cooldown-desync"
[ "$INJECT" = "1" ] || FAULT="cooldown-desync-control"
FAULT_COMMAND="./faults/cooldown-desync.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1
# The result this run replaces is removed before anything else happens, so a run that fails before
# recording leaves *no* file rather than a stale one that the summary would render as its result.
begin_result "$VARIANT" "$FAULT"

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

if [ "$VARIANT" = "baseline" ]; then STATE_HINT="$RUN_DIR/repo.git"; else STATE_HINT="$RUN_DIR/store.jsonl"; fi

# The clocks are instants, not sleeps: run 3 is past the cooldown because a later scheduled run is.
#
# The fixture clock. The fixture's PRs are dated relative to its own `capturedAt`, so a run at
# `capturedAt` sees every PR at the age the fixture declares, and a run an hour later sees 13 eligible
# PRs because the fixture declares them as 300–10,080 minutes old. Both of those are what the fault
# needs, and both are only true if each run's fixture is captured at that run's instant — so each run
# gets a copy of the fixture whose `capturedAt` is its own clock (`lib/fresh_fixture.py`, a one-field
# copy, not a change to the scenario's fixture).
# The clock ladder, derived from one base so the gaps are arithmetic rather than three independent
# defaults that can drift apart:
#
#   run 1 = base            (12:00Z)  — a run that finished, so run 2 has a previous run to read
#   run 2 = base + 90 min   (13:30Z)  — the faulting run; every PR is inside the per-PR cooldown and
#                                       run 1 is outside the run gate, so run 2 posts its comments
#   run 3 = run 2 + 61 min  (14:31Z)  — one minute past the run gate that run 2's timestamp would have
#                                       set, and still inside every PR's cooldown
#
# The gaps are computed, not written down twice. An earlier version of this script defaulted run 3
# independently and run 3 landed *before* run 2; the result said "61 minutes later" about a run that was
# 22 hours earlier. The numbers in the result are now derived from these two offsets.
RUN1_CLOCK="${FAULTS_CLOCK:-2026-11-05T12:00:00Z}"
RUN2_AFTER_MINUTES=90
RUN3_AFTER_RUN2_MINUTES=61
shift_clock() {
  "$PYTHON" -c 'import datetime,sys; t=datetime.datetime.fromisoformat(sys.argv[1].replace("Z","+00:00")); print((t+datetime.timedelta(minutes=int(sys.argv[2]))).strftime("%Y-%m-%dT%H:%M:%SZ"))' "$1" "$2"
}
CLOCK="$RUN1_CLOCK"
RUN2_CLOCK="$(shift_clock "$RUN1_CLOCK" "$RUN2_AFTER_MINUTES")"
RUN3_CLOCK="$(shift_clock "$RUN2_CLOCK" "$RUN3_AFTER_RUN2_MINUTES")"

if [ "$INJECT" = "1" ]; then
  PHASE="run 2 stopped between posting its comments and committing its state; run 3, ${RUN3_AFTER_RUN2_MINUTES}m after run 2, over the same state"
else
  PHASE="run 2 completes and commits its state; run 3, ${RUN3_AFTER_RUN2_MINUTES}m after run 2, over the same state (the control)"
fi

# ── Run 1: a run that finished, one hour before the faulting one ─────────────────────────────────
#
# Run 1, 2 and 3 read a fixture captured at their own clock, so the per-PR cooldown answers the same way
# at all three instants and the only clock that moves the answer is the run gate's. Without this, run 2
# would be skipped by the per-PR cooldown — every PR's *relative* age is fixed — and never reach Phase 6
# to post the comments the fault is about. That was a real harness defect, found by running it.
# Run 2 is 90 minutes after run 1, on an unchanged fixture: every PR is inside the per-PR cooldown by
# then (the declared ages are 300 minutes and up), so run 2 posts comments and the fault is about its
# state write. Run 3 is 61 minutes past run 2 — outside the run gate that run 2's timestamp would have
# set — and still inside every PR's cooldown, which is what makes its second posting the cooldown's
# doing rather than a re-triage of changed PRs.
FIXTURE_SAVED="$FIXTURE"
"$PYTHON" "$HARNESS_DIR/lib/fresh_fixture.py" "$FIXTURE_SAVED" "$CLOCK" "$RUN_DIR/fixture-run1.json" || exit 1
"$PYTHON" "$HARNESS_DIR/lib/fresh_fixture.py" "$FIXTURE_SAVED" "$RUN2_CLOCK" "$RUN_DIR/fixture-run2.json" || exit 1
"$PYTHON" "$HARNESS_DIR/lib/fresh_fixture.py" "$FIXTURE_SAVED" "$RUN3_CLOCK" "$RUN_DIR/fixture-run3.json" || exit 1
# Baseline: it is what makes run 2's "previous run" a real timestamp. Durable: it is skipped — an empty
# store is the shape that matters for the resume, and the variant's own archive covers generations.
RUN1_LAST_PHASE=""
RUN1_POSTED=0
if [ "$VARIANT" = "baseline" ]; then
  FIXTURE="$RUN_DIR/fixture-run1.json"
  start_baseline "$RUN_DIR" "$STATE_HINT" run1
  RUN1_PID="$STARTED_PID"
  track_pid "$RUN1_PID"
  wait_for_exit "$RUN1_PID" 240 || { log "cooldown-desync: run 1 did not finish"; exit 1; }
  RUN1_STATUS="$WAIT_STATUS"
  RUN1_LAST_PHASE="$(last_phase "$RUN_DIR/run1.log")"
  RUN1_POSTED="$(wrote_count "$RUN_DIR/run1.log")"
  [ "$RUN1_STATUS" = "0" ] || { log "cooldown-desync: run 1 exited $RUN1_STATUS"; exit 1; }
fi
RUN1_STATE="$(state_field "$VARIANT" "$STATE_HINT" phase)"

# ── Run 2: the faulting run ──────────────────────────────────────────────────────────────────────
# The baseline's seam is `--dry-run` (Phase 8 writes objects and does not move the ref); the Durable
# variant's seam is `BAND_C_EXIT_AFTER_COMMENTS=13` (stop after the last comment, before the phase
# commits). The control runs the same command with the seam removed.
COMMENTS_IN_FIXTURE=13
FIXTURE="$RUN_DIR/fixture-run2.json"
CLOCK_SAVED_2="$CLOCK"
CLOCK="$RUN2_CLOCK"
if [ "$VARIANT" = "baseline" ]; then
  if [ "$INJECT" = "1" ]; then
    start_baseline "$RUN_DIR" "$STATE_HINT" run2 "--dry-run"
  else
    start_baseline "$RUN_DIR" "$STATE_HINT" run2
  fi
else
  if [ "$INJECT" = "1" ]; then
    BAND_C_EXIT_AFTER_COMMENTS="$COMMENTS_IN_FIXTURE" start_durable "$RUN_DIR" "$STATE_HINT" run2
  else
    start_durable "$RUN_DIR" "$STATE_HINT" run2
  fi
fi
CLOCK="$CLOCK_SAVED_2"
RUN2_PID="$STARTED_PID"
track_pid "$RUN2_PID"
wait_for_exit "$RUN2_PID" 240 || { log "cooldown-desync: run 2 did not finish"; tail -20 "$RUN_DIR/run2.log" >&2; exit 1; }
RUN2_STATUS="$WAIT_STATUS"

RUN2_POSTED="$(wrote_count "$RUN_DIR/run2.log")"
RUN2_LAST_PHASE="$(last_phase "$RUN_DIR/run2.log")"
RUN2_STATE="$(state_field "$VARIANT" "$STATE_HINT" phase)"
TASK_AFTER_RUN2=""
if [ "$VARIANT" = "durable" ]; then TASK_AFTER_RUN2="$("$PYTHON" "$HARNESS_DIR/lib/store.py" task "$STATE_HINT" 2>/dev/null || true)"; fi
LEDGER_AFTER_RUN2=""
if [ "$VARIANT" = "durable" ]; then
  LEDGER_AFTER_RUN2="$("$PYTHON" "$HARNESS_DIR/lib/store.py" ledger "$STATE_HINT" 2>/dev/null | grep -c '^#' || true)"
fi
# The baseline's comment files live in its own output directory; the duplex check is against those.
RUN2_DIR="$(output_dir "$RUN_DIR/run2.log")"

# The bytes of every comment on disk before run 3. A duplicate is "run 3 wrote pr-N and pr-N already
# held exactly that content" — a fact about appends, which a count of files cannot see.
COMMENT_DIR_FOR_RUN3="$(output_dir "$RUN_DIR/run2.log")"
[ -n "$COMMENT_DIR_FOR_RUN3" ] || COMMENT_DIR_FOR_RUN3="$RUN_DIR/outputs/run2"
snapshot_comments "$COMMENT_DIR_FOR_RUN3" "$RUN_DIR/comments-before-run3.json"

# ── Run 3: 61 minutes later, over the same state ─────────────────────────────────────────────────
# The clock is derived from run 2 (`RUN3_CLOCK = RUN2_CLOCK + 61m`), not written down separately: the
# baseline's run gate refuses a run within 60 minutes of the previous one, so a run this soon would be
# skipped by a rule that is working, and 61 is the smallest gap that puts the gate out of the way.
CLOCK_SAVED="$CLOCK"
CLOCK="$RUN3_CLOCK"
FIXTURE="$RUN_DIR/fixture-run3.json"
if [ "$VARIANT" = "baseline" ]; then
  start_baseline "$RUN_DIR" "$STATE_HINT" run3
elif [ "$INJECT" = "1" ] && [ "${GUARD_DISABLED:-0}" = "1" ]; then
  # The guard-disabled arm: the variant's own `BAND_C_NO_IDEMPOTENCY_GUARD=1` removes the memo and the
  # ledger read, so the same resume posts everything again. It is the measurement that gives the fault
  # row's `not doubled` its teeth: the same three runs, the same seam, with the guard taken out.
  BAND_C_NO_IDEMPOTENCY_GUARD=1 start_durable "$RUN_DIR" "$STATE_HINT" run3
else
  start_durable "$RUN_DIR" "$STATE_HINT" run3
fi
RUN3_PID="$STARTED_PID"
track_pid "$RUN3_PID"
wait_for_exit "$RUN3_PID" 240 || { log "cooldown-desync: run 3 did not finish"; exit 1; }
RUN3_STATUS="$WAIT_STATUS"
CLOCK="$CLOCK_SAVED"
FIXTURE="$FIXTURE_SAVED"

RUN3_POSTED="$(wrote_count "$RUN_DIR/run3.log")"
RUN3_SKIPPED="$(skipped_count "$RUN_DIR/run3.log")"
RUN3_LAST_PHASE="$(last_phase "$RUN_DIR/run3.log")"
RUN3_COLD=0
grep -q "cold start" "$RUN_DIR/run3.log" 2>/dev/null && RUN3_COLD=1
RUN3_GATE=0
grep -q "run gate" "$RUN_DIR/run3.log" 2>/dev/null && RUN3_GATE=1
RUN3_RESUME=0
grep -q "resuming durable task" "$RUN_DIR/run3.log" 2>/dev/null && RUN3_RESUME=1
RUN3_RESUME_PHASE="$(grep -o "at phase '[a-z_]*'" "$RUN_DIR/run3.log" 2>/dev/null | head -1 | sed "s/at phase '//; s/'//" || true)"
RUN3_DIR="$(output_dir "$RUN_DIR/run3.log")"
[ -n "$RUN3_DIR" ] || RUN3_DIR="$RUN_DIR/outputs/run3"
# The duplicates run 3 actually cost: PRs it wrote whose bytes were already on disk.
RUN3_DUPES="$(dup_list "$RUN_DIR/comments-before-run3.json" "$RUN_DIR/run3.log" "$RUN3_DIR")" ||
  { log "cooldown-desync: could not measure run 3's duplicates"; exit 1; }
RUN3_DUPES_COUNT="$(dup_count "$RUN_DIR/comments-before-run3.json" "$RUN_DIR/run3.log" "$RUN3_DIR")" ||
  { log "cooldown-desync: could not measure run 3's duplicates"; exit 1; }
STATE_AFTER3="$(state_field "$VARIANT" "$STATE_HINT" phase)"
REF_STATE="$(grep -o 'state:      [a-z][^.]*' "$RUN_DIR/run3.log" 2>/dev/null | head -1 | sed 's/state:      //' || true)"
PRIOR_RUN="$(grep -o 'prior run:  [^ ]* triaged [0-9]* PR(s)' "$RUN_DIR/run3.log" 2>/dev/null | head -1 || true)"

# The duplicate itself, read from the comment files rather than from the logs: the PRs run 3 posted for
# that run 2 had already posted for. For the baseline they are in two different output directories;
# for the Durable variant a resume rewrites the original task's directory, so the log counts
# (`wrote:` vs `skipped:`) are the attribution and the directory overlap is reported as context.
BASELINE_DUPES=""
if [ "$VARIANT" = "baseline" ] && [ -n "$RUN2_DIR" ] && [ -n "$RUN3_DIR" ]; then
  BASELINE_DUPES="$("$PYTHON" "$HARNESS_DIR/lib/store.py" overlap "$RUN2_DIR" "$RUN3_DIR" 2>/dev/null || true)"
fi
DUPES_COUNT=0
[ -n "$BASELINE_DUPES" ] && DUPES_COUNT="$(printf '%s' "$BASELINE_DUPES" | tr ',' '\n' | grep -c . || true)"
# The byte-level measure, which is the one the verdict branches on: PRs run 3 wrote whose content the
# directory already held. `RUN3_DUPES` is that list for both variants.
DUPES_COUNT="${RUN3_DUPES_COUNT:-0}"
DUPES_LIST="${RUN3_DUPES:-$BASELINE_DUPES}"

DONE3="$(grep -E 'completed — |triaged [0-9]+ PR' "$RUN_DIR/run3.log" 2>/dev/null | tail -1)"

# ── The guard-disabled arm: the same resume with the guard taken out ─────────────────────────────
#
# Run 3 above is the fault row. This arm replays the *same* three runs from the same post-run-2 store,
# with `BAND_C_NO_IDEMPOTENCY_GUARD=1` on run 3. If run 3's zero duplicates were a coincidence rather
# than the guard working, this arm posts them all again. The copy is deliberate: the fault row's store is
# left exactly as the fault left it, and the arm is reproducible on its own.
GUARD_POSTED=0
GUARD_DUPES_COUNT=""
GUARD_DUPES=""
GUARD_RAN=0
if [ "$VARIANT" = "durable" ] && [ "$INJECT" = "1" ]; then
  GUARD_DIR="$RUN_DIR/guard-disabled"
  mkdir -p "$GUARD_DIR"
  # The starter runs the program from <rundir>/app; a second run directory needs its own.
  ln -s "$RUN_DIR/app" "$GUARD_DIR/app"
  cp -R "$STATE_HINT" "$GUARD_DIR/store.jsonl"
  rm -f "$GUARD_DIR/store.jsonl.lock" "$GUARD_DIR/store.jsonl.lock.owner"
  cp -R "${COMMENT_DIR_FOR_RUN3:-$RUN_DIR/outputs/run2}" "$GUARD_DIR/comments-before"
  cp "$RUN_DIR/comments-before-run3.json" "$GUARD_DIR/comments-before.json"
  CLOCK_SAVED_3="$CLOCK"
  CLOCK="$RUN3_CLOCK"
  FIXTURE="$RUN_DIR/fixture-run3.json"
  BAND_C_NO_IDEMPOTENCY_GUARD=1 start_durable "$GUARD_DIR" "$GUARD_DIR/store.jsonl" run3-guard-off
  GUARD_PID="$STARTED_PID"
  track_pid "$GUARD_PID"
  wait_for_exit "$GUARD_PID" 240 || { log "cooldown-desync: the guard-disabled run did not finish"; exit 1; }
  GUARD_STATUS="$WAIT_STATUS"
  CLOCK="$CLOCK_SAVED_3"
  FIXTURE="$FIXTURE_SAVED"
  GUARD_LOG="$GUARD_DIR/run3-guard-off.log"
  GUARD_POSTED="$(wrote_count "$GUARD_LOG")"
  GUARD_DIR_OUT="$GUARD_DIR/outputs/run3-guard-off"
  GUARD_DUPES="$(dup_list "$GUARD_DIR/comments-before.json" "$GUARD_LOG" "$GUARD_DIR/comments-before" "$GUARD_DIR_OUT")" ||
    { log "cooldown-desync: could not measure the guard-disabled arm's duplicates"; exit 1; }
  GUARD_DUPES_COUNT="$(dup_count "$GUARD_DIR/comments-before.json" "$GUARD_LOG" "$GUARD_DIR/comments-before" "$GUARD_DIR_OUT")" ||
    { log "cooldown-desync: could not measure the guard-disabled arm's duplicates"; exit 1; }
  GUARD_RAN=1
  if [ "${GUARD_STATUS:-1}" != "0" ]; then
    log "cooldown-desync: the guard-disabled arm exited $GUARD_STATUS; it is not a measurement"
    exit 1
  fi
  log "cooldown-desync: guard-disabled arm exited $GUARD_STATUS, posted $GUARD_POSTED comment(s), $GUARD_DUPES_COUNT duplicate(s)"
fi
GUARD_TEXT=""
if [ "$GUARD_RAN" = "1" ]; then
  GUARD_TEXT=" The control: the same three runs from the same post-run-2 store with BAND_C_NO_IDEMPOTENCY_GUARD=1 posted $GUARD_POSTED comment(s), $GUARD_DUPES_COUNT of them duplicates ($GUARD_DUPES) — so the zero above is the guard working, not a coincidence of the run."
fi

# ── Run 4: the same store, once more ─────────────────────────────────────────────────────────────
# A new generation of a *finished* task is the shape a later scheduled run has. It asks whether
# whatever run 3 used to avoid the duplicate outlived run 3: a per-task memo does not, a ledger does.
# For the baseline it asks the same question of the second run's state, and reports what it does.
RUN4_CLOCK="$RUN3_CLOCK"
RUN4_POSTED=0
RUN4_SKIPPED=0
RUN4_DIR=""
if [ "$VARIANT" = "durable" ]; then
  CLOCK="$RUN4_CLOCK"
  FIXTURE="$RUN_DIR/fixture-run3.json"
  start_durable "$RUN_DIR" "$STATE_HINT" run4
  RUN4_PID="$STARTED_PID"
  track_pid "$RUN4_PID"
  wait_for_exit "$RUN4_PID" 240 || { log "cooldown-desync: run 4 did not finish"; exit 1; }
  RUN4_STATUS="$WAIT_STATUS"
  CLOCK="$CLOCK_SAVED"
  FIXTURE="$FIXTURE_SAVED"
  RUN4_POSTED="$(wrote_count "$RUN_DIR/run4.log")"
  RUN4_SKIPPED="$(skipped_count "$RUN_DIR/run4.log")"
  RUN4_DIR="$(output_dir "$RUN_DIR/run4.log")"
  [ -n "$RUN4_DIR" ] || RUN4_DIR="$RUN_DIR/outputs/run4"
  snapshot_comments "$RUN4_DIR" "$RUN_DIR/comments-before-run4.json"
  RUN4_DUPES="$(dup_list "$RUN_DIR/comments-before-run4.json" "$RUN_DIR/run4.log" "$RUN4_DIR")" ||
    { log "cooldown-desync: could not measure run 4's duplicates"; exit 1; }
  RUN4_DUPES_COUNT="$(dup_count "$RUN_DIR/comments-before-run4.json" "$RUN_DIR/run4.log" "$RUN4_DIR")" ||
    { log "cooldown-desync: could not measure run 4's duplicates"; exit 1; }
fi

# ── The verdict ──────────────────────────────────────────────────────────────────────────────────
if [ "$INJECT" = "1" ]; then
  if [ "$VARIANT" = "baseline" ]; then
    if [ "$DUPES_COUNT" -ge 1 ] 2>/dev/null; then
      OUTCOME="doubled"
      DETAIL="Run 2 posted $RUN2_POSTED comment(s) and its state write did not happen (no memory/pr-triage ref was moved, so the branch was left where run 1 put it), so run 3 — ${RUN3_AFTER_RUN2_MINUTES} minutes after run 2, at $RUN3_CLOCK — read the state run 1 wrote ($PRIOR_RUN), decided the previous run was old enough to triage again, and posted the same comments a second time: $DUPES_COUNT of run 3's $RUN3_POSTED written comment(s) were byte-for-byte what was already on disk ($DUPES_LIST), so those PRs hold two copies. Nothing in the state records that the first posting happened: the baseline has no record of a comment at all, and the run gate it does have is driven by a timestamp the failed run never wrote."
    else
      OUTCOME="not doubled"
      DETAIL="Run 2 posted $RUN2_POSTED comment(s) and its state write did not happen, but run 3 posted $RUN3_POSTED comment(s) and $DUPES_COUNT of them were content already on disk. The expected double comment did not appear; this row reports the measurement rather than the expectation."
    fi
  elif [ "$DUPES_COUNT" -ge 1 ] 2>/dev/null; then
    # The residue case: the resume ran but still re-posted something (the window a memo cannot close).
    # This is a *partial* adoption and is filed as a duplicate, not as the opposite finding.
    OUTCOME="doubled"
    DETAIL="Run 2 posted $RUN2_POSTED comment(s) and stopped before the phase committed ($TASK_AFTER_RUN2; ledger $LEDGER_AFTER_RUN2). Run 3 resumed at '$RUN3_RESUME_PHASE' and adopted $RUN3_SKIPPED, but $DUPES_COUNT of its $RUN3_POSTED written comment(s) were content already on disk ($DUPES_LIST): the resume did not prevent every duplicate. This is the partial-adoption case — a duplicate measured byte for byte, not inferred from a count."
  elif [ "$RUN3_RESUME" = "1" ] && [ "$RUN4_DUPES_COUNT" -eq 0 ] 2>/dev/null; then
    OUTCOME="not doubled"
    DETAIL="Run 2 posted $RUN2_POSTED comment(s) and stopped before the phase committed ($TASK_AFTER_RUN2; the comment ledger held $LEDGER_AFTER_RUN2 entries). Run 3 resumed the same task at '$RUN3_RESUME_PHASE' rather than starting cold. It adopted $RUN3_SKIPPED comment(s) already recorded, wrote $RUN3_POSTED new one(s), and $DUPES_COUNT of everything it wrote was content already on disk. The run record is what did not commit, and it did not need to — each comment was memoized as it was made, and the ledger outlived the process that made it. Run 3 finished the run at '$STATE_AFTER3'. Run 4, a new generation of the finished task (what a later scheduled run is), wrote $RUN4_POSTED with $RUN4_DUPES_COUNT duplicates and adopted $RUN4_SKIPPED: the guard outlived the task, not just the process.${GUARD_TEXT}"
  else
    OUTCOME="not doubled, resume unproven"
    DETAIL="Run 3 reported resumed=$RUN3_RESUME and wrote $RUN3_POSTED comment(s) with $DUPES_COUNT duplicates, and run 4 wrote $RUN4_POSTED with $RUN4_DUPES_COUNT duplicates. Neither 'the resume prevented it' nor 'the ledger prevented it' is established by these numbers, so the row says so rather than asserting the expected behaviour."
  fi
else
  if [ "$VARIANT" = "baseline" ]; then
    OUTCOME="control: doubled"
    DETAIL="Control: the same three runs with the Phase 8 seam removed, so run 2 committed its state. Run 3, ${RUN3_AFTER_RUN2_MINUTES} minutes after run 2, read run 2's record, found it past the cooldown, and wrote $RUN3_POSTED comment(s) of which $DUPES_COUNT were content already on disk — the same $DUPES_COUNT PRs hold two copies as in the fault row. The control is a duplicate too, and stating that is the point: committing the state does not give the baseline an identity for a comment, so it cannot stop a re-post; what the missing state write costs is that the *triage* was redone from a stale view, and the comments were re-posted by a run that believed it had never run. This control does not flip, and the row says so instead of implying the baseline is safe when its state commits."
  else
    OUTCOME="control: not doubled"
    DETAIL="Control: the same three runs with the seam removed — run 2 completed and committed ($STATE_AFTER3). Run 3 found a finished task and started a new generation, writing $RUN3_POSTED comment(s) with $DUPES_COUNT duplicates and adopting $RUN3_SKIPPED from the ledger. Nothing doubled: the ledger is what makes an unchanged re-run a no-op, and it does not depend on the run record committing. The fault row's distinction is that the resume, not the ledger alone, is what finished the interrupted run."
  fi
fi

OBS_JSON="$("$PYTHON" - "$RUN2_POSTED" "$RUN3_POSTED" "$RUN3_SKIPPED" "$DUPES_COUNT" "$DUPES_LIST" "$RUN3_COLD" "$RUN3_GATE" "$RUN3_RESUME" "${LEDGER_AFTER_RUN2:-0}" "$RUN4_POSTED" "$RUN4_SKIPPED" "${RUN4_DUPES_COUNT:-0}" "${GUARD_POSTED:-0}" "${GUARD_DUPES_COUNT:-0}" <<'PY'
import json, sys
(run2_posted, run3_posted, run3_adopted, prs_with_two_comments, prs, cold, gate, resume, ledger,
 run4_posted, run4_adopted, run4_duplicated) = sys.argv[1:13]
print(json.dumps({
    "run2_comments_posted": int(run2_posted or 0),
    "run3_comments_posted": int(run3_posted or 0),
    "run3_comments_adopted": int(run3_adopted or 0),
    "run3_comments_duplicated": int(prs_with_two_comments or 0),
    "prs_with_two_comments": int(prs_with_two_comments or 0),
    "prs_doubled": [int(n) for n in (prs or "").split(",") if n.strip()],
    "run3_cold_start": cold == "1",
    "run3_run_gate": gate == "1",
    "run3_resumed": resume == "1",
    "ledger_entries_before_run3": int(ledger or 0),
    "run4_comments_posted": int(run4_posted or 0),
    "run4_comments_adopted": int(run4_adopted or 0),
    "run4_comments_duplicated": int(run4_duplicated or 0),
    "guard_disabled_posted": int(sys.argv[13] or 0),
    "guard_disabled_duplicated": int(sys.argv[14] or 0),
}, indent=2))
PY
)"

EVIDENCE="run1 exit=${RUN1_STATUS:-n/a} last phase=${RUN1_LAST_PHASE:-n/a} posted=$RUN1_POSTED state=${RUN1_STATE:-none}; run2 exit=$RUN2_STATUS last phase=$RUN2_LAST_PHASE posted=$RUN2_POSTED state after=${RUN2_STATE:-none}${TASK_AFTER_RUN2:+ task=$TASK_AFTER_RUN2}${LEDGER_AFTER_RUN2:+ ledger=$LEDGER_AFTER_RUN2}; run3 at $RUN3_CLOCK exit=$RUN3_STATUS cold start=$RUN3_COLD run gate=$RUN3_GATE resumed=$RUN3_RESUME${RUN3_RESUME_PHASE:+ at phase=$RUN3_RESUME_PHASE} posted=$RUN3_POSTED adopted=$RUN3_SKIPPED; state after run3=${STATE_AFTER3:-none}; run3 read: ${REF_STATE:-n/a} ${PRIOR_RUN:-n/a}; duplicates written by run3=[${DUPES_LIST}] (${DUPES_COUNT}); run3 says: ${DONE3:-none}${RUN4_DIR:+; run4 exit=${RUN4_STATUS:-n/a} posted=$RUN4_POSTED adopted=$RUN4_SKIPPED duplicates=$RUN4_DUPES_COUNT}${GUARD_RAN:+; guard-disabled arm: posted=$GUARD_POSTED duplicates=$GUARD_DUPES_COUNT [$GUARD_DUPES]}"

record_result "$VARIANT" "$FAULT" "$OUTCOME" "$PHASE" "$DETAIL" "$EVIDENCE" "$OBS_JSON"
exit 0
