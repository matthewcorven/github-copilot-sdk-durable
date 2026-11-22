#!/usr/bin/env bash
# Two runs of the Band C baseline.
#
#   ./run.sh                 # run 1 triages; run 2 reads the state back and is inside the cooldown
#   KEEP=1 ./run.sh          # keep the scratch directory for inspection
#   RUN2_AFTER_MINUTES=61 ./run.sh   # put run 2 past the cooldown: it triages again
#   NOW=2026-11-05T14:00:00Z ./run.sh   # override the base clock both runs start from
#
# `NOW` is the *base* clock, not run 1's private clock: run 1 starts at it and run 2 starts at
# `NOW + RUN2_AFTER_MINUTES`, so the two can be combined. Passing `NOW` to run 1 alone and letting
# run 2 keep its own default would compare two different clocks, and the cooldown is a comparison
# between them.
#
# Both runs are separate processes against one bare repository under the scratch directory. The two
# output directories (`run-1/`, `run-2/`) are per process; the state branch (`repo.git`) is shared,
# which is the whole point: run 2 has to read run 1's `memory/pr-triage` branch to know it is inside
# the cooldown.
#
# Scratch directory: `.scratch/band-c-triage/` in the worktree root, never inside this tree, so a
# run cannot damage the repo. It carries an empty `.band-c-scratch` marker. The script removes a
# directory it created and refuses (exit 2) a path that already holds content it did not write.
#
# Exit codes: 0 both runs behaved; 1 a run failed; 2 bad usage or a refused scratch directory.

set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
worktree="$(cd "$here/../.." && pwd)"
scratch="$worktree/.scratch/band-c-triage"
marker=".band-c-scratch"
program="$here/baseline/PrTriageBaseline.cs"
fixture="$here/baseline/fixture/pull-requests.json"

if [ -n "${DOTNET:-}" ]; then
  dotnet="$DOTNET"
elif [ -x /opt/homebrew/bin/dotnet ]; then
  dotnet=/opt/homebrew/bin/dotnet
else
  dotnet="$(command -v dotnet || true)"
fi

if [ -z "$dotnet" ]; then
  echo "run.sh: no dotnet on PATH (expected /opt/homebrew/bin/dotnet)" >&2
  exit 2
fi

# Only a directory this script marked is removed. Anything else with content is refused, not emptied.
if [ -e "$scratch" ] && [ ! -d "$scratch" ]; then
  echo "run.sh: '$scratch' exists and is not a directory; refusing to use it." >&2
  exit 2
fi

if [ -d "$scratch" ]; then
  listing="$(ls -A "$scratch" 2>/dev/null)"
  if [ -f "$scratch/$marker" ]; then
    rm -rf "$scratch"
  elif [ -n "$listing" ]; then
    echo "run.sh: refusing to use '$scratch': it already has content and was not created by" >&2
    echo "        run.sh, so run.sh will not empty it. Move it aside or set KEEP=1 after a run." >&2
    exit 2
  else
    rmdir "$scratch"
  fi
fi

mkdir -p "$scratch" && : > "$scratch/$marker" || {
  echo "run.sh: could not create the scratch directory '$scratch'" >&2
  exit 2
}

cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then
    echo "kept scratch: $scratch"
  else
    rm -rf "$scratch"
  fi
}
trap cleanup EXIT

echo "scratch:   $scratch"
echo "dotnet:    $dotnet ($("$dotnet" --version))"
echo "fixture:   $fixture"
echo

# Build once so compiler output does not land inside either run's stdout.
if ! "$dotnet" build "$program" >/dev/null 2>&1; then
  "$dotnet" build "$program"
  echo "run.sh: build failed" >&2
  exit 1
fi

now_args=()
if [ -n "${NOW:-}" ]; then
  now_args=(--now "$NOW")
fi

# Run 2 starts at run 1's base plus the requested shift. `--now` and `--after` compose in the
# baseline (--after shifts whatever base is in effect), so both are always passed: pinning run 2
# to run 1's instant is what made RUN2_AFTER_MINUTES ineffective when NOW was set.
second_now_args=(--after "${RUN2_AFTER_MINUTES:-0}")
if [ -n "${NOW:-}" ]; then
  second_now_args=(--now "$NOW" "${second_now_args[@]}")
fi

echo "════════════════════════════════════════════════════════════════════════"
echo " run 1 — cold start: no memory/pr-triage branch, all eight phases"
echo "════════════════════════════════════════════════════════════════════════"
"$dotnet" run --no-build "$program" -- \
  --run-dir "$scratch/run-1" \
  --repo "$scratch/repo.git" \
  --fixture "$fixture" \
  "${now_args[@]+"${now_args[@]}"}"
status1=$?
if [ "$status1" -ne 0 ]; then
  echo "run.sh: run 1 exited $status1" >&2
  exit 1
fi

echo
echo "════════════════════════════════════════════════════════════════════════"
echo " run 2 — reads run 1's state back and applies the cooldown rule"
echo "════════════════════════════════════════════════════════════════════════"
second="$scratch/run-2.log"
"$dotnet" run --no-build "$program" -- \
  --run-dir "$scratch/run-2" \
  --repo "$scratch/repo.git" \
  --fixture "$fixture" \
  "${second_now_args[@]}" | tee "$second"
status2=${PIPESTATUS[0]}
if [ "$status2" -ne 0 ]; then
  echo "run.sh: run 2 exited $status2" >&2
  exit 1
fi

echo
if [ "${RUN2_AFTER_MINUTES:-0}" -lt 60 ]; then
  if ! grep -q "run skipped by the one-hour cooldown" "$second"; then
    echo "run 2 did not honour the cooldown: the state was read but the run was not skipped" >&2
    rm -f "$second"
    exit 1
  fi
  echo "cooldown honoured: run 2 read the state, did not triage, and did not write state."
else
  if grep -q "run skipped by the one-hour cooldown" "$second"; then
    echo "run 2 was skipped although RUN2_AFTER_MINUTES=${RUN2_AFTER_MINUTES} is past the cooldown" >&2
    rm -f "$second"
    exit 1
  fi
  echo "cooldown expired: run 2 triaged again and wrote a second commit on memory/pr-triage."
fi
rm -f "$second"

echo
echo "state branch:"
"git" --git-dir "$scratch/repo.git" log --oneline memory/pr-triage
echo
echo "run 1 outputs: $scratch/run-1/{comments,triage-report.md,triage-data.json,labels.json}"
echo "run 2 outputs: $scratch/run-2/"
