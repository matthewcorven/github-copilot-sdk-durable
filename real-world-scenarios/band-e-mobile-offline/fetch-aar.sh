#!/usr/bin/env bash
# Downloads the pinned Foundry Local for Android AAR and verifies its SHA-256.
#
# The AAR is not committed to this repository: its license forbids redistribution (see
# DECISION-bridge.md §5). This script is the substitute, so a clone can obtain a binary it
# can prove came from Microsoft's release pipeline unmodified.
#
#   ./fetch-aar.sh                 # ipc (the mode the decision record selects)
#   ./fetch-aar.sh embedded        # the other mode, for the packaging comparison
#   ./fetch-aar.sh both            # both, for measuring the size difference
#   ./fetch-aar.sh ipc --verify-only   # verify what is already in libs/, download nothing
#
# Exits non-zero on a hash mismatch and deletes the file it cannot vouch for.
set -euo pipefail

VERSION=0.1.6
RELEASE_REPO=microsoft/FoundryLocalAndroid
RELEASE_TAG="v${VERSION}"

readonly IPC_SHA=7dc74260d2dbd16c2c3c02bcd9de1d852bb89366f71a63ea9813fe764c2665c1
readonly EMBEDDED_SHA=6a8ba85433fca1f48535986f529702fb43ad8d83bcd5df30f30a6c7969474e87

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
libs="$here/libs"

usage() {
  cat <<'USAGE'
Downloads the pinned Foundry Local for Android AAR and verifies its SHA-256.

The AAR is not committed to this repository: its license forbids redistribution (see
DECISION-bridge.md §5). This script is the substitute, so a clone can obtain a binary it
can prove came from Microsoft's release pipeline unmodified.

  ./fetch-aar.sh                     # ipc (the mode the decision record selects)
  ./fetch-aar.sh embedded            # the other mode, for the packaging comparison
  ./fetch-aar.sh both                # both, for measuring the size difference
  ./fetch-aar.sh ipc --verify-only   # verify what is already in libs/, download nothing

Exits non-zero on a hash mismatch and deletes the file it cannot vouch for.
USAGE
}

sha256_of() {
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | cut -d' ' -f1
  elif command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    echo "fetch-aar.sh: no shasum or sha256sum on PATH" >&2
    return 1
  fi
}

expected_sha() {
  case "$1" in
    ipc) echo "$IPC_SHA" ;;
    embedded) echo "$EMBEDDED_SHA" ;;
    *) echo "fetch-aar.sh: unknown mode '$1' (expected ipc or embedded)" >&2; return 1 ;;
  esac
}

verify() {
  local mode="$1" file="$libs/foundry-local-${mode}-sdk-${VERSION}.aar"
  local want actual
  want="$(expected_sha "$mode")"

  if [[ ! -f "$file" ]]; then
    echo "MISSING  $file" >&2
    return 1
  fi

  actual="$(sha256_of "$file")"
  if [[ "$actual" != "$want" ]]; then
    echo "MISMATCH $file" >&2
    echo "         expected $want" >&2
    echo "         actual   $actual" >&2
    return 1
  fi

  echo "OK       $(basename "$file")  sha256=$actual"
}

download() {
  local mode="$1"
  local name="foundry-local-${mode}-sdk-${VERSION}.aar"
  local url="https://github.com/${RELEASE_REPO}/releases/download/${RELEASE_TAG}/${name}"
  local file="$libs/$name"

  if [[ -f "$file" ]] && verify "$mode" >/dev/null 2>&1; then
    verify "$mode"
    return 0
  fi

  mkdir -p "$libs"
  echo "GET      $url"
  if ! curl -fSL --retry 3 -o "$file.part" "$url"; then
    rm -f "$file.part"
    echo "fetch-aar.sh: download failed; check network and that release ${RELEASE_TAG} exists" >&2
    return 1
  fi
  mv "$file.part" "$file"

  if ! verify "$mode"; then
    rm -f "$file"
    echo "fetch-aar.sh: deleted $name because its hash did not verify" >&2
    return 1
  fi
}

modes=()
verify_only=0
for arg in "$@"; do
  case "$arg" in
    --verify-only) verify_only=1 ;;
    -h|--help) usage; exit 0 ;;
    ipc|embedded|both) modes+=("$arg") ;;
    *) echo "fetch-aar.sh: unknown argument '$arg'" >&2; usage >&2; exit 2 ;;
  esac
done
[[ ${#modes[@]} -eq 0 ]] && modes+=(ipc)

expanded=()
for mode in "${modes[@]}"; do
  if [[ "$mode" == both ]]; then expanded+=(ipc embedded); else expanded+=("$mode"); fi
done
modes=("${expanded[@]}")

if [[ $verify_only -eq 1 ]]; then
  failed=0
  for mode in "${modes[@]}"; do verify "$mode" || failed=1; done
  exit "$failed"
fi

for mode in "${modes[@]}"; do download "$mode"; done
