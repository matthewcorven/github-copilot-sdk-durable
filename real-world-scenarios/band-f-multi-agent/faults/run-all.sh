#!/usr/bin/env bash
# Runs every fault against both variants and writes the summary the band README quotes.
#
#   ./run-all.sh                     # every fault, both variants, controls included
#   ./run-all.sh durable             # one variant
#   ./run-all.sh --no-controls       # the five faults only
#   ./run-all.sh --faults kill-mid-delegation,store-outage
#
# Each fault script is independent and re-runnable: it builds its own run directory, starts its own
# processes, and removes both on exit. This script only sequences them, records the exit status of
# each, and renders the table from the results the scripts wrote and nothing else.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

VARIANTS=()
CONTROLS=1
ONLY=""
while [ $# -gt 0 ]; do
  case "$1" in
    --no-controls) CONTROLS=0; shift ;;
    --faults) ONLY="${2:-}"; shift 2 ;;
    --faults=*) ONLY="${1#--faults=}"; shift ;;
    baseline|durable) VARIANTS+=("$1"); shift ;;
    *) log "run-all: unknown argument '$1'"; exit 2 ;;
  esac
done
if [ ${#VARIANTS[@]} -eq 0 ]; then VARIANTS=(baseline durable); fi

ALL_FAULTS=(kill-mid-delegation restart-after-checkpoint duplicate-delivery competing-managers store-outage)
FAULTS=()
if [ -n "$ONLY" ]; then
  IFS=',' read -r -a requested <<< "$ONLY"
  for wanted in "${requested[@]}"; do
    found=0
    for fault in "${ALL_FAULTS[@]}"; do [ "$wanted" = "$fault" ] && found=1; done
    [ "$found" = "1" ] || { log "run-all: unknown fault '$wanted'"; exit 2; }
    FAULTS+=("$wanted")
  done
else
  FAULTS=("${ALL_FAULTS[@]}")
fi

STATUS_FILE="$SCRATCH_ROOT/run-all.status"
mkdir -p "$SCRATCH_ROOT"
: > "$STATUS_FILE"

require_tools || exit 1
if [ "$(baseline_unrunnable_reason >/dev/null 2>&1; echo $?)" != "0" ]; then
  log "run-all: no endpoint for the baseline; its rows will be recorded as not exercised"
fi

FAILED=0
for variant in "${VARIANTS[@]}"; do
  for fault in "${FAULTS[@]}"; do
    log "run-all: $variant / $fault"
    "$HERE/$fault.sh" --variant "$variant" >/dev/null 2>&1
    status=$?
    printf '%s %s %s\n' "$variant" "$fault" "$status" >> "$STATUS_FILE"
    [ "$status" = "0" ] || { log "run-all: $variant / $fault exited $status (harness error)"; FAILED=1; }
    if [ "$CONTROLS" = "1" ]; then
      log "run-all: $variant / $fault --fault-not-injected"
      "$HERE/$fault.sh" --variant "$variant" --fault-not-injected >/dev/null 2>&1
      status=$?
      printf '%s %s-control %s\n' "$variant" "$fault" "$status" >> "$STATUS_FILE"
      [ "$status" = "0" ] || { log "run-all: $variant / $fault control exited $status (harness error)"; FAILED=1; }
    fi
  done
done

# The summariser exits non-zero when a result or control is missing, and its output is the band's
# table. Its status is checked before the table is written: a missing result must not leave
# `results/summary.txt` holding a table of `**missing**` rows and an exit code of 0.
SUMMARY="$("$PYTHON" "$HERE/lib/summarise.py" "$HERE/results" "$STATUS_FILE")"
SUMMARY_STATUS=$?
printf '%s\n' "$SUMMARY" > "$HERE/results/summary.txt"
printf '%s\n' "$SUMMARY"
if [ "$SUMMARY_STATUS" -ne 0 ]; then
  log "run-all: the summary reports a missing result or control (exit $SUMMARY_STATUS)"
  FAILED=1
fi
exit $FAILED
