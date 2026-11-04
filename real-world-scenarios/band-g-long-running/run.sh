#!/usr/bin/env bash
# Process 1 of the Band G baseline: run the Magentic workflow until the human plan-review gate,
# record the checkpoint id, and exit. The approval is supplied by resume.sh in a second process.
#
# Env:
#   OPENAI_API_KEY  required. Any OpenAI-compatible key.
#   OPENAI_BASE_URL default https://api.openai.com/v1 (e.g. https://openrouter.ai/api/v1)
#   OPENAI_MODEL    default gpt-4o
#   CHECKPOINT_DIR  default: a fresh scratch directory under the OS temp dir. It is the handoff
#                   artifact for resume.sh, so it survives this process; resume.sh removes it when
#                   it is done, unless KEEP=1.
#   KEEP=1          keep the checkpoint directory even if this process fails.
#
# The last line of stdout is the checkpoint directory, so a caller can chain:
#   CKPT="$(./run.sh | tail -1)" && ./resume.sh "$CKPT"

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ -z "${OPENAI_API_KEY:-}" ]]; then
  echo "OPENAI_API_KEY is not set; run.sh needs an OpenAI-compatible endpoint." >&2
  exit 1
fi

if [[ -z "${CHECKPOINT_DIR:-}" ]]; then
  TMPROOT="${TMPDIR:-/tmp}"
  TMPROOT="${TMPROOT%/}"
  CHECKPOINT_DIR="$(mktemp -d "$TMPROOT/band-g-checkpoints.XXXXXX")"
  rm -rf "$CHECKPOINT_DIR"
  mkdir -p "$CHECKPOINT_DIR"
  SCRATCH=1
else
  rm -rf "$CHECKPOINT_DIR"
  mkdir -p "$CHECKPOINT_DIR"
  SCRATCH=0
fi

# The scratch directory has to outlive this process: resume.sh resumes from it. It is removed by
# resume.sh at the end of the flow, or by this script if it fails and leaves nothing to resume from.
cleanup() {
  if [[ $1 -ne 0 && "$SCRATCH" == "1" && "${KEEP:-0}" != "1" ]]; then
    rm -rf "$CHECKPOINT_DIR"
  fi
}
trap 'cleanup $?' EXIT

export CHECKPOINT_DIR

echo "checkpoint directory: $CHECKPOINT_DIR"
echo

# Build first so compiler warnings go to stderr of the build, not into the captured output the
# evidence file quotes. `dotnet run --no-build` then prints only the program's own output.
if ! dotnet build "$HERE/baseline/BaselineCheckpoint.cs" >/dev/null 2>&1; then
  dotnet build "$HERE/baseline/BaselineCheckpoint.cs"
  echo "run.sh: build failed" >&2
  exit 1
fi

dotnet run --no-build "$HERE/baseline/BaselineCheckpoint.cs" -- run
status=$?

if [[ $status -ne 0 ]]; then
  echo "run.sh: process 1 exited $status without reaching a resumable gate" >&2
  exit "$status"
fi

echo
echo "$CHECKPOINT_DIR"
