#!/usr/bin/env bash
# Band F2 Durable variant, process 1: create a run and serve its turns.
#
#   ./run.sh                                  # a fresh run in a fresh scratch directory
#   KEEP=1 ./run.sh                           # keep the run directory after this process exits
#   ./run.sh --kill-after-delegating 0        # after turn 0's delegation commits, print the pid and
#                                             #   wait to be killed (evidence.sh reads the pid)
#   RUN_DIR=/path ./run.sh                    # put the run somewhere else
#   BAND_F_ROUTING=scripted ./run.sh          # the scenario decides; no model call for routing
#
# The run directory holds the store and nothing else; the scenario never writes state anywhere else.
# The whole directory is removed on exit unless KEEP=1, so a kill-and-resume sequence is
# `KEEP=1 ./run.sh ...` followed by `KEEP=1 ./resume.sh`.
#
# Scratch provenance: this script never empties or deletes a directory it did not create. Every
# directory it creates is marked with an empty `.band-f-durable-scratch` file.
#   - RUN_DIR unset, or set to a path that does not exist: created and marked.
#   - RUN_DIR exists and is empty: used as it is, left unmarked, and not removed on exit.
#   - RUN_DIR holds only a `.band-f-durable-scratch` marker: a scratch directory a previous run left
#     behind (a kill with KEEP=1), so it is adopted.
#   - RUN_DIR is a `.band-f-durable-scratch` directory that holds anything else, or is non-empty and
#     unmarked: refused with exit 2, and not touched. Pick a new path, or remove it yourself.
#
# run.sh always creates a run. resume.sh never does: a resume must find the directory the previous
# process left, together with the store's record of what it had done to it.
#
# Exit codes: 0 the run completed or stopped where the run's input said to; 1 the run failed;
#             2 nothing to do (usage, a directory this script will not touch, or no dotnet).

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
  echo "run.sh: no dotnet on PATH (expected /opt/homebrew/bin/dotnet)" >&2
  exit 2
fi

refuse() {
  echo "run.sh: refusing to use $1: $2" >&2
  echo "        Point RUN_DIR at a new or empty path, or remove it yourself." >&2
  echo "        run.sh does not empty a directory it cannot prove it created." >&2
  exit 2
}

run_dir_created=0
if [ ! -e "$run_dir" ]; then
  mkdir -p "$run_dir" || {
    echo "run.sh: could not create RUN_DIR '$run_dir'" >&2
    exit 2
  }
  run_dir_created=1
elif [ ! -d "$run_dir" ]; then
  refuse "$run_dir" "it exists and is not a directory"
else
  listing="$(ls -A "$run_dir" 2>/dev/null)"
  if [ -f "$run_dir/$SCRATCH_MARKER" ] && [ "$listing" = "$SCRATCH_MARKER" ]; then
    run_dir_created=1
  elif [ -n "$listing" ]; then
    if [ -f "$run_dir/$SCRATCH_MARKER" ]; then
      refuse "$run_dir" "it is a run.sh scratch directory that already holds a run"
    fi
    refuse "$run_dir" "it already has content and was not created by run.sh"
  fi
  # An existing empty directory is usable, but it is not ours: it stays unmarked and in place.
fi

if [ "$run_dir_created" = 1 ]; then
  : > "$run_dir/$SCRATCH_MARKER" || {
    echo "run.sh: could not write the scratch marker in '$run_dir'" >&2
    exit 2
  }
fi

cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then
    echo "kept: $run_dir"
    return 0
  fi

  # Only a directory this script created is ours to remove, and the marker is what says so.
  if [ "$run_dir_created" = 1 ] && [ -f "$run_dir/$SCRATCH_MARKER" ]; then
    rm -rf "$run_dir"
    return 0
  fi

  echo "not removing non-scratch directory: $run_dir" >&2
}
trap cleanup EXIT

# Built before the run so compiler output never lands in the captured trace.
if ! "$dotnet" build "$here/BandFDurable.csproj" -v quiet --nologo >/dev/null 2>&1; then
  "$dotnet" build "$here/BandFDurable.csproj" --nologo
  echo "run.sh: build failed" >&2
  exit 1
fi

echo "run dir:   $run_dir"
echo "dotnet:    $dotnet ($("$dotnet" --version))"
echo

BAND_F_RUN_DIR="$run_dir" "$dotnet" "$here/bin/Debug/net10.0/BandFDurable.dll" run "$@"
status=$?

echo
if [ "$status" -ne 0 ]; then
  echo "run:       the process exited $status; the store is kept at $run_dir/store.jsonl" >&2
  exit "$status"
fi

echo "store:     $run_dir/store.jsonl"
