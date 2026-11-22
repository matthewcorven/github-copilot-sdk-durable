#!/usr/bin/env bash
# invoke.sh - prove the approval survives a container restart, against the deployment.
#
# The run has five stages, and every response, session object and store capture is
# saved under the run directory so each claim is checkable rather than narrated:
#
#   1. ask        a turn on the agent. It reads the working state through its own tool
#                 (which opens the store in the container) and is asked what it contains.
#                 agent_session_id S is recorded here.
#   2. assess     a turn that commits an assessment and opens the approval gate. The
#                 approval is a task parked on an external event, with a durable deadline.
#   3. stop       POST .../sessions/S:stop. The platform tears down the container.
#   4. resume     a turn on the SAME S, with a request id that is new to this process, so
#                 the only way it can name the parked approval is by reading the store.
#                 The answer quotes the deadline written before the stop and the
#                 approval task id, and says which task status it found.
#   5. decide     a turn that records a human decision. The event wakes the task and it
#                 resumes at the approval step; the transcript afterwards shows the
#                 decision, and the store shows the task terminal.
#
# The control stage (--control) sends stage 1's question again and shows the container
# starting from an empty store, so "the state was there" is distinguished from "a fresh
# container answers plausibly".
#
# Usage: ./invoke.sh [--control]
# Env:   OUT_DIR (default ../evidence/run-<utc stamp>), TURN_TIMEOUT (default 240)

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
[ -f "$ROOT/deployment.env" ] || { echo "invoke.sh: run ./deploy.sh first." >&2; exit 1; }
# shellcheck disable=SC1091
. "$ROOT/deployment.env"

RESOURCE="https://ai.azure.com"
API_VERSION="v1"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT_DIR="${OUT_DIR:-$ROOT/evidence/run-$STAMP}"
REQUEST_ID="PA-D2-0001"
TURN_TIMEOUT="${TURN_TIMEOUT:-240}"
CONTROL=0
[ "${1:-}" = "--control" ] && CONTROL=1

mkdir -p "$OUT_DIR"

# `az rest` is the path D1 proved works in this preview and needs no extra extension.
post_agent() { # label request-json response-json
  local label="$1" req="$2" res="$3"
  echo "-- POST $label"
  local started elapsed
  started="$(date -u +%s)"
  set +e
  az rest --method POST \
    --url "$FOUNDRY_BASE_URL/agents/$AGENT/endpoint/protocols/openai/responses?api-version=$API_VERSION" \
    --resource "$RESOURCE" --body @"$req" -o json > "$res"
  local status=$?
  set -e
  elapsed=$(( $(date -u +%s) - started ))
  echo "   ${elapsed}s, exit $status"
  [ $status -eq 0 ] || { echo "invoke.sh: $label failed; see $res" >&2; return 1; }
  python3 - "$res" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
print("   status:", d.get("status"), "| id:", d.get("id"), "| session:", d.get("agent_session_id"))
PY
}

session_get() { # label session-id out-file
  az rest --method GET \
    --url "$FOUNDRY_BASE_URL/agents/$AGENT/endpoint/sessions/$2?api-version=$API_VERSION" \
    "--resource" "$RESOURCE" -o json > "$3" 2>/dev/null || echo '{}' > "$3"
  python3 - "$1" "$3" <<'PY'
import json, sys
label, path = sys.argv[1:3]
d = json.load(open(path))
print(f"   {label}: status={d.get('status')} created={d.get('created_at')} last_accessed={d.get('last_accessed_at')}")
PY
}

session_stop() { # session-id out-file
  # Captured with its status: a stop that failed and a stop that returned an empty body
  # must not look the same. A non-zero exit is reported and the file records it.
  local code
  set +e
  az rest --method POST \
    --url "$FOUNDRY_BASE_URL/agents/$AGENT/endpoint/sessions/$1:stop?api-version=$API_VERSION" \
    --resource "$RESOURCE" -o json > "$2" 2> "$2.err"
  code=$?
  set -e
  echo "   stop exit: $code"
  # The stop returns 202 with an empty body - the action was accepted, and the state
  # machine is what proves it. The session object is captured immediately afterwards by
  # `session_get`, and `status: idle` with a `stopped_at` is the confirmation.
  if [ -s "$2" ]; then cat "$2"; else echo "   (empty body: the action was accepted; the session object below is the proof)"; fi
  if [ -s "$2.err" ]; then echo "   stderr:"; sed 's/^/     /' "$2.err"; fi
}

# The response's own text, then the tool outputs verbatim. The tool outputs are what the
# claims rest on: a model that summarises is a witness, not the evidence, and the first
# run of this scenario failed because a resumed turn's assistant message disagreed with
# the store its own tool calls had just reported. Both are saved, in call order.
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

tools_of() {
  python3 - "$1" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
print("-- tool calls and their outputs, in order --")
for item in d.get("output") or []:
    kind = item.get("type")
    if kind == "function_call":
        print("CALL", item.get("name"), item.get("arguments"))
    elif kind == "function_call_output":
        print("OUTPUT", item.get("name") or item.get("call_id"))
        print(item.get("output"))
PY
}

events_of() { # response-json out-json
  python3 - "$1" "$2" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
json.dump({"id": d.get("id"), "status": d.get("status"), "agent_session_id": d.get("agent_session_id"),
           "output": d.get("output"), "usage": d.get("usage")},
          open(sys.argv[2], "w"), indent=1)
PY
}

# Fails the run when a turn did not land on the session the run is about. A capture that
# says "resumed" over a fresh session is the failure mode this band exists to catch, so the
# check is in the script and not in a reader's judgement.
assert_same_session() { # label response-json
  python3 - "$1" "$2" "$SESSION" <<'PY'
import json, sys
label, path, expected = sys.argv[1:4]
actual = json.load(open(path)).get("agent_session_id") or ""
if actual != expected:
    raise SystemExit(f"{label}: expected session {expected}, the response reports {actual or 'none'}")
print("   session confirmed:", actual)
PY
}

# The agent version every turn ran on. A version change mid-run would mean the two halves
# of the restart were different images, which would make "the same agent resumed" a weaker
# claim than it looks - the first run of this scenario did change version mid-run (a deploy
# raced the run), and this check is what pins that down.
VERSION=""
assert_same_version() { # label response-json
  python3 - "$1" "$2" "${VERSION:-}" <<'PY'
import json, sys
label, path, expected = sys.argv[1:4]
actual = str((json.load(open(path)).get("agent_reference") or {}).get("version") or "")
if not expected:
    raise SystemExit(f"{label}: no expected agent version; the guard cannot verify a restart without one")
if actual != expected:
    raise SystemExit(
        f"{label}: every turn in this run must use one agent version; this one ran {actual}, "
        f"the run started on {expected}")
print("   agent version:", actual or "none")
PY
}

session_id_of() {
  python3 -c "import json,sys;print(json.load(open('$1')).get('agent_session_id') or '')"
}

printf '%s\n' "$REQUEST_ID" > "$OUT_DIR/00-request-id.txt"

# -- 1. ask -------------------------------------------------------------------
python3 - "$REQUEST_ID" "$OUT_DIR/10-ask.request.json" <<'PY'
import json, sys
rid, out = sys.argv[1:3]
json.dump({"input": (
    "Before anything else, read your durable working state for request "
    f"{rid} with the working_state tool and tell me, as JSON, exactly what it contains: "
    "phase, approval, approvalTask, and storePath. Then call store_report and tell me how "
    "many documents and tasks the store holds. Do not invent state: if the store is empty, "
    "say that it is empty and what the store path is."), "stream": False}, open(out, "w"), indent=1)
PY
post_agent "ask" "$OUT_DIR/10-ask.request.json" "$OUT_DIR/10-ask.response.json"
text_of "$OUT_DIR/10-ask.response.json" > "$OUT_DIR/10-ask.result.txt"
tools_of "$OUT_DIR/10-ask.response.json" > "$OUT_DIR/10-ask.tools.txt"
SESSION="$(session_id_of "$OUT_DIR/10-ask.response.json")"
printf '%s\n' "$SESSION" > "$OUT_DIR/10-session-id.txt"
echo "   session id: $SESSION"
# The version every turn must share. Captured here, asserted on every following turn: a
# run that straddles two versions is two images pretending to be one restart.
VERSION="$(python3 -c "import json;d=json.load(open('$OUT_DIR/10-ask.response.json'));print((d.get('agent_reference') or {}).get('version') or '')")"
[ -n "$VERSION" ] || { echo "invoke.sh: the first response reports no agent version; a run that cannot pin the version cannot claim a restart." >&2; exit 1; }
printf '%s\n' "$VERSION" > "$OUT_DIR/10-agent-version.txt"
assert_same_version "ask" "$OUT_DIR/10-ask.response.json"
# No `session-after-ask` capture. `last_accessed_at` is the platform's last *access*, and a
# turn does not appear to move it until the next one is served: a GET taken right after
# `ask` and one taken right after `assess` came back byte-identical in the first version of
# this script, which cannot be a live read and would make the capture a forgery. The
# session objects that can be trusted here are the ones taken around the stop and the
# resume, where the state change is the platform's own (`31` and `41`).

if [ "$CONTROL" = "1" ]; then
  # The control: the same question with no agent_session_id, so the platform starts a
  # fresh container. What it prints is what an empty store looks like - and the store
  # path it names is the same path a resumed session names.
  SESSION=""
  echo "-- control: fresh session, same question"
  out="$OUT_DIR/90-control.response.json"
  post_agent "control" "$OUT_DIR/10-ask.request.json" "$out"
  text_of "$out" > "$OUT_DIR/90-control.result.txt"
  tools_of "$out" > "$OUT_DIR/90-control.tools.txt"
  echo "run directory: $OUT_DIR"
  exit 0
fi

# -- 2. assess ----------------------------------------------------------------
python3 - "$REQUEST_ID" "$OUT_DIR/20-assess.request.json" <<'PY'
import json, sys
rid, out = sys.argv[1:3]
json.dump({"input": (
    f"Request {rid}: provider NPI 1720180003, CPT 31628 with ICD-10 R91.1, 14 days of "
    "antibiotics with no resolution, PET/CT FDG-avid 1.8 cm nodule. Coverage criteria are "
    "met. Use record_assessment to commit this assessment and open the human approval gate, "
    "then read working_state and report the approval task id, the task status, the event it "
    "is waiting on and the deadline - as the tool returned them."), "stream": False}, open(out, "w"), indent=1)
PY
# Stage 2 runs on the SAME session as stage 1. Without this the platform starts a new
# sandbox, whose store is a different file: the first run of this scenario made exactly
# that mistake and the evidence it produced was a fresh session wearing a resume's
# clothes. The session id is asserted, not assumed, below.
python3 - "$OUT_DIR/20-assess.request.json" "$SESSION" <<'PY'
import json, sys
path, session = sys.argv[1:3]
body = json.load(open(path))
body["agent_session_id"] = session
json.dump(body, open(path, "w"), indent=1)
PY
post_agent "assess" "$OUT_DIR/20-assess.request.json" "$OUT_DIR/20-assess.response.json"
assert_same_session "assess" "$OUT_DIR/20-assess.response.json"
assert_same_version "assess" "$OUT_DIR/20-assess.response.json"
text_of "$OUT_DIR/20-assess.response.json" > "$OUT_DIR/20-assess.result.txt"
tools_of "$OUT_DIR/20-assess.response.json" > "$OUT_DIR/20-assess.tools.txt"
# No session object here, and none after `ask`: see the note on the `ask` stage. The two
# that are kept (`31`, `41`) bracket the state change the platform itself performs.

# -- 3. stop ------------------------------------------------------------------
echo "-- stop session $SESSION"
session_stop "$SESSION" "$OUT_DIR/30-session-stop.json"
session_get "session-after-stop" "$SESSION" "$OUT_DIR/31-session-after-stop.json"
sleep 5

# -- 4. resume ----------------------------------------------------------------
# A request id that is new to this process. Nothing in the resumed turn's prompt names
# the deadline or the approval task id; the only source is the store the previous
# container wrote.
python3 - "$REQUEST_ID" "$OUT_DIR/40-resume.request.json" <<'PY'
import json, sys
rid, out = sys.argv[1:3]
json.dump({"input": (
    f"Request {rid} again. This container was stopped and restarted. Read your durable "
    "working state with working_state, then call resume_work, and tell me: which approval "
    "task you found, what status it was in, the exact deadline value stored with it, and "
    "whether anything about this request had to be redone. Quote the values, do not "
    "summarise them."), "stream": False}, open(out, "w"), indent=1)
PY
python3 - "$OUT_DIR/40-resume.request.json" "$SESSION" <<'PY'
import json, sys
path, session = sys.argv[1:3]
body = json.load(open(path))
body["agent_session_id"] = session
json.dump(body, open(path, "w"), indent=1)
PY
post_agent "resume" "$OUT_DIR/40-resume.request.json" "$OUT_DIR/40-resume.response.json"
assert_same_session "resume" "$OUT_DIR/40-resume.response.json"
assert_same_version "resume" "$OUT_DIR/40-resume.response.json"
text_of "$OUT_DIR/40-resume.response.json" > "$OUT_DIR/40-resume.result.txt"
tools_of "$OUT_DIR/40-resume.response.json" > "$OUT_DIR/40-resume.tools.txt"
session_get "session-after-resume" "$SESSION" "$OUT_DIR/41-session-after-resume.json"

# -- 5. decide ----------------------------------------------------------------
python3 - "$REQUEST_ID" "$OUT_DIR/50-decide.request.json" <<'PY'
import json, sys
rid, out = sys.argv[1:3]
json.dump({"input": (
    f"The medical director has reviewed request {rid} and decided: approved. Reviewer "
    "'Dr. Chen', reason 'coverage criteria met; PET-avid nodule meets imaging criteria'. "
    "Use record_approval with those values, then read working_state and store_report and "
    "report the final phase, the decision, the reviewer, the approval task's status and "
    "its checkpoint phase. Quote what the tools returned."), "stream": False}, open(out, "w"), indent=1)
PY
python3 - "$OUT_DIR/50-decide.request.json" "$SESSION" <<'PY'
import json, sys
path, session = sys.argv[1:3]
body = json.load(open(path))
body["agent_session_id"] = session
json.dump(body, open(path, "w"), indent=1)
PY
post_agent "decide" "$OUT_DIR/50-decide.request.json" "$OUT_DIR/50-decide.response.json"
assert_same_session "decide" "$OUT_DIR/50-decide.response.json"
assert_same_version "decide" "$OUT_DIR/50-decide.response.json"
text_of "$OUT_DIR/50-decide.response.json" > "$OUT_DIR/50-decide.result.txt"
tools_of "$OUT_DIR/50-decide.response.json" > "$OUT_DIR/50-decide.tools.txt"
# No session object here: it would come back identical to `41`, for the reason
# `last_accessed_at` moves only when the next request is served.

# -- every capture, in one place ----------------------------------------------
for response in "$OUT_DIR"/1*-response.json "$OUT_DIR"/2*-response.json "$OUT_DIR"/4*-response.json "$OUT_DIR"/5*-response.json; do
  [ -f "$response" ] || continue
  events_of "$response" "${response%.response.json}.capture.json"
done

echo
echo "run directory: $OUT_DIR"
ls -1 "$OUT_DIR"
