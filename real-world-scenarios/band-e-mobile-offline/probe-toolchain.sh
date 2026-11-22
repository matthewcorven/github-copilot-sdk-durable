#!/usr/bin/env bash
# Reproduces the toolchain probe recorded in evidence/maui-toolchain-probe.txt.
#
# Writes nothing into this repository: the scaffold project lands under a scratch
# directory that is removed on exit. Run from this directory.
#
#   ./probe-toolchain.sh            # print the report to stdout; exits with the build status
#   ./probe-toolchain.sh > evidence/maui-toolchain-probe.txt
#
# Exit status is 0 only when the MAUI Android build succeeded, so this can gate work: a
# machine without the Android SDK leaves the build at `error XA5300` and this exits 1.
set -u

say() { printf '\n=== %s ===\n' "$*"; }

say "uname / date"
uname -sm
date -u +%Y-%m-%dT%H:%M:%SZ

say "dotnet --version"
dotnet --version 2>&1

say "dotnet --info (SDK, workloads, base path)"
dotnet --info 2>&1

say "dotnet workload list"
dotnet workload list 2>&1

say "dotnet workload search maui"
dotnet workload search maui 2>&1

say "dotnet new list maui"
dotnet new list maui 2>&1

say "Android SDK environment"
printf 'ANDROID_HOME=%s\nANDROID_SDK_ROOT=%s\n' "${ANDROID_HOME:-<unset>}" "${ANDROID_SDK_ROOT:-<unset>}"
ls -d "$HOME/Library/Android/sdk" 2>&1 || true
command -v adb emulator sdkmanager 2>&1 || echo "adb/emulator/sdkmanager: not on PATH"

say "Java runtime"
command -v java javac 2>&1 || true
/usr/libexec/java_home -V 2>&1 || true
ls /Library/Java/JavaVirtualMachines 2>&1 || true

scratch="$(mktemp -d "${TMPDIR:-/tmp}/band-e-probe.XXXXXX")"
trap 'rm -rf "$scratch"' EXIT

if dotnet new maui -n BandEProbe -o "$scratch/BandEProbe" >/dev/null 2>&1; then
  # The template emits Android, iOS and Mac Catalyst heads. This band is Android-only,
  # so the probe narrows the target to net10.0-android before building.
  csproj="$scratch/BandEProbe/BandEProbe.csproj"
  if [ -f "$csproj" ]; then
    sed -i.bak '/net10.0-ios;net10.0-maccatalyst/d' "$csproj"
  fi

  say "dotnet build -f net10.0-android (android-only MAUI app)"
  (
    cd "$scratch/BandEProbe" &&
    dotnet build -f net10.0-android 2>&1
  )
  status=$?
  printf '\nexit=%s\n' "$status"
else
  say "dotnet new maui"
  echo "template 'maui' unavailable; see 'dotnet new list maui' above"
  status=1
fi

# The report is worthless if a reader cannot branch on it: a run whose build says
# "Build FAILED." but exits 0 would read as healthy to any script that follows the README.
exit "$status"
