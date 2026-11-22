#!/usr/bin/env bash
# One run of the Ralph Loop baseline, against a scratch target directory.
#
#   ./run.sh              # run, verify, delete the target
#   KEEP=1 ./run.sh       # run, verify, keep the target for inspection
#   ./run.sh 3            # override the iteration cap for this run
#
# The target is created fresh under .scratch/ next to this script. It is never the repo.
#
# RALPH_MODEL=<model> overrides the pinned model (gpt-5.1-codex-mini). The loop reads it;
# run.sh only passes it through.

set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
worktree="$(cd "$here/../.." && pwd)"
target="$worktree/.scratch/band-b-target"

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
    echo "kept target: $target"
  else
    rm -rf "$target"
  fi
}
trap cleanup EXIT

rm -rf "$target"
mkdir -p "$target"
cp -R "$here/baseline/target/." "$target/"

echo "target:    $target"
echo "dotnet:    $dotnet ($("$dotnet" --version))"
echo "watch it:  tail -f $target/PLAN.md"
echo "           ls -R $target/docs"
echo

( cd "$target" && "$dotnet" run "$here/baseline/ralph-loop.cs" "$@" )
loop_status=$?

echo
# The cap is a documented outcome, not a verification failure: report it as itself.
if [ "$loop_status" -eq 3 ]; then
  echo "iteration cap reached; the plan is still open. See the loop output above."
  echo "target: $target"
  exit 3
fi

if ! bash "$here/baseline/verify.sh" "$target"; then
  echo
  echo "the loop stopped but the target does not match the plan"
  echo "target: $target"
  exit 1
fi

if [ "$loop_status" -ne 0 ]; then
  echo "the target matches the plan, but the loop did not exit on completion (status $loop_status)"
  exit "$loop_status"
fi
