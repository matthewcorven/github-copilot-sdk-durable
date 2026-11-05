#!/usr/bin/env bash
# Band F baseline: run the handoff workflow with a scripted user, logging every change of the agent
# that holds control. The workflow checkpoints itself with the framework's file-backed store, so the
# same directory can also be resumed by `./run.sh --resume <dir>` (see below).
#
# The checkpoint directory is the handoff artifact for a later resume, so a successful run keeps it
# and prints it as the last line of stdout. Pass --clean (or CLEAN=1) to remove a directory this
# script created once the run has succeeded; a directory it did not create is never removed.
#
# Scratch directories: this script never empties or deletes a directory it did not create. Every
# directory it creates is marked with an empty `.band-f-scratch` file.
#   - CHECKPOINT_DIR unset: a fresh directory is created under the OS temp dir and marked.
#   - CHECKPOINT_DIR set, path does not exist: it is created and marked.
#   - CHECKPOINT_DIR set, path exists but is empty: it is used as it is and left unmarked, so this
#     script never deletes it.
#   - CHECKPOINT_DIR set, path holds only a `.band-f-scratch` marker: it is a scratch directory this
#     script left behind earlier, so it is reused.
#   - CHECKPOINT_DIR set, path exists and is non-empty otherwise: this script refuses with exit 2
#     and does not touch the directory.
#
# Exit codes: 0 the workflow completed; 1 endpoint or build failure, or the workflow errored;
#             2 bad usage or a refused CHECKPOINT_DIR.
#
# Env:
#   OPENAI_API_KEY  required. Any OpenAI-compatible key. Nothing else here is provider-specific.
#   OPENAI_BASE_URL default https://api.openai.com/v1 (e.g. https://openrouter.ai/api/v1)
#   OPENAI_MODEL    default gpt-4o
#   CHECKPOINT_DIR  default: a fresh scratch directory under the OS temp dir
#   CONTROL_LOG     default: $CHECKPOINT_DIR/control-transfer.jsonl
#   CLEAN=1         remove a scratch directory this script created after a successful run
#
# The last line of stdout is the checkpoint directory and it survives the process, so a caller can
# chain a resume with no other channel:
#   CKPT="$(./run.sh | tail -1)" && ./run.sh --resume "$CKPT"
#
# Usage: ./run.sh [-h|--help] [--clean] [--resume <checkpoint-dir>]

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRATCH_MARKER=".band-f-scratch"

usage() {
  sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed -e 's/^# \{0,1\}//'
}

MODE="run"
ARGS=("$@")
if [[ ${#ARGS[@]} -gt 0 ]]; then
  for ((i = 0; i < ${#ARGS[@]}; i++)); do
    case "${ARGS[$i]}" in
      -h|--help)
        usage
        exit 0
        ;;
      --clean)
        CLEAN=1
        ;;
      --resume)
        if [[ -z "${ARGS[$((i + 1))]:-}" ]]; then
          echo "run.sh: --resume needs a checkpoint directory" >&2
          exit 2
        fi
        MODE="resume"
        CHECKPOINT_DIR="${ARGS[$((i + 1))]}"
        i=$((i + 1))
        ;;
      *)
        echo "run.sh: unknown argument '${ARGS[$i]}'. Use --help." >&2
        exit 2
        ;;
    esac
  done
fi

if [[ -z "${OPENAI_API_KEY:-}" ]]; then
  echo "OPENAI_API_KEY is not set; run.sh needs an OpenAI-compatible endpoint." >&2
  echo "  export OPENAI_API_KEY=...                       # any compatible key" >&2
  echo "  export OPENAI_BASE_URL=https://openrouter.ai/api/v1   # optional" >&2
  echo "  export OPENAI_MODEL=openai/gpt-4o                     # optional, this is the default" >&2
  exit 1
fi

SCRATCH=0
if [[ "$MODE" == "resume" ]]; then
  if [[ ! -d "$CHECKPOINT_DIR" ]]; then
    echo "run.sh: --resume needs an existing checkpoint directory; '$CHECKPOINT_DIR' is not one." >&2
    exit 2
  fi
elif [[ -z "${CHECKPOINT_DIR:-}" ]]; then
  TMPROOT="${TMPDIR:-/tmp}"
  TMPROOT="${TMPROOT%/}"
  CHECKPOINT_DIR="$(mktemp -d "$TMPROOT/band-f-checkpoints.XXXXXX")" || {
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
    SCRATCH=1
  elif [[ -n "$listing" ]]; then
    echo "run.sh: refusing to use CHECKPOINT_DIR '$CHECKPOINT_DIR': it already has content and was" >&2
    echo "        not created by run.sh, so run.sh will not empty it." >&2
    echo "        Point CHECKPOINT_DIR at a new or empty path, or unset it for a fresh scratch dir." >&2
    exit 2
  fi
fi

if [[ "$SCRATCH" == "1" ]]; then
  if ! : > "$CHECKPOINT_DIR/$SCRATCH_MARKER"; then
    echo "run.sh: could not write the scratch marker in '$CHECKPOINT_DIR'" >&2
    exit 1
  fi
fi

export CHECKPOINT_DIR

# The directory is kept on both outcomes so the run's control-transfer log can be read back and so
# the printed path is resumable. Only an explicit --clean/CLEAN=1 removes a scratch directory this
# script created; a directory it did not create is never removed.
cleanup() {
  if [[ $1 -eq 0 && "$SCRATCH" == "1" && "${CLEAN:-0}" == "1" ]]; then
    rm -rf "$CHECKPOINT_DIR"
  fi
}
trap 'cleanup $?' EXIT

# Build first so compiler warnings go to the build's stderr, not into the captured output.
if ! dotnet build "$HERE/baseline/BaselineHandoff.cs" >/dev/null 2>&1; then
  dotnet build "$HERE/baseline/BaselineHandoff.cs"
  echo "run.sh: build failed" >&2
  exit 1
fi

dotnet run --no-build "$HERE/baseline/BaselineHandoff.cs" -- "$MODE"
status=$?

if [[ $status -ne 0 ]]; then
  echo "run.sh: the workflow exited $status; checkpoint directory kept at $CHECKPOINT_DIR" >&2
  exit "$status"
fi

echo
echo "$CHECKPOINT_DIR"
