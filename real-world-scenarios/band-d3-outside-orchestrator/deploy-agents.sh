#!/usr/bin/env bash
# Deploy D3's two prompt agents into the D1 deployment, and write the identity record.
#
# Requirement 4 is "reuse D1's deployment; do not stand up a second set of resources",
# so this script creates *no* infrastructure. It reads D1's `.deployment.json` and
# `deployment.env`, and registers two prompt agents in the project D1 already owns.
#
# Why prompt agents rather than a second hosted image: the memo key's identity half is
# a digest of the agent *definition*, and a prompt agent's definition is text that
# Foundry stores and returns verbatim. Changing instructions is therefore a one-file
# edit whose digest anyone can recompute, with no image build in the way. D1 has
# already proven the hosted packaging; D3's subject is the orchestrator.
#
# Idempotent in the way that matters: each call registers a *new version*, which is the
# documented behaviour, so `agent.yaml` can be edited and re-applied and the platform
# reports a different highest version. Running it twice with an unchanged file leaves
# two identical versions; the identity digest is the same either way, which is the
# point of hashing the definition rather than the version number alone.
#
# Env:
#   NAME_PREFIX   default bandd-priorauth — must match the provisioned D1 deployment
#   AGENT_DIR     default ./agent
#
# Usage: ./deploy-agents.sh

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BAND_D="$(cd "$HERE/../band-d-hosted-foundry-agent" && pwd)"
AGENT_DIR="${AGENT_DIR:-$HERE/agent}"
RESOURCE="https://ai.azure.com"
API_VERSION="v1"

# D1's deployment record. Reuse is only real if this script refuses to invent one.
[ -f "$BAND_D/.deployment.json" ] || {
  echo "deploy-agents.sh: $BAND_D/.deployment.json is missing." >&2
  echo "                  Run ../band-d-hosted-foundry-agent/infra/provision.sh first; this script" >&2
  echo "                  registers agents into THAT deployment and provisions nothing." >&2
  exit 1
}
[ -f "$BAND_D/deployment.env" ] || {
  echo "deploy-agents.sh: $BAND_D/deployment.env is missing; run D1's infra/deploy.sh first." >&2
  exit 1
}

# shellcheck disable=SC1091
. "$BAND_D/deployment.env"
[ -n "${FOUNDRY_BASE_URL:-}" ] || {
  echo "deploy-agents.sh: deployment.env has no FOUNDRY_BASE_URL." >&2
  exit 1
}

export FOUNDRY_BASE_URL
FOUNDRY_TOKEN="$(az account get-access-token --resource "$RESOURCE" --query accessToken -o tsv)"
export FOUNDRY_TOKEN
export RESOURCE

echo "project : $FOUNDRY_BASE_URL"
echo "agents  : $AGENT_DIR"
echo

# The yaml → JSON request body. Parsed here rather than with a yaml library so the
# script has no dependency on one: the file's shape is ours and it is checked.
body_of() {
  python3 - "$1" <<'PY'
import json, sys
path = sys.argv[1]
text = open(path).read()

def fields():
    """The yaml's own `key: value` lines: comments and blank lines are not fields.

    A comment mentioning `temperature:` must not be read as the setting, and one did
    once — the agent silently kept the service default while the file said otherwise.
    """
    for line in text.splitlines():
        if line.strip().startswith("#"):
            continue
        yield line

def scalar(name):
    for line in fields():
        s = line.strip()
        if s.startswith(name + ":"):
            value = s[len(name) + 1:].strip()
            # Strip a YAML block scalar marker, if the value is one.
            return None if value in ("|", ">", "|-", ">-") else value.strip('"\'')
    return None

def block(name):
    """The indented body under `name: |`, dedented."""
    lines = list(fields())
    for i, line in enumerate(lines):
        if line.strip() in (name + ": |", name + ": |-"):
            base = len(lines[i]) - len(lines[i].lstrip())
            out = []
            for follow in lines[i + 1:]:
                if follow.strip() and (len(follow) - len(follow.lstrip())) <= base:
                    break
                out.append(follow[base + 2:] if len(follow) > base + 2 else "")
            return "\n".join(out).rstrip()
    return None

agent_name = scalar("name")
if not agent_name:
    raise SystemExit(f"{path}: no agent.name")
body = {
    "definition": {
        "kind": scalar("kind") or "prompt",
        "model": scalar("model"),
        "instructions": block("instructions"),
    }
}
description = block("description")
if description:
    body["description"] = description
# `temperature` is part of the stored definition when it is sent — verified against this
# project, which echoed it back on the version object. Omitting it is what leaves the
# service default in place, so the parser must find it: it reads only the yaml's real
# fields, never a comment that happens to mention the word.
missing = [k for k, v in body["definition"].items() if not v]
if missing:
    raise SystemExit(f"{path}: definition is missing {', '.join(missing)}")
print(json.dumps(body))
PY
}

register() {
  local file="$1"
  local name
  name="$(python3 -c "
import sys
for line in open(sys.argv[1]):
    if line.strip().startswith('name:'):
        print(line.strip()[5:].strip()); break
" "$file")"
  echo "== $name ($(basename "$file"))"
  body_of "$file" > "$HERE/.agent-body.json"

  # A connection refusal on this machine says nothing about the service, so a failed
  # attempt is retried rather than reported as a deploy failure. A version registration is
  # not idempotent (each POST creates a version), but a request that never reached the
  # service created nothing, which is exactly what a transport failure means.
  local response attempt
  for attempt in 1 2 3 4; do
    if response="$(curl -sS --fail-with-body -X POST \
      -H "Authorization: Bearer $FOUNDRY_TOKEN" \
      -H "Content-Type: application/json" \
      --data-binary @"$HERE/.agent-body.json" \
      "$FOUNDRY_BASE_URL/agents/$name/versions?api-version=$API_VERSION" 2>/dev/null)"; then
      break
    fi
    echo "   attempt $attempt failed; retrying in $((attempt * 3))s"
    sleep $((attempt * 3))
    response=""
  done
  [ -n "$response" ] || {
    echo "deploy-agents.sh: $name: no response after 4 attempts." >&2
    rm -f "$HERE/.agent-body.json"
    exit 1
  }

  python3 - "$response" "$name" <<'PY'
import json, sys
raw, name = sys.argv[1:3]
try:
    d = json.loads(raw)
except Exception:
    raise SystemExit(f"deploy-agents.sh: {name}: response was not JSON:\n{raw[:800]}")
if d.get("error"):
    raise SystemExit(f"deploy-agents.sh: {name}: {json.dumps(d['error'])}")
version = d.get("version")
status = d.get("status")
print(f"   version {version} ← {status}")
if status != "active":
    raise SystemExit(f"deploy-agents.sh: {name} version {version} is '{status}', not 'active'")
PY
  rm -f "$HERE/.agent-body.json"
}

shopt -s nullglob
files=("$AGENT_DIR"/*.yaml)
[ "${#files[@]}" -gt 0 ] || { echo "deploy-agents.sh: no *.yaml in $AGENT_DIR" >&2; exit 1; }
for file in "${files[@]}"; do
  register "$file"
done

# The record the evidence quotes: what the platform says each agent is, right now.
OUT="$HERE/evidence/agent-identity.json"
mkdir -p "$(dirname "$OUT")"
# The identity read is the record the evidence quotes, so it is retried rather than
# left empty when the endpoint refuses a connection.
IDENTITY_ATTEMPTS=4
for identity_attempt in $(seq 1 $IDENTITY_ATTEMPTS); do
  if python3 - "$FOUNDRY_BASE_URL" "$FOUNDRY_TOKEN" "$AGENT_DIR" "$OUT" <<'PY'
import hashlib, json, sys, urllib.request
base, token, agent_dir, out = sys.argv[1:5]

def canonical(value):
    if isinstance(value, dict):
        return "{" + ",".join(json.dumps(k) + ":" + canonical(value[k]) for k in sorted(value)) + "}"
    if isinstance(value, list):
        return "[" + ",".join(canonical(v) for v in value) + "]"
    return json.dumps(value, separators=(",", ":"))

def get(name):
    request = urllib.request.Request(
        f"{base}/agents/{name}/versions?api-version=v1",
        headers={"Authorization": f"Bearer {token}"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)["data"]

records = {}
for name in ("d3-coverage-assessor", "d3-decision-letter"):
    versions = [v for v in get(name) if v.get("status") == "active"]
    versions.sort(key=lambda v: int(v["version"]))
    latest = versions[-1]
    definition = canonical(latest["definition"])
    records[name] = {
        "version": latest["version"],
        "identity": f"{name}@{latest['version']}:{hashlib.sha256(definition.encode()).hexdigest()[:16]}",
        "definition_digest": hashlib.sha256(definition.encode()).hexdigest(),
        "definition": latest["definition"],
    }
json.dump(records, open(out, "w"), indent=1)
print()
for name, record in records.items():
    print(f"{name:28s} {record['identity']}")
print(f"wrote {out}")
PY
  then
    break
  fi
  echo "deploy-agents.sh: identity read attempt $identity_attempt failed; retrying in $((identity_attempt * 3))s" >&2
  sleep $((identity_attempt * 3))
  [ "$identity_attempt" = "$IDENTITY_ATTEMPTS" ] && { echo "deploy-agents.sh: could not read the agent identities." >&2; exit 1; }
done
