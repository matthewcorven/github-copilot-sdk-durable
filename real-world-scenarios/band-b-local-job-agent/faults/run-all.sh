#!/usr/bin/env bash
# Runs every Band B fault against both variants, plus the run-equivalence test, and renders the summary
# the band README quotes.
#
#   ./faults/run-all.sh                 # everything, controls included (about 40 minutes, live model calls)
#   ./faults/run-all.sh durable         # one variant
#   ./faults/run-all.sh --no-controls   # the five faults and the diff test, no controls
#
# A plain run writes to a directory of its own and prints the `cp` that would promote it; it does not
# touch `faults/results/`, which is the evidence. A machine that lacks what a fault needs would otherwise
# replace five measured rows with `not exercised` and nothing would notice. Promoting is explicit:
#
#   FAULTS_RESULTS_DIR=$PWD/faults/results ./faults/run-all.sh
#
# Each fault script is independent and re-runnable: it builds its own run directory, starts its own
# processes, and removes both on exit. This script only sequences them, records each one's exit status, and
# renders the table from the results the scripts wrote and nothing else.
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

# `run-equivalence` has no control: its *repeat* run is the comparison that makes it non-vacuous, and the
# comparison's own self-check runs inside it.
FAULTS=(kill-mid-turn restart-after-checkpoint duplicate-delivery concurrent-clients store-outage)
NO_CONTROL=(store-outage)

mkdir -p "$SCRATCH_ROOT"
STATUS_FILE="$SCRATCH_ROOT/run-all.status"
: > "$STATUS_FILE"

require_tools || exit 1

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
SCRIPT_LOG="$SCRATCH_ROOT/scripts.log"
: > "$SCRIPT_LOG"

record_status() {
  printf '%s %s %s\n' "$1" "$2" "$3" >> "$STATUS_FILE"
  [ "$3" = "0" ] || { log "run-all: $1 / $2 exited $3 (harness error)"; FAILED=1; }
}

for variant in "${VARIANTS[@]}"; do
  for fault in "${FAULTS[@]}"; do
    log "run-all: $variant / $fault"
    # The script's own output is kept, not discarded: "harness: wrote faults/results/..." is part of what
    # makes the log a record of the run rather than a list of names.
    "$HERE/$fault.sh" --variant "$variant" >> "$SCRIPT_LOG" 2>&1
    record_status "$variant" "$fault" "$?"
    if [ "$CONTROLS" = "1" ]; then
      skip=0
      for never in "${NO_CONTROL[@]}"; do
        [ "$fault" = "$never" ] && [ "$variant" = "baseline" ] && skip=1
      done
      if [ "$skip" = "0" ]; then
        log "run-all: $variant / $fault (control)"
        "$HERE/$fault.sh" --variant "$variant" --fault-not-injected >> "$SCRIPT_LOG" 2>&1
        record_status "$variant" "$fault-control" "$?"
      fi
    fi
  done
  log "run-all: $variant / run-equivalence"
  "$HERE/run-equivalence.sh" --variant "$variant" >> "$SCRIPT_LOG" 2>&1
  record_status "$variant" "run-equivalence" "$?"
done

# A result that had to be refused (a measured row under `not exercised`) is a harness failure, and so is
# a summary that cannot render.
"$PYTHON" "$HERE/lib/summarise.py" "$RESULTS_DIR" "$STATUS_FILE" "$COMMIT" | tee "$RESULTS_DIR/summary.txt"
SUMMARY_STATUS=${PIPESTATUS[0]}
[ "$SUMMARY_STATUS" = "0" ] || { log "run-all: summarise.py exited $SUMMARY_STATUS"; FAILED=1; }

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
  cat "$SCRIPT_LOG" 2>/dev/null || echo "(no script output recorded)"
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
