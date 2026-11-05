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

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

if [ "$VARIANT" = "baseline" ]; then STATE_HINT="$RUN_DIR/repo.git"; else STATE_HINT="$RUN_DIR/store.jsonl"; fi

if [ "$INJECT" = "1" ]; then
  PHASE="run 2 stopped between posting its comments and committing its state; run 3, 61m later, over the same state"
else
  PHASE="run 2 completes and commits its state; run 3, 61m later, over the same state (the control)"
fi

# The clocks are instants, not sleeps: run 3 is past the cooldown because a later scheduled run is.
#
# The fixture clock. The fixture's PRs are dated relative to its own `capturedAt`, so a run at
# `capturedAt` sees every PR at the age the fixture declares, and a run an hour later sees 13 eligible
# PRs because the fixture declares them as 300–10,080 minutes old. Both of those are what the fault
# needs, and both are only true if each run's fixture is captured at that run's instant — so each run
# gets a copy of the fixture whose `capturedAt` is its own clock (`lib/fresh_fixture.py`, a one-field
# copy, not a change to the scenario's fixture).
RUN3_CLOCK="${FAULTS_CLOCK:-2026-11-05T13:31:00Z}"

# ── Run 1: a run that finished, one hour before the faulting one ─────────────────────────────────
#
# Run 1, 2 and 3 read a fixture captured at their own clock, so the per-PR cooldown answers the same way
# at all three instants and the only clock that moves the answer is the run gate's. Without this, run 2
# would be skipped by the per-PR cooldown — every PR's *relative* age is fixed — and never reach Phase 6
# to post the comments the fault is about. That was a real harness defect, found by running it.
# Run 2 is 90 minutes after run 1, on an unchanged fixture: every PR is inside the per-PR cooldown by
# then (the declared ages are 300 minutes and up), so run 2 posts comments and the fault is about its
# state write. Run 3 is one minute past that — outside the run gate — and still inside every PR's
# cooldown, which is what makes its second posting the cooldown's doing rather than a re-triage of
# changed PRs.
RUN2_CLOCK="${CLOCK:-2026-11-05T12:00:00Z}"
RUN2_CLOCK="$("$PYTHON" -c 'import datetime,sys; t=datetime.datetime.fromisoformat(sys.argv[1].replace("Z","+00:00")); print((t+datetime.timedelta(minutes=90)).strftime("%Y-%m-%dT%H:%M:%SZ"))' "$RUN2_CLOCK")"
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

# ── Run 3: 61 minutes later, over the same state ─────────────────────────────────────────────────
# A separate clock, and the reason it is 61: the baseline's run gate refuses a run within 60 minutes of
# the previous one, so a run 10 minutes later would be skipped by a rule that is working. Past the
# hour, the gate cannot be what stops it and a second triage is the run's own decision.
CLOCK_SAVED="$CLOCK"
CLOCK="$RUN3_CLOCK"
FIXTURE="$RUN_DIR/fixture-run3.json"
if [ "$VARIANT" = "baseline" ]; then
  start_baseline "$RUN_DIR" "$STATE_HINT" run3
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

DONE3="$(grep -E 'completed — |triaged [0-9]+ PR' "$RUN_DIR/run3.log" 2>/dev/null | tail -1)"

# ── Run 4: the same store, once more ─────────────────────────────────────────────────────────────
# A new generation of a *finished* task is the shape a later scheduled run has. It asks whether
# whatever run 3 used to avoid the duplicate outlived run 3: a per-task memo does not, a ledger does.
# For the baseline it asks the same question of the second run's state, and reports what it does.
RUN4_CLOCK="${RUN3_CLOCK}"
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
fi

# ── The verdict ──────────────────────────────────────────────────────────────────────────────────
if [ "$INJECT" = "1" ]; then
  if [ "$VARIANT" = "baseline" ]; then
    if [ "$DUPES_COUNT" -ge 1 ] 2>/dev/null; then
      OUTCOME="doubled"
      DETAIL="Run 2 posted $RUN2_POSTED comment(s) and its state write did not happen ($RUN2_STATE; memory/pr-triage was left where run 1 put it), so run 3 — 61 minutes later, past the cooldown — read the state run 1 wrote ($PRIOR_RUN), decided the previous run was old enough to triage again, and posted the same $RUN3_POSTED comment(s) a second time: $DUPES_COUNT PR(s) hold two comments, posted by two runs, with nothing in the state recording that the first posting happened. There is no duplicate detection to fail: the baseline has no record of a comment at all, and the run gate it does have is driven by a timestamp the failed run never wrote."
    else
      OUTCOME="not doubled"
      DETAIL="Run 2 posted $RUN2_POSTED comment(s) and its state write did not happen, but run 3 posted $RUN3_POSTED comment(s) and $DUPES_COUNT PR(s) were found to hold two comments. The expected double comment did not appear; this row reports that rather than the expectation."
    fi
  elif [ "$RUN3_RESUME" = "1" ] && [ "$RUN3_POSTED" = "0" ] && [ "$RUN4_POSTED" = "0" ]; then
    OUTCOME="not doubled"
    DETAIL="Run 2 posted $RUN2_POSTED comment(s) and stopped before the phase committed ($TASK_AFTER_RUN2; the comment ledger held $LEDGER_AFTER_RUN2 entries). Run 3 resumed the same task at '$RUN3_RESUME_PHASE' rather than starting cold, found the comments already recorded, and posted $RUN3_POSTED new one(s) while adopting $RUN3_SKIPPED: the run record is what did not commit, and it did not need to — each comment was memoized as it was made, and the ledger outlived the process that made it. Run 3 finished the run at '$STATE_AFTER3'. Run 4, a new generation of the finished task (what a later scheduled run is), posted $RUN4_POSTED and adopted $RUN4_SKIPPED: the guard outlived the task, not just the process."
  else
    OUTCOME="doubled"
    DETAIL="Run 2 posted $RUN2_POSTED comment(s); run 3 resumed=$RUN3_RESUME and posted $RUN3_POSTED more with $RUN3_SKIPPED adopted. The memo did not survive the missing run-record commit, which is the opposite of the expected behaviour and is reported as the finding."
  fi
else
  if [ "$VARIANT" = "baseline" ]; then
    OUTCOME="control: not doubled"
    DETAIL="Control: the same three runs with the Phase 8 seam removed — run 2 committed its state (memory/pr-triage at '$RUN2_STATE'). Run 3, 61 minutes later, therefore read run 2's state, found it past the cooldown and triaged again, posting $RUN3_POSTED comment(s): $DUPES_COUNT PR(s) hold two comments. The duplicate is still there — the baseline has no identity for a comment — and that is the honest reading: removing the missing state write does not remove the duplicate, it only moves which run is inside the cooldown. What the fault adds is that the failed run's own comments are attributable to nobody. The control proves the harness's three-run shape is not what produced the fault row's numbers; it does not prove the baseline is otherwise safe."
  else
    OUTCOME="control: not doubled"
    DETAIL="Control: the same three runs with the seam removed — run 2 completed and committed ($STATE_AFTER3). Run 3 found a finished task and started a new generation, posting $RUN3_POSTED comment(s) and adopting $RUN3_SKIPPED from the ledger. Nothing doubled: the ledger is what makes an unchanged re-run a no-op, and it does not depend on the run record committing. The fault row's distinction is that the resume, not the ledger alone, is what finished the interrupted run."
  fi
fi

OBS_JSON="$("$PYTHON" - "$RUN2_POSTED" "$RUN3_POSTED" "$RUN3_SKIPPED" "$DUPES_COUNT" "$BASELINE_DUPES" "$RUN3_COLD" "$RUN3_GATE" "$RUN3_RESUME" "${LEDGER_AFTER_RUN2:-0}" "$RUN4_POSTED" "$RUN4_SKIPPED" <<'PY'
import json, sys
(run2_posted, run3_posted, run3_adopted, prs_with_two_comments, prs, cold, gate, resume, ledger,
 run4_posted, run4_adopted) = sys.argv[1:12]
print(json.dumps({
    "run2_comments_posted": int(run2_posted or 0),
    "run3_comments_posted": int(run3_posted or 0),
    "run3_comments_adopted": int(run3_adopted or 0),
    "prs_with_two_comments": int(prs_with_two_comments or 0),
    "prs_doubled": [int(n) for n in (prs or "").split(",") if n.strip()],
    "run3_cold_start": cold == "1",
    "run3_run_gate": gate == "1",
    "run3_resumed": resume == "1",
    "ledger_entries_before_run3": int(ledger or 0),
    "run4_comments_posted": int(run4_posted or 0),
    "run4_comments_adopted": int(run4_adopted or 0),
}, indent=2))
PY
)"

EVIDENCE="run1 exit=${RUN1_STATUS:-n/a} last phase=${RUN1_LAST_PHASE:-n/a} posted=$RUN1_POSTED state=${RUN1_STATE:-none}; run2 exit=$RUN2_STATUS last phase=$RUN2_LAST_PHASE posted=$RUN2_POSTED state after=${RUN2_STATE:-none}${TASK_AFTER_RUN2:+ task=$TASK_AFTER_RUN2}${LEDGER_AFTER_RUN2:+ ledger=$LEDGER_AFTER_RUN2}; run3 at $RUN3_CLOCK exit=$RUN3_STATUS cold start=$RUN3_COLD run gate=$RUN3_GATE resumed=$RUN3_RESUME${RUN3_RESUME_PHASE:+ at phase=$RUN3_RESUME_PHASE} posted=$RUN3_POSTED adopted=$RUN3_SKIPPED; state after run3=${STATE_AFTER3:-none}; run3 read: ${REF_STATE:-n/a} ${PRIOR_RUN:-n/a}; PRs with two comments=[${BASELINE_DUPES}]; run3 says: ${DONE3:-none}${RUN4_DIR:+; run4 exit=${RUN4_STATUS:-n/a} posted=$RUN4_POSTED adopted=$RUN4_SKIPPED}"

record_result "$VARIANT" "$FAULT" "$OUTCOME" "$PHASE" "$DETAIL" "$EVIDENCE" "$OBS_JSON"
exit 0
