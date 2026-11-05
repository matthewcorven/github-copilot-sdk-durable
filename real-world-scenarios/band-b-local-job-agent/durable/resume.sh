#!/usr/bin/env bash
# Every later process of the Band B Durable variant: reopen the store run.sh left and continue
# from the iteration the store says is next.
#
#   ./resume.sh                     # continue the run in .scratch/band-b-durable/target
#   KEEP=1 ./resume.sh              # keep the scratch directory after this process exits
#   RUN_DIR=/path ./resume.sh       # resume a run whose scratch area is somewhere else
#
# Nothing is re-created. The target is the directory the previous process left, and the store
# inside it is the record of what that process had done. resume.sh passes no iteration cap: the
# cap is part of the run's stored input, and a second one would defeat the demonstration.
#
# Scratch provenance: the directory is removed on exit unless KEEP=1, and only when run.sh marked
# it as scratch — an empty `.band-b-scratch` file in RUN_DIR. Any other directory is left in place
# with a `not removing non-scratch directory` message, so a hand-written directory is safe to
# point this script at.
#
# Exit statuses match run.sh: 0 complete, 1 the target does not match the plan, 2 nothing to
# resume, 3 the run's cap was reached with the plan still open.

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
  echo "resume.sh: no dotnet on PATH (expected /opt/homebrew/bin/dotnet)" >&2
  exit 2
fi

if [ ! -f "$target/PLAN.md" ]; then
  echo "resume.sh: no target at $target. Run run.sh first and keep it (KEEP=1)." >&2
  exit 2
fi

if [ ! -f "$target/.durable/store.jsonl" ]; then
  echo "resume.sh: no durable store at $target/.durable/store.jsonl; there is nothing to resume." >&2
  exit 2
fi

# Decided up front, so the exit path does not depend on what the resumed run leaves behind. The
# marker records provenance directly: a path check would be wrong for a symlink or a RUN_DIR that
# deliberately sits outside the worktree.
scratch_dir=0
if [ -f "$run_dir/$SCRATCH_MARKER" ]; then
  scratch_dir=1
fi

cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then
    echo "kept: $target"
    return 0
  fi

  if [ "$scratch_dir" = 1 ]; then
    rm -rf "$run_dir"
  else
    echo "not removing non-scratch directory: $run_dir" >&2
  fi
}
trap cleanup EXIT

echo "target:    $target"
echo "dotnet:    $dotnet ($("$dotnet" --version))"
echo

( cd "$target" && "$dotnet" run "$here/ralph-loop-durable.cs" "$@" )
loop_status=$?

echo
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

if ! bash "$band/baseline/verify.sh" "$target"; then
  echo
  echo "the loop stopped but the target does not match the plan"
  echo "target: $target"
  exit 1
fi
