#!/usr/bin/env bash
# One run of the Band C Durable variant.
#
#   ./run.sh                        # fresh scratch store and run directory, one generation
#   KEEP=1 ./run.sh                 # keep everything for inspection
#   REUSE=1 ./run.sh                # keep the previous run's store and add a generation to it
#   ./run.sh --now 2027-01-01T00:00:00Z     # move the clock the fixture's ages are relative to
#   BAND_C_EXIT_AFTER_PHASE=comment ./run.sh
#   BAND_C_EXIT_AFTER_COMMENTS=7 ./run.sh
#
# Two runs against one store are how the idempotency is shown by hand: the second reuses every
# assessment and adopts every comment instead of posting it again.
#
#   KEEP=1 ./run.sh && REUSE=1 ./run.sh     # the second one posts nothing
#   BAND_C_EXIT_AFTER_COMMENTS=7 ./run.sh && REUSE=1 ./run.sh   # the seam, then the resume
#
# REUSE=1 keeps a store a previous run left behind. Without it the scratch directory is recreated,
# because a run that starts from the previous run's state is a different experiment and should have
# to say so.
#
# The scratch directory is `.scratch/band-c-durable/` in the worktree root — never inside this tree.
# It is created with an empty `.band-c-durable-scratch` marker; this script removes a directory it
# created and refuses (exit 2, touching nothing) one that already holds content it did not write.
#
# Exit codes: whatever the run exited with, or 2 for bad usage / a refused scratch directory.
# 42 means a seam fired, which is a deliberate stop, not a failure.

set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
band="$(cd "$here/.." && pwd)"
worktree="$(cd "$band/../.." && pwd)"
scratch="${RUN_DIR:-$worktree/.scratch/band-c-durable}"
marker=".band-c-durable-scratch"
store="${DURABLE_STORE:-$scratch/store.jsonl}"
fixture="$band/baseline/fixture/pull-requests.json"
project="$here/BandCDurable.csproj"
dll="$here/bin/Debug/net10.0/BandCDurable.dll"
clock="${NOW:-2026-11-05T12:00:00Z}"

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

reusing=0
if [ -d "$scratch" ]; then
  listing="$(ls -A "$scratch" 2>/dev/null)"
  if [ -f "$scratch/$marker" ] && [ "${REUSE:-0}" = "1" ]; then
    # A store a previous run left behind, adopted on request rather than recreated.
    reusing=1
  elif [ -f "$scratch/$marker" ]; then
    rm -rf "$scratch"
  elif [ -n "$listing" ]; then
    echo "run.sh: refusing to use '$scratch': it already has content and was not created by run.sh," >&2
    echo "        so run.sh will not empty it. Move it aside, or point RUN_DIR somewhere new." >&2
    exit 2
  else
    rmdir "$scratch"
  fi
fi

mkdir -p "$scratch" && : > "$scratch/$marker" || {
  echo "run.sh: could not create the scratch directory '$scratch'" >&2
  exit 2
}

if [ "$reusing" = "1" ]; then
  echo "run.sh: reusing the store a previous run left in '$scratch' (REUSE=1)"
fi

cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then
    echo
    echo "kept scratch: $scratch"
  else
    rm -rf "$scratch"
  fi
}
trap cleanup EXIT

if ! "$dotnet" build "$project" --nologo -v quiet; then
  echo "run.sh: the build failed" >&2
  exit 1
fi

echo
echo "dotnet:    $dotnet ($("$dotnet" --version))"
echo "run:       $scratch"
echo

"$dotnet" "$dll" --store "$store" --run-dir "$scratch/run" --fixture "$fixture" --now "$clock" "$@"
status=$?

echo
if [ "$status" -eq 42 ]; then
  echo "stopped at a seam (exit 42). The store holds the checkpoint; run again with the same RUN_DIR"
  echo "and the same DURABLE_STORE to resume:"
  echo "  KEEP=1 RUN_DIR=$scratch ./run.sh"
  exit 0
fi

if [ "$status" -eq 3 ]; then
  echo "refused: another process holds this store. Nothing was read and nothing was written."
  exit 3
fi

if [ "$status" -ne 0 ]; then
  echo "run.sh: the run exited $status" >&2
  exit "$status"
fi

echo "store:     $store"
echo "output:    $scratch/run/{comments,labels.json,triage-report.md,triage-data.json}"
echo
echo "to see what the store holds, without this program:"
echo "  python3 $here/evidence/_store.py task   $store"
echo "  python3 $here/evidence/_store.py ledger $store"
