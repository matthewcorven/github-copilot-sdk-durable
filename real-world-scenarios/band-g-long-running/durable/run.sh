#!/usr/bin/env bash
# Process 1 of the Band G Durable variant: create the run, drive it to the approval gate, and exit.
#
#   ./run.sh                          # fresh scratch run directory, gate window 30s
#   GATE_SECONDS=8 ./run.sh           # a shorter window, for a scripted approval or a timeout
#   FAULT=write ./run.sh              # leave the process before that model step commits
#   KEEP=1 ./run.sh                   # keep the scratch directory after this process exits
#   RUN_DIR=/path ./run.sh            # put the whole scratch area somewhere else
#
# The run directory holds the whole run: the durable store (`store.jsonl`), the scene the run writes
# its own files into (`scene/`), and `run.json`. Nothing is written inside this tree.
#
# The directory is removed on exit unless KEEP=1, and only when THIS script marked it as its own
# with an empty `.band-g-durable-scratch` file. A caller's directory is never emptied and never
# removed, and a pre-populated RUN_DIR is refused rather than reused.
#
# Scratch provenance, in full:
#   - RUN_DIR unset, or set to a path that does not exist: created and marked.
#   - RUN_DIR exists and is empty: used as it is, left unmarked, and not removed on exit.
#   - RUN_DIR holds only the marker: a scratch directory a previous run left behind (a failure with
#     KEEP=1), so it is adopted rather than refused.
#   - RUN_DIR is a marked directory holding anything else, or is non-empty and unmarked: refused
#     with exit 2, and not touched. Pick a new path, or remove it yourself.
#
# The model. A run needs no endpoint: the scenario supplies each turn's answer after the turn, which
# is the `--deterministic` path. The turn itself is unchanged — admission, session send, assistant
# message, turn checkpoint — so the memo and the model-call ledger count real turns. LIVENESS below
# is about what the model said, not about whether a turn happened.
#
# Exit codes: 0 the run reached the gate and the directory holds it; 1 the driver reported a
# failure; 2 bad usage or a refused directory; anything else is the driver's own status.

set -uo pipefail

SCRATCH_MARKER=".band-g-durable-scratch"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
band="$(cd "$here/.." && pwd)"
worktree="$(cd "$band/../.." && pwd)"
run_dir="${RUN_DIR:-$worktree/.scratch/band-g-durable}"
store="$run_dir/store.jsonl"

gate_seconds="${GATE_SECONDS:-30}"
fault="${FAULT:-}"

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
    echo "run.sh: the replay runtime is missing at $runtime_path and could not be built." >&2
    echo "        Set DURABLE_RUNTIME_PATH to a Copilot runtime, or build the replay runtime." >&2
    exit 2
  fi
fi
export DURABLE_RUNTIME_PATH="$runtime_path"
if [ -z "${REPLAY_FIXTURE:-}" ]; then
  export REPLAY_FIXTURE="$fixture_path"
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

  if [ "$run_dir_created" = 1 ] && [ -f "$run_dir/$SCRATCH_MARKER" ]; then
    rm -rf "$run_dir"
    return 0
  fi

  echo "not removing non-scratch directory: $run_dir" >&2
}
trap cleanup EXIT

mkdir -p "$run_dir/scene"

echo "run:       $run_dir"
echo "store:     $store"
echo "dotnet:    $dotnet ($("$dotnet" --version))"
echo

args=(--run "$run_dir" start --gate-seconds "$gate_seconds" --deterministic)
if [ -n "$fault" ]; then
  args+=(--fault "$fault")
fi
# Every argument past the script's own is passed through to the driver, so evidence.sh can arm the
# control run without a second entry point.
args+=("$@")

"$dotnet" run "$here/band-g-durable.cs" -p:CopilotSkipCliDownload=true -- "${args[@]}"
status=$?

echo
if [ "$status" -eq 0 ]; then
  if [ -f "$store" ] && [ -f "$run_dir/run.json" ]; then
    echo "the run is at its gate; the directory holds it."
    echo "approve it with: RUN_DIR=$run_dir ./resume.sh"
  else
    echo "the driver exited 0 but wrote no store; nothing to resume from."
    exit 1
  fi
elif [ "$status" -eq 42 ]; then
  echo "the driver left the process at the armed fault (exit 42); the store holds the last commit."
  echo "resume it with: RUN_DIR=$run_dir ./resume.sh"
fi

exit "$status"
