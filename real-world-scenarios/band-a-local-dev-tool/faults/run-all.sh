#!/usr/bin/env bash
# Runs every fault against both variants and writes a summary of what the run observed.
#
#   ./faults/run-all.sh              # every fault, both variants, controls included
#   ./faults/run-all.sh durable      # one variant
#   ./faults/run-all.sh --no-controls
#
# Each fault script is independent and re-runnable: it builds its own run directory, starts its own
# processes, and removes both on exit. This script only sequences them, records the exit status of each,
# and renders the table the band README quotes, from the results the scripts wrote and nothing else.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

VARIANTS=()
CONTROLS=1
while [ $# -gt 0 ]; do
  case "$1" in
    --no-controls) CONTROLS=0; shift ;;
    baseline|durable) VARIANTS+=("$1"); shift ;;
    *) log "run-all: unknown argument '$1'"; exit 2 ;;
  esac
done
if [ ${#VARIANTS[@]} -eq 0 ]; then VARIANTS=(baseline durable); fi

FAULTS=(kill-mid-turn restart-after-checkpoint duplicate-delivery concurrent-clients store-outage)
mkdir -p "$SCRATCH_ROOT"
STATUS_FILE="$SCRATCH_ROOT/run-all.status"
: > "$STATUS_FILE"

require_tools || exit 1

# The checked-in results are the evidence. A run only writes there when it is told to promote, because a
# machine without Playwright would otherwise replace five measured baseline rows with "not exercised" and
# nothing would notice. A plain run writes to its own directory and says how to promote it.
if [ "${FAULTS_RESULTS_DIR:-}" = "" ]; then
  RESULTS_DIR="$SCRATCH_ROOT/results-$(date -u +%Y%m%dT%H%M%SZ)"
  export FAULTS_RESULTS_DIR="$RESULTS_DIR"
else
  RESULTS_DIR="$FAULTS_RESULTS_DIR"
fi
mkdir -p "$RESULTS_DIR"
log "run-all: results will be written to $RESULTS_DIR"
if [ "$RESULTS_DIR" != "$CHECKED_IN_RESULTS" ]; then
  log "run-all: the checked-in evidence in faults/results/ is not being touched; promote with:"
  log "run-all:   cp -R $RESULTS_DIR/. $CHECKED_IN_RESULTS/"
fi

FAILED=0
for variant in "${VARIANTS[@]}"; do
  for fault in "${FAULTS[@]}"; do
    log "run-all: $variant / $fault"
    # The script's own output is kept, not discarded: "harness: wrote faults/results/..." is part of what
    # makes the log a record of the run rather than a list of names.
    "$HERE/$fault.sh" --variant "$variant" >> "$SCRATCH_ROOT/scripts.log" 2>&1
    status=$?
    printf '%s %s %s\n' "$variant" "$fault" "$status" >> "$STATUS_FILE"
    [ "$status" = "0" ] || { log "run-all: $variant / $fault exited $status (harness error)"; FAILED=1; }
    if [ "$CONTROLS" = "1" ] && [ "$fault" != "store-outage" -o "$variant" = "durable" ]; then
      "$HERE/$fault.sh" --variant "$variant" --fault-not-injected >> "$SCRATCH_ROOT/scripts.log" 2>&1
      status=$?
      printf '%s %s-control %s\n' "$variant" "$fault" "$status" >> "$STATUS_FILE"
      [ "$status" = "0" ] || { log "run-all: $variant / $fault control exited $status (harness error)"; FAILED=1; }
    fi
  done
done

# summarise.py's own status is part of the run's status: a missing or stale result is a failure of the
# evidence, and discarding it would let a run "succeed" while printing **missing**.
"$PYTHON" "$HERE/lib/summarise.py" "$RESULTS_DIR" "$STATUS_FILE" "$COMMIT" | tee "$RESULTS_DIR/summary.txt"
SUMMARY_STATUS=${PIPESTATUS[0]}
[ "$SUMMARY_STATUS" = "0" ] || { log "run-all: summarise.py exited $SUMMARY_STATUS"; FAILED=1; }

# The run's own log, written where the results are, so it always matches what it describes.
{
  echo "# faults/run-all.sh"
  echo
  echo "commit: $COMMIT"
  echo "results: $RESULTS_DIR"
  echo "controls: $([ "$CONTROLS" = "1" ] && echo included || echo excluded)"
  echo "variants: ${VARIANTS[*]}"
  echo
  echo '## harness status'
  cat "$STATUS_FILE"
  echo
  echo '## what each script said'
  cat "$SCRATCH_ROOT/scripts.log" 2>/dev/null || echo "(no script output recorded)"
  echo
  echo '## rendered summary'
  cat "$RESULTS_DIR/summary.txt"
} > "$RESULTS_DIR/run-all.log"

if [ "$RESULTS_DIR" = "$CHECKED_IN_RESULTS" ]; then
  log "run-all: wrote the checked-in evidence in $CHECKED_IN_RESULTS"
else
  log "run-all: wrote $RESULTS_DIR; promote with: cp -R $RESULTS_DIR/. $CHECKED_IN_RESULTS/"
fi
exit $FAILED
