#!/usr/bin/env bash
# Answer the orchestrator's human approval gate.
#
#   ./gate.sh approve "A. Reviewer" "note"   # or pend_for_review
#   ./gate.sh status                         # what the gate is showing
#   ./gate.sh --raw '{"action":"approve",...}'  # write the file verbatim
#
# The gate is a file the orchestrator polls, so answering it is the whole of the
# human side: no API, no session, no shared memory. That is deliberate — it means
# the decision can be made from another terminal, another process, or another day,
# which is what "a person, later" actually is.
#
# It refuses to overwrite an existing decision: a gate is answered once, and a
# second answer would be indistinguishable from the first in the record.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
worktree="$(cd "$here/../.." && pwd)"
run_dir="${RUN_DIR:-$worktree/.scratch/band-d3}"
gate_dir="$run_dir/gate"

[ -d "$gate_dir" ] || { echo "gate.sh: no gate at $gate_dir (no run, or it has not reached the gate)." >&2; exit 2; }

case "${1:-status}" in
  status)
    if [ ! -f "$gate_dir/request.json" ]; then
      echo "gate.sh: the run has not asked a question yet ($gate_dir/request.json missing)." >&2
      exit 2
    fi
    echo "question : $(cat "$gate_dir/request.json")"
    if [ -f "$gate_dir/decision.json" ]; then
      echo "answered : $(cat "$gate_dir/decision.json")"
    else
      echo "answered : no — write one with: ./gate.sh approve|pend_for_review <reviewer> [note]"
    fi
    ;;
  --raw)
    body="${2:?gate.sh --raw needs a JSON body}"
    [ -f "$gate_dir/decision.json" ] && {
      echo "gate.sh: this gate is already answered ($gate_dir/decision.json); a gate is answered once." >&2
      exit 2
    }
    printf '%s\n' "$body" > "$gate_dir/decision.json"
    echo "wrote $gate_dir/decision.json"
    ;;
  approve|pend_for_review)
    action="$1"
    reviewer="${2:-D3 evidence reviewer}"
    note="${3:-}"
    [ -f "$gate_dir/decision.json" ] && {
      echo "gate.sh: this gate is already answered ($gate_dir/decision.json); a gate is answered once." >&2
      exit 2
    }
    python3 - "$action" "$reviewer" "$note" "$gate_dir/decision.json" <<'PY'
import json, sys
action, reviewer, note, out = sys.argv[1:5]
if action not in ("approve", "pend_for_review"):
    raise SystemExit(f"gate.sh: action '{action}' is not approve or pend_for_review")
json.dump({"action": action, "reviewer": reviewer, "note": note}, open(out, "w"), indent=1)
print(f"wrote {out}")
PY
    ;;
  *)
    echo "gate.sh: unknown command '$1' (status | approve | pend_for_review | --raw)" >&2
    exit 2
    ;;
esac
