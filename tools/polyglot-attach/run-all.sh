#!/usr/bin/env bash
# Verifies that the deterministic replay runtime is language-neutral: every
# non-.NET SDK client attaches to it over stdio and completes one recorded turn.
#
# Each check starts the replay runtime as a child process, opens the session the
# shared fixture records, sends one turn, and asserts the recorded response. All
# five checks use the same fixture (tools/ReplayRuntime/fixtures/basic.json), so a
# divergence points at the client rather than at the transcript.
#
# Usage:
#   tools/polyglot-attach/run-all.sh                 # run every available client
#   tools/polyglot-attach/run-all.sh node python     # run only the named clients
#
# Exit status is non-zero only when an *available* toolchain failed. A client
# whose toolchain is not installed is reported as SKIP, never as a failure.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
FIXTURE="$ROOT/tools/ReplayRuntime/fixtures/basic.json"
RUNTIME_PROJECT="$ROOT/tools/ReplayRuntime/ReplayRuntime.csproj"
RUNTIME_DLL="$ROOT/tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime.dll"
BUILD_DIR="$HERE/target"

# A client that hangs must not hang the runner. The runtime itself gives up on a
# stalled tool result after 30s, so 120s leaves room for a cold compile.
CHECK_TIMEOUT="${CHECK_TIMEOUT:-120}"

ALL_CLIENTS=(node python go rust java)
if [[ $# -gt 0 ]]; then
  CLIENTS=("$@")
else
  CLIENTS=("${ALL_CLIENTS[@]}")
fi

mkdir -p "$BUILD_DIR"

failures=0
skips=0
passes=0

# Runs a command with a watchdog. Output is captured so a passing check prints one
# line and a failing check prints its diagnostics.
run_check() {
  local name="$1"
  shift
  local log="$BUILD_DIR/$name.log"

  "$@" >"$log" 2>&1 &
  local pid=$!
  local waited=0
  while kill -0 "$pid" 2>/dev/null; do
    if (( waited >= CHECK_TIMEOUT )); then
      kill "$pid" 2>/dev/null
      wait "$pid" 2>/dev/null
      echo "FAIL     $name (timed out after ${CHECK_TIMEOUT}s)"
      sed 's/^/         /' "$log" | head -20
      return 1
    fi
    sleep 1
    waited=$((waited + 1))
  done

  wait "$pid"
  local status=$?
  if (( status != 0 )); then
    echo "FAIL     $name (exit $status)"
    sed 's/^/         /' "$log" | head -20
    return 1
  fi

  echo "PASS     $name"
  sed 's/^/         /' "$log" | head -5
  return 0
}

skip() {
  echo "SKIP     $1 ($2)"
  skips=$((skips + 1))
}

# A toolchain counts as available only when it actually runs. macOS ships launcher
# shims at /usr/bin/java and /usr/bin/javac that exist on PATH but fail with
# "Unable to locate a Java Runtime", so presence alone is not enough.
tool_works() {
  local command="$1"
  local version_flag="$2"
  command -v "$command" >/dev/null 2>&1 || return 1
  "$command" "$version_flag" >/dev/null 2>&1
}

pass() {
  passes=$((passes + 1))
}

fail() {
  failures=$((failures + 1))
}

# ---------------------------------------------------------------------------
# The runtime is the shared subject of every check, so build it once.
# ---------------------------------------------------------------------------

if ! command -v dotnet >/dev/null 2>&1; then
  echo "SKIP     all clients (the .NET SDK is required to build the replay runtime)"
  echo
  echo "0 passed, 0 failed, ${#CLIENTS[@]} skipped."
  exit 0
fi

if [[ ! -f "$FIXTURE" ]]; then
  echo "FAIL     setup (shared fixture not found at $FIXTURE)"
  exit 1
fi

echo "=== Building the replay runtime ==="
if ! dotnet build "$RUNTIME_PROJECT" --nologo -v q >"$BUILD_DIR/runtime-build.log" 2>&1; then
  echo "FAIL     setup (replay runtime did not build)"
  sed 's/^/         /' "$BUILD_DIR/runtime-build.log" | head -20
  exit 1
fi

if [[ ! -f "$RUNTIME_DLL" ]]; then
  echo "FAIL     setup (replay runtime assembly not found at $RUNTIME_DLL)"
  exit 1
fi

# Every client launches the runtime the same way, so the launch contract is
# identical across languages and only the client code differs.
RUNTIME_CMD=(dotnet "$RUNTIME_DLL" --fixture "$FIXTURE")

echo "=== Attaching from each SDK client ==="

for client in "${CLIENTS[@]}"; do
  case "$client" in
    node)
      if ! tool_works node --version; then
        skip node "node is not installed"
        continue
      fi
      if run_check node node "$HERE/node/attach.mjs" "${RUNTIME_CMD[@]}"; then pass; else fail; fi
      ;;

    python)
      if ! tool_works python3 --version; then
        skip python "python3 is not installed"
        continue
      fi
      if run_check python python3 "$HERE/python/attach.py" "${RUNTIME_CMD[@]}"; then pass; else fail; fi
      ;;

    go)
      if ! tool_works go version; then
        skip go "the Go toolchain is not installed"
        continue
      fi
      if ! (cd "$HERE/go" && go build -o "$BUILD_DIR/go-attach" .) >"$BUILD_DIR/go-build.log" 2>&1; then
        echo "FAIL     go (build failed)"
        sed 's/^/         /' "$BUILD_DIR/go-build.log" | head -20
        fail
        continue
      fi
      if run_check go "$BUILD_DIR/go-attach" "${RUNTIME_CMD[@]}"; then pass; else fail; fi
      ;;

    rust)
      if ! tool_works cargo --version; then
        skip rust "the Rust toolchain (cargo) is not installed"
        continue
      fi
      # --offline keeps the check hermetic: the crate has no dependencies, so a
      # network round trip would only be a way to fail.
      if ! (cd "$HERE/rust" && cargo build --offline --quiet) >"$BUILD_DIR/rust-build.log" 2>&1; then
        echo "FAIL     rust (build failed)"
        sed 's/^/         /' "$BUILD_DIR/rust-build.log" | head -20
        fail
        continue
      fi
      if run_check rust "$HERE/rust/target/debug/attach" "${RUNTIME_CMD[@]}"; then pass; else fail; fi
      ;;

    java)
      if ! tool_works javac -version || ! tool_works java -version; then
        skip java "a JDK (javac and java) is not installed"
        continue
      fi
      if ! javac -d "$BUILD_DIR/java" "$HERE/java/Attach.java" >"$BUILD_DIR/java-build.log" 2>&1; then
        echo "FAIL     java (build failed)"
        sed 's/^/         /' "$BUILD_DIR/java-build.log" | head -20
        fail
        continue
      fi
      if run_check java java -cp "$BUILD_DIR/java" Attach "${RUNTIME_CMD[@]}"; then pass; else fail; fi
      ;;

    *)
      echo "FAIL     $client (unknown client; expected one of: ${ALL_CLIENTS[*]})"
      fail
      ;;
  esac
done

echo
echo "$passes passed, $failures failed, $skips skipped."

if (( failures > 0 )); then
  exit 1
fi

exit 0
