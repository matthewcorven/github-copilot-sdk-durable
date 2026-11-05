#!/usr/bin/env bash
# Produces the recordings evidence/durable-kill-and-resume.md is assembled from.
#
#   ./durable/evidence/run-all.sh
#
# Writes to .scratch/band-b-evidence/ (the scratch targets and their stores) and
# .scratch/logs/ (one recording per flow). Then run ./durable/evidence/render.py to build the
# evidence file from those recordings.
#
# Every recording is a whole transcript: the command, its output, and its exit status. `rec`
# writes all three to the same file, so a block lifted from one cannot mix commands from
# different invocations. Nothing here is retyped by hand.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
worktree="$(cd "$root/../.." && pwd)"
cd "$worktree"

band=real-world-scenarios/band-b-local-job-agent/durable
ev=.scratch/band-b-evidence
log=.scratch/logs
mkdir -p "$log"

# rec <transcript> <command...> — append the command, its output, and its exit status.
rec() {
  local out="$1"; shift
  printf '$ %s\n' "$*" >> "$out"
  "$@" >> "$out" 2>&1
  printf 'exit=%s\n\n' "$?" >> "$out"
}

# flow <name> "<command as typed>" <command...> — a whole transcript: the command, its output,
# and its exit status, so a flow block can be lifted without inventing a status.
flow() {
  local name="$1"; local display="$2"; shift 2
  echo "===== $name ====="
  {
    printf '$ %s\n' "$display"
    "$@" 2>&1
    printf 'exit=%s\n' "$?"
  } > "$log/$name.txt"
  tail -1 "$log/$name.txt"
}

# record_diff <name> <other>: the idempotency comparison, recorded rather than retyped.
record_diff() {
  local name="$1"; local other="$2"
  echo "===== $name ====="
  {
    printf '$ cd .scratch/band-b-evidence\n'
    printf '$ diff -r --exclude=.durable reference/target %s\n' "$other"
    ( cd "$ev" && diff -r --exclude=.durable reference/target "$other" 2>&1 )
    printf 'exit=%s\n' "$?"
  } > "$log/$name.txt"
  tail -1 "$log/$name.txt"
}

flow reference "RUN_DIR=.scratch/band-b-evidence/reference KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10" env RUN_DIR=$ev/reference KEEP=1 ./$band/run.sh 10
flow kill-resume-1 "RUN_DIR=.scratch/band-b-evidence/kill-resume KEEP=1 STOP_AFTER_ITERATION=3 ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10" env RUN_DIR=$ev/kill-resume KEEP=1 STOP_AFTER_ITERATION=3 ./$band/run.sh 10
flow kill-resume-2 "RUN_DIR=.scratch/band-b-evidence/kill-resume KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh" env RUN_DIR=$ev/kill-resume KEEP=1 ./$band/resume.sh
record_diff diff-kill-resume kill-resume/target

# The task record at the end of the kill-and-resume flow, and the per-conversation checkpoint
# history of the same store. Both read straight out of the JSONL file by the scripts beside this
# one, so the identifiers in the evidence are the store's own.
{
  echo "\$ python3 real-world-scenarios/band-b-local-job-agent/durable/evidence/read-task.py kill-resume"
  python3 "$root/durable/evidence/read-task.py" kill-resume 2>&1
  printf 'exit=%s\n\n' "$?"
  echo "\$ python3 real-world-scenarios/band-b-local-job-agent/durable/evidence/read-documents.py kill-resume"
  python3 "$root/durable/evidence/read-documents.py" kill-resume 2>&1
  printf 'exit=%s\n\n' "$?"
} > "$log/kill-resume-store.txt"

flow mid-turn-1 "RUN_DIR=.scratch/band-b-evidence/mid-turn KEEP=1 STOP_AFTER_ITERATION=2 STOP_BEFORE_MEMO=1 ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10" env RUN_DIR=$ev/mid-turn KEEP=1 STOP_AFTER_ITERATION=2 STOP_BEFORE_MEMO=1 ./$band/run.sh 10
flow mid-turn-2 "RUN_DIR=.scratch/band-b-evidence/mid-turn KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh" env RUN_DIR=$ev/mid-turn KEEP=1 ./$band/resume.sh
record_diff diff-mid-turn mid-turn/target
flow memo-hit-1 "RUN_DIR=.scratch/band-b-evidence/memo-hit KEEP=1 STOP_AFTER_ITERATION=2 STOP_AFTER_MEMO=1 ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10" env RUN_DIR=$ev/memo-hit KEEP=1 STOP_AFTER_ITERATION=2 STOP_AFTER_MEMO=1 ./$band/run.sh 10
flow memo-hit-2 "RUN_DIR=.scratch/band-b-evidence/memo-hit KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh" env RUN_DIR=$ev/memo-hit KEEP=1 ./$band/resume.sh
record_diff diff-memo-hit memo-hit/target
flow cap-1 "RUN_DIR=.scratch/band-b-evidence/cap KEEP=1 STOP_AFTER_ITERATION=2 ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 3" env RUN_DIR=$ev/cap KEEP=1 STOP_AFTER_ITERATION=2 ./$band/run.sh 3
flow cap-2 "RUN_DIR=.scratch/band-b-evidence/cap KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh" env RUN_DIR=$ev/cap KEEP=1 ./$band/resume.sh
flow cap-3 "RUN_DIR=.scratch/band-b-evidence/cap KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh 9" env RUN_DIR=$ev/cap KEEP=1 ./$band/resume.sh 9

# The restart count on the record after the resume that just ran, next to the banner the resume
# printed before the runner incremented it.
{
  echo "\$ python3 real-world-scenarios/band-b-local-job-agent/durable/evidence/read-restarts.py cap"
  python3 "$root/durable/evidence/read-restarts.py" cap 2>&1
  printf 'exit=%s\n\n' "$?"
} > "$log/cap-restarts.txt"

flow divergence-1 "RUN_DIR=.scratch/band-b-evidence/divergence KEEP=1 STOP_AFTER_ITERATION=2 ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10" env RUN_DIR=$ev/divergence KEEP=1 STOP_AFTER_ITERATION=2 ./$band/run.sh 10

# The hand edit between the two processes: two open items reordered, and section-08 ticked
# although its H1 is still missing. The store is not touched.
t=$log/edit-plan.txt
: > "$t"
rec "$t" python3 real-world-scenarios/band-b-local-job-agent/durable/evidence/edit-plan.py \
  .scratch/band-b-evidence/divergence/target/PLAN.md

flow divergence-2 "RUN_DIR=.scratch/band-b-evidence/divergence KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh" env RUN_DIR=$ev/divergence KEEP=1 ./$band/resume.sh

flow in-flight-1 "RUN_DIR=.scratch/band-b-evidence/in-flight KEEP=1 STOP_AFTER_ITERATION=2 STOP_MID_TURN=1 ./real-world-scenarios/band-b-local-job-agent/durable/run.sh 10" env RUN_DIR=$ev/in-flight KEEP=1 STOP_AFTER_ITERATION=2 STOP_MID_TURN=1 ./$band/run.sh 10

# The store as the resume will find it: the dead turn next to the one before it that finished.
{
  echo "\$ python3 real-world-scenarios/band-b-local-job-agent/durable/evidence/read-documents.py in-flight"
  python3 "$root/durable/evidence/read-documents.py" in-flight 2>&1
  printf 'exit=%s\n\n' "$?"
} > "$log/in-flight-store.txt"

flow in-flight-2 "RUN_DIR=.scratch/band-b-evidence/in-flight KEEP=1 ./real-world-scenarios/band-b-local-job-agent/durable/resume.sh" env RUN_DIR=$ev/in-flight KEEP=1 ./$band/resume.sh

# ── §7: the scratch-provenance controls ───────────────────────────────────
# One transcript per control: every command that makes up the control, in order, with its output
# and exit status.

t=$log/control-hand-written.txt
: > "$t"
hand=$ev/hand-written
rm -rf "$hand"; mkdir -p "$hand/target/.durable"
printf 'keep me\n' > "$hand/notes.txt"
printf '# Plan\n\nStatus: in-progress\n' > "$hand/target/PLAN.md"
printf '{"v":1}\n' > "$hand/target/.durable/store.jsonl"
rec "$t" env RUN_DIR=$hand ./$band/resume.sh
rec "$t" ls -A "$hand"

t=$log/control-pre-populated.txt
: > "$t"
pre=$ev/pre-populated-target
rm -rf "$pre"; mkdir -p "$pre/runs" "$pre/target"
printf 'hand written\n' > "$pre/target/notes.txt"
rec "$t" env RUN_DIR=$pre/runs/new TARGET=$pre/target ./$band/run.sh 1
rec "$t" ls -A "$pre/runs"
rec "$t" ls -A "$pre/target"

t=$log/control-removed.txt
: > "$t"
created=$ev/created-by-run
rm -rf "$created"
rec "$t" env RUN_DIR=$created ./$band/run.sh 1
rec "$t" ls -d "$created"

t=$log/control-resumed.txt
: > "$t"
kept=$ev/created-kept
rm -rf "$kept"
rec "$t" env RUN_DIR=$kept KEEP=1 STOP_AFTER_ITERATION=1 ./$band/run.sh 10
rec "$t" ls -A "$kept"
rec "$t" env RUN_DIR=$kept ./$band/resume.sh
rec "$t" ls -d "$kept"

echo "===== done: recordings in $log ====="
