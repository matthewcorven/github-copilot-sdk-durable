#!/usr/bin/env bash
# Evidence for the Band G Durable variant. Every claim the README makes is produced here, by running
# the scenario, in a scratch directory this script owns.
#
#   ./evidence.sh                 # run every flow, write evidence/durable-gate-restart.md
#   ./evidence.sh --keep          # leave the scratch directory in place afterwards
#   ./evidence.sh --only memo     # one flow, printed rather than assembled
#
# `--keep` is `KEEP=1` and `--only <flow>` is `ONLY=<flow>`, spelled both ways because a shell script
# is called both ways. The flows are: happy, memo, duplicate, late, abort, control.
#
# The output is written to evidence/durable-gate-restart.md. Every console block in that file is a
# whole recording: the command, its output, and its exit status, captured by this script. Nothing is
# retyped beside its output. The assertion at the end fails the script if a claim's expected line
# never appeared, so a flow that stops proving its point cannot quietly produce a passing report.
#
# Scratch: everything is written under $RUN_ROOT (default .scratch/band-g-evidence/ in the worktree
# root). This script never removes a directory it did not create.
#
# Before/after control: flow 5 replays flow 2 with the layer's re-entry wait removed and shows the
# parent reading a child that has not finished. That is a real mutation of lib/Durable.cs, built and
# run, not a description of one; `git checkout -- lib/Durable.cs` restores it.

set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
band="$(cd "$here/.." && pwd)"
worktree="$(cd "$band/../.." && pwd)"
run_root="${RUN_ROOT:-$worktree/.scratch/band-g-evidence}"
keep="${KEEP:-0}"
only="${ONLY:-}"

# The flags and the environment variables are the same two settings. Parsing them here rather than
# documenting them and ignoring them: a `--keep` that silently did nothing would be worse than absent.
while [ "$#" -gt 0 ]; do
  case "$1" in
    --keep)
      keep=1
      shift
      ;;
    --only)
      if [ "$#" -lt 2 ]; then
        echo "evidence.sh: --only needs a flow name (happy, memo, duplicate, late, abort, control)." >&2
        exit 2
      fi
      only="$2"
      shift 2
      ;;
    --only=*)
      only="${1#--only=}"
      shift
      ;;
    -h|--help)
      sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed -e 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "evidence.sh: unknown argument '$1'" >&2
      echo "             usage: ./evidence.sh [--keep] [--only <flow>]" >&2
      exit 2
      ;;
  esac
done

case "$only" in
  ""|happy|memo|duplicate|late|abort|control) ;;
  *)
    echo "evidence.sh: no flow called '$only'. The flows are: happy, memo, duplicate, late, abort, control." >&2
    exit 2
    ;;
esac

if [ -n "${DOTNET:-}" ]; then
  dotnet="$DOTNET"
elif [ -x /opt/homebrew/bin/dotnet ]; then
  dotnet=/opt/homebrew/bin/dotnet
else
  dotnet="$(command -v dotnet || true)"
fi

if [ -z "$dotnet" ]; then
  echo "evidence.sh: no dotnet on PATH (expected /opt/homebrew/bin/dotnet)" >&2
  exit 2
fi

runtime_path="$worktree/tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime"
fixture_path="$worktree/tools/ReplayRuntime/fixtures/basic.json"
if [ -n "${DURABLE_RUNTIME_PATH:-}" ]; then
  runtime_path="$DURABLE_RUNTIME_PATH"
elif [ ! -x "$runtime_path" ]; then
  "$dotnet" build "$worktree/tools/ReplayRuntime/ReplayRuntime.csproj" -p:CopilotSkipCliDownload=true --nologo -v quiet >/dev/null 2>&1
fi
export DURABLE_RUNTIME_PATH="$runtime_path"
export REPLAY_FIXTURE="${REPLAY_FIXTURE:-$fixture_path}"

source_file="$here/band-g-durable.cs"

if [ -e "$run_root" ]; then
  if [ -f "$run_root/.band-g-evidence" ]; then
    rm -rf "$run_root"
  else
    echo "evidence.sh: refusing to use '$run_root': it exists and this script did not create it." >&2
    echo "             Point RUN_ROOT at a new or empty path, or remove it yourself." >&2
    exit 2
  fi
fi
mkdir -p "$run_root"
: > "$run_root/.band-g-evidence"

# One cleanup, installed wherever the exit path changes, reading `keep` when it runs rather than when
# it was installed. A claim failure sets `keep=1` and re-installs this, so the recordings the failure
# message points at are still there to read.
cleanup() {
  if [ "$keep" = "1" ]; then
    echo "kept: $run_root"
    return 0
  fi

  rm -rf "$run_root"
}
trap cleanup EXIT

recordings="$run_root/recordings"
mkdir -p "$recordings"

# One recording: the command, its whole output, and its exit status, in one file.
record() {
  local name="$1"
  shift
  local file="$recordings/$name.txt"
  {
    printf '$ %s\n' "$*"
    "$@" 2>&1
    printf 'exit=%s\n' "$?"
  } > "$file"
}

# Runs the driver directly, so the script controls the flags and the ordering.
drive() {
  local run_dir="$1"
  shift
  "$dotnet" run "$source_file" -p:CopilotSkipCliDownload=true -- --run "$run_dir" "$@"
}

build() {
  "$dotnet" build "$source_file" -p:CopilotSkipCliDownload=true --nologo -v quiet
}

echo "building the scenario"
echo "──────────────────────────────────────────────────────────────"
if ! build; then
  echo "evidence.sh: the scenario does not build; nothing was run." >&2
  exit 1
fi

wanted() {
  [ -z "$only" ] || [ "$only" = "$1" ]
}

# ── flow 1: the two-process happy path ──────────────────────────────────────────────────────────
# Process 1 drives the run to the gate and leaves. Process 2 records the approval and finishes the
# run. This is the shape the baseline demonstrates, reproduced against the durable store.
if wanted happy; then
  echo
  echo "flow 1: two-process restart/resume"
  echo "──────────────────────────────────────────────────────────────"
  happy="$run_root/happy"
  mkdir -p "$happy"

  record happy-start env RUN_DIR="$happy" GATE_SECONDS=120 KEEP=1 "$here/run.sh"
  record happy-approve env RUN_DIR="$happy" APPROVE=1 AS=alice COMMENT="ship it" KEEP=1 "$here/resume.sh"
  record happy-inspect drive "$happy" inspect
fi

# ── flow 2: kill before a model step commits, and resume ───────────────────────────────────────
# Process 1 leaves the process before the `write` step commits its checkpoint. The steps whose
# checkpoints did commit are on disk; the write step is not. Process 2 resumes and continues.
#
# The measurement is the ledger, and it is taken twice from the same script: once as the variant runs
# and once with the memo bypassed. The difference between the two is what the memo is worth, stated
# as a number rather than as a property. The control also proves the fault was real: without the
# memo the same resume makes one call per step, including the steps already on disk.
#
# What this measures, stated precisely: the kill point is *before* the gate, which is the case the
# baseline also avoids by construction — its pre-gate steps have not run when it stops, so there is
# nothing for it to repeat there. The comparison below is therefore **within this variant**: it shows
# that the memo is what saves the calls, not that the variant beats the baseline at this seam. The
# baseline-relative claim is in the three-way table, where it is marked as likely rather than measured.
if wanted memo; then
  echo
  echo "flow 2: kill before a step commits, resume on the memo"
  echo "──────────────────────────────────────────────────────────────"
  memo="$run_root/memo"
  control="$run_root/memo-control"

  memo_flow() {
    local dir="$1" tag="$2"
    shift 2

    mkdir -p "$dir"
    record "$tag-start" env RUN_DIR="$dir" FAULT=write GATE_SECONDS=120 KEEP=1 "$here/run.sh" "$@"
    record "$tag-after-kill" drive "$dir" inspect
    record "$tag-resume" env RUN_DIR="$dir" KEEP=1 "$here/resume.sh" "$@"
    record "$tag-at-gate" drive "$dir" inspect
    record "$tag-finish" env RUN_DIR="$dir" APPROVE=1 AS=alice COMMENT="approved after the restart" KEEP=1 "$here/resume.sh" "$@"
  }

  memo_flow "$memo" memo
  memo_flow "$control" memo-control --no-memo
fi

# ── flow 3: a duplicate approval cannot overwrite the first ────────────────────────────────────
# Two reviewers approve the same live gate. The first write lands; the second finds one on file and is
# told so. Only then is the run driven, so the refusal comes from the decision document's own
# first-writer-wins rule — not from the "this run is already terminal" check, which is a different
# guard and would prove a different thing.
if wanted duplicate; then
  echo
  echo "flow 3: duplicate approval, both while the gate is live"
  echo "──────────────────────────────────────────────────────────────"
  dup="$run_root/duplicate"
  mkdir -p "$dup"

  record dup-start env RUN_DIR="$dup" GATE_SECONDS=120 KEEP=1 "$here/run.sh"
  record dup-approve drive "$dup" approve --deterministic --as alice --comment "first approval"
  record dup-duplicate drive "$dup" approve --deterministic --as mallory --comment "second approval"
  record dup-before-drive drive "$dup" inspect
  record dup-drive env RUN_DIR="$dup" KEEP=1 "$here/resume.sh"
  record dup-inspect drive "$dup" inspect
fi

# ── flow 4: a late decision cannot overwrite a verdict ─────────────────────────────────────────
# Two things have to be true, and they are two captures because they are two different moments.
#
# 4a: the gate's own deadline fires in a fresh process, and an approval that arrives afterwards cannot
#     turn the timeout into an approval. This is the durable-deadline half of requirement 1 and the
#     "late wakeup" half of requirement 3, in one flow: the gate is re-entered from a checkpoint whose
#     deadline has already passed, so it wakes at once, records a timeout, and the run finishes. The
#     approval that follows finds a decision on file and is refused by the document rule.
#
# 4b: a decision that arrives after the run has finished is refused as late. This is the case the
#     layer's terminal guard exists for, and it is a different refusal from 4a's.
if wanted late; then
  echo
  echo "flow 4: a late decision after the gate's deadline, and after the run"
  echo "──────────────────────────────────────────────────────────────"
  late="$run_root/late"
  mkdir -p "$late"

  # Process 1 stops at the gate. Nothing is decided while the window is open, so process 2 — started
  # after the deadline has passed — is the one that finds it expired.
  record late-start env RUN_DIR="$late" GATE_SECONDS=4 KEEP=1 "$here/run.sh"
  echo "        waiting for the gate's 4s window to pass before the next process opens the store"
  sleep 6
  record late-expire env RUN_DIR="$late" KEEP=1 "$here/resume.sh"
  record late-inspect drive "$late" inspect

  # 4a: the approval comes after the timeout. It must not become the verdict.
  record late-approve-after-timeout drive "$late" approve --deterministic --as alice --comment "approved too late"
  record late-after-timeout-inspect drive "$late" inspect

  # 4b: and a decision arriving after the run finished is refused for being late, not for naming a
  # decision already on file — those are two different guards and the captures distinguish them.
  record late-finish env RUN_DIR="$late" KEEP=1 "$here/resume.sh"
  record late-approve-after-finish drive "$late" approve --deterministic --as mallory --comment "after the run"
fi

# ── flow 5: abort cascades from the parent to its children ────────────────────────────────────
# The run is at its gate: the parent is Waiting and the gate child is Running. Aborting the parent
# aborts the child first — the layer's cascade — and both end terminal, with the parent's verdict an
# abort rather than a decision it never received.
if wanted abort; then
  echo
  echo "flow 5: abort while waiting"
  echo "──────────────────────────────────────────────────────────────"
  abort="$run_root/abort"
  mkdir -p "$abort"

  record abort-start env RUN_DIR="$abort" GATE_SECONDS=300 KEEP=1 "$here/run.sh"
  record abort-before drive "$abort" inspect
  record abort-do drive "$abort" abort
  record abort-after drive "$abort" inspect
  record abort-approve-late drive "$abort" approve --deterministic --as mallory --comment "after the abort"
fi

# ── flow 6: the before/after control for the layer fix ────────────────────────────────────────
# The re-entry wait is removed from lib/Durable.cs, the scenario is rebuilt, and a two-process flow is
# replayed: a run is left at its gate, then a fresh process re-enters the parent and the gate child
# together. Without the wait, the parent's post-gate phase runs before its gate child has finished and
# reads a verdict that does not exist yet. The file is restored from the copy taken below, whatever
# happens, and the restore is checked against that copy rather than against HEAD: HEAD holds the
# pre-fix file while the branch is uncommitted, so a HEAD comparison would say "restored" when it is
# not and "restored" when it is.
if wanted control; then
  echo
  echo "flow 6: the before/after control for the re-entry wait"
  echo "──────────────────────────────────────────────────────────────"
  control="$run_root/control"
  mkdir -p "$control"

  cp "$worktree/lib/Durable.cs" "$run_root/Durable.cs.as-checked-out"
  restore() {
    cp "$run_root/Durable.cs.as-checked-out" "$worktree/lib/Durable.cs"
  }
  on_exit() {
    restore
    cleanup
  }
  trap on_exit EXIT

  if python3 "$here/evidence/remove-reentry-wait.py" "$worktree/lib/Durable.cs"; then
    echo "        mutation: the re-entry wait was removed from lib/Durable.cs"
  else
    echo "evidence.sh: the mutation could not be applied; the control cannot be built." >&2
    restore
    exit 1
  fi

  if build; then
    # The same shape as flow 4: a window long enough that the approval lands inside it, and the
    # resume that follows is exactly the process the wait exists for.
    record control-start env RUN_DIR="$control" GATE_SECONDS=120 KEEP=1 "$here/run.sh"
    record control-resume env RUN_DIR="$control" APPROVE=1 AS=alice COMMENT="approved" KEEP=1 "$here/resume.sh"
    record control-inspect drive "$control" inspect
  else
    echo "evidence.sh: the mutated build failed; the control produced nothing." >&2
  fi

  restore
  trap cleanup EXIT
  if cmp -s "$worktree/lib/Durable.cs" "$run_root/Durable.cs.as-checked-out"; then
    echo "        restored: lib/Durable.cs is byte-identical to the copy taken before the control"
  else
    echo "evidence.sh: lib/Durable.cs was NOT restored; the copy is $run_root/Durable.cs.as-checked-out" >&2
    exit 1
  fi
fi

# ── the assertions ────────────────────────────────────────────────────────────────────────────
# Each flow claims something specific. A claim whose evidence is missing fails the run, so a broken
# flow cannot produce a report that reads as a pass.
echo
echo "checking every claim"
echo "──────────────────────────────────────────────────────────────"

failures=0
claim() {
  local name="$1" file="$2" pattern="$3"
  if [ ! -f "$recordings/$file.txt" ]; then
    echo "FAIL  $name: no recording $file" >&2
    failures=$((failures + 1))
    return
  fi

  if grep -qF -- "$pattern" "$recordings/$file.txt"; then
    echo "ok    $name"
  else
    echo "FAIL  $name: '$pattern' is not in $file" >&2
    failures=$((failures + 1))
  fi
}

rejects() {
  local name="$1" file="$2" pattern="$3"
  if [ ! -f "$recordings/$file.txt" ]; then
    echo "FAIL  $name: no recording $file" >&2
    failures=$((failures + 1))
    return
  fi

  if grep -qF -- "$pattern" "$recordings/$file.txt"; then
    echo "FAIL  $name: '$pattern' must not appear in $file" >&2
    failures=$((failures + 1))
  else
    echo "ok    $name"
  fi
}

if wanted happy; then
  claim "flow 1: process 1 stops at the gate" happy-start "waiting at the gate"
  claim "flow 1: process 2 approves and finishes" happy-approve "Terminal (approved)"
  claim "flow 1: the finished run has four model calls" happy-inspect "4 calls [plan, research, write, brief], 0 memo hits"
fi

if wanted memo; then
  # The variant. Four calls for four steps, and the memo explains why the resume added only one.
  claim "flow 2: process 1 leaves before the write step commits its model call" memo-start "leaving the process before write commits its checkpoint"
  claim "flow 2: three steps are on disk when the process dies" memo-after-kill "3 calls [plan, research, write], 0 memo hits"
  claim "flow 2: the resumed run reaches the gate" memo-resume "waiting at the gate"
  claim "flow 2: the resume added no call at all — the plan is read back" memo-resume "3 calls [plan, research, write], 1 memo hits"
  claim "flow 2: approving adds the one call that was missing" memo-finish "4 calls [plan, research, write, brief], 1 memo hits"

  # The control: the same two-process flow with the memo bypassed. The kill lands in the same place,
  # the resume re-calls what the store already held, and the run ends up one call further along.
  claim "control: the kill leaves the same three steps on disk" memo-control-after-kill "3 calls [plan, research, write], 0 memo hits"
  claim "control: without the memo the resume re-calls the write step" memo-control-resume "4 calls [plan, research, write, write], 0 memo hits"
  claim "control: the same run still reaches the same gate" memo-control-at-gate "4 calls [plan, research, write, write], 0 memo hits"
  claim "control: the same run still finishes approved" memo-control-finish "Terminal (approved)"
fi

if wanted duplicate; then
  claim "flow 3: the first approval is recorded" dup-approve "recorded: alice approved the plan"
  claim "flow 3: the second approval is refused while the run is still live" dup-duplicate "ignored: a decision is already on file (alice, approved)"
  rejects "flow 3: that refusal did not come from the terminal check" dup-duplicate "the workflow is Terminal"
  claim "flow 3: the decision on file is still alice's before the run is driven" dup-before-drive "approved by alice: first approval"
  claim "flow 3: the run finishes on the first approval" dup-inspect "verdict:    approved"
  claim "flow 3: the decision on file is still alice's afterwards" dup-inspect "approved by alice: first approval"
fi

if wanted late; then
  # 4a: the gate's deadline fires, in a process that did not set it. The deadline is the run's, read
  # back from the checkpoint, so a process that starts after it has passed wakes at once.
  claim "flow 4a: the gate wakes past its recorded deadline in a fresh process" late-inspect "Terminal (timed-out)"
  claim "flow 4a: the run ends on the timeout rather than waiting again" late-inspect "outcome:    failed — the plan was not approved (timed-out)"
  claim "flow 4a: an approval after the timeout cannot turn it into a verdict" late-approve-after-timeout "refused: the workflow is Terminal, so this decision is late"
  claim "flow 4a: the verdict is still the timeout" late-after-timeout-inspect "Terminal (timed-out)"

  # 4b: a decision after the run finished is refused as late. The same guard, reached by a different
  # route, and the capture says so rather than implying the document rule refused it.
  claim "flow 4b: a decision after the run is late" late-approve-after-finish "refused: the workflow is Terminal, so this decision is late"
fi

if wanted abort; then
  claim "flow 5: the run is at the gate before the abort" abort-before "t1 Waiting"
  claim "flow 5: the gate child is terminal after the abort" abort-after "Terminal"
  claim "flow 5: the parent's verdict is an abort" abort-after "verdict:    aborted"
  claim "flow 5: an approval after the abort is refused" abort-approve-late "refused: the workflow is Terminal, so this decision is late"
fi

if wanted control; then
  # Without the wait the parent's post-gate phase reads a gate that has not finished, so the run ends
  # on a verdict it never received instead of the approval. With the wait, the same flow completes
  # approved — that is flow 4, and it is the after half of this control.
  claim "flow 6: the gate did finish approved in the control" control-inspect "gate:       t59 Terminal (approved)"
  claim "flow 6: without the wait the parent read the gate before it finished" control-inspect "the gate produced no verdict"
  claim "flow 6: without the wait the run fails instead of completing" control-inspect "outcome:    failed"
fi

echo
if [ "$failures" -ne 0 ]; then
  echo "evidence.sh: $failures claim(s) did not hold. No report was written." >&2
  # Keep the recordings the message above points at: a failure is exactly when they are worth reading,
  # so the trap must not delete them on the way out. `keep` and the installed trap are both updated.
  keep=1
  trap cleanup EXIT
  echo "             The recordings are in $recordings" >&2
  exit 1
fi

echo "every claim held."
echo

if [ -n "$only" ]; then
  echo "one flow was selected, so no report was assembled."
  echo "recordings: $recordings"
  exit 0
fi

python3 "$here/evidence/render.py" \
  --recordings "$recordings" \
  --out "$here/evidence/durable-gate-restart.md" \
  --worktree "$worktree" \
  --dotnet "$("$dotnet" --version)" \
  --runtime "$runtime_path"
