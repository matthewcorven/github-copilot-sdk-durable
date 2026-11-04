#!/usr/bin/env bash
# Reproduces the Durable variant's evidence: restart mid-enquiry, resume, and concurrency.
#
# Usage:
#   ./evidence.sh              # run every capture, print it, and remove scratch on exit
#   KEEP=1 ./evidence.sh       # keep the scratch directory
#
# Stages 1-3 use the recorded runtime in tools/ReplayRuntime with the fixture in fixtures/, so
# they are deterministic: no Copilot sign-in, no model, no network. The kill point is
# BAND_A_EXIT_AFTER_PHASE, which exits the process the moment that phase commits.
#
# Stage 4 needs the real Copilot CLI: the recorded runtime serves every pinned session from the
# first recording, so it cannot give two customers two different transcripts. Pass LIVE=1 to run
# stage 4 against the signed-in `copilot` executable.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
RUNTIME="$ROOT/tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime"

[ -x "$RUNTIME" ] || { echo "evidence.sh: build the recorded runtime first: $DOTNET build tools/ReplayRuntime" >&2; exit 1; }

SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/band-a-durable-evidence.XXXXXX")"
cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then echo; echo "Scratch kept: $SCRATCH"; else rm -rf "$SCRATCH"; fi
}
trap cleanup EXIT INT TERM

"$DOTNET" publish "$HERE/BandADurable.csproj" -c Release -o "$SCRATCH/app" --nologo -v quiet
APP="$SCRATCH/app/BandADurable.dll"

run() {
  local store="$1" fixture="$2"; shift 2
  local log="$SCRATCH/run.log"
  BAND_A_STORE="$store" BAND_A_FIXTURE="$SCRATCH/app/fixtures/$fixture" \
  DURABLE_RUNTIME_PATH="$RUNTIME" Logging__LogLevel__Default=Warning \
    "$DOTNET" "$APP" "$@" > "$log" 2>&1
  local status=$?
  grep -v '^      \|NU1903\|^warn: Microsoft.EntityFrameworkCore' "$log"
  return $status
}

echo "############ 1. restart mid-enquiry: exit after the 'searching' phase commits ############"
rm -f "$SCRATCH/restart.jsonl"
run "$SCRATCH/restart.jsonl" enquiry.json --enquiry "Looking for a 3-bed family home near good schools, budget around \$500k" \
  --exit-after-phase searching | sed "s|$SCRATCH|<scratch>|g"
echo "exit code: $?  (42 is the seam: the store is committed, and the process is gone)"

echo
echo "############ 2. resume the same store in a new process ############"
run "$SCRATCH/restart.jsonl" enquiry.json --resume | sed "s|$SCRATCH|<scratch>|g"

echo
echo "############ 3. late wakeup after rejection cannot overwrite the terminal state ############"
rm -f "$SCRATCH/rejected.jsonl"
run "$SCRATCH/rejected.jsonl" rejection.json --enquiry "BUY CRYPTO NOW!!! Not actually looking for property lol" \
  --late-wakeup | sed "s|$SCRATCH|<scratch>|g"
echo
echo "############ 3b. the rejection survives a restart (the baseline loses it) ############"
run "$SCRATCH/rejected.jsonl" rejection.json --resume | sed "s|$SCRATCH|<scratch>|g"

echo
echo "############ 4. concurrency: two enquiries for two customers, submitted together ############"
if [ "${LIVE:-0}" = "1" ]; then
  LIVE_ARGS=()
else
  LIVE_ARGS=(--fixture "$SCRATCH/app/fixtures/concurrent.json")
  echo "# recorded runtime: both pinned sessions are served from the first recording, so"
  echo "# this stage shows isolation of the durable documents, not of the model transcripts."
fi
rm -f "$SCRATCH/concurrent.jsonl"
if [ "${LIVE:-0}" = "1" ]; then
  BAND_A_STORE="$SCRATCH/concurrent.jsonl" Logging__LogLevel__Default=Warning \
    "$DOTNET" "$APP" \
      --enquiry "Looking for a 3-bed family home near good schools in the suburbs, budget around \$500k" \
      --enquiry "Need a downtown condo with parking, 1-2 bedrooms, under \$350k" 2>&1 | sed "s|$SCRATCH|<scratch>|g"
else
  run "$SCRATCH/concurrent.jsonl" concurrent.json \
    --enquiry "first customer: 3-bed family home" \
    --enquiry "second customer: downtown condo with parking" | sed "s|$SCRATCH|<scratch>|g"
fi

echo
echo "############ 5. concurrency: eight writers race one document ############"
rm -f "$SCRATCH/contend.jsonl"
run "$SCRATCH/contend.jsonl" enquiry.json --enquiry "contended enquiry" --contend 8 | sed "s|$SCRATCH|<scratch>|g"
