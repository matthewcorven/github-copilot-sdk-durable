#!/usr/bin/env bash
# Verifies the app's Android boundary, and the durable layer's freedom from the platform.
#
# Requirement 4 of issue #166 says the Durable layer must never reference Android types and the
# bridge boundary must stay clean. This script makes that checkable: BandEMobile.csproj runs it as
# the `VerifyAndroidBoundary` target before every CoreCompile, so a violation fails the build rather
# than surviving as a comment.
#
#   ./verify-android-boundary.sh            # check; exit 1 on a violation
#   ./verify-android-boundary.sh --list     # also print the files checked
#
# Three rules:
#   1. Exactly one file under app/ may name an Android type: app/Chat/BridgeShim.cs.
#      app/Platforms/ is excluded: those are the MAUI template's Android entry points, not scenario
#      code, and MainActivity has to be an Activity.
#   2. No file under app/Durability/ may name an Android type in code. The durable layer is the thing
#      the issue says must not know Android exists; its own comments are allowed to say so, because a
#      rule nobody can explain next to the code it governs is a rule that gets deleted.
#   3. BandEMobile.csproj may reference exactly one Foundry Local AAR, and it must be the embedded
#      one. Both AARs register a manager factory through a ContentProvider, so an APK with both is
#      ambiguous rather than merely large.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
app="$here/app"
boundary="$app/Chat/BridgeShim.cs"
csproj="$app/BandEMobile.csproj"
show_list=0
[[ "${1:-}" == "--list" ]] && show_list=1

fail=0

if [[ ! -f "$boundary" ]]; then
  echo "verify-android-boundary: $boundary is missing; the boundary must exist for this check to mean anything" >&2
  exit 1
fi

# Strips // and /// comments before matching, so a file that explains the boundary is not treated as
# a file that crosses it. A rule that cannot be explained next to the code it governs would be a rule
# nobody documents.
strip_comments() {
  # Only line comments: this file's own sources use // and ///, and a /* */ block that mentions
  # Android is not a reference either, so a miss here is in the conservative direction (it would
  # report a violation, not hide one).
  sed -e 's,//.*$,,' "$1"
}

check_android_outside_boundary() {
  local file="$1"
  if strip_comments "$file" | grep -qE '(^|[^A-Za-z0-9_.])Android\.|using[[:space:]]+Android[.;[:space:]]'; then
    echo "BOUNDARY VIOLATION: ${file#"$here"/} names an Android type; only ${boundary#"$here"/} may" >&2
    strip_comments "$file" | grep -nE '(^|[^A-Za-z0-9_.])Android\.|using[[:space:]]+Android[.;[:space:]]' >&2 || true
    fail=$((fail + 1))
  fi
}

sources=()
while IFS= read -r file; do
  sources+=("$file")
done < <(find "$app" -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*' -not -path "$app/Platforms/*" | sort)

[[ $show_list -eq 1 ]] && printf 'checked %d source files\n' "${#sources[@]}"

for file in "${sources[@]}"; do
  [[ "$file" == "$boundary" ]] && continue
  check_android_outside_boundary "$file"
done

# Rule 2: the durable layer may not name an Android type in code.
durable="$app/Durability"
if [[ -d "$durable" ]]; then
  while IFS= read -r file; do
    check_android_outside_boundary "$file"
  done < <(find "$durable" -name '*.cs' | sort)
fi

# Rule 3: one AAR, and it is the embedded one.
ipc_refs=$(grep -c 'foundry-local-ipc-sdk' "$csproj" || true)
embedded_refs=$(grep -c 'foundry-local-embedded-sdk' "$csproj" || true)
if [[ "$ipc_refs" != "0" ]]; then
  echo "CSProj REFERENCES THE IPC AAR ($ipc_refs line(s)); E2 packages the embedded AAR only" >&2
  fail=$((fail + 1))
fi
if [[ "$embedded_refs" == "0" ]]; then
  echo "CSProj does not reference foundry-local-embedded-sdk; E2 needs it" >&2
  fail=$((fail + 1))
fi

if [[ $fail -ne 0 ]]; then
  echo "verify-android-boundary: $fail violation(s)" >&2
  exit 1
fi

echo "verify-android-boundary: OK — ${#sources[@]} sources scanned, one boundary file" \
     "($(basename "$boundary")), durable layer Android-free, embedded AAR the only one referenced"
