#!/usr/bin/env bash
# Process 2 of the Band G baseline: resume the Magentic workflow from the checkpoint that run.sh
# recorded, supply the plan approval, and run to completion.
#
# The checkpoint directory comes from $1 or $CHECKPOINT_DIR. It must be the directory run.sh printed.
# It is removed on exit unless KEEP=1, because it is scratch state outside the repository.
#
# Env:
#   OPENAI_API_KEY  required. Same endpoint and model as run.sh, because the resumed workflow
#                   rebuilds the agents before restoring the checkpoint.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

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

export CHECKPOINT_DIR

echo "checkpoint directory: $CHECKPOINT_DIR"
echo

if ! dotnet build "$HERE/baseline/BaselineCheckpoint.cs" >/dev/null 2>&1; then
  dotnet build "$HERE/baseline/BaselineCheckpoint.cs"
  echo "resume.sh: build failed" >&2
  exit 1
fi

dotnet run --no-build "$HERE/baseline/BaselineCheckpoint.cs" -- resume
status=$?

if [[ "${KEEP:-0}" != "1" ]]; then
  rm -rf "$CHECKPOINT_DIR"
fi

exit "$status"
