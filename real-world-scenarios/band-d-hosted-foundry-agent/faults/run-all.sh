#!/usr/bin/env bash
# Every Band D4 fault, against every variant that can have it, controls included, then the
# summary is rendered from the result files and nothing else.
#
#   ./faults/run-all.sh                    # the whole matrix, into results/
#   ./faults/run-all.sh durable-outside    # one variant, into a scratch directory
#   ./faults/run-all.sh --no-controls      # faults only, into a scratch directory
#   KEEP=1 ./faults/run-all.sh ...         # keep every run directory and container
#
# **Only a complete run writes `results/`.** A partial run is a diagnostic, so it writes into
# `$SCRATCH_ROOT/partial-results/` and says where. A partial run that overwrote the checked-in
# tree would leave the band's evidence describing a matrix nobody ran.
#
# A script exits 0 when it ran, including when the application lost work — that is the
# observation. A non-zero exit is a harness error: a container that never started, a marker
# that never appeared, a kill that could not be aimed at the process it was meant to hit.
# Those are recorded in `run-status.txt` and make this script exit non-zero.
#
# Faults that a variant genuinely cannot have are recorded with `lib/not_exercised.sh`, so a
# matrix cell is a reason rather than an empty square.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

ONLY=""
CONTROLS=1
while [ $# -gt 0 ]; do
  case "$1" in
    baseline|durable-inside|durable-outside) ONLY="$1"; shift ;;
    --no-controls) CONTROLS=0; shift ;;
    -h|--help) sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed -e 's/^# \{0,1\}//'; exit 0 ;;
    *) log "run-all.sh: unknown argument '$1'"; exit 2 ;;
  esac
done

if [ -n "$ONLY" ] || [ "$CONTROLS" != "1" ]; then
  RESULTS_DIR="$SCRATCH_ROOT/partial-results"
  log "run-all.sh: partial run — writing to ${RESULTS_DIR#"$REPO_ROOT"/} instead of faults/results/"
  log "run-all.sh: the checked-in results are unchanged; only a full run writes them"
fi
STATUS_FILE="$RESULTS_DIR/run-status.txt"

require_tools || exit 1

# The result guard is a test, not a claim: a matrix is only as good as the refusals behind it.
log "run-all.sh: running the result guard's own tests"
if ! python3 "$HERE/lib/test_result.py" > "$SCRATCH_ROOT/test-result.log" 2>&1; then
  log "run-all.sh: the result guard's tests failed; a matrix it wrote would not be trustworthy"
  tail -20 "$SCRATCH_ROOT/test-result.log" >&2
  exit 1
fi

mkdir -p "$RESULTS_DIR"
: > "$STATUS_FILE"
printf 'started %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$STATUS_FILE"

FAULTS=(client-dies-mid-run container-restart duplicate-delivery concurrent-clients store-outage agent-call-failure)
VARIANTS=(baseline durable-inside durable-outside)
[ -n "$ONLY" ] && VARIANTS=("$ONLY")

# Which fault runs against which variant. A fault absent from this list is recorded as not
# exercised for that variant, with the reason, rather than skipped.
supports() { # fault variant
  case "$1:$2" in
    client-dies-mid-run:baseline|client-dies-mid-run:durable-outside) return 0 ;;
    container-restart:baseline|container-restart:durable-inside) return 0 ;;
    duplicate-delivery:baseline|duplicate-delivery:durable-inside) return 0 ;;
    concurrent-clients:baseline|concurrent-clients:durable-inside|concurrent-clients:durable-outside) return 0 ;;
    store-outage:baseline|store-outage:durable-inside|store-outage:durable-outside) return 0 ;;
    agent-call-failure:baseline|agent-call-failure:durable-inside|agent-call-failure:durable-outside) return 0 ;;
    *) return 1 ;;
  esac
}

reason_for() { # fault variant
  case "$1:$2" in
    client-dies-mid-run:durable-inside)
      printf '%s' "this variant has no client process to kill" ;;
    container-restart:durable-outside)
      printf '%s' "this variant's restart is the death of its client, which is client-dies-mid-run" ;;
    duplicate-delivery:durable-outside)
      printf '%s' "this variant's gate refuses a second answer by construction, which is a property of its gate and not a fault to inject" ;;
    *) printf '%s' "no injection for this variant" ;;
  esac
}

detail_for() { # fault variant
  case "$1:$2" in
    client-dies-mid-run:durable-inside)
      printf '%s' "D2's turn is a platform request served inside the hosted container; the harness has no client of its own to kill, and the platform's foreground-cancellation behaviour is documented rather than exercised here. Failing to kill something would have been reported as a missing result; this says which variant and why." ;;
    container-restart:durable-outside)
      printf '%s' "D3's state is a local store and its process is the run. Stopping it is a client death (client-dies-mid-run); there is no container of its own to restart, and pretending otherwise would have measured the harness's own process." ;;
    duplicate-delivery:durable-outside)
      printf '%s' "gate.sh refuses to overwrite an existing decision, so the duplicate is refused before any state is touched. That is worth knowing and is not a fault injection: the observation would be about the harness's own file-write guard." ;;
    *) printf '%s' "no injection for this variant" ;;
  esac
}

failures=0
for variant in "${VARIANTS[@]}"; do
  for fault in "${FAULTS[@]}"; do
    if ! supports "$fault" "$variant"; then
      log "run-all.sh: $variant / $fault — not exercised, recording why"
      mkdir -p "$RESULTS_DIR/$variant"
      "$HERE/lib/not_exercised.sh" --variant "$variant" --fault "$fault" \
        --out "$RESULTS_DIR/$variant/$fault.json" \
        --reason "$(reason_for "$fault" "$variant")" \
        --detail "$(detail_for "$fault" "$variant")" >/dev/null 2>&1
      printf '%s %s %s\n' "$variant" "$fault" "$?" >> "$STATUS_FILE"
      if [ "$CONTROLS" = "1" ]; then
        "$HERE/lib/not_exercised.sh" --variant "$variant" --fault "$fault" \
          --out "$RESULTS_DIR/$variant/$fault-control.json" \
          --reason "control for a fault this variant cannot have" \
          --detail "No control exists for a fault that is not injected. Recorded so a control column is a reason rather than a blank." >/dev/null 2>&1
        printf '%s %s-control %s\n' "$variant" "$fault" "$?" >> "$STATUS_FILE"
      fi
      continue
    fi

    log ""
    log "── $variant / $fault ──────────────────────────────────────────────"
    FAULTS_RESULTS_DIR="$RESULTS_DIR" "$HERE/$fault.sh" --variant "$variant"
    status=$?
    printf '%s %s %s\n' "$variant" "$fault" "$status" >> "$STATUS_FILE"
    if [ "$status" -ne 0 ]; then
      log "run-all.sh: $variant / $fault exited $status — a harness error, not a result"
      failures=$((failures + 1))
      continue
    fi

    if [ "$CONTROLS" = "1" ]; then
      log ""
      log "── $variant / $fault (control) ────────────────────────────────────"
      FAULTS_RESULTS_DIR="$RESULTS_DIR" "$HERE/$fault.sh" --variant "$variant" --fault-not-injected
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
if ! python3 "$HERE/lib/summarise.py" "$RESULTS_DIR" > "$RESULTS_DIR/summary.txt"; then
  log "run-all.sh: the summary could not be rendered; see the problems it printed"
  exit 1
fi
python3 "$HERE/lib/summarise.py" "$RESULTS_DIR" --md > /dev/null || exit 1
log "wrote ${RESULTS_DIR#"$REPO_ROOT"/}/summary.txt and summary.md"

if [ "$failures" -ne 0 ]; then
  log "run-all.sh: $failures script(s) reported a harness error. run-status.txt says which."
  exit 1
fi
exit 0
