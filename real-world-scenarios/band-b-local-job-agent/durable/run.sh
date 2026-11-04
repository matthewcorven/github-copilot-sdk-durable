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
# Everything the run writes lives under RUN_DIR (default .scratch/band-b-durable/ in the
# worktree root) — the scratch target and its durable store — never inside this tree. The whole
# directory is removed on exit unless KEEP=1, so a kill-and-resume sequence is
# `KEEP=1 ./run.sh` followed by `KEEP=1 ./resume.sh`.
#
# RALPH_MODEL overrides the pinned model (gpt-5.1-codex-mini), exactly as in the baseline.
#
# run.sh always creates the target fresh. resume.sh never does: a resume must find the directory
# the previous process left, together with the store's record of what it had done to it.

set -uo pipefail

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

cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then
    echo "kept: $target"
  else
    rm -rf "$run_dir"
  fi
}
trap cleanup EXIT

# A run's target is its own: the baseline template, copied, never the template itself.
rm -rf "$target"
mkdir -p "$target"
cp -R "$band/baseline/target/." "$target/"

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
