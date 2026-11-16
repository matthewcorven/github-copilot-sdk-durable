#!/usr/bin/env bash
# Write the result a fault gets when this variant cannot have it, so a matrix cell is a
# recorded reason rather than an empty square.
#
#   not_exercised.sh --variant V --fault F --out FILE --reason "..." --detail "..." [--control]
#
# `--control` records the control cell for a fault this variant cannot have, so the file's
# `harness.command` carries `--fault-not-injected` instead of repeating the fault's line.
#
# A missing result and a `not exercised` result are different claims: the first says the
# run did not happen, the second says it happened and this variant cannot be asked this
# question. The matrix renders both, and only one of them is the band's evidence.
set -uo pipefail
HERE_LIB="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

VARIANT=""
FAULT=""
OUT=""
REASON=""
DETAIL=""
CONTROL=0
while [ $# -gt 0 ]; do
  case "$1" in
    --variant) VARIANT="$2"; shift 2 ;;
    --fault) FAULT="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --reason) REASON="$2"; shift 2 ;;
    --detail) DETAIL="$2"; shift 2 ;;
    # A control cell is not the fault's row; `lib/result.py` spells the invocation from
    # FAULT_INJECT, so the flag has to reach it from here as well.
    --control) CONTROL=1; shift ;;
    *) printf 'not_exercised: unknown argument %s\n' "$1" >&2; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] && [ -n "$FAULT" ] && [ -n "$OUT" ] || {
  printf 'not_exercised: --variant, --fault and --out are required\n' >&2; exit 2
}
if [ "$CONTROL" = "1" ]; then
  export FAULT_INJECT=0
else
  export FAULT_INJECT=1
fi

# A cell a variant cannot have was never run, so it never used a container. Said explicitly
# rather than left to the envelope's default, because a reader comparing rows should not
# have to infer which six results are records rather than runs.
FAULTS_DOCKER="not used - this cell records why the fault cannot be run, so nothing was started"
export FAULTS_DOCKER

TEMPLATE="$(mktemp -t band-d4-nae)"
FAULT_LIB="$HERE_LIB" FAULT_VARIANT="$VARIANT" FAULT_REASON="$REASON" FAULT_DETAIL="$DETAIL" \
python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

variant = os.environ["FAULT_VARIANT"]
doc = {
    "outcome": f"not exercised: {os.environ['FAULT_REASON']}",
    "platform": block(
        touched=[],
        observed=(
            "not exercised in this run: this fault has no platform-side question in it, and no hosted "
            "deployment existed to ask one"
        ),
        note=(
            "Recorded as not exercised rather than borrowed from another variant's result. A fault a "
            "variant cannot have is a fact about the variant's shape, not a gap in the harness."
        ),
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": os.environ["FAULT_DETAIL"],
        "observations": {"fault_exercised": False, "variant": variant},
    },
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY

python3 "$HERE_LIB/result.py" --out "$OUT" --fault "$FAULT" --variant "$VARIANT" --template "$TEMPLATE"
status=$?
rm -f "$TEMPLATE"
exit $status
