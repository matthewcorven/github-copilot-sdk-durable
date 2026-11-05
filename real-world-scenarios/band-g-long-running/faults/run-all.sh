#!/usr/bin/env bash
# Every Band G fault, against both variants, controls included, then the summary is rendered from the
# result files.
#
#   ./faults/run-all.sh                  # everything (~10 minutes)
#   ./faults/run-all.sh durable          # one variant
#   ./faults/run-all.sh --no-controls    # the six faults only
#   KEEP=1 ./faults/run-all.sh ...       # keep every run directory
#
# Each script writes one JSON file per fault per variant, plus one for its control. A script that exits
# non-zero is a harness error and stops the run: a fault that could not be driven is not a result. A
# script that exits 0 has recorded an outcome, including `not exercised` and including a lost run.
#
# After every script the runner records its exit status in `results/run-status.txt`, which the summary
# reads: "all faults ran" is a claim about that file, not about the absence of an error message.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

ONLY=""
STATUS_FILE="$RESULTS_DIR/run-status.txt"

while [ $# -gt 0 ]; do
  case "$1" in
    baseline|durable)
      ONLY="$1"
      shift
      ;;
    --no-controls)
      CONTROLS=0
      shift
      ;;
    -h|--help)
      sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed -e 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      log "run-all.sh: unknown argument '$1'"
      exit 2
      ;;
  esac
done

CONTROLS="${CONTROLS:-1}"

require_tools || exit 1
build_variants || exit 1

mkdir -p "$RESULTS_DIR"
: > "$STATUS_FILE"

FAULTS=(kill-during-wait restart-after-checkpoint duplicate-approval late-approval abort-mid-wait store-outage)
VARIANTS=(baseline durable)
[ -n "$ONLY" ] && VARIANTS=("$ONLY")

failures=0
for variant in "${VARIANTS[@]}"; do
  for fault in "${FAULTS[@]}"; do
    log ""
    log "── $variant / $fault ──────────────────────────────────────────────"
    "$HERE/$fault.sh" --variant "$variant"
    status=$?
    printf '%s %s %s\n' "$variant" "$fault" "$status" >> "$STATUS_FILE"
    if [ "$status" -ne 0 ]; then
      log "run-all.sh: $variant / $fault exited $status — a harness error, not a result"
      failures=$((failures + 1))
      continue
    fi

    # `abort-mid-wait` has no control and says why in its own script header: the control for an abort is
    # a run that was never aborted, which the band's own two-process evidence already measures.
    if [ "$CONTROLS" = "1" ] && [ "$fault" != "abort-mid-wait" ]; then
      log ""
      log "── $variant / $fault (control) ────────────────────────────────────"
      "$HERE/$fault.sh" --variant "$variant" --fault-not-injected
      status=$?
      printf '%s %s-control %s\n' "$variant" "$fault" "$status" >> "$STATUS_FILE"
      if [ "$status" -ne 0 ]; then
        log "run-all.sh: $variant / $fault control exited $status — a harness error"
        failures=$((failures + 1))
      fi
    fi
  done
done

log ""
log "rendering the summary from the result files"
if ! "$PYTHON" "$HERE/lib/summarise.py" "$RESULTS_DIR" "$STATUS_FILE" > "$RESULTS_DIR/summary.txt"; then
  log "run-all.sh: the summary could not be rendered; a fault has no result for a variant"
  exit 1
fi
"$PYTHON" "$HERE/lib/summarise.py" "$RESULTS_DIR" "$STATUS_FILE" --md > "$RESULTS_DIR/summary.md" || exit 1
log "wrote faults/results/summary.txt and faults/results/summary.md"

if [ "$failures" -ne 0 ]; then
  log "run-all.sh: $failures script(s) reported a harness error. The summary above says which."
  exit 1
fi
exit 0
