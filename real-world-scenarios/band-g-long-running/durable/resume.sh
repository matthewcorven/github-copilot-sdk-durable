#!/usr/bin/env bash
# Every later process of the Band G Durable variant.
#
#   ./resume.sh                              # re-enter the run and drive it to the gate again
#   APPROVE=1 ./resume.sh                    # record an approval, then drive the run to completion
#   COMMENT="ship it" AS=alice ./resume.sh   # the reviewer and their comment
#   REJECT=1 ./resume.sh                     # record a rejection; the run ends, after the gate
#   KEEP=1 ./resume.sh                       # keep the scratch directory after this process exits
#   RUN_DIR=/path ./resume.sh                # a run whose scratch area is somewhere else
#
# The verbs the driver exposes and this script does not are one-liners on the same run directory:
#
#   dotnet run band-g-durable.cs -- --run <dir> wait --timeout 60
#   dotnet run band-g-durable.cs -- --run <dir> inspect
#   dotnet run band-g-durable.cs -- --run <dir> abort
#
# Nothing is re-created. The directory is the one the previous process left, and the store inside it
# is the record of what that process had done. `resume` passes no gate window: the window is part of
# the run's stored input, so a resumed run cannot get a fresh one.
#
# Scratch provenance matches run.sh: the directory is removed on exit unless KEEP=1, and only when
# run.sh marked it with an empty `.band-g-durable-scratch` file. Any other directory is left in
# place with a `not removing non-scratch directory` message.
#
# Exit codes: 0 the run reached the gate (or finished); 1 the driver reported a failure; 2 nothing
# to resume, or bad usage; 4 the decision was refused because one is already on file.

set -uo pipefail

SCRATCH_MARKER=".band-g-durable-scratch"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
band="$(cd "$here/.." && pwd)"
worktree="$(cd "$band/../.." && pwd)"
run_dir="${RUN_DIR:-$worktree/.scratch/band-g-durable}"
store="$run_dir/store.jsonl"

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

# The runtime the harness talks to. The run needs no model endpoint, so it needs no live runtime
# either: the repository's own recorded runtime serves the transport from a fixture, which is what
# makes a run repeatable. A caller with a real runtime supplies it and skips the build.
runtime_path="$worktree/tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime"
fixture_path="$worktree/tools/ReplayRuntime/fixtures/basic.json"
if [ -n "${DURABLE_RUNTIME_PATH:-}" ]; then
  runtime_path="$DURABLE_RUNTIME_PATH"
elif [ ! -x "$runtime_path" ]; then
  "$dotnet" build "$worktree/tools/ReplayRuntime/ReplayRuntime.csproj" -p:CopilotSkipCliDownload=true --nologo -v quiet >/dev/null 2>&1
  if [ ! -x "$runtime_path" ]; then
    echo "resume.sh: the replay runtime is missing at $runtime_path and could not be built." >&2
    echo "        Set DURABLE_RUNTIME_PATH to a Copilot runtime, or build the replay runtime." >&2
    exit 2
  fi
fi
export DURABLE_RUNTIME_PATH="$runtime_path"
if [ -z "${REPLAY_FIXTURE:-}" ]; then
  export REPLAY_FIXTURE="$fixture_path"
fi

if [ ! -f "$run_dir/run.json" ]; then
  echo "resume.sh: no run at $run_dir. Run run.sh first and keep its directory (KEEP=1)." >&2
  exit 2
fi

if [ ! -f "$store" ]; then
  echo "resume.sh: no durable store at $store; there is nothing to resume." >&2
  exit 2
fi

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

echo "run:       $run_dir"
echo "dotnet:    $dotnet ($("$dotnet" --version))"
echo

# The verdict, when the caller asks for one. A decision is recorded first and the run is driven
# second, in the same process: a decision on its own is a fact the store holds, and the gate is what
# turns it into a verdict.
if [ -n "${APPROVE:-}" ] || [ -n "${REJECT:-}" ]; then
  verb="approve"
  [ -n "${REJECT:-}" ] && verb="reject"

  decision_args=(--run "$run_dir" "$verb" --deterministic --as "${AS:-resume}")
  if [ -n "${COMMENT:-}" ]; then
    decision_args+=(--comment "$COMMENT")
  fi

  "$dotnet" run "$here/band-g-durable.cs" -p:CopilotSkipCliDownload=true -- "${decision_args[@]}"
  status=$?
  if [ "$status" -ne 0 ] && [ "$status" -ne 4 ]; then
    echo
    echo "the decision was not recorded (driver exit $status)." >&2
    exit "$status"
  fi

  echo
fi

"$dotnet" run "$here/band-g-durable.cs" -p:CopilotSkipCliDownload=true -- --run "$run_dir" resume --deterministic "$@"
status=$?

echo
if [ "$status" -eq 0 ]; then
  if [ -f "$store" ] && "$dotnet" run "$here/band-g-durable.cs" -p:CopilotSkipCliDownload=true -- --run "$run_dir" inspect 2>/dev/null | grep -q "waiting at the gate"; then
    echo "the run is back at its gate; the decision is still the store's to apply."
  fi
fi

exit "$status"
