#!/usr/bin/env bash
# Count what the *service* says it ran, not what the orchestrator says it called.
#
#   ./count-calls.sh                 # current counts, one line per agent
#   ./count-calls.sh --json          # the same, as JSON, for the evidence files
#   ./count-calls.sh --baseline FILE # record a starting point
#   ./count-calls.sh --since FILE    # calls made since that starting point
#
# ── Why this is the counter that matters ────────────────────────────────────
#
# An orchestrator can count its own calls and be wrong: a memo hit is a place it
# did *not* call, and a process that died mid-call counted one it may not have
# finished. Neither number is evidence about the platform.
#
# The platform keeps a Response record for every request it accepted, and lists
# them per agent at `GET /agents/{name}/endpoint/protocols/openai/responses`.
# That list is the service's own record: it grows by exactly one per invocation,
# it survives the client process, and nothing the orchestrator does can write to
# it. The count below is `len()` of that list.
#
# The read is bounded by a `created_at` watermark rather than trusted to be
# complete: a response is listed when the platform accepted it, which is before it
# completed, so a count taken while a call is in flight can include that call.
# Evidence files record the watermark with the counts so a reader can tell.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
band_d="$(cd "$here/../band-d-hosted-foundry-agent" && pwd)"

[ -f "$band_d/deployment.env" ] || { echo "count-calls.sh: $band_d/deployment.env is missing." >&2; exit 2; }
# shellcheck disable=SC1091
. "$band_d/deployment.env"
[ -n "${FOUNDRY_BASE_URL:-}" ] || { echo "count-calls.sh: deployment.env has no FOUNDRY_BASE_URL." >&2; exit 2; }

TOKEN="$(az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv)"
[ -n "$TOKEN" ] || { echo "count-calls.sh: az returned an empty token." >&2; exit 2; }

# The agents D3 calls. D1's own hosted agents are listed too, so a reader can see
# that this run did not touch them.
AGENTS="${AGENTS:-d3-coverage-assessor d3-decision-letter}"

#
# A read that fails is a failure, not a zero. Every read is retried, because both the
# token endpoint and the project endpoint occasionally refuse a connection, and a counter
# that answered 0 when it could not read would make "no new calls" indistinguishable from
# "the query broke" — the one confusion this file exists to prevent.
snapshot() {
  local attempt out
  for attempt in 1 2 3 4 5; do
    if out="$(python3 - "$FOUNDRY_BASE_URL" "$TOKEN" $AGENTS <<'PY'
import json, sys, urllib.request

base, token = sys.argv[1], sys.argv[2]
agents = sys.argv[3:]


def page(name, after=None):
    """One page of the agent's response list, with a large page size.

    The list is **paginated** (`has_more`), and its default page is 20 — a count taken
    without paging stops at 20 and then stops growing, which reads as "no new calls" on
    every run after the twentieth. That is the trap this function exists to close.
    """
    url = f"{base}/agents/{name}/endpoint/protocols/openai/responses?api-version=v1&limit=100"
    if after:
        url += f"&after={after}"
    request = urllib.request.Request(url, headers={"Authorization": "Bearer " + token})
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response)


def listing(name):
    rows, after, pages = [], None, 0
    while True:
        data = page(name, after)
        chunk = data.get("data") or []
        rows.extend(chunk)
        pages += 1
        if not data.get("has_more") or not chunk:
            return rows, pages
        after = data.get("last_id") or chunk[-1].get("id")
        if not after:
            raise SystemExit(f"count-calls.sh: {name}: has_more with no cursor; cannot count.")
        if pages > 200:
            raise SystemExit(f"count-calls.sh: {name}: more than 200 pages; refusing to guess a count.")


out = {}
for name in agents:
    try:
        rows, pages = listing(name)
    except urllib.error.HTTPError as error:
        raise SystemExit(f"count-calls.sh: {name}: HTTP {error.code} {error.read()[:400]!r}")
    out[name] = {
        "count": len(rows),
        "pages": pages,
        "latest": max((r.get("created_at") or 0) for r in rows) if rows else None,
        "incomplete": [r.get("id") for r in rows if r.get("status") != "completed"],
    }
print(json.dumps({"agents": out}, indent=1))
PY
)"; then
      printf '%s\n' "$out"
      return 0
    fi
    echo "count-calls.sh: read attempt $attempt failed; retrying in $((attempt * 2))s" >&2
    sleep $((attempt * 2))
  done
  echo "count-calls.sh: could not read the response lists after 5 attempts." >&2
  echo "                Refusing to report a count that was not read." >&2
  return 1
}

case "${1:-show}" in
  --baseline)
    target="${2:?count-calls.sh --baseline needs a path}"
    snapshot > "$target" || { rm -f "$target"; exit 1; }
    echo "baseline written to $target"
    python3 - "$target" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
for name, row in d["agents"].items():
    print(f"  {name:28s} {row['count']:4d}")
PY
    ;;
  --since)
    baseline="${2:?count-calls.sh --since needs a path}"
    now="$here/.count-now.json"
    snapshot > "$now" || { rm -f "$now"; exit 1; }
    python3 - "$baseline" "$now" <<'PY'
import json, sys
before = json.load(open(sys.argv[1]))["agents"]
after = json.load(open(sys.argv[2]))["agents"]
total = 0
for name in sorted(after):
    was = before.get(name, {}).get("count", 0)
    now = after[name]["count"]
    delta = now - was
    total += delta
    print(f"  {name:28s} {was:4d} → {now:4d}  (+{delta})")
    if delta < 0:
        raise SystemExit("count-calls.sh: the count went DOWN; the platform lists are not monotonic here.")
print(f"  {'TOTAL':28s} {sum(v.get('count',0) for v in before.values()):4d} → "
      f"{sum(v['count'] for v in after.values()):4d}  (+{total})")
sys.exit(0)
PY
    rm -f "$now"
    ;;
  --json)
    snapshot
    ;;
  show|"")
    snapshot | python3 -c "
import json, sys
d = json.load(sys.stdin)
for name, row in d['agents'].items():
    print(f\"  {name:28s} {row['count']:4d} responses\" + (f\"  (incomplete: {row['incomplete']})\" if row['incomplete'] else ''))
"
    ;;
  *)
    echo "count-calls.sh: unknown argument '$1' (--json | --baseline FILE | --since FILE)" >&2
    exit 2
    ;;
esac
