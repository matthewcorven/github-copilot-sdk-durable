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
STATUS_FILE="$SCRATCH_ROOT/run-all.status"
mkdir -p "$SCRATCH_ROOT"
: > "$STATUS_FILE"

require_tools || exit 1

FAILED=0
for variant in "${VARIANTS[@]}"; do
  for fault in "${FAULTS[@]}"; do
    log "run-all: $variant / $fault"
    "$HERE/$fault.sh" --variant "$variant" >/dev/null 2>&1
    status=$?
    printf '%s %s %s\n' "$variant" "$fault" "$status" >> "$STATUS_FILE"
    [ "$status" = "0" ] || { log "run-all: $variant / $fault exited $status (harness error)"; FAILED=1; }
    if [ "$CONTROLS" = "1" ] && [ "$fault" != "store-outage" -o "$variant" = "durable" ]; then
      "$HERE/$fault.sh" --variant "$variant" --fault-not-injected >/dev/null 2>&1
      status=$?
      printf '%s %s-control %s\n' "$variant" "$fault" "$status" >> "$STATUS_FILE"
      [ "$status" = "0" ] || { log "run-all: $variant / $fault control exited $status (harness error)"; FAILED=1; }
    fi
  done
done

"$PYTHON" "$HERE/lib/summarise.py" "$HERE/results" "$STATUS_FILE" | tee "$HERE/results/summary.txt"
exit $FAILED
