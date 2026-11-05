#!/usr/bin/env bash
# Run the D3 orchestrator once, against the D1 deployment.
#
#   ./run.sh                    # new run under .scratch/band-d3, stops at the gate
#   ./run.sh --resume           # reopen the store that run.sh left and continue
#   KEEP=1 ./run.sh             # keep the scratch run after this process exits
#   RUN_DIR=/path ./run.sh      # put the run somewhere else
#
# The run is the durable task, not the process. `run.sh` starts one; `--resume`
# starts another over the same store; nothing about the run lives in this script.
#
# ── Why this stops at the gate ──────────────────────────────────────────────
#
# The gate is a real human round-trip: the orchestrator writes
# `gate/request.json`, prints the process id, and polls for `gate/decision.json`.
# It does not fabricate one. Evidence runs therefore stop here — a live process,
# one agent call settled, the next agent call not started — which is exactly the
# seam requirement 2 kills at. `./gate.sh approve "reviewer"` answers it, from
# another terminal or another process, the way a person would.
#
# ── Scratch provenance (same rule as Band B) ────────────────────────────────
#
# This script never empties or deletes a directory it did not create. It marks the
# one it creates with an empty `.band-d3-scratch` file, and only removes a marked
# directory. A RUN_DIR that already has content and no marker is refused (exit 2),
# not cleared.
#
# Env:
#   RUN_DIR              default <worktree>/.scratch/band-d3
#   REVIEWER             default "D3 evidence reviewer"
#   GATE_TIMEOUT_SECONDS default 600
#   MEMO_KEY_MODE        `identity` (default) or `naive` — the control
#   RUN_REQUEST_FILE     default ./evidence/request.txt
#   DOTNET               default /opt/homebrew/bin/dotnet

set -uo pipefail

SCRATCH_MARKER=".band-d3-scratch"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
band_d="$(cd "$here/../band-d-hosted-foundry-agent" && pwd)"
worktree="$(cd "$here/../.." && pwd)"
run_dir="${RUN_DIR:-$worktree/.scratch/band-d3}"

if [ -n "${DOTNET:-}" ]; then
  dotnet="$DOTNET"
elif [ -x /opt/homebrew/bin/dotnet ]; then
  dotnet=/opt/homebrew/bin/dotnet
else
  dotnet="$(command -v dotnet || true)"
fi
[ -n "$dotnet" ] || { echo "run.sh: no dotnet on PATH (expected /opt/homebrew/bin/dotnet)" >&2; exit 2; }

resume=0
for arg in "$@"; do
  case "$arg" in
    --resume) resume=1 ;;
    *) echo "run.sh: unknown argument '$arg' (only --resume)" >&2; exit 2 ;;
  esac
done

refuse() {
  echo "run.sh: refusing to use $1: $2" >&2
  echo "        Point RUN_DIR at a new path, or remove it yourself." >&2
  exit 2
}

run_dir_created=0
if [ "$resume" = 0 ]; then
  if [ ! -e "$run_dir" ]; then
    mkdir -p "$run_dir" || { echo "run.sh: could not create RUN_DIR '$run_dir'" >&2; exit 2; }
    run_dir_created=1
  elif [ ! -d "$run_dir" ]; then
    refuse "$run_dir" "it exists and is not a directory"
  else
    listing="$(ls -A "$run_dir" 2>/dev/null)"
    if [ -z "$listing" ]; then
      : # an existing empty directory is usable and is not ours to remove
    elif [ -f "$run_dir/$SCRATCH_MARKER" ]; then
      refuse "$run_dir" "a previous run is there. Resume it with --resume, or pick a new RUN_DIR"
    else
      refuse "$run_dir" "it already has content and was not created by run.sh"
    fi
  fi

  if [ "$run_dir_created" = 1 ]; then
    : > "$run_dir/$SCRATCH_MARKER" || { echo "run.sh: could not write the scratch marker" >&2; exit 2; }
  fi
else
  [ -f "$run_dir/store/orchestrator.jsonl" ] || {
    echo "run.sh: no store at $run_dir/store/orchestrator.jsonl; there is nothing to resume." >&2
    exit 2
  }
fi

cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then
    echo "kept: $run_dir"
    return 0
  fi

  if [ -f "$run_dir/$SCRATCH_MARKER" ]; then
    rm -rf "$run_dir"
  else
    echo "not removing non-scratch directory: $run_dir" >&2
  fi
}
trap cleanup EXIT

# --- Foundry access ---------------------------------------------------------
#
# Reuse means reading D1's record of the deployment, not rediscovering it. The
# token is minted here and passed to the orchestrator in its environment, so no
# credential is written to the store, the run directory, or this file.

[ -f "$band_d/deployment.env" ] || {
  echo "run.sh: $band_d/deployment.env is missing; run D1's infra/deploy.sh first." >&2
  exit 2
}
# shellcheck disable=SC1091
. "$band_d/deployment.env"
[ -n "${FOUNDRY_BASE_URL:-}" ] || { echo "run.sh: deployment.env has no FOUNDRY_BASE_URL." >&2; exit 2; }

export FOUNDRY_BASE_URL
export FOUNDRY_TOKEN
FOUNDRY_TOKEN="$(az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv)" || {
  echo "run.sh: could not mint a token; is 'az login' current?" >&2
  exit 2
}
[ -n "$FOUNDRY_TOKEN" ] || { echo "run.sh: az returned an empty token." >&2; exit 2; }

export RUN_DIR="$run_dir"
export REVIEWER="${REVIEWER:-D3 evidence reviewer}"
export GATE_TIMEOUT_SECONDS="${GATE_TIMEOUT_SECONDS:-600}"
export MEMO_KEY_MODE="${MEMO_KEY_MODE:-identity}"

if [ "$resume" = 0 ]; then
  request_file="${RUN_REQUEST_FILE:-$here/evidence/request.txt}"
  [ -f "$request_file" ] || { echo "run.sh: no request at $request_file" >&2; exit 2; }
  export RUN_REQUEST
  RUN_REQUEST="$(cat "$request_file")"
fi

echo "run dir:   $run_dir"
echo "dotnet:    $dotnet ($("$dotnet" --version))"
echo "project:   $FOUNDRY_BASE_URL"
[ "$resume" = 1 ] && echo "resuming:  the store run.sh left" || echo "request:   ${RUN_REQUEST_FILE:-$here/evidence/request.txt}"
echo

"$dotnet" run "$here/orchestrator.cs"
status=$?

echo
if [ "$status" -ne 0 ]; then
  echo "run.sh: the orchestrator exited with status $status"
  echo "        run dir: $run_dir"
  exit "$status"
fi

echo "run dir: $run_dir"
