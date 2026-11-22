#!/usr/bin/env bash
# Band F2 Durable variant, every later process: reopen the store run.sh left and give control back to
# the agent the checkpoint names.
#
#   ./resume.sh                     # continue the run in .scratch/band-f-durable
#   KEEP=1 ./resume.sh              # keep the run directory after this process exits
#   RUN_DIR=/path ./resume.sh       # resume a run whose directory is somewhere else
#
# Nothing is re-created. The store is the record of what the previous process had done, and the
# checkpoint in it names the holder. resume.sh passes no kill point of its own: those are part of the
# run's stored input, and a second set would defeat the demonstration.
#
# Scratch provenance: the directory is removed on exit unless KEEP=1, and only when run.sh marked it
# as scratch — an empty `.band-f-durable-scratch` file in RUN_DIR. Any other directory is left in
# place with a `not removing non-scratch directory` message, so a hand-written directory is safe to
# point this script at.
#
# Exit codes match run.sh: 0 the run completed, 1 it failed, 2 nothing to resume.

set -uo pipefail

SCRATCH_MARKER=".band-f-durable-scratch"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
band="$(cd "$here/.." && pwd)"
worktree="$(cd "$band/../.." && pwd)"
run_dir="${RUN_DIR:-$worktree/.scratch/band-f-durable}"

if [ -n "${DOTNET:-}" ]; then
  dotnet="$DOTNET"
elif [ -x /opt/homebrew/bin/dotnet ]; then
  dotnet=/opt/homebrew/bin/dotnet
else
  dotnet="$(command -v dotnet || true)"
fi

if [ -z "$dotnet" ]; then
  echo "resume.sh: no dotnet on PATH (expected /opt/homebrew/bin/dotnet)" >&2
  exit 2
fi

if [ ! -f "$run_dir/store.jsonl" ]; then
  echo "resume.sh: no durable store at $run_dir/store.jsonl; there is nothing to resume." >&2
  echo "           Run run.sh first and keep the directory (KEEP=1)." >&2
  exit 2
fi

if [ ! -f "$here/bin/Debug/net10.0/BandFDurable.dll" ]; then
  "$dotnet" build "$here/BandFDurable.csproj" -v quiet --nologo >/dev/null 2>&1 || {
    echo "resume.sh: the scenario is not built and the build failed" >&2
    "$dotnet" build "$here/BandFDurable.csproj" --nologo
    exit 1
  }
fi

# Decided up front, so the exit path does not depend on what the resumed run leaves behind.
scratch_dir=0
if [ -f "$run_dir/$SCRATCH_MARKER" ]; then
  scratch_dir=1
fi

cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then
    echo "kept: $run_dir"
    return 0
  fi

  if [ "$scratch_dir" = 1 ]; then
    rm -rf "$run_dir"
  else
    echo "not removing non-scratch directory: $run_dir" >&2
  fi
}
trap cleanup EXIT

echo "run dir:   $run_dir"
echo "dotnet:    $dotnet ($("$dotnet" --version))"
echo

BAND_F_RUN_DIR="$run_dir" "$dotnet" "$here/bin/Debug/net10.0/BandFDurable.dll" resume "$@"
status=$?

echo
if [ "$status" -ne 0 ]; then
  echo "resume:    the process exited $status; the store is kept at $run_dir/store.jsonl" >&2
  exit "$status"
fi

echo "store:     $run_dir/store.jsonl"
