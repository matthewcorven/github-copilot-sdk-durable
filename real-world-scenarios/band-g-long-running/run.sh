#!/usr/bin/env bash
# Process 1 of the Band G baseline: run the Magentic workflow until the human plan-review gate,
# record the checkpoint id, and exit. The approval is supplied by resume.sh in a second process.
#
# Scratch directories: this script never empties or deletes a directory it did not create. Every
# directory it creates is marked with an empty `.band-g-scratch` file, and resume.sh removes a
# checkpoint directory only when that marker is present.
#   - CHECKPOINT_DIR unset: a fresh directory is created under the OS temp dir and marked. If this
#     process fails before a resumable gate is written, it removes that directory again, unless
#     KEEP=1.
#   - CHECKPOINT_DIR set, path does not exist: it is created and marked, and then treated like the
#     directory above.
#   - CHECKPOINT_DIR set, path exists but is empty: it is used as it is and left unmarked, so
#     neither this script nor resume.sh deletes it.
#   - CHECKPOINT_DIR set, path holds only a `.band-g-scratch` marker: it is a scratch directory this
#     script left behind when a previous run failed before the gate with KEEP=1, so it is reused.
#   - CHECKPOINT_DIR set, path exists and is non-empty otherwise: this script refuses with exit 2
#     and does not touch the directory. Pick a new or empty path, or unset CHECKPOINT_DIR.
#
# Exit codes: 0 reached the gate and printed the checkpoint directory; 1 endpoint or build failure;
#             2 bad usage or a refused CHECKPOINT_DIR; anything else is the workflow's own status.
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
#
# Usage: ./run.sh [-h|--help]

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
  echo "OPENAI_API_KEY is not set; run.sh needs an OpenAI-compatible endpoint." >&2
  exit 1
fi

if [[ -z "${CHECKPOINT_DIR:-}" ]]; then
  TMPROOT="${TMPDIR:-/tmp}"
  TMPROOT="${TMPROOT%/}"
  CHECKPOINT_DIR="$(mktemp -d "$TMPROOT/band-g-checkpoints.XXXXXX")" || {
    echo "run.sh: could not create a scratch checkpoint directory under '$TMPROOT'" >&2
    exit 1
  }
  SCRATCH=1
elif [[ ! -e "$CHECKPOINT_DIR" ]]; then
  mkdir -p "$CHECKPOINT_DIR" || {
    echo "run.sh: could not create CHECKPOINT_DIR '$CHECKPOINT_DIR'" >&2
    exit 1
  }
  SCRATCH=1
elif [[ ! -d "$CHECKPOINT_DIR" ]]; then
  echo "run.sh: CHECKPOINT_DIR '$CHECKPOINT_DIR' exists and is not a directory; refusing to use it." >&2
  exit 2
else
  listing="$(ls -A "$CHECKPOINT_DIR" 2>/dev/null)"
  if [[ -f "$CHECKPOINT_DIR/$SCRATCH_MARKER" && "$listing" == "$SCRATCH_MARKER" ]]; then
    # A marker and nothing else: a previous run that failed before the gate with KEEP=1 left its own
    # scratch directory behind. Adopt it rather than refuse it with a false provenance claim.
    SCRATCH=1
  elif [[ -f "$CHECKPOINT_DIR/$SCRATCH_MARKER" ]]; then
    echo "run.sh: refusing to use CHECKPOINT_DIR '$CHECKPOINT_DIR': it is a run.sh scratch directory" >&2
    echo "        that already holds a run's checkpoint, and run.sh will not empty it." >&2
    echo "        Point CHECKPOINT_DIR at a new or empty path, or unset it for a fresh scratch dir." >&2
    exit 2
  elif [[ -n "$listing" ]]; then
    echo "run.sh: refusing to use CHECKPOINT_DIR '$CHECKPOINT_DIR': it already has content and was" >&2
    echo "        not created by run.sh, so run.sh will not empty it." >&2
    echo "        Point CHECKPOINT_DIR at a new or empty path, or unset it for a fresh scratch dir." >&2
    exit 2
  else
    # An existing empty directory is usable, but it is not ours: leave it unmarked and in place.
    SCRATCH=0
  fi
fi

if [[ "$SCRATCH" == "1" ]]; then
  if ! : > "$CHECKPOINT_DIR/$SCRATCH_MARKER"; then
    echo "run.sh: could not write the scratch marker in '$CHECKPOINT_DIR'" >&2
    exit 1
  fi
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
