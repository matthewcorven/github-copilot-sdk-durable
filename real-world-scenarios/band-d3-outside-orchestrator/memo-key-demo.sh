#!/usr/bin/env bash
# Requirement 3: a changed agent definition is a new step, an unchanged one is reused.
#
#   ./memo-key-demo.sh
#
# The claim is a property of the key, so the demonstration varies exactly one thing —
# the store — and holds everything else fixed: the same request, the same orchestrator,
# the same project, and the same *current* agent definitions on the platform.
#
#   A  empty store, definition v1          → 1 call   (a miss: nothing to reuse)
#   B  A's store, definition still v1      → 0 calls  (a hit: the memo is reused)
#   C  edit + redeploy, fresh store        → 1 call   (a miss: a new step)
#   D  edit + redeploy, A's store, NAIVE key → 0 calls (WRONG: reuses the old result)
#   E  D's store, identity key             → 1 call   (the key, corrected)
#
# B is the half that makes reuse real rather than a cache that never fills. C is the half
# that makes the edit matter. D and E are the control pair: **the same store and the same
# edited agent**, differing only in whether the key carries the agent's identity. D
# reports the assessment the *first* definition produced as though the second had produced
# it; E cannot, because the key it looks for names an identity that memo does not carry.
#
# Every count is the platform's own response list (`count-calls.sh`), never the
# orchestrator's log. Each scenario's log and store report is under evidence/memo-key/.

set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
worktree="$(cd "$here/../.." && pwd)"
out="$here/evidence/memo-key"
scratch="$worktree/.scratch/band-d3-memo-key"
yaml="$here/agent/coverage-agent.yaml"
backup="$scratch/coverage-agent.yaml.orig"
rm -rf "$scratch" "$out"
mkdir -p "$scratch" "$out"
cp "$yaml" "$backup"
# The edit is part of the demonstration, not a lasting change: restore the file however
# this script ends, including a failure between the edit and the restore below.
trap 'cp "$backup" "$yaml" 2>/dev/null || true' EXIT

if [ -n "${DOTNET:-}" ]; then dotnet="$DOTNET"
elif [ -x /opt/homebrew/bin/dotnet ]; then dotnet=/opt/homebrew/bin/dotnet
else dotnet="$(command -v dotnet || true)"; fi
[ -n "$dotnet" ] || { echo "memo-key-demo.sh: no dotnet" >&2; exit 2; }

request="$(cat "$here/evidence/request.txt")"
base_url="$(python3 -c "
for line in open('$here/../band-d-hosted-foundry-agent/deployment.env'):
    if line.startswith('FOUNDRY_BASE_URL='):
        print(line.split('=', 1)[1].strip()); break
")"
[ -n "$base_url" ] || { echo "memo-key-demo.sh: no FOUNDRY_BASE_URL in D1's deployment.env" >&2; exit 2; }
export FOUNDRY_BASE_URL="$base_url"
export FOUNDRY_TOKEN
FOUNDRY_TOKEN="$(az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv)"
[ -n "$FOUNDRY_TOKEN" ] || { echo "memo-key-demo.sh: az returned an empty token" >&2; exit 2; }

# ── the two things measured ─────────────────────────────────────────────────

calls() {
  "$here/count-calls.sh" --json | python3 -c "
import json, sys
print(json.load(sys.stdin)['agents']['d3-coverage-assessor']['count'])
"
}

# The identity the *platform* reports for the coverage agent, canonicalised the same way
# the orchestrator does it, so the two agree by construction rather than by convention.
identity() {
  local attempt out
  for attempt in 1 2 3 4; do
    if out="$(python3 - "$base_url" "$FOUNDRY_TOKEN" <<'PY'
import hashlib, json, sys, urllib.request
base, token = sys.argv[1:3]

def canonical(value):
    if isinstance(value, dict):
        return "{" + ",".join(json.dumps(k) + ":" + canonical(value[k]) for k in sorted(value)) + "}"
    if isinstance(value, list):
        return "[" + ",".join(canonical(v) for v in value) + "]"
    return json.dumps(value, separators=(",", ":"))

request = urllib.request.Request(
    f"{base}/agents/d3-coverage-assessor/versions?api-version=v1",
    headers={"Authorization": "Bearer " + token})
with urllib.request.urlopen(request, timeout=60) as response:
    versions = [v for v in json.load(response)["data"] if v.get("status") == "active"]
# Numerically, and never by the order the platform returned them in: it lists v2 before v1.
versions.sort(key=lambda v: int(v["version"]))
latest = versions[-1]
digest = hashlib.sha256(canonical(latest["definition"]).encode()).hexdigest()
print(f"d3-coverage-assessor@{latest['version']}:{digest[:16]}")
PY
)"; then
      [ -n "$out" ] && { printf '%s\n' "$out"; return 0; }
    fi
    echo "memo-key-demo.sh: identity read attempt $attempt failed; retrying in $((attempt * 3))s" >&2
    sleep $((attempt * 3))
  done
  echo "memo-key-demo.sh: could not read the agent identity." >&2
  return 1
}

# ── one scenario: `$1` label, `$2` memo-key mode, `$3` a store to copy ("" fresh) ──
scenario() {
  local label="$1" mode="$2" source="${3:-}"
  local dir="$scratch/$label"
  mkdir -p "$dir"
  [ -n "$source" ] && cp -R "$source/." "$dir/"

  # Read from the platform, never from a cached count: a count taken before the deploy
  # would already be stale by the time the run starts.
  local before after
  before="$(calls 2>"$out/$label.count-error")"
  case "$before" in ''|*[!0-9]*)
    echo "memo-key-demo.sh: could not read the count before $label:" >&2
    sed 's/^/  /' "$out/$label.count-error" >&2
    return 1 ;;
  esac

  (
    export RUN_DIR="$dir"
    export RUN_REQUEST="$request"
    export MEMO_KEY_MODE="$mode"
    export REVIEWER="memo-key demo"
    # A token minted once for the whole demonstration outlives its usefulness: the
    # runs in between take minutes, and a 401 part-way would look like a memo-key
    # result. Mint one per scenario so the credential is never the variable.
    export FOUNDRY_TOKEN
    FOUNDRY_TOKEN="$(az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv)"
    export RUN_ID="memo-key-$label"
    # Every scenario leaves the phase unadvanced, so every store is one a killed process
    # could have left and every later process re-enters `coverage`. A store that had already
    # moved on would make "no call" mean "nothing left to do", which proves nothing.
    export STOP_AFTER_COVERAGE=stuck
    "$dotnet" run "$here/orchestrator.cs"
  ) > "$out/$label.log" 2>&1

  # The platform lists a response shortly after it completes, and the list read is
  # eventually consistent: a count taken immediately after the process exits can be one
  # short. So the "after" count is retried until it is at least the "before" count plus
  # what the log says was called — which makes a missing call visible as a failure rather
  # than as a legitimate zero.
  local expected=0
  grep -q "memo hit" "$out/$label.log" || expected=1
  local reentered=0
  grep -q "resuming durable task" "$out/$label.log" && reentered=1
  after="$before"
  local attempt
  for attempt in 1 2 3 4 5 6; do
    after="$(calls)"
    case "$after" in ''|*[!0-9]*) after="$before" ;; esac
    [ $((after - before)) -ge "$expected" ] && break
    sleep 5
  done
  local delta=$((after - before))
  [ "$delta" -ge 0 ] || { echo "memo-key-demo.sh: the count went down in $label" >&2; return 1; }

  "$here/store-report.py" "$dir/store/orchestrator.jsonl" > "$out/$label.store.json" 2>/dev/null || true
  echo "$delta" > "$out/$label.delta"

  # The key and the agent that produced it come from what this scenario's orchestrator
  # *said it used* — its own line — not from "the first coverage memo in the store". A
  # store that was copied in can hold an older memo beside the one this run settled, and
  # reporting that one would describe the previous scenario's work as this one's.
  local key agent
  # Two greps rather than one alternation: macOS sed is BSD and does not support `\|`.
  key="$( { grep -o '^\[coverage\] memo hit [^ ]*' "$out/$label.log"
            grep -o '^\[coverage\] memo miss [^ ]*' "$out/$label.log"; } | head -1 | sed 's/^.* //')"
  # Which agent produced the result: the *reused* one on a hit (the memo was written by an
  # earlier run), and the one just resolved on a miss.
  agent="$(grep -o '^\[coverage\] reusing .*' "$out/$label.log" | head -1 | sed 's/^.* //')"
  [ -n "$agent" ] || agent="$(grep -o '^\[coverage\] identity .*' "$out/$label.log" | head -1 | sed 's/^.* //')"
  [ -n "$key" ] || key="(no coverage step ran)"
  [ -n "$agent" ] || agent="(none)"

  printf '%-14s %-8s calls +%d%s\n' "$label" "$mode" "$delta" \
    "$([ "$reentered" = 1 ] && echo "  (re-entered coverage on a store that had not advanced)" || echo "  (new run)")"
  printf '  key      %s\n' "$key"
  printf '  produced %s\n' "$agent"
  echo "$key" > "$out/$label.key"
  echo "$agent" > "$out/$label.agent"
}

# ── A and B: reuse, with nothing changed ────────────────────────────────────

# The demonstration does not assume the platform is in a particular state: it deploys
# the *current* file first and calls that v1. Whatever version the project was left at,
# the run below starts from a definition that matches the file, so the later edit is the
# only change and the identity it produces is comparable.
if grep -q "Additionally:" "$yaml"; then
  echo "memo-key-demo.sh: $yaml still carries the demonstration edit; restore it first." >&2
  exit 2
fi
"$here/deploy-agents.sh" > "$out/10-deploy-v1.log" 2>&1 || {
  echo "memo-key-demo.sh: baseline deploy failed; see $out/10-deploy-v1.log" >&2
  exit 1
}

echo "== A: empty store, definition v1 =="
id_v1="$(identity)"
echo "  identity $id_v1"
scenario A-miss-v1 identity "" || exit 1

echo
echo "== B: A's store, definition still v1 =="
id_same="$(identity)"
[ "$id_same" = "$id_v1" ] || { echo "  identity changed with no edit ($id_same); B would not test reuse." >&2; exit 1; }
echo "  identity $id_same (unchanged)"
scenario B-hit-v1 identity "$scratch/A-miss-v1" || exit 1

echo
echo "== A2: a store written under the NAIVE key, still v1 (the control's input) =="
# The control needs a memo under the key it will look for *and* written by the definition
# that ran before the edit. It cannot borrow A's: A's memo is under the identity key, which
# the naive key cannot match, and producing it here — before the edit — is what makes D's
# reuse a stale one rather than a legitimate hit.
scenario A2-naive-v1 naive "" || exit 1

# ── the edit ────────────────────────────────────────────────────────────────

echo
echo "== edit the agent definition, then re-deploy =="
python3 - "$yaml" <<'PY'
import sys
path = sys.argv[1]
text = open(path).read()
marker = "    End with a single line of the form"
addition = (
    "    Additionally: when the ordering provider's specialty is absent from the\n"
    "    request, say so explicitly in the first criterion's Missing line and name the\n"
    "    specialty types that would satisfy it.\n\n"
)
if addition not in text:
    open(path, "w").write(text.replace(marker, addition + marker, 1))
    print("  appended a paragraph to the coverage agent's instructions")
PY
"$here/deploy-agents.sh" > "$out/20-deploy-v2.log" 2>&1 || {
  echo "  re-deploy failed; see $out/20-deploy-v2.log" >&2
  cp "$backup" "$yaml"
  exit 1
}
id_v2="$(identity)"
echo "  identity $id_v2"
[ "$id_v2" != "$id_v1" ] || { echo "  the identity did not change; the edit did not reach the platform." >&2; exit 1; }
grep -E 'version [0-9]+' "$out/20-deploy-v2.log" | sed 's/^/  /'

# ── C, D, E ─────────────────────────────────────────────────────────────────

echo
echo "== C: fresh store under the edited (v2) definition =="
scenario C-miss-v2 identity "" || exit 1

echo
echo "== D: CONTROL — A2's store (written by v1), v2 live, NAIVE key =="
scenario D-stale-naive naive "$scratch/A2-naive-v1" || exit 1

echo
echo "== E: a copy of D's store, identity key =="
# A copy, so D's record in evidence/memo-key/D-stale-naive.store.json is the one D left
# and E cannot overwrite the memo this comparison is about.
scenario E-fixed identity "$scratch/D-stale-naive" || exit 1

echo
echo "  (agent/coverage-agent.yaml is restored on exit; the platform still runs v2)"

# ── verdict ─────────────────────────────────────────────────────────────────

echo
echo "== what the platform's own counts say =="
status=0
check() {
  if [ "$2" = "$3" ]; then echo "  PASS  $1"; else echo "  FAIL  $1 (expected $2, got $3)" >&2; status=1; fi
}

a="$(cat "$out/A-miss-v1.delta")"; b="$(cat "$out/B-hit-v1.delta")"
c="$(cat "$out/C-miss-v2.delta")"; d="$(cat "$out/D-stale-naive.delta")"
e="$(cat "$out/E-fixed.delta")"

check "A empty store, definition v1: one call" 1 "$a"
check "B same store, definition unchanged: no call — the memo is reused" 0 "$b"
check "C definition changed: one call — a new step" 1 "$c"
check "D control, naive key: no call — it reused a result v2 never produced" 0 "$d"
check "E D's store, identity key: one call — the stale memo is invisible" 1 "$e"

echo
echo "  D's store reports the coverage step as produced by $(cat "$out/D-stale-naive.agent")"
echo "  while the platform was running $id_v2 — that is the bug the key prevents."
echo "  E, on a copy of that store, could only produce $(cat "$out/E-fixed.agent")."
echo
if [ "$status" = 0 ]; then
  echo "  the agent identity in the key is what makes B a hit, C a miss, and D's reuse impossible."
else
  echo "  the demonstration did not hold; see the logs under $out." >&2
fi
exit "$status"
