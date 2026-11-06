#!/usr/bin/env bash
# Process 1 of the Band B Durable variant: create a run and iterate it.
#
#   ./run.sh                        # fresh scratch target, iterate to the cap or completion
#   KEEP=1 ./run.sh                 # keep the scratch directory after this process exits
#   ./run.sh 3                      # this run's iteration cap (the default is in the source)
#   STOP_AFTER_ITERATION=2 ./run.sh 3
#                                   # commit iteration 2, then stop: the "killed after N" seam
#   RUN_DIR=/path ./run.sh          # put the whole scratch area somewhere else
#
# Every run writes under RUN_DIR (default .scratch/band-b-durable/ in the worktree root) — the
# scratch target and its durable store — never inside this tree. The whole directory is removed on
# exit unless KEEP=1, so a kill-and-resume sequence is `KEEP=1 ./run.sh` followed by
# `KEEP=1 ./resume.sh`.
#
# Scratch provenance: this script never empties or deletes a directory it did not create. Every
# directory it creates is marked with an empty `.band-b-scratch` file.
#   - RUN_DIR unset, or set to a path that does not exist: it is created and marked.
#   - RUN_DIR exists and is empty: it is used as it is, left unmarked, and not removed on exit, so
#     a caller's own empty directory stays theirs.
#   - RUN_DIR holds only a `.band-b-scratch` marker: it is a scratch directory a previous run left
#     behind (a failure with KEEP=1), so it is adopted rather than refused.
#   - RUN_DIR is a `.band-b-scratch` directory that holds anything else, or is non-empty and
#     unmarked: this script refuses with exit 2 and does not touch it. Pick a new path, or remove
#     it yourself. It will not empty a directory on a guess.
# The same rule applies to TARGET: a pre-populated target is refused, never emptied.
#
# RALPH_MODEL overrides the pinned model (gpt-5.1-codex-mini), exactly as in the baseline.
#
# run.sh always creates the target fresh. resume.sh never does: a resume must find the directory
# the previous process left, together with the store's record of what it had done to it.

set -uo pipefail

SCRATCH_MARKER=".band-b-scratch"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
band="$(cd "$here/.." && pwd)"
worktree="$(cd "$band/../.." && pwd)"
run_dir="${RUN_DIR:-$worktree/.scratch/band-b-durable}"
target="${TARGET:-$run_dir/target}"

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
  echo "        Point RUN_DIR (and TARGET) at a new or empty path, or remove it yourself." >&2
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
    # A marker and nothing else: a previous run left its own scratch directory behind (a failure
    # with KEEP=1, or a target that was never created). Adopt it rather than refuse it.
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
    echo "kept: $target"
    return 0
  fi

  # Only a directory this script created is ours to remove, and the marker is what says so. A
  # caller's RUN_DIR is left in place whether or not it is empty.
  if [ "$run_dir_created" = 1 ] && [ -f "$run_dir/$SCRATCH_MARKER" ]; then
    rm -rf "$run_dir"
    return 0
  fi

  echo "not removing non-scratch directory: $run_dir" >&2
  if [ "$target_created" = 1 ]; then
    rm -rf "$target"
  fi
}
# Armed before the target is validated, so a refusal below does not leave a created RUN_DIR behind.
trap cleanup EXIT

target_created=0
if [ ! -e "$target" ]; then
  target_created=1
elif [ ! -d "$target" ]; then
  refuse "$target" "TARGET exists and is not a directory"
elif [ -n "$(ls -A "$target" 2>/dev/null)" ]; then
  refuse "$target" "TARGET already has content"
else
  echo "run.sh: using the existing empty directory as TARGET: $target"
fi

# A run's target is its own: the baseline template, copied, never the template itself. The
# directory is either one this script created above or one it verified empty, so there is nothing
# here to empty.
mkdir -p "$target"
cp -R "$band/baseline/target/." "$target/"

echo "target:    $target"
echo "dotnet:    $dotnet ($("$dotnet" --version))"
echo

( cd "$target" && "$dotnet" run "$here/ralph-loop-durable.cs" "$@" )
loop_status=$?

echo
if [ "$loop_status" -eq 4 ]; then
  echo "refused: another process owns this run's store; nothing was done."
  echo "target: $target"
  exit 4
fi
if [ "$loop_status" -eq 3 ]; then
  echo "iteration cap reached; the plan is still open. See the loop output above."
  echo "target: $target"
  exit 3
fi

if [ "$loop_status" -ne 0 ]; then
  echo "the durable variant exited with status $loop_status"
  echo "target: $target"
  exit "$loop_status"
fi

# A run that stopped at the seam has not finished the job — the plan is still open by
# construction — so it is not verified. verify.sh would report a failure the run never claimed.
if [ -n "${STOP_AFTER_ITERATION:-}" ]; then
  echo "stopped at the seam: iteration ${STOP_AFTER_ITERATION} is committed, the run is not finished."
  echo "resume with: KEEP=1 RUN_DIR=$run_dir ./resume.sh"
  echo "target: $target"
  exit 0
fi

if ! bash "$band/baseline/verify.sh" "$target"; then
  echo
  echo "the loop stopped but the target does not match the plan"
  echo "target: $target"
  exit 1
fi
