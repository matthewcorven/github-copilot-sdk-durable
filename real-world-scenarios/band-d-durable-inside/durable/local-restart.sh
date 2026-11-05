#!/usr/bin/env bash
# The restart, exercised locally: two processes, one store file, no platform.
#
# This is the deterministic counterpart of `infra/invoke.sh`. The deployment proves the
# platform keeps the session volume; this proves the *scenario's* logic against the same
# filesystem semantics without a deployment, so the claim is not hostage to a subscription.
# It is what `invoke.sh` would do if a container stop were a process exit.
#
# Sequence, each step its own process:
#   1. assess        commit the assessment and park the approval (process A)
#   2. probe --mark  write a survival marker beside the store           (process A)
#   3. state         read it back                                       (process A)
#   4. resume        re-enter the parked task with no answer             (process B)
#   5. deliver       deliver the human answer                            (process C)
#   6. resume        re-enter again; completes at the approval           (process D)
#   7. probe --read  read the marker written by process A                (process D)
#
# Every process opens and closes the store itself, so each one is a "container": the file
# lock is taken and released, nothing is shared in memory.
#
# Env: STORE_DIR (default: a fresh directory under .scratch/), PYTHON (default: an
#      interpreter that imports `durable` and `copilot`).
#
# Usage: ./local-restart.sh [--keep]
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BAND="$(cd "$HERE/.." && pwd)"
REPO="$(cd "$BAND/../.." && pwd)"

PYTHON="${PYTHON:-}"
for candidate in "$REPO/.scratch/agentvenv/bin/python" "$REPO/lib/py/.venv/bin/python" python3; do
  [ -n "$PYTHON" ] && break
  if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c "import durable, copilot" >/dev/null 2>&1; then
    PYTHON="$candidate"
  fi
done
[ -n "$PYTHON" ] || { echo "local-restart.sh: no interpreter imports durable and copilot" >&2; exit 1; }

STORE_DIR="${STORE_DIR:-$REPO/.scratch/band-d2-local-restart}"
OUT="${OUT:-$BAND/evidence/local-restart}"
rm -rf "$STORE_DIR" "$OUT"
mkdir -p "$STORE_DIR" "$OUT"

export DURABLE_STORE_PATH="$STORE_DIR/working-state.jsonl"
export PYTHONPATH="$REPO/lib/py:$BAND/agent"
cd "$BAND/agent"

step() { # name command...
  local name="$1"; shift
  echo "\$ $*" >> "$OUT/commands.txt"
  "$@" > "$OUT/$name.json"
  echo "-- $name" >> "$OUT/$name.log"
  python3 -c "import json,sys;print(json.dumps(json.load(open('$OUT/$name.json')),indent=1))" >> "$OUT/$name.log"
}

echo "store: $DURABLE_STORE_PATH" > "$OUT/commands.txt"
echo "python: $PYTHON" >> "$OUT/commands.txt"

# -- process A ---------------------------------------------------------------
step 01-assess "$PYTHON" d2_tool.py assess --request LOCAL-1 --text "criteria met"
step 02-marker-written "$PYTHON" d2_tool.py probe --mark
step 03-state "$PYTHON" d2_tool.py state --request LOCAL-1
sha256sum "$DURABLE_STORE_PATH" > "$OUT/10-store-after-process-A.txt" 2>/dev/null || \
  shasum -a 256 "$DURABLE_STORE_PATH" >> "$OUT/10-store-after-process-A.txt"

# -- process B: the restart, with nothing to act on ---------------------------
step 04-resume-no-answer "$PYTHON" d2_tool.py resume --request LOCAL-1

# -- process C: the human answers --------------------------------------------
step 05-deliver "$PYTHON" -c "
import json
from durable_state import Workspace
workspace = Workspace.open()
print(json.dumps({'woken': workspace.deliver('LOCAL-1', {'decision': 'approved', 'reviewer': 'Dr Okoro', 'reason': 'criteria met'})}, indent=1))
workspace.close()
"

# -- process D: the restart that completes -----------------------------------
step 06-resume-answered "$PYTHON" d2_tool.py resume --request LOCAL-1
step 07-marker-read "$PYTHON" d2_tool.py probe --read
sha256sum "$DURABLE_STORE_PATH" > "$OUT/20-store-after-process-D.txt" 2>/dev/null || \
  shasum -a 256 "$DURABLE_STORE_PATH" >> "$OUT/20-store-after-process-D.txt"

echo "wrote $OUT"
ls -1 "$OUT"
[ "${1:-}" = "--keep" ] || true
