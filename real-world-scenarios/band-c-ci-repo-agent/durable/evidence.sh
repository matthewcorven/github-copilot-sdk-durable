#!/usr/bin/env bash
# Reproduces the Durable variant's evidence: the same triage as the baseline, idempotent comments
# across a resume, and the concurrency refusal.
#
#   ./evidence.sh            # run every stage, print the capture, remove the scratch directory
#   KEEP=1 ./evidence.sh     # keep the scratch directory for inspection
#   RACE_ROUNDS=10 ./evidence.sh
#
# Every stage is a real invocation of the built program against a real Durable store under
# `.scratch/band-c-durable-evidence/` in the worktree root — never inside this tree. Each `run`
# prints the command, its output and its exit status together, and writes the same transcript to a
# log, so nothing in the evidence file has to be retyped.
#
# Two things are deliberate:
#   * stage 5 starts N processes at once against one store and requires exactly one to proceed. It
#     is repeated RACE_ROUNDS times, because one race is a coincidence.
#   * the controls (2b, 4b, 5b) are the before half of every claim: the same command with the guard
#     removed, or against separate stores, where the expected behaviour is the opposite.
#
# Exit codes: 0 every stage matched; 1 a stage did not; 2 bad usage or no dotnet.

set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
band="$(cd "$here/.." && pwd)"
worktree="$(cd "$band/../.." && pwd)"
scratch="$worktree/.scratch/band-c-durable-evidence"
marker=".band-c-durable-scratch"
fixture="$band/baseline/fixture/pull-requests.json"
baseline="$band/baseline/PrTriageBaseline.cs"
project="$here/BandCDurable.csproj"
dll="$here/bin/Debug/net10.0/BandCDurable.dll"
logs="$scratch/logs"
clock="2026-11-05T12:00:00Z"
rounds="${RACE_ROUNDS:-5}"

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

if [ -e "$scratch" ] && [ ! -d "$scratch" ]; then
  echo "evidence.sh: '$scratch' exists and is not a directory; refusing to use it." >&2
  exit 2
fi

if [ -d "$scratch" ]; then
  listing="$(ls -A "$scratch" 2>/dev/null)"
  if [ -f "$scratch/$marker" ]; then
    rm -rf "$scratch"
  elif [ -n "$listing" ]; then
    echo "evidence.sh: refusing to use '$scratch': it already has content and was not created by" >&2
    echo "             evidence.sh, so evidence.sh will not empty it. Move it aside first." >&2
    exit 2
  else
    rmdir "$scratch"
  fi
fi

mkdir -p "$scratch" "$logs" && : > "$scratch/$marker" || {
  echo "evidence.sh: could not create the scratch directory '$scratch'" >&2
  exit 2
}

cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then
    echo
    echo "kept scratch: $scratch"
  else
    rm -rf "$scratch"
  fi
}
trap cleanup EXIT

failures=0

say() {
  echo
  echo "════════════════════════════════════════════════════════════════════════════════════════"
  echo " $*"
  echo "════════════════════════════════════════════════════════════════════════════════════════"
}

# run <log-name> <display> <env assignment>... -- <program args>...
# Prints the command, then its output, then its exit status; keeps the same transcript in a log.
run() {
  local name="$1" display="$2"; shift 2
  local envs=()
  while [ "$1" != "--" ]; do envs+=("$1"); shift; done
  shift

  {
    echo "\$ $display"
    if [ "${#envs[@]}" -eq 0 ]; then
      "$@" 2>&1
    else
      env "${envs[@]}" "$@" 2>&1
    fi
    echo "[exit $?]"
  } > "$logs/$name.raw" 2>&1

  # The transcript is filtered for the reader; the status comes from the command, not from sed.
  local status
  status=$(sed -n 's/^\[exit \([0-9]*\)\]$/\1/p' "$logs/$name.raw" | tail -1)
  sed "s|$scratch|<scratch>|g; s|$worktree|<worktree>|g" "$logs/$name.raw" | tee "$logs/$name.txt"
  return "${status:-1}"
}

# exactly <expected> <actual> <what>
exactly() {
  if [ "$1" = "$2" ]; then
    echo "  ✓ $3: $2"
  else
    echo "  ✗ $3: expected $1, got $2"
    failures=$((failures + 1))
  fi
}

# store <command> <store> — a plain read of the store, printed as the command and its output.
store() {
  echo "\$ python3 evidence/_store.py $1 <scratch>/${2#"$scratch"/}"
  python3 "$here/evidence/_store.py" "$1" "$2"
}

echo "scratch:   $scratch"
echo "dotnet:    $dotnet ($("$dotnet" --version))"
echo "fixture:   $fixture"
echo "clock:     $clock"
echo "rounds:    $rounds"

# ── 0. Build both ───────────────────────────────────────────────────────────────────────────
say "0. build both variants"
if ! "$dotnet" build "$project" --nologo -v quiet > "$logs/build-durable.txt" 2>&1; then
  cat "$logs/build-durable.txt"
  echo "evidence.sh: the durable variant did not build" >&2
  exit 1
fi
echo "built:     $dll"
if ! "$dotnet" build "$baseline" --nologo -v quiet > "$logs/build-baseline.txt" 2>&1; then
  cat "$logs/build-baseline.txt"
  echo "evidence.sh: the baseline did not build" >&2
  exit 1
fi
echo "built:     the baseline"

# ── 1. Same fixture, same clock, same output documents ──────────────────────────────────────
say "1. requirement 1 — the same fixture through both variants"
run baseline "dotnet run baseline/PrTriageBaseline.cs -- --run-dir <scratch>/baseline --repo <scratch>/baseline/repo.git --fixture pull-requests.json --now $clock" \
  -- "$dotnet" run --no-build "$baseline" -- \
  --run-dir "$scratch/baseline" --repo "$scratch/baseline/repo.git" --fixture "$fixture" --now "$clock"
exactly 0 "$?" "the baseline exited"
echo
run same-1 "dotnet $dll --store <scratch>/same/store.jsonl --run-dir <scratch>/same/run-1 --fixture pull-requests.json --now $clock" \
  -- "$dotnet" "$dll" --store "$scratch/same/store.jsonl" --run-dir "$scratch/same/run-1" \
  --fixture "$fixture" --now "$clock"
exactly 0 "$?" "the durable variant exited"
echo
echo "\$ python3 evidence/compare.py --outputs <scratch>/baseline <scratch>/same/run-1"
python3 "$here/evidence/compare.py" --outputs "$scratch/baseline" "$scratch/same/run-1" \
  | tee "$logs/compare-outputs.txt"
exactly 0 "${PIPESTATUS[0]}" "every output document is byte-identical"
echo
echo "\$ python3 evidence/compare.py --code"
python3 "$here/evidence/compare.py" --code | tee "$logs/compare-code.txt"
exactly 0 "${PIPESTATUS[0]}" "the shared triage code is identical line for line"

# ── 2. Re-run unchanged: no new comment ─────────────────────────────────────────────────────
say "2. requirement 2 — re-run unchanged does not re-comment"
run same-2 "dotnet $dll --store <scratch>/same/store.jsonl --run-dir <scratch>/same/run-2 --fixture pull-requests.json --now $clock" \
  -- "$dotnet" "$dll" --store "$scratch/same/store.jsonl" --run-dir "$scratch/same/run-2" \
  --fixture "$fixture" --now "$clock"
exactly 0 "$?" "the second run exited"
posted=$(grep -c 'wrote:      comments/' "$logs/same-2.txt")
skipped=$(grep -c 'skipped:    comments/' "$logs/same-2.txt")
exactly 0 "$posted" "comments posted by run 2"
exactly 13 "$skipped" "comments run 2 adopted rather than re-posted"
echo
store ledger "$scratch/same/store.jsonl" | head -3
echo
echo "\$ diff -r <scratch>/same/run-1/comments <scratch>/same/run-2/comments"
diff -r "$scratch/same/run-1/comments" "$scratch/same/run-2/comments" > /dev/null 2>&1
exactly 0 "$?" "run 2's comment files are byte-identical to run 1's"

# ── 2b. The control: the same re-run with the guard removed ─────────────────────────────────
say "2b. control — the same re-run with BAND_C_NO_IDEMPOTENCY_GUARD=1"
run same-3 "BAND_C_NO_IDEMPOTENCY_GUARD=1 dotnet $dll --store <scratch>/same/store.jsonl --run-dir <scratch>/same/run-3 --fixture pull-requests.json --now $clock" \
  env BAND_C_NO_IDEMPOTENCY_GUARD=1 -- "$dotnet" "$dll" --store "$scratch/same/store.jsonl" \
  --run-dir "$scratch/same/run-3" --fixture "$fixture" --now "$clock"
exactly 0 "$?" "the guard-disabled run exited"
reposted=$(grep -c 'wrote:      comments/' "$logs/same-3.txt")
exactly 13 "$reposted" "comments re-posted with the guard off"
# The guard-disabled run's ledger grew by thirteen entries, not by one. The store shows it as a
# count of entries, which is what a reader can check without trusting the line above.
entries=$(python3 "$here/evidence/_store.py" ledger "$scratch/same/store.jsonl" | head -1)
exactly "ledger entries: 26" "$entries" "the control added thirteen entries to the ledger (13 + 13)"

# ── 3. Interrupted after commenting, then resumed ───────────────────────────────────────────
say "3. requirement 3 — interrupted after commenting, then resumed"
run interrupted-1 "BAND_C_EXIT_AFTER_PHASE=comment dotnet $dll --store <scratch>/interrupted/store.jsonl --run-dir <scratch>/interrupted/run --fixture pull-requests.json --now $clock" \
  env BAND_C_EXIT_AFTER_PHASE=comment -- "$dotnet" "$dll" --store "$scratch/interrupted/store.jsonl" \
  --run-dir "$scratch/interrupted/run" --fixture "$fixture" --now "$clock"
exactly 42 "$?" "the seam exited 42 after the comment phase committed"
exactly 13 "$(ls "$scratch/interrupted/run/comments" | wc -l | tr -d ' ')" "comments on disk at the seam"
mkdir -p "$scratch/interrupted/before"
cp "$scratch"/interrupted/run/comments/*.md "$scratch/interrupted/before/"
echo
run interrupted-2 "dotnet $dll --store <scratch>/interrupted/store.jsonl --run-dir <scratch>/interrupted/run --fixture pull-requests.json --now $clock" \
  -- "$dotnet" "$dll" --store "$scratch/interrupted/store.jsonl" --run-dir "$scratch/interrupted/run" \
  --fixture "$fixture" --now "$clock"
exactly 0 "$?" "the resume exited"
exactly 13 "$(ls "$scratch/interrupted/run/comments" | wc -l | tr -d ' ')" "comments on disk after the resume"
exactly 0 "$(grep -c 'wrote:      comments/' "$logs/interrupted-2.txt")" "comments the resume wrote"
echo
echo "\$ diff -r <scratch>/interrupted/before <scratch>/interrupted/run/comments"
diff -r "$scratch/interrupted/before" "$scratch/interrupted/run/comments" > /dev/null 2>&1
exactly 0 "$?" "every comment file is unchanged by the resume"
echo
store task "$scratch/interrupted/store.jsonl" | sed -n '1p;$p'

# ── 4. Interrupted *inside* the comment phase ───────────────────────────────────────────────
say "4. the harder seam — stopped after 7 of 13 comments, inside the comment phase"
run midway-1 "BAND_C_EXIT_AFTER_COMMENTS=7 dotnet $dll --store <scratch>/midway/store.jsonl --run-dir <scratch>/midway/run --fixture pull-requests.json --now $clock" \
  env BAND_C_EXIT_AFTER_COMMENTS=7 -- "$dotnet" "$dll" --store "$scratch/midway/store.jsonl" \
  --run-dir "$scratch/midway/run" --fixture "$fixture" --now "$clock"
exactly 42 "$?" "the seam exited 42 after the seventh comment"
echo
echo "\$ python3 evidence/_store.py memos <scratch>/midway/store.jsonl"
python3 "$here/evidence/_store.py" memos "$scratch/midway/store.jsonl" | tee "$logs/midway-memos.txt"
exactly 7 "$(wc -l < "$logs/midway-memos.txt" | tr -d ' ')" "step memos committed by the interrupted process"
echo
run midway-2 "dotnet $dll --store <scratch>/midway/store.jsonl --run-dir <scratch>/midway/run --fixture pull-requests.json --now $clock" \
  -- "$dotnet" "$dll" --store "$scratch/midway/store.jsonl" --run-dir "$scratch/midway/run" \
  --fixture "$fixture" --now "$clock"
exactly 0 "$?" "the resume exited"
exactly 7 "$(grep -c 'skipped:    comments/' "$logs/midway-2.txt")" "comments the resume skipped by memo"
exactly 6 "$(grep -c 'wrote:      comments/' "$logs/midway-2.txt")" "comments the resume posted"
exactly 13 "$(ls "$scratch/midway/run/comments" | wc -l | tr -d ' ')" "comments on disk afterwards"

# ── 4b. The control: the same mid-phase resume with the guard removed ───────────────────────
say "4b. control — the same mid-phase resume with the guard removed"
run midway-off-1 "BAND_C_EXIT_AFTER_COMMENTS=7 BAND_C_NO_IDEMPOTENCY_GUARD=1 dotnet $dll --store <scratch>/midway-off/store.jsonl ..." \
  env BAND_C_EXIT_AFTER_COMMENTS=7 BAND_C_NO_IDEMPOTENCY_GUARD=1 -- \
  "$dotnet" "$dll" --store "$scratch/midway-off/store.jsonl" --run-dir "$scratch/midway-off/run" \
  --fixture "$fixture" --now "$clock" > /dev/null 2>&1
run midway-off-2 "BAND_C_NO_IDEMPOTENCY_GUARD=1 dotnet $dll --store <scratch>/midway-off/store.jsonl --run-dir <scratch>/midway-off/run ..." \
  env BAND_C_NO_IDEMPOTENCY_GUARD=1 -- "$dotnet" "$dll" --store "$scratch/midway-off/store.jsonl" \
  --run-dir "$scratch/midway-off/run" --fixture "$fixture" --now "$clock"
exactly 0 "$?" "the guard-disabled resume exited"
exactly 13 "$(grep -c 'wrote:      comments/' "$logs/midway-off-2.txt")" "comments re-posted by the control resume"
exactly 0 "$(grep -c 'skipped:    comments/' "$logs/midway-off-2.txt")" "comments the control resume skipped"

# ── 5. Concurrency ─────────────────────────────────────────────────────────────────────────
say "5. requirement 4 — $rounds rounds of 2 simultaneous runs against one store"
total_refused=0
for round in $(seq 1 "$rounds"); do
  directory="$scratch/race-$round"
  mkdir -p "$directory"
  echo
  echo "── round $round"
  pids=()
  for index in 1 2; do
    "$dotnet" "$dll" --store "$directory/store.jsonl" --run-dir "$directory/run-$index" \
      --fixture "$fixture" --now "$clock" > "$directory/run-$index.log" 2>&1 &
    pids+=($!)
  done
  statuses=()
  for index in 1 2; do
    wait "${pids[$((index - 1))]}"
    statuses+=($?)
  done
  echo "  run-1 exit=${statuses[0]}   run-2 exit=${statuses[1]}"
  completed=0
  refused=0
  for status in "${statuses[@]}"; do
    if [ "$status" -eq 0 ]; then completed=$((completed + 1)); fi
    if [ "$status" -eq 3 ]; then refused=$((refused + 1)); fi
  done
  exactly 1 "$completed" "runs that proceeded"
  exactly 1 "$refused" "runs refused at the store open"
  total_refused=$((total_refused + refused))
  grep -h "cannot open the store" "$directory"/run-*.log 2>/dev/null | head -1 \
    | sed "s|$scratch|<scratch>|g; s|^/|/|" | sed 's/^/  refusal: /'
done
echo
exactly "$rounds" "$total_refused" "refusals across $rounds rounds"

# ── 5b. The control: two stores, both proceed ───────────────────────────────────────────────
say "5b. control — the same two processes against separate stores"
mkdir -p "$scratch/control-a" "$scratch/control-b"
"$dotnet" "$dll" --store "$scratch/control-a/store.jsonl" --run-dir "$scratch/control-a/run" \
  --fixture "$fixture" --now "$clock" > "$logs/control-a.txt" 2>&1 &
pid_a=$!
"$dotnet" "$dll" --store "$scratch/control-b/store.jsonl" --run-dir "$scratch/control-b/run" \
  --fixture "$fixture" --now "$clock" > "$logs/control-b.txt" 2>&1 &
pid_b=$!
wait "$pid_a"; status_a=$?
wait "$pid_b"; status_b=$?
echo "  control-a exit=$status_a"
echo "  control-b exit=$status_b"
exactly 0 "$status_a" "process A against its own store"
exactly 0 "$status_b" "process B against its own store"

# ── 6. Read the store back ──────────────────────────────────────────────────────────────────
say "6. what the store itself says about the interrupted and resumed run"
store task "$scratch/midway/store.jsonl" | tee "$logs/midway-task.txt"
echo
store ledger "$scratch/midway/store.jsonl" | head -4
echo
store prs "$scratch/midway/store.jsonl" | head -4
echo
store versions "$scratch/midway/store.jsonl"

# ── 7. The line count for the matrix ───────────────────────────────────────────────────────
say "7. custom durability code, counted"
echo "\$ python3 evidence/count.py"
python3 "$here/evidence/count.py" | tee "$logs/count.txt"
exactly 0 "${PIPESTATUS[0]}" "every declaration is classified and the split adds up"

# ── 8. The gap this variant found, reproduced ──────────────────────────────────────────────
say "8. the layer gap (filed as #201): a memo written inside a commit does not return"
run blocked "dotnet run evidence/blocked-input.cs" \
  -- "$dotnet" run "$here/evidence/blocked-input.cs"
exactly 7 "$?" "the pre-fix shape is confirmed unreachable (7 = the watchdog fired)"

echo
echo "════════════════════════════════════════════════════════════════════════════════════════"
if [ "$failures" -eq 0 ]; then
  echo " all stages matched their expectations."
else
  echo " $failures stage(s) did not match. See the ✗ lines above."
fi
echo "════════════════════════════════════════════════════════════════════════════════════════"
exit $([ "$failures" -eq 0 ] && echo 0 || echo 1)
