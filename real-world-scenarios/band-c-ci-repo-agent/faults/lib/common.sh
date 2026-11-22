#!/usr/bin/env bash
# The shared machinery every Band C fault script uses: build once, run a variant to a named moment,
# fault it, observe it, record one JSON result.
#
# A fault script is a caller of this file, not a copy of it. Four things live here:
#
#   - a *run directory*: scratch that holds the variant's state and its log, recreated per run so
#     nothing is inherited from a previous one;
#   - a *variant*: how to build it and how to start it, out of process, as its own PID;
#   - an *injection point*: the moment the fault happens, named so a reader can point at it. Band A
#     paused a JSON-RPC gate; Band C has no model and no gate, so where a variant offers a seam the
#     seam is used (`BAND_C_EXIT_*`), and where it offers none the fault is an operating-system act on
#     the real process at a marker the harness waits for — never a sleep and a hope, and never an
#     exception thrown *inside* the program to fake a crash;
#   - a *result*: one JSON file per fault per variant, written even when the fault could not be
#     exercised, so "not exercised" is a recorded outcome rather than a missing file.
#
# The record contains the same keys Band A's does — variant, fault, outcome, phase, detail, evidence,
# harness{dotnet, runtime, command}, recordedAt — so the same reader and the same summariser shape
# apply to both harnesses.
#
# The cleanup trap is what makes the whole thing safe to re-run: every PID a script starts is tracked
# and reaped by PID and by run-directory path, and each run directory is removed unless KEEP=1.

set -uo pipefail

HARNESS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BAND_DIR="$(cd "$HARNESS_DIR/.." && pwd)"
REPO_ROOT="$(cd "$BAND_DIR/../.." && pwd)"
RESULTS_DIR="$HARNESS_DIR/results"
SCRATCH_ROOT="${FAULTS_SCRATCH:-$REPO_ROOT/.scratch/band-c-faults}"
FIXTURE="${FAULTS_FIXTURE:-$BAND_DIR/baseline/fixture/pull-requests.json}"
BASELINE_PROGRAM="$BAND_DIR/baseline/PrTriageBaseline.cs"
DURABLE_PROJECT="$BAND_DIR/durable/BandCDurable.csproj"

if [ -n "${DOTNET:-}" ]; then
  DOTNET_BIN="$DOTNET"
elif [ -x /opt/homebrew/bin/dotnet ]; then
  DOTNET_BIN=/opt/homebrew/bin/dotnet
else
  DOTNET_BIN="$(command -v dotnet || true)"
fi

PYTHON="${PYTHON:-python3}"

# The clock every script starts from. It is the fixture's own captured time, so a run reproduces on any
# date; the cooldown questions are asked by moving the clock (`--after`), never by waiting.
CLOCK="${FAULTS_CLOCK:-2026-11-05T12:00:00Z}"

log() { printf '%s\n' "$*" >&2; }

require_tools() {
  local missing=0
  if [ -z "$DOTNET_BIN" ] || [ ! -x "$DOTNET_BIN" ]; then
    log "harness: no dotnet (expected /opt/homebrew/bin/dotnet, 10.0.x)"; missing=1
  fi
  command -v "$PYTHON" >/dev/null 2>&1 || { log "harness: python3 is required"; missing=1; }
  command -v git >/dev/null 2>&1 || { log "harness: git is required (the baseline's state is a git branch)"; missing=1; }
  return $missing
}

# Publishes a variant once per run into the run directory's `app/`. The baseline is a file-based app
# (`dotnet run file.cs`), which `dotnet publish` refuses here (it tries NativeAOT and needs a clang
# toolchain), so it is built with `-o` and launched as a DLL — the same program, out of process, with a
# PID a fault can signal. That is a change to how the harness *starts* the baseline, not to
# `baseline/`: the file is not edited, and the launch is the same entry point `run.sh` uses.
prepare_variant() {
  local variant="$1" out="$2"
  case "$variant" in
    baseline)
      [ -f "$out/PrTriageBaseline.dll" ] && return 0
      log "harness: building baseline"
      "$DOTNET_BIN" build "$BASELINE_PROGRAM" -o "$out" --nologo -v quiet >&2 2>&1
      [ -f "$out/PrTriageBaseline.dll" ] || { log "harness: the baseline did not build"; return 1; }
      ;;
    durable)
      [ -f "$out/BandCDurable.dll" ] && return 0
      log "harness: building durable"
      "$DOTNET_BIN" build "$DURABLE_PROJECT" -o "$out" --nologo -v quiet >&2 2>&1
      [ -f "$out/BandCDurable.dll" ] || { log "harness: the durable variant did not build"; return 1; }
      ;;
    *)
      log "harness: unknown variant '$variant'"
      return 2
      ;;
  esac
}

# ─── Starting a variant ───────────────────────────────────────────────────────────────────────────
#
# Both starters record the PID of the *program* (not of a wrapper) in <rundir>/<name>.pid and leave it
# in the global STARTED_PID. The subshell `exec`s the program, so the PID the shell started is the PID
# the operating system will signal and the PID `wait` reports on — which is what makes `kill -9` a kill
# of the real process and an exit status a fact rather than a guess. A starter must therefore be called
# directly, never inside `$(...)`: a command substitution would put the child in a subshell that exits.
#
# Baseline: the state is the bare repository at <rundir>/repo.git; every run is a cold start.
# Durable:  the state is the JSONL store; the process decides resume-or-new from the task record.

STARTED_PID=""

# The output directory is removed before each start. A process that was `kill -9`'d can leave a
# `comments/pr-NNN.md` *directory* on its way to writing a file — the open and the create are two
# syscalls — and `Directory.CreateDirectory` (the durable variant) then throws on the path it wants to
# own. That is the killed process's residue, not the fault, so the starter clears the directory it is
# about to hand the run.
start_baseline() {
  local rundir="$1" repo="$2" name="$3"
  shift 3
  rm -rf "$rundir/outputs/$name"
  mkdir -p "$rundir/outputs/$name"
  (
    cd "$rundir/app" || exit 1
    exec "$DOTNET_BIN" PrTriageBaseline.dll \
      --run-dir "$rundir/outputs/$name" --repo "$repo" --fixture "$FIXTURE" \
      --now "$CLOCK" "$@"
  ) >"$rundir/$name.log" 2>&1 &
  STARTED_PID=$!
  echo "$STARTED_PID" > "$rundir/$name.pid"
  cp "$rundir/$name.pid" "$rundir/pid"
}

start_durable() {
  local rundir="$1" store="$2" name="$3"
  shift 3
  rm -rf "$rundir/outputs/$name"
  mkdir -p "$rundir/outputs/$name"
  (
    cd "$rundir/app" || exit 1
    exec "$DOTNET_BIN" BandCDurable.dll \
      --store "$store" --run-dir "$rundir/outputs/$name" --fixture "$FIXTURE" \
      --now "$CLOCK" "$@"
  ) >"$rundir/$name.log" 2>&1 &
  STARTED_PID=$!
  echo "$STARTED_PID" > "$rundir/$name.pid"
  cp "$rundir/$name.pid" "$rundir/pid"
}

pid_of() { cat "$1/$2.pid" 2>/dev/null || true; }

# ─── Waiting for a named moment ───────────────────────────────────────────────────────────────────
#
# `wait_for_file`, `wait_for_line` and `wait_for_comments` are how a fault script learns that the
# program has reached the phase it means to fault. Each is a condition on an artefact the program
# itself produced — a log line it printed, a file it wrote, a count of comments in its output directory
# — polled with a deadline, so a program that never arrives is a harness error rather than a fault
# that silently did not happen.

wait_for_file() {
  local path="$1" seconds="${2:-60}"
  local deadline=$((SECONDS + seconds))
  while [ ! -f "$path" ]; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 0.02
  done
  return 0
}

# wait_for_line <log> <fixed-string> [seconds]. Reads the log the process writes, not its pipe.
wait_for_line() {
  local path="$1" text="$2" seconds="${3:-60}"
  local deadline=$((SECONDS + seconds))
  while [ ! -f "$path" ] || ! grep -qF -- "$text" "$path" 2>/dev/null; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 0.02
  done
  return 0
}

wait_for_pid_exit() {
  local pid="$1" seconds="${2:-240}"
  local deadline=$((SECONDS + seconds))
  while kill -0 "$pid" 2>/dev/null; do
    [ "$SECONDS" -lt "$deadline" ] || return 124
    sleep 0.05
  done
  return 0
}

# Waits for `pid` to exit and leaves its status in WAIT_STATUS. Only a PID this shell started can be
# waited on; a starter used correctly (directly, not in a command substitution) guarantees that.
wait_for_exit() {
  local pid="$1" seconds="${2:-240}"
  WAIT_STATUS=""
  wait_for_pid_exit "$pid" "$seconds" || return 124
  if wait "$pid" 2>/dev/null; then WAIT_STATUS=0; else WAIT_STATUS=$?; fi
  return 0
}

# `kill_tree` reaps a PID and its children: the cleanup trap's tool. `kill_hard` is the fault itself —
# SIGKILL to the PID the shell started, then a confirmed poll, so "killed" is a fact rather than a
# signal that was sent.
kill_tree() {
  local pid="${1:-}"
  [ -n "$pid" ] || return 0
  local child
  for child in $(pgrep -P "$pid" 2>/dev/null || true); do kill_tree "$child"; done
  kill -9 "$pid" 2>/dev/null || true
  local deadline=$((SECONDS + 10))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.05; done
}

kill_hard() {
  local pid="$1" seconds="${2:-15}"
  kill -9 "$pid" 2>/dev/null || return 1
  local deadline=$((SECONDS + seconds))
  while kill -0 "$pid" 2>/dev/null; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 0.05
  done
  return 0
}

kill_soft() {
  local pid="$1" seconds="${2:-30}"
  kill -TERM "$pid" 2>/dev/null || return 1
  local deadline=$((SECONDS + seconds))
  while kill -0 "$pid" 2>/dev/null; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 0.05
  done
  return 0
}

# Kills a started process and any stray whose command line names the run directory. It leaves the run
# directory alone: a script that means to restart over the same state must not have the state removed
# underneath it.
stop_process() {
  local rundir="$1" name="$2"
  local pid
  pid="$(pid_of "$rundir" "$name")"
  kill_tree "$pid"
  local stray
  for stray in $(pgrep -f "$rundir" 2>/dev/null || true); do
    [ "$stray" = "$$" ] && continue
    kill -9 "$stray" 2>/dev/null || true
  done
  rm -f "$rundir/$name.pid"
}

# ─── Reading the two stores, straight from the bytes ─────────────────────────────────────────────
#
# A fault's observation must not depend on a process still being alive, and must not trust what it
# printed. These read the artefact itself — the baseline's blob on `memory/pr-triage`, the Durable
# variant's JSONL — through lib/store.py, a reader with no opinion about either program.
#
# `comments_in` counts the Phase 6 output of one run: one file per commented PR. That is what "the same
# comment twice" means here, because the workflow's `add-comment` safe output is a file.

comments_in() {
  local dir="$1/outputs/$2/comments"
  [ -d "$dir" ] || { echo 0; return 0; }
  ls -1 "$dir" 2>/dev/null | grep -c '\.md$' || true
}

# The two counts a comment question is asked with, read from the run's own log. They are deliberately
# different from the on-disk file count: the Durable variant adopts a comment it has already posted by
# rewriting its file, so "how many files exist" and "how many comments were posted again" are two
# different numbers and the second one is the question. `wrote:` is a new post; `skipped:` is an
# adoption.
wrote_count() { grep -c "^  wrote:      comments/pr-[0-9]*\.md" "$1" 2>/dev/null || true; }
skipped_count() { grep -c "^  skipped:    comments/pr-[0-9]*\.md" "$1" 2>/dev/null || true; }

# last_phase <log> — the last `phase n/8` header the run printed, or empty.
last_phase() {
  grep -o 'phase [0-9]/8  [a-z ]*' "$1" 2>/dev/null | tail -1 | sed 's/phase [0-9]\/8  //' || true
}

# output_dir <log> — the directory the run says it writes to. A Durable resume continues the original
# task, whose input still names the *first* run's output directory, so this is how a fault script finds
# where a resumed run's comment files actually landed instead of assuming it is its own --run-dir.
output_dir() { grep -m1 '^output:     ' "$1" 2>/dev/null | sed 's/^output:     //' || true; }

# The comment effect, measured as *duplicates* rather than as counts.
#
# A comment file is written once per PR and is rewritten (with identical bytes) when a run adopts it, so
# counting files cannot see "the same comment posted twice"; counting `wrote:` lines can only see how
# many times a process *said* it wrote, not whether the content was already there. `dup_count` asks the
# stricter question: PRs this run wrote whose content was already on disk, byte for byte, when the run
# started. That is true only for a genuine duplicate post.
#
# Usage: snapshot_comments <comments-dir> <state.json>   capture the bytes before the run
#        dup_count <state.json> <log> <comments-dir>     PRs written whose bytes were already there
#        dup_list  <state.json> <log> <comments-dir>     the same, comma separated
snapshot_comments() {
  "$PYTHON" "$HARNESS_DIR/lib/store.py" snapshot "$1" "$2"
}

# A reader that fails must not be read as "zero duplicates". The list is written to a temp file first and
# the command's status is checked, so an error is a harness error rather than a silently empty answer —
# which is exactly how a false "0" reaches a result string.
dup_list() {
  # dup_list <snapshot.json> <run-log> <comments-dir>...
  local snapshot="$1" log="$2"
  shift 2
  local out status
  out="$("$PYTHON" "$HARNESS_DIR/lib/store.py" duplicates "$snapshot" "$log" "$@" 2>&1)"
  status=$?
  if [ "$status" != "0" ]; then
    log "harness: could not measure duplicates: $out"
    return 1
  fi
  printf '%s' "$out" | paste -sd, -
}

dup_count() {
  local list
  list="$(dup_list "$@")" || return 1
  if [ -z "$list" ]; then echo 0; else printf '%s' "$list" | tr ',' '\n' | grep -c .; fi
}

# state_field <variant> <path> <field> — one field of the committed state, or empty.
state_field() {
  "$PYTHON" "$HARNESS_DIR/lib/store.py" state "$1" "$2" "$3" 2>/dev/null || true
}

# run_ids <variant> <path> — the PR numbers in the committed run's triaged list, comma separated.
run_ids() {
  "$PYTHON" "$HARNESS_DIR/lib/store.py" triaged "$1" "$2" 2>/dev/null || true
}

# run_count <variant> <path> — how many runs the state remembers.
run_count() {
  "$PYTHON" "$HARNESS_DIR/lib/store.py" runs "$1" "$2" 2>/dev/null || true
}

# Removes the result this run is about to replace, before the run starts.
#
# Every script writes into results/, and a harness-error path exits before record_result is reached. If
# the previous file were left in place, a failed run would leave a stale document that summarise.py would
# render as this run's result — a full matrix row describing a run that did not happen. Unlinking first
# makes a failed run a *missing* result, which summarise.py reports and fails on.
begin_result() {
  rm -f "$RESULTS_DIR/$1/$2.json"
}

write_result() {
  local variant="$1" fault="$2" json="$3"
  mkdir -p "$RESULTS_DIR/$variant"
  printf '%s\n' "$json" > "$RESULTS_DIR/$variant/$fault.json"
  log "harness: wrote faults/results/$variant/$fault.json"
}

# The one way a result is recorded. `detail` says what was observed; `evidence` carries the raw
# observations the claim comes from, so a reader can check the sentence against them.
#
# `observations` is optional and, when given, is a JSON object of the numbers a comparison table needs
# to quote (the cooldown-desync fault's duplicate counts, for instance). It is additive: a result with
# no observations has exactly the keys Band A's results have, and one with them still has all of those.
record_result() {
  local variant="$1" fault="$2" outcome="$3" phase="$4" detail="$5" evidence="${6:-}" observations="${7:-}"
  local json
  json="$("$PYTHON" - "$variant" "$fault" "$outcome" "$phase" "$detail" "$evidence" "$observations" <<'PY'
import datetime, json, os, sys
variant, fault, outcome, phase, detail, evidence, observations = sys.argv[1:8]
doc = {
    "variant": variant,
    "fault": fault,
    "outcome": outcome,
    "phase": phase,
    "detail": detail,
    "evidence": evidence,
    "harness": {
        "dotnet": os.environ.get("DOTNET_BIN") or os.environ.get("DOTNET") or "dotnet",
        "runtime": "none (Band C has no model and no recorded runtime)",
        "command": os.environ.get("FAULT_COMMAND", ""),
    },
    "recordedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
}
if observations:
    doc["observations"] = json.loads(observations)
print(json.dumps(doc, indent=2, ensure_ascii=False))
PY
)"
  write_result "$variant" "$fault" "$json"
}

record_not_exercised() {
  local variant="$1" fault="$2" phase="$3" reason="$4" evidence="${5:-}"
  record_result "$variant" "$fault" "not exercised" "$phase" "$reason" "$evidence"
}

new_run_dir() {
  local variant="$1" fault="$2"
  local dir="$SCRATCH_ROOT/$variant/$fault"
  rm -rf "$dir"
  mkdir -p "$dir"
  echo "$dir"
}

# ─── Cleanup ─────────────────────────────────────────────────────────────────────────────────────
#
# Every fault script installs this by sourcing this file. It stops what the run started — by PID, and
# by run-directory path for anything the shell did not start — and removes the run's scratch, which is
# what makes a second run start from nothing. KEEP=1 keeps the directories, the only way to look at a
# failure afterwards.
CLEANUP_DIRS=()
CLEANUP_PIDS=()
cleanup_all() {
  local status=$?
  local pid dir
  for pid in ${CLEANUP_PIDS[@]+"${CLEANUP_PIDS[@]}"}; do kill_tree "$pid"; done
  for dir in ${CLEANUP_DIRS[@]+"${CLEANUP_DIRS[@]}"}; do
    for pid in $(pgrep -f "$dir" 2>/dev/null || true); do
      [ "$pid" = "$$" ] && continue
      kill -9 "$pid" 2>/dev/null || true
    done
    if [ "${KEEP:-0}" != "1" ]; then
      rm -rf "$dir"
      # `new_run_dir` creates the variant's directory too; remove it when the last run has gone, so a
      # finished suite leaves the scratch root as empty as it found it.
      rmdir "$(dirname "$dir")" 2>/dev/null || true
    fi
  done
  return $status
}
trap cleanup_all EXIT INT TERM

track_pid() { CLEANUP_PIDS+=("$1"); }
track_dir() { CLEANUP_DIRS+=("$1"); }
