#!/usr/bin/env bash
# Starts the Band A Durable variant and prints the URL. Ctrl-C stops it.
#
# Usage:
#   ./run.sh                          # build, run, print the URL, remove scratch on exit
#   KEEP=1 ./run.sh                   # keep the scratch directory (store, Copilot sign-in, properties.db)
#   PORT=5028 ./run.sh                # use a port other than 5028
#
# The Durable variant is deliberately the baseline's sibling: same page, same pipeline, same
# tools. What differs is where the state lives. The baseline keeps a Dictionary<string, Agent>
# in memory; this one keeps an enquiry document in a DurableStore on disk, so the dashboard a
# restart opens is the dashboard the previous process left behind.
#
# Nothing is written inside the repository. BAND_A_STORE puts the store in scratch; it survives
# Ctrl-C, so KEEP=1 followed by BAND_A_STORE=... ./run.sh resumes the same enquiries.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$HERE/BandADurable.csproj"
PORT="${PORT:-5028}"

DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1; then
  echo "run.sh: no dotnet executable found; install the .NET 10 SDK" >&2
  exit 1
fi

SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/band-a-durable.XXXXXX")"
STORE="${BAND_A_STORE:-$SCRATCH/band-a-store.jsonl}"

cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then
    echo
    echo "Scratch directory kept: $SCRATCH"
    echo "Store kept:             $STORE"
    echo "Resume with:            BAND_A_STORE=$STORE ./run.sh"
  else
    rm -rf "$SCRATCH"
  fi
}
trap cleanup EXIT INT TERM

"$DOTNET" publish "$PROJECT" -c Release -o "$SCRATCH/app" --nologo -v quiet

echo
echo "Band A Durable variant (BandADurable)"
echo "  URL:     http://localhost:$PORT"
echo "  Store:   $STORE   (durable: survives a restart)"
echo "  Scratch: $SCRATCH   (KEEP=1 to keep it)"
echo "  Stop with Ctrl-C."
echo

cd "$SCRATCH/app"
BAND_A_STORE="$STORE" ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:$PORT" \
  exec "$DOTNET" BandADurable.dll
