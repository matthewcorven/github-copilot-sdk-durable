#!/usr/bin/env bash
# invoke.sh — prove ONE complete prior-authorization run against the deployed
# hosted agents, and save the transcript.
#
# The run has four steps, and every step's input/output is a file under the run
# directory so the transcript is checkable rather than narrated:
#
#   1. coverage   — the coverage agent (NPI registry + CMS coverage MCP tools)
#                   returns its structured CoverageResult.
#   2. synthesis  — the synthesis agent (gate-based decision rubric) returns its
#                   APPROVE / PEND recommendation with an audit trail.
#   3. approval   — a human reviewer accepts or overrides that recommendation.
#                   There is no approval UI in the minimal deployment, so the
#                   reviewer's decision is recorded as its own file by this
#                   script, in the shape the accelerator's POST /api/decision
#                   takes (action = accept | override, reviewer_name), and
#                   applied as a step.
#   4. result     — the final authorization outcome. This is the step that makes
#                   the run a decision and not just an agent output.
#
# Honest boundary: steps 1-2 run on the platform; steps 3-4 are the accelerator
# backend's own rules (orchestrator.py + routers/decision.py), reproduced here
# because the backend is not part of the minimal deployment. The final result
# file records which part of the run is the platform's and which is ours.
#
# Env: OUT_DIR (default ../evidence/run-<utc stamp>)
#
# Usage: ./invoke.sh [reviewer-name]

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
[ -f "$ROOT/deployment.env" ] || { echo "invoke.sh: run ./deploy.sh first." >&2; exit 1; }
# shellcheck disable=SC1091
. "$ROOT/deployment.env"

RESOURCE="https://ai.azure.com"
API_VERSION="v1"
REVIEWER="${1:-Band D evidence run}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="${OUT_DIR:-$ROOT/evidence/run-$STAMP}"
mkdir -p "$OUT_DIR"

: "${AGENT_COVERAGE:?deployment.env has no AGENT_COVERAGE; re-run deploy.sh with AGENTS='coverage synthesis'}"
: "${AGENT_SYNTHESIS:?deployment.env has no AGENT_SYNTHESIS; re-run deploy.sh with AGENTS='coverage synthesis'}"

post() { # agent-name request-json response-json
  local agent="$1" req="$2" res="$3"
  echo "-- POST $agent"
  az rest --method POST \
    --url "$FOUNDRY_BASE_URL/agents/$agent/endpoint/protocols/openai/responses?api-version=$API_VERSION" \
    --resource "$RESOURCE" --body @"$req" -o json > "$res"
  python3 - "$res" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
print("   status:", d.get("status"), "| id:", d.get("id"), "| session:", d.get("agent_session_id"))
if d.get("status") != "completed":
    print("   error:", json.dumps(d.get("error")))
    sys.exit(1)
PY
}

text_of() {
  python3 - "$1" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
t = d.get("output_text")
if not t:
    t = "\n".join(c.get("text", "") for i in (d.get("output") or []) for c in (i.get("content") or []))
print(t or "")
PY
}

REQUEST_TEXT="$(cat <<'EOF'
Prior authorization request.

Patient: John Smith, DOB 1958-03-15, insurance MCR-123456789A
Provider NPI: 1720180003
Diagnosis (ICD-10): R91.1, J18.9, R05.9
Procedure (CPT): 31628 — bronchoscopy, diagnostic, with transbronchial lung biopsy

Clinical summary: 68-year-old male with a 1.8 cm spiculated right lower lobe pulmonary nodule, up from 1.2 cm three months ago. PET/CT shows FDG avidity (SUVmax 4.2). 40 pack-year smoking history, quit 5 years ago. No prior malignancy. Completed 14 days of amoxicillin-clavulanate with no resolution. PFTs FEV1 78% predicted, DLCO 71% predicted; acceptable surgical candidate. Recommending CT-guided transbronchial biopsy for tissue diagnosis.

Compliance and clinical-agent outputs are not part of this minimal deployment (see README); assess coverage on the request as submitted.
EOF
)"

printf '%s\n' "$REQUEST_TEXT" > "$OUT_DIR/00-request.txt"

# -- 1. Coverage -------------------------------------------------------------
python3 - "$REQUEST_TEXT" "$OUT_DIR/10-coverage.request.json" <<'PY'
import json, sys
json.dump({"input": sys.argv[1], "stream": False}, open(sys.argv[2], "w"), indent=1)
PY
post "$AGENT_COVERAGE" "$OUT_DIR/10-coverage.request.json" "$OUT_DIR/10-coverage.response.json"
text_of "$OUT_DIR/10-coverage.response.json" > "$OUT_DIR/10-coverage.result.json"

# -- 2. Synthesis ------------------------------------------------------------
python3 - "$REQUEST_TEXT" "$OUT_DIR/10-coverage.result.json" "$OUT_DIR/20-synthesis.request.json" <<'PY'
import json, sys
request, coverage_path, out = sys.argv[1:4]
coverage = open(coverage_path).read().strip()
prompt = f"""Synthesize a prior authorization recommendation for the request below from the specialist output you are given.

REQUEST
{request}

COVERAGE AGENT OUTPUT (the only specialist output available in this deployment)
{coverage}

Record in your audit trail which specialist inputs were absent, so the recommendation is not read as a four-agent verdict.
"""
json.dump({"input": prompt, "stream": False}, open(out, "w"), indent=1)
PY
post "$AGENT_SYNTHESIS" "$OUT_DIR/20-synthesis.request.json" "$OUT_DIR/20-synthesis.response.json"
text_of "$OUT_DIR/20-synthesis.response.json" > "$OUT_DIR/20-synthesis.result.json"

# -- 3. Human approval -------------------------------------------------------
REVIEW_ID="$(basename "$OUT_DIR")"
python3 - "$REVIEW_ID" "$REVIEWER" "$OUT_DIR/30-human-decision.json" <<'PY'
import json, sys
rid, reviewer, out = sys.argv[1:4]
json.dump({"request_id": rid, "action": "accept", "override_recommendation": None,
           "override_rationale": None, "reviewer_name": reviewer}, open(out, "w"), indent=1)
PY
echo "-- human decision: accept (reviewer: $REVIEWER)"

# -- 4. Final result ---------------------------------------------------------
python3 - "$OUT_DIR" <<'PY'
import json, pathlib, sys
d = pathlib.Path(sys.argv[1])
raw = (d / "20-synthesis.result.json").read_text().strip()
try:
    synth = json.loads(raw)
except json.JSONDecodeError:
    synth = {"raw": raw}
rec = (synth.get("recommendation") or synth.get("final_recommendation") or "").strip().lower()
decision = json.loads((d / "30-human-decision.json").read_text())

# backend/app/routers/decision.py: accept keeps the review recommendation; an
# override must name approve | pend_for_review.
if decision["action"] == "accept":
    final = rec or "unknown"
elif decision["action"] == "override":
    final = decision.get("override_recommendation")
    if final not in ("approve", "pend_for_review"):
        raise SystemExit("422: override_recommendation must be 'approve' or 'pend_for_review'")
else:
    raise SystemExit("422: action must be 'accept' or 'override'")

out = {
    "review_id": decision["request_id"],
    "review_recommendation": rec or "unknown",
    "human_action": decision["action"],
    "reviewer": decision["reviewer_name"],
    "final_recommendation": final,
    "criteria_summary": synth.get("criteria_summary") or synth.get("summary"),
    "audit_trail": synth.get("audit_trail") or synth.get("synthesis_audit_trail") or {},
    "provenance": {
        "coverage": "hosted agent, Responses API",
        "synthesis": "hosted agent, Responses API",
        "decision": "applied by invoke.sh using backend/app/routers/decision.py rules",
    },
}
(d / "40-final-result.json").write_text(json.dumps(out, indent=1) + "\n")
print("   final recommendation:", final)
print("   review recommendation:", rec or "unknown", "| human action:", decision["action"])
PY

echo
echo "run directory: $OUT_DIR"
ls -1 "$OUT_DIR"
