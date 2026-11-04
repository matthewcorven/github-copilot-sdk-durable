#!/usr/bin/env bash
# Starts the Band A baseline (the vendored AgentOrchestrator) and prints the URL.
# Ctrl-C stops it.
#
# Usage:
#   ./run.sh            # build, run, print the URL, remove the scratch directory on exit
#   KEEP=1 ./run.sh     # keep the scratch directory (Copilot sign-in, properties database)
#   PORT=5028 ./run.sh  # use a port other than 5027
#
# The app writes two things: `properties.db` (SQLite, seeded from Data/Properties at
# first start) and the Copilot client's base directory (`AppState.cs` puts it under
# AppContext.BaseDirectory). Both therefore resolve under the scratch directory this
# script publishes into, never inside the repository.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$HERE/baseline/AgentOrchestrator.csproj"
PORT="${PORT:-5027}"

# The repo has no root solution and this machine has more than one dotnet; prefer the
# Homebrew SDK that carries the .NET 10 Blazor toolchain.
DOTNET="${DOTNET:-/opt/homebrew/bin/dotnet}"
if [ ! -x "$DOTNET" ]; then
  DOTNET="$(command -v dotnet || true)"
fi
if [ -z "$DOTNET" ]; then
  echo "run.sh: no dotnet executable found; install the .NET 10 SDK" >&2
  exit 1
fi

SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/band-a-baseline.XXXXXX")"
cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then
    echo
    echo "Scratch directory kept: $SCRATCH"
  else
    rm -rf "$SCRATCH"
  fi
}
trap cleanup EXIT INT TERM

# Publish into the scratch directory so AppContext.BaseDirectory — the root the app
# uses for properties.db and for the Copilot base directory — is inside scratch.
"$DOTNET" publish "$PROJECT" -c Release -o "$SCRATCH/app" --nologo -v quiet

echo
echo "Band A baseline (AgentOrchestrator, unmodified upstream behaviour)"
echo "  URL:     http://localhost:$PORT"
echo "  Scratch: $SCRATCH   (KEEP=1 to keep it)"
echo "  Stop with Ctrl-C."
echo

cd "$SCRATCH/app"
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:$PORT" \
  "$DOTNET" BandABaseline.dll
