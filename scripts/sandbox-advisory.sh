#!/usr/bin/env bash
#
# Advisory sandbox report: for each sandbox example, print the platform, the backend the
# runtime reports, and the example's own `run mode:` line. Runs on any CI runner with no
# Copilot sign-in, and is advisory only — a backend difference here must never fail a
# pull request (docs/SANDBOX_CI.md).
#
# Why the recorded runtime: CI has no Copilot sign-in, so the examples run against
# tools/ReplayRuntime with DURABLE_RUNTIME_PATH set. In that mode SandboxReport.Resolve()
# reports SandboxRunMode.Replayed, and the runtime reports backend `unsupported` with
# platform `other` because no backend engaged. That is the policy half only: it proves
# policy propagation, never OS enforcement. A Replayed line is NOT enforcement proof.
#
# Exit status: 0 once the report is printed, 1 only if the recorded runtime did not build or
# the report did not reach its summary. The examples' own exit statuses are reported, never
# propagated.
#
# `set +e` is deliberate and required. The workflow step is `shell: bash`, which GitHub runs
# as `/usr/bin/bash --noprofile --norc -e -o pipefail {0}`. The inherited `-e` would abort
# the script at the first non-zero example — before `status=$?` is read and before the
# summary prints — and the job's `continue-on-error` would then show a green check with a
# truncated report. `set +e` restores the error handling this script was written for.
set +e
set -uo pipefail
set +e

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/tools/ReplayRuntime/ReplayRuntime.csproj"
runtime="$root/tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime"
examples=(33-sandbox-sdk-filesystem 34-sandbox-sdk-network-credentials 35-sandbox-sdk-observability-bypass)

# The backend the runtime selects on a platform. These are the SandboxBackend values from
# GitHub.Copilot.SDK 1.0.26 (docs/SANDBOX_MATRIX.md section 1), so they are the names the
# runtime would report — an expectation about the platform, not a detection.
expected_backend() {
  case "$1" in
    linux) echo "bubblewrap" ;;
    macos) echo "seatbelt" ;;
    windows) echo "process_container" ;;
    *) echo "unknown" ;;
  esac
}

detect_platform() {
  if [[ -n "${RUNNER_OS:-}" ]]; then
    case "$RUNNER_OS" in
      Linux) echo "linux" ;;
      macOS) echo "macos" ;;
      Windows) echo "windows" ;;
      *) echo "unknown" ;;
    esac
    return
  fi

  # Local runs only. In CI RUNNER_OS is always set, so the Windows branches below exist for a
  # developer running this from Git Bash on Windows, not for the workflow.
  case "$(uname -s)" in
    Linux) echo "linux" ;;
    Darwin) echo "macos" ;;
    MINGW* | MSYS* | CYGWIN*) echo "windows" ;;
    *) echo "unknown" ;;
  esac
}

# Whether the platform's backend program is on this host at all. This is a host probe, not
# the runtime's report: it says the prerequisite is present, never that anything engaged.
present() {
  if command -v "$1" >/dev/null 2>&1; then echo "present: $(command -v "$1")"; else echo "present: no"; fi
}

host_probe() {
  case "$1" in
    linux) echo "bwrap $(present bwrap)" ;;
    macos) echo "sandbox-exec $(present sandbox-exec)" ;;
    windows)
      echo "not performed (check the OS build supports BaseContainer before expecting process_container)"
      ;;
    *)
      echo "not performed"
      ;;
  esac
}

platform="$(detect_platform)"
backend="$(expected_backend "$platform")"

echo "=== Sandbox advisory (advisory only: a difference here never fails a pull request) ==="
echo "platform: $platform"
echo "expected backend on $platform: $backend"
echo "  the value the runtime reports when a backend engages on $platform. This line is an"
echo "  expectation about the platform, not a detection (docs/SANDBOX_CI.md)."
echo "host probe: $(host_probe "$platform")"
echo

if ! dotnet build "$project" --nologo -v q; then
  echo "sandbox-advisory: could not build $project" >&2
  exit 1
fi

# The recorded runtime is built as a framework-dependent single file, so the binary is the
# extension-less `ReplayRuntime` on every OS. Windows still resolves `ReplayRuntime.exe`, so
# prefer that name when it is the one that exists.
if [[ -f "$runtime.exe" ]]; then
  runtime="$runtime.exe"
fi
if [[ ! -f "$runtime" ]]; then
  echo "sandbox-advisory: the recorded runtime was not produced at $runtime" >&2
  exit 1
fi

ran_policy=0
printed_mode=0
engaged_note=""
for name in "${examples[@]}"; do
  echo "=== $name.cs ==="
  echo "platform: $platform"
  # Allowed to fail: a Replayed example stops at an on-disk assertion the recording cannot
  # satisfy. `set +e` above is what keeps the assignment from aborting the script.
  output="$(cd "$root/examples" && DURABLE_RUNTIME_PATH="$runtime" dotnet run "$name.cs" 2>&1)"
  status=$?

  # The example's own lines, with compiler warnings filtered so the log reads as output. The
  # full output is kept: the report line is the point of the job, so nothing is truncated.
  printf '%s\n' "$output" | grep -v 'warning [A-Z]\+[0-9]\+' | sed 's/^/  /'

  # The policy half prints its resolved policy before any enforcement assertion, so its
  # presence shows the mapping was built and asserted even when enforcement was not.
  if printf '%s\n' "$output" | grep -q '^resolved .*policy:'; then
    echo "policy half: ran (the resolved policy and its mapping assertions printed)"
    ran_policy=$((ran_policy + 1))
  else
    echo "policy half: did not run (the example stopped before printing its resolved policy)"
  fi

  line="$(printf '%s\n' "$output" | sed -n 's/^\(run mode: .*\)$/\1/p' | head -1)"
  if [[ -n "$line" ]]; then
    reported_backend="$(printf '%s\n' "$line" | sed -n 's/.*backend: \([^,)]*\).*/\1/p')"
    reported_platform="$(printf '%s\n' "$line" | sed -n 's/.*platform: \([^,)]*\).*/\1/p')"
    echo "DETECTED $name.cs: $line"
    echo "  backend: $reported_backend (reported by the runtime)"
    echo "  platform: $reported_platform (reported by the runtime)"
    if [[ "$reported_backend" != "unsupported" && "$reported_backend" != "none" ]]; then
      engaged_note="$engaged_note $name=$reported_backend"
    fi
    printed_mode=$((printed_mode + 1))
  else
    echo "DETECTED $name.cs: run mode: not reported (exit $status)"
    echo "  backend: not reported by the runtime; no backend engaged in Replayed mode"
    echo "  platform: $platform (detected by the runner; the runtime reported nothing)"
    echo "         The example stopped before its sandbox report, at an on-disk assertion the"
    echo "         recording cannot satisfy. Expected in Replayed mode; see docs/SANDBOX_CI.md."
  fi
  echo
done

echo "=== Summary ==="
echo "platform reported by the job: $platform"
echo "examples that printed a run mode: $printed_mode of ${#examples[@]}"
echo "examples whose policy half ran: $ran_policy of ${#examples[@]}"
if [[ -n "$engaged_note" ]]; then
  echo "note: a backend engaged for:$engaged_note — read that example's enforcement assertions"
  echo "      as enforcement evidence; the rest are Replayed and prove the policy only."
fi
echo "mode: Replayed (no Copilot sign-in in CI). This is the policy half only: policy"
echo "      propagation, never OS enforcement. The three-part enforcement proof is verified"
echo "      only in Enforced mode, on a signed-in host with a real backend."

# The workflow greps for this marker to tell a completed report from a script that died.
echo "advisory-report: complete"
exit 0
