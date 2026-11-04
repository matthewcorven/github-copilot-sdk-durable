#!/usr/bin/env bash
# Process 2 of the Band G baseline: resume the Magentic workflow from the checkpoint that run.sh
# recorded, supply the plan approval, and run to completion.
#
# The checkpoint directory comes from $1 or $CHECKPOINT_DIR. It must be the directory run.sh printed.
# It is removed on exit unless KEEP=1, and only when run.sh marked it as scratch: a directory is
# considered run.sh's own when it contains the empty `.band-g-scratch` marker file that run.sh
# writes into every directory it creates. Any other directory, including one that merely holds a
# gate.json, is left in place with a `not removing non-scratch directory: <path>` message. A
# hand-written directory is therefore safe to point this script at.
#
# Env:
#   OPENAI_API_KEY  required. Same endpoint and model as run.sh, because the resumed workflow
#                   rebuilds the agents before restoring the checkpoint.
#   KEEP=1          keep the checkpoint directory even when run.sh created it.
#
# Exit codes: 0 the workflow completed; 1 endpoint or build failure (the checkpoint directory is left
#             in place, so a retry can run after the build is fixed); 2 bad usage or no gate.json;
#             anything else is the workflow's own status. A run.sh scratch directory is removed on
#             exit, success or failure, unless KEEP=1; any other directory is kept.
#
# Usage: ./resume.sh [-h|--help] <checkpoint-directory>

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRATCH_MARKER=".band-g-scratch"

usage() {
  sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed -e 's/^# \{0,1\}//'
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

if [[ -z "${OPENAI_API_KEY:-}" ]]; then
  echo "OPENAI_API_KEY is not set; resume.sh needs the same OpenAI-compatible endpoint." >&2
  exit 1
fi

CHECKPOINT_DIR="${1:-${CHECKPOINT_DIR:-}}"
if [[ -z "$CHECKPOINT_DIR" ]]; then
  echo "usage: ./resume.sh <checkpoint-directory>   (or set CHECKPOINT_DIR)" >&2
  echo "       the directory is the last line run.sh prints" >&2
  exit 2
fi

if [[ ! -f "$CHECKPOINT_DIR/gate.json" ]]; then
  echo "resume.sh: no gate.json in '$CHECKPOINT_DIR'. Run run.sh first and keep its directory." >&2
  exit 2
fi

# Only a directory run.sh marked as scratch is ours to delete. Decided up front, so the exit path
# does not depend on what the resumed workflow leaves behind. The marker records provenance directly
# instead of guessing from the path: a temp-root or prefix check would be wrong for a symlink, a
# custom TMPDIR, or a deliberate CHECKPOINT_DIR outside the temp directory, and would miss a caller's
# directory that merely sits under the temp root with a hand-written gate.json.
SCRATCH_DIR=0
if [[ -f "$CHECKPOINT_DIR/$SCRATCH_MARKER" ]]; then
  SCRATCH_DIR=1
fi

remove_scratch() {
  if [[ "${KEEP:-0}" == "1" ]]; then
    return 0
  fi
  if [[ "$SCRATCH_DIR" == "1" ]]; then
    rm -rf "$CHECKPOINT_DIR"
  else
    echo "not removing non-scratch directory: $CHECKPOINT_DIR" >&2
  fi
}

export CHECKPOINT_DIR

echo "checkpoint directory: $CHECKPOINT_DIR"
echo

if ! dotnet build "$HERE/baseline/BaselineCheckpoint.cs" >/dev/null 2>&1; then
  dotnet build "$HERE/baseline/BaselineCheckpoint.cs"
  echo "resume.sh: build failed" >&2
  exit 1
fi

# Armed only once the build succeeded: a build failure is retryable (missing package, no network) and
# leaves the checkpoint directory in place, matching run.sh, which keeps its scratch when it did not
# reach the gate. From here on the directory is removed on exit, success or failure, unless KEEP=1.
trap remove_scratch EXIT

dotnet run --no-build "$HERE/baseline/BaselineCheckpoint.cs" -- resume
status=$?

exit "$status"
