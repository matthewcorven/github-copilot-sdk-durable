#!/usr/bin/env bash
# Every Band G fault, against both variants, controls included, then the summary is rendered from the
# result files.
#
#   ./faults/run-all.sh                  # everything, into results/ (~10 minutes)
#   ./faults/run-all.sh durable          # one variant, into a scratch directory
#   ./faults/run-all.sh --no-controls    # the six faults only, into a scratch directory
#   KEEP=1 ./faults/run-all.sh ...       # keep every run directory
#
# **Only a complete run writes `results/`.** A partial run — one variant, or no controls — would leave the
# checked-in tree describing an incomplete matrix (a summary with missing rows, or a control-less one that
# still reads as the band's evidence). So a partial run writes into `$SCRATCH_ROOT/partial-results/`
# instead and says where, and `results/` is only ever the output of a full run. That is what makes
# "checked-in results are what the scripts emit" true of the committed tree rather than of the last thing
# anybody ran.
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

# A partial run is a diagnostic, not evidence, so it never lands in the checked-in tree.
if [ -n "$ONLY" ] || [ "$CONTROLS" != "1" ]; then
  PARTIAL="$SCRATCH_ROOT/partial-results"
  RESULTS_DIR="$PARTIAL"
  log "run-all.sh: partial run — writing to ${PARTIAL#"$REPO_ROOT"/} instead of faults/results/"
  log "run-all.sh: the checked-in results are unchanged; only a full run writes them"
fi
STATUS_FILE="$RESULTS_DIR/run-status.txt"

require_tools || exit 1
build_variants || exit 1

mkdir -p "$RESULTS_DIR"
: > "$STATUS_FILE"

FAULTS=(kill-during-wait restart-after-checkpoint duplicate-approval late-approval abort-mid-wait store-outage)
VARIANTS=(baseline durable)
[ -n "$ONLY" ] && VARIANTS=("$ONLY")

# The middle column has its own script: it needs a scheduler, its verbs are different, and two of the six
# faults do not mean the same thing there. Running it from here keeps one command producing the whole
# matrix; the script itself records why a fault landed differently.
EXTENSION_FAULTS=(kill-during-wait restart-after-checkpoint duplicate-approval late-approval abort-mid-wait store-outage)

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

if [ "$CONTROLS" = "1" ] && { [ -z "$ONLY" ] || [ "$ONLY" = "extension" ]; }; then
  for fault in "${EXTENSION_FAULTS[@]}"; do
    log ""
    log "── extension / $fault ──────────────────────────────────────────────"
    "$HERE/extension.sh" --fault "$fault"
    status=$?
    printf '%s %s %s\n' extension "$fault" "$status" >> "$STATUS_FILE"
    if [ "$status" -ne 0 ]; then
      log "run-all.sh: extension / $fault exited $status — a harness error, not a result"
      failures=$((failures + 1))
      continue
    fi
    log "── extension / $fault (control) ────────────────────────────────────"
    "$HERE/extension.sh" --fault "$fault" --fault-not-injected
    status=$?
    printf '%s %s-control %s\n' extension "$fault" "$status" >> "$STATUS_FILE"
    [ "$status" -ne 0 ] && { log "run-all.sh: extension / $fault control exited $status"; failures=$((failures + 1)); }
  done
fi

log ""
log "rendering the summary from the result files"
if ! "$PYTHON" "$HERE/lib/summarise.py" "$RESULTS_DIR" "$STATUS_FILE" > "$RESULTS_DIR/summary.txt"; then
  log "run-all.sh: the summary could not be rendered; a fault has no result for a variant"
  exit 1
fi
{
  "$PYTHON" "$HERE/lib/summarise.py" "$RESULTS_DIR" "$STATUS_FILE" --md || exit 1
  echo
  "$PYTHON" "$HERE/lib/three_way.py" "$RESULTS_DIR" --md || exit 1
} > "$RESULTS_DIR/summary.md"
log "wrote ${RESULTS_DIR#"$REPO_ROOT"/}/summary.txt and ${RESULTS_DIR#"$REPO_ROOT"/}/summary.md"

# A full run also checks that the band README's table is the one this run produced. The README tells a
# reader the table is generated; without this, "generated" was a claim about the past. A drift is a failure,
# not a warning, because the README is what a reader who never opens faults/results/ will believe.
if [ "$RESULTS_DIR" = "$HARNESS_DIR/results" ]; then
  if ! "$PYTHON" "$HERE/lib/check_readme_table.py" "$BAND_DIR/README.md" "$RESULTS_DIR"; then
    log "run-all.sh: the band README's three-way table is not what the results produce (see above)"
    exit 1
  fi
fi

if [ "$failures" -ne 0 ]; then
  log "run-all.sh: $failures script(s) reported a harness error. The summary above says which."
  exit 1
fi
exit 0
