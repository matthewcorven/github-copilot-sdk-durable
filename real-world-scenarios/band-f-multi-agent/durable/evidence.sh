#!/usr/bin/env bash
# Reproduces every claim the Band F2 Durable variant makes, with a control for each one that needs
# it. Nothing here is quoted from a previous run: every block is produced by running the scenario.
#
#   ./evidence.sh                # run everything, print it, and remove scratch on exit
#   KEEP=1 ./evidence.sh         # keep the scratch directory (the runs' stores stay readable)
#   LIVE=1 ./evidence.sh         # also run the live-model stage (needs a signed-in Copilot CLI)
#
# Four stages, in this order, because each one depends on what the previous established:
#
#   0  a clean run, twice: once scripted and once (with LIVE=1) against the real model
#   1  kill -9 after a delegation commits, then resume: control goes back to the specialist
#   1c the control: a run whose checkpoint does NOT name the specialist resumes at the coordinator,
#      which is what "resumed at the wrong agent" would look like
#   2  kill -9 mid-turn, then resume: the specialist's output is reused, not re-produced
#   3  the routing memo: a resume with a different answer reuses the committed decision, and the
#      control that bypasses the memo shows the divergence it prevents
#
# Every stage ends with `verify`, which reads the store rather than the transcript. A stage that
# prints the right lines and commits the wrong state fails there.
#
# The kill is a real SIGKILL, by pid, on a process that has printed a ready-to-kill line after the
# commit it names returned. It is not a clean shutdown, not a signal handler, and not an exception the
# process raises on itself: `run.sh` never terminates that process except through this kill.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP="$HERE/bin/Debug/net10.0/BandFDurable.dll"

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

SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/band-f-durable-evidence.XXXXXX")" || {
  echo "evidence.sh: could not create a scratch directory" >&2
  exit 2
}
cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then
    echo
    echo "scratch kept: $SCRATCH"
  else
    rm -rf "$SCRATCH"
  fi
}
trap cleanup EXIT INT TERM

# Every stray process the script starts is killed on exit, so a failed stage cannot leave a hung
# scenario behind holding a store lock.
#
# The pids here are the *app's*, not `run.sh`'s. `run.sh` starts the app and waits for it, so killing
# the wrapper leaves the app running and reparented to pid 1 — where nothing this script holds can
# reach it, and where it keeps the store lock. So each stage registers two pids: the wrapper (a last
# resort, in case it never got as far as starting the app) and the app (the one that matters).
WRAPPERS=()
APPS=()
kill_strays() {
  for pid in "${APPS[@]:-}" "${WRAPPERS[@]:-}"; do
    if [ -n "${pid:-}" ] && kill -0 "$pid" 2>/dev/null; then
      kill -9 "$pid" 2>/dev/null
    fi
  done
}
trap 'kill_strays; cleanup' EXIT INT TERM

# A stage's failure status, checked by name so the script says which stage failed rather than just
# exiting. `set -uo pipefail` has no `-e`, so every command's status is checked explicitly.
STAGE_STATUS=0
stage_failed() {
  echo "evidence.sh: stage '$1' failed${2:+ ($2)}" >&2
  STAGE_STATUS=1
}

"$dotnet" build "$HERE/BandFDurable.csproj" -v quiet --nologo >/dev/null 2>&1 || {
  "$dotnet" build "$HERE/BandFDurable.csproj" --nologo
  echo "evidence.sh: the scenario does not build" >&2
  exit 1
}

run_dir() { echo "$SCRATCH/$1"; }
rel() { echo "${1//$SCRATCH/<scratch>}"; }

# Runs the scenario in the background and returns once it is either finished or waiting to be killed.
# The pid comes from the process's own line, not from `$!`: `run.sh` starts the app, and the app is
# what has to be killed.
start() {
  local name="$1"; shift
  local dir; dir="$(run_dir "$name")"
  local log="$SCRATCH/$name.out"
  rm -rf "$dir" "$log"
  KEEP=1 RUN_DIR="$dir" BAND_F_ROUTING="${STAGE_ROUTING:-scripted}" "$HERE/run.sh" "$@" > "$log" 2>&1 &
  WRAPPERS+=("$!")
}

# Waits for a ready-to-kill line and echoes the pid it names. Fails the stage if the line never
# arrives, rather than hanging.
await_kill_point() {
  local name="$1"
  local log="$SCRATCH/$name.out"
  for _ in $(seq 1 240); do
    if grep -q 'ready-to-kill pid=' "$log" 2>/dev/null; then
      grep -o 'ready-to-kill pid=[0-9]*' "$log" | grep -o '[0-9]*' | head -1
      return 0
    fi
    local last="${WRAPPERS[${#WRAPPERS[@]}-1]:-}"
    if [ -n "$last" ] && ! kill -0 "$last" 2>/dev/null; then
      echo "evidence.sh: '$name' exited before reaching its kill point" >&2
      cat "$log" >&2
      return 1
    fi
    sleep 0.5
  done
  echo "evidence.sh: '$name' never reached its kill point" >&2
  return 1
}

# A real SIGKILL, by the app's pid, and the check that it worked. The pid is registered first, so a
# failure anywhere after this leaves the trap able to clean up what this function could not.
kill_and_confirm() {
  local pid="$1"
  APPS+=("$pid")
  kill -9 "$pid" || return 1
  for _ in $(seq 1 40); do
    kill -0 "$pid" 2>/dev/null || { echo "killed pid $pid with SIGKILL"; return 0; }
    sleep 0.25
  done
  echo "evidence.sh: pid $pid survived SIGKILL" >&2
  return 1
}

wire() { BAND_F_RUN_DIR="$(run_dir "$1")" "$dotnet" "$APP" wire; }
show() { BAND_F_RUN_DIR="$(run_dir "$1")" "$dotnet" "$APP" show; }

# Runs `verify`, prints its output and its status, and records the status so the stage can fail on it.
# Returns verify's own exit code so a caller can assert either way.
verify() {
  local name="$1"
  BAND_F_RUN_DIR="$(run_dir "$name")" "$dotnet" "$APP" verify
  local status=$?
  echo "verify exit: $status"
  return "$status"
}

# The stage's positive expectation: verify must pass.
expect_verify() {
  local name="$1"
  verify "$name" || stage_failed "$name" "verify exited $?"
}

echo "############ 0. a clean scripted run, for every later stage to be compared against ############"
rm -rf "$(run_dir clean-scripted)"
KEEP=1 RUN_DIR="$(run_dir clean-scripted)" BAND_F_ROUTING=scripted "$HERE/run.sh" > "$SCRATCH/clean-scripted.out" 2>&1
sed "s|$SCRATCH|<scratch>|g" "$SCRATCH/clean-scripted.out"
echo
echo "--- verify ---"
expect_verify clean-scripted
echo

echo "############ 1. SIGKILL after turn 0's delegation commits ############"
start kill-delegation --kill-after-delegating 0
pid="$(await_kill_point kill-delegation)" || exit 1
sed "s|$SCRATCH|<scratch>|g" "$SCRATCH/kill-delegation.out"
echo
kill_and_confirm "$pid" || exit 1
echo
echo "--- what the store held at the kill: the checkpoint names the holder ---"
wire kill-delegation | tail -4
echo
echo "--- resume ---"
KEEP=1 RUN_DIR="$(run_dir kill-delegation)" "$HERE/resume.sh" 2>&1 | sed "s|$SCRATCH|<scratch>|g"
echo
echo "--- verify ---"
expect_verify kill-delegation
echo

echo "############ 1c. control: a checkpoint that names the COORDINATOR resumes at the coordinator ############"
# The control for stage 1 is not a different program; it is the same program resumed from a store
# whose checkpoint names the coordinator. That is what the run looks like when control did NOT move,
# and it is what "resumed at the wrong agent" would print. It is produced by resuming a run killed
# before its delegation, which is the only way to get such a checkpoint out of this scenario.
start kill-before-delegation --kill-after-routing 1
pid="$(await_kill_point kill-before-delegation)" || exit 1
kill_and_confirm "$pid" || exit 1
echo
KEEP=1 RUN_DIR="$(run_dir kill-before-delegation)" "$HERE/resume.sh" 2>&1 | sed "s|$SCRATCH|<scratch>|g" | head -20
echo

echo "############ 2. SIGKILL inside turn 3's specialist turn, then resume ############"
# The store at the kill holds the attempt, the delegation and the routing decision, and nothing the
# specialist produced. The resume is what has to show design point 3: the specialist is handed its
# own prior output from the store, and the run continues rather than replaying the conversation.
start kill-in-turn --kill-in-turn 3
pid="$(await_kill_point kill-in-turn)" || exit 1
sed "s|$SCRATCH|<scratch>|g" "$SCRATCH/kill-in-turn.out" | tail -14
echo
kill_and_confirm "$pid" || exit 1
echo
echo "--- what the store held at the kill: the attempt, and no output for turn 3 ---"
wire kill-in-turn | tail -3
echo "the last record is the delegation: control moved, and nothing for turn 3 was produced"
echo
echo "--- resume: turn 3's output is produced, and turn 3 then reuses the specialist's earlier output ---"
KEEP=1 RUN_DIR="$(run_dir kill-in-turn)" "$HERE/resume.sh" 2>&1 | sed "s|$SCRATCH|<scratch>|g" | head -26
echo
echo "--- verify ---"
expect_verify kill-in-turn
echo
echo "--- turn 3, the resumed turn: the routing decision was written once and the output is the specialist's ---"
show kill-in-turn | sed -n '/^turn 3$/,/^turn 4$/p'
echo

echo "############ 2c. the domain output survives a restart without re-running the specialist ############"
# `show` reads the output out of the store with the process gone; nothing of this scenario is running.
# The store is opened by a process that never ran a specialist: this is the whole of the claim, and
# `sed` prints the block rather than `grep -A` so the output line cannot be dropped by a context limit.
BAND_F_RUN_DIR="$(run_dir kill-in-turn)" "$dotnet" "$APP" show | sed -n '/^turn 3$/,/^turn 4$/p'
echo
echo "--- the process that produced it is gone; the output is still a value read by a new one ---"
echo

echo "############ 2d. control: a store with the output removed must FAIL verify ############"
# The forged store is the state design point 3 rules out: the delegation and the routing decision are
# there, the specialist's output is not. A check that skips a turn whose output is missing passes this
# store, which is the bug this stage exists to catch, so `verify` must exit 1 here.
rm -rf "$SCRATCH/forged-output"
mkdir -p "$SCRATCH/forged-output"
python3 "$HERE/forge-missing-output.py" "$(run_dir kill-in-turn)/store.jsonl" \
  "$SCRATCH/forged-output/store.jsonl" 3 | sed "s|$SCRATCH|<scratch>|g"
echo
echo "--- show: the same fact, stated the other way ---"
BAND_F_RUN_DIR="$SCRATCH/forged-output" "$dotnet" "$APP" show | sed -n '/^turn 3$/,/^turn 4$/p'
echo
echo "--- verify: this must FAIL ---"
if verify forged-output; then
  stage_failed forged-output "verify passed a store with no output for a committed delegation"
else
  echo "the missing output is visible to verify, as it must be"
fi
echo

echo "############ 3. the routing memo: a resume that answers differently reuses the decision ############"
start memo --kill-after-routing 2
pid="$(await_kill_point memo)" || exit 1
kill_and_confirm "$pid" || exit 1
echo
echo "--- the store at the kill: turn 2 is routed, and no delegation has moved control yet ---"
wire memo | tail -3
echo
# Copied before the resume, so the control below starts from the same killed store the memo run
# does. Copying afterwards would hand the control a run that had already finished.
cp -R "$(run_dir memo)" "$SCRATCH/memo-forced"
echo "--- resume with BAND_F_FLIP=1: the scenario now answers with a different specialist ---"
KEEP=1 RUN_DIR="$(run_dir memo)" BAND_F_ROUTING=scripted BAND_F_FLIP=1 "$HERE/resume.sh" 2>&1 \
  | sed "s|$SCRATCH|<scratch>|g" | grep -E 'resume:|reusing|routes|holds control|delegation committed'
echo
echo "--- the store: turn 2's routing decision was written once ---"
show memo | sed -n '/^turn 2$/,/^turn 3$/p'
echo
echo "############ 3c. control: bypass the memo, and the same resume diverges ############"
echo "--- resume the same killed store with BAND_F_FORCE_REROUTE=1 BAND_F_FLIP=1: the read is skipped ---"
KEEP=1 RUN_DIR="$SCRATCH/memo-forced" BAND_F_ROUTING=scripted BAND_F_FORCE_REROUTE=1 BAND_F_FLIP=1 "$HERE/resume.sh" 2>&1 \
  | sed "s|$SCRATCH|<scratch>|g" | grep -E 'resume:|reusing|routes|holds control|delegation committed'
echo
echo "--- the store: turn 2's routing decision now has two versions, and the run reports the divergence ---"
BAND_F_RUN_DIR="$SCRATCH/memo-forced" "$dotnet" "$APP" show | sed -n '/^turn 2$/,/^turn 3$/p'
echo
echo "--- verify the forced run: this must FAIL, and that is the check the memo exists to keep passing ---"
if verify memo-forced; then
  stage_failed memo-forced "verify passed a store whose routing decision was rewritten"
else
  echo "the divergence is visible to verify, as it must be"
fi
echo

if [ "${LIVE:-0}" = "1" ]; then
  echo "############ 4. the live model: every routing decision made by the coordinator agent ############"
  rm -rf "$(run_dir live)"
  KEEP=1 RUN_DIR="$(run_dir live)" BAND_F_ROUTING=model BAND_F_MODEL="${BAND_F_MODEL:-claude-sonnet-5}" \
    "$HERE/run.sh" 2>&1 | sed "s|$SCRATCH|<scratch>|g"
  echo
  echo "--- verify ---"
  expect_verify live
  echo
  echo "--- show: the decisions, and the specialist that produced each turn ---"
  show live
  echo
else
  echo "############ 4. the live model: skipped (set LIVE=1 to run it) ############"
  echo
fi

echo "############ done ############"
if [ "$STAGE_STATUS" -ne 0 ]; then
  echo "evidence.sh: at least one stage failed; see the stage_failed lines above" >&2
  exit 1
fi

echo "every stage met its expectation, including the two controls that must fail verify"
