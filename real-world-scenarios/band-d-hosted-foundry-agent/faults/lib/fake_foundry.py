#!/usr/bin/env python3
"""A stand-in for the Foundry data plane, so agent-call behaviour is measurable offline.

**What this is not.** It is not Foundry, and no result in this directory claims it is. The
platform's own behaviour is quoted from D1's captures in ``evidence/platform-behaviour.md``
and from the four answers in ``evidence/attribution.md``; every number this file produces
is in a result's ``application`` block.

**What it is.** The thing that makes "how many agent calls did the run make" a
measurement rather than a claim. It serves the two endpoints the D3 orchestrator calls:

    GET  /agents/{name}/versions?api-version=v1
    POST /agents/{name}/endpoint/protocols/openai/responses?api-version=v1&agent_version=N

and appends one line per request to ``<root>/calls.jsonl``. A call count is the number of
lines for an agent, which no participant in the run can write to. That is the same
discipline D3 used with the platform's own response list; the difference is only that the
list belongs to this process instead of to Azure.

Two control files let a fault be aimed at a specific call without a sleep:

    <root>/control/fail-agents.txt     one agent name per line: answer that agent 503
    <root>/control/hold-agents.txt     one agent name per line: hold the request until
                                       <root>/control/release-holds exists

A held request is a real in-flight call — the orchestrator is inside ``InvokeAsync``,
waiting on the socket — which is the seam a kill of the client, or a restart of the
container, has to land in.

Usage: fake_foundry.py --root DIR --port N
"""

from __future__ import annotations

import argparse
import json
import os
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

#: The identities each name reports. D3 keys its memos on `name@version:digest`, so a
#: stable digest here means a stable memo key across processes, which is what lets a
#: resumed run recognise a completed step.
DEFINITIONS = {
    "coverage-assessment-agent": {
        "version": "1",
        "instructions": "Assess the request against the coverage criteria.",
    },
    "synthesis-agent": {
        "version": "1",
        "instructions": "Synthesize the assessment into a determination.",
    },
    "d3-coverage-assessor": {
        "version": "1",
        "instructions": "D3: assess the request against the coverage criteria.",
    },
    "d3-decision-letter": {
        "version": "1",
        "instructions": "D3: write the determination summary.",
    },
}

#: The answer each agent gives. Deterministic on purpose: a run that repeats a step has
#: to be distinguishable only by the call count, never by a different answer.
OUTPUTS = {
    "coverage-assessment-agent": (
        "CRITERION 1 Provider Specialty-Procedure Appropriateness: MET\n"
        "CRITERION 2 Site of Service: MET\n"
        "RECOMMENDATION: approve PA-1042 (2 of 2 criteria met)"
    ),
    "synthesis-agent": (
        "DETERMINATION: approved\n"
        "RECOMMENDATION: approve PA-1042 (2 of 2 criteria met)"
    ),
    "d3-coverage-assessor": (
        "CRITERION 1 Provider Specialty-Procedure Appropriateness: MET\n"
        "CRITERION 2 Site of Service: MET\n"
        "RECOMMENDATION: approve PA-1042 (2 of 2 criteria met)"
    ),
    "d3-decision-letter": (
        "DETERMINATION: approved - 2 of 2 criteria met, reviewer approved\n"
        "RECOMMENDATION: approve PA-1042"
    ),
}

_LOCK = threading.Lock()

RESPONSES = re.compile(r"^/agents/(?P<agent>[^/]+)/endpoint/protocols/openai/responses$")
VERSIONS = re.compile(r"^/agents/(?P<agent>[^/]+)/versions$")


def _lines(path: str) -> list[str]:
    try:
        with open(path, encoding="utf-8") as handle:
            return [line.strip() for line in handle if line.strip()]
    except OSError:
        return []


class Handler(BaseHTTPRequestHandler):
    root = ""
    protocol_version = "HTTP/1.1"

    # -- logging is the measurement, so it is JSONL and never prose -------------------

    def _record(self, agent: str, version: str | None) -> None:
        entry = {
            "at": time.time(),
            "atIso": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "agent": agent,
            "agentVersion": version,
            "method": self.command,
            "path": self.path,
        }
        with _LOCK, open(os.path.join(self.root, "calls.jsonl"), "a", encoding="utf-8") as handle:
            handle.write(json.dumps(entry, sort_keys=True) + "\n")

    def _send(self, code: int, payload: dict) -> None:
        body = json.dumps(payload).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _control(self, name: str) -> list[str]:
        return _lines(os.path.join(self.root, "control", name))

    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler's spelling
        parsed = urlparse(self.path)
        match = VERSIONS.match(parsed.path)
        if not match:
            self._send(404, {"error": f"no route for {parsed.path}"})
            return

        agent = match.group("agent")
        definition = DEFINITIONS.get(agent)
        if definition is None:
            self._send(404, {"error": f"agent '{agent}' has no versions"})
            return

        self._record(agent, definition["version"])
        self._send(
            200,
            {
                "data": [
                    {
                        "version": definition["version"],
                        "status": "active",
                        "created_at": int(time.time()) - 600,
                        "definition": {"instructions": definition["instructions"]},
                    }
                ]
            },
        )

    def do_POST(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        match = RESPONSES.match(parsed.path)
        if not match:
            self._send(404, {"error": f"no route for {parsed.path}"})
            return

        agent = match.group("agent")
        length = int(self.headers.get("Content-Length") or 0)
        self.rfile.read(length)
        version = parse_qs(parsed.query).get("agent_version", [None])[0]

        # An agent named in the fail list answers 503 for as long as the name is there.
        # That is an application-visible agent-call failure, not a platform outage: the
        # orchestrator's retry loop sees exactly the shape Azure's 5xx produces.
        fail = agent in self._control("fail-agents.txt")
        if fail:
            self._record(agent, version)
            self._send(503, {"error": {"code": "agent_unavailable", "message": "injected"}})
            return

        # The request is recorded *before* a hold, because the fact the harness needs is
        # "the service accepted this request", which is true the moment it arrives. A hold
        # that recorded afterwards would be invisible to a harness that kills the client
        # while the client is inside the call — the exact seam this fault uses.
        self._record(agent, version)

        if agent in self._control("hold-agents.txt"):
            release = os.path.join(self.root, "control", "release-holds")
            deadline = time.time() + 300
            while not os.path.exists(release) and time.time() < deadline:
                time.sleep(0.05)

        if agent not in DEFINITIONS:
            self._send(404, {"error": f"agent '{agent}' does not exist"})
            return

        self._send(
            200,
            {
                "id": f"resp_{int(time.time() * 1000)}",
                "object": "response",
                "status": "completed",
                "background": False,
                "output_text": OUTPUTS[agent],
            },
        )

    def log_message(self, *_args: object) -> None:
        pass


def main() -> int:
    parser = argparse.ArgumentParser(prog="fake-foundry")
    parser.add_argument("--root", required=True)
    parser.add_argument("--port", type=int, required=True)
    args = parser.parse_args()

    os.makedirs(os.path.join(args.root, "control"), exist_ok=True)
    Handler.root = args.root
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    server.daemon_threads = True
    # One line the harness waits for, so readiness is not a probe request that would land in
    # the very log the call count is read from.
    with open(os.path.join(args.root, "ready"), "w", encoding="utf-8") as handle:
        handle.write(f"port={args.port} pid={os.getpid()}\n")
    print(f"fake-foundry: listening on 127.0.0.1:{args.port}, root {args.root}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
