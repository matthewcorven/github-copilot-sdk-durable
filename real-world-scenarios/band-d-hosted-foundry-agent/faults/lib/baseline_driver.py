#!/usr/bin/env python3
"""The D1 baseline, driven through the same seams the fault scripts use.

D1 is the platform baseline: the accelerator's two hosted agents, invoked over the
Responses protocol. It has no local store and no orchestration of its own — that is the
finding the band records, not a gap in the harness. What this script provides is the
*drive*: one process that starts a run, writes each step's result to disk, and can be
interrupted at a named step, so the same six faults can reach it.

It is the same shape as D3's small driver, deliberately: a client process, a call count
taken from the service's own log, and results on disk.

    driver.py run --run-dir DIR --request TXT [--hold-step coverage|synthesis]
    driver.py resume --run-dir DIR
    driver.py count --run-dir DIR

``--hold-step`` is where a fault lands: the response for that step is read but not
returned, so the process is inside the call when it is killed. ``resume`` re-drives from
the first step with no result file, which is what a client that came back would do.

Exit codes: 0 finished, 3 a step failed (the service answered an error), 4 nothing to
resume from, 5 the resume needed to repeat a step that had already completed.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

COVERAGE = "coverage-assessment-agent"
SYNTHESIS = "synthesis-agent"
STEPS = [(COVERAGE, "coverage"), (SYNTHESIS, "synthesis")]

SERVICE_OFFLINE = "store-outage: the platform's data plane is unreachable"


def _base_url() -> str:
    url = os.environ.get("FOUNDRY_BASE_URL", "").strip().rstrip("/")
    if not url:
        raise SystemExit("driver.py: FOUNDRY_BASE_URL is not set")
    return url


def _token() -> str:
    return os.environ.get("FOUNDRY_TOKEN", "").strip() or "offline"


def _call(agent: str, prompt: str) -> dict:
    url = f"{_base_url()}/agents/{agent}/endpoint/protocols/openai/responses?api-version=v1"
    body = json.dumps({"input": prompt, "stream": False}).encode()
    request = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {_token()}"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return json.loads(response.read().decode())
    except urllib.error.HTTPError as error:
        detail = error.read().decode(errors="replace")[:400]
        raise RuntimeError(f"{agent} returned HTTP {error.code}: {detail}") from None
    except (urllib.error.URLError, TimeoutError) as error:
        raise RuntimeError(f"{SERVICE_OFFLINE} ({error})") from None


def _step_path(run_dir: str, step: str) -> str:
    return os.path.join(run_dir, "steps", f"{step}.json")


def _write(path: str, payload: dict) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=1, sort_keys=True)
        handle.write("\n")


def _read(path: str) -> dict | None:
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def count(run_dir: str) -> dict:
    """How many requests the service logged, per agent, and the run's own step files.

    The log belongs to the service (``lib/fake_foundry.py``), not to this process, so the
    number is the same however many clients drove the run.
    """
    path = os.path.join(run_dir, "service", "calls.jsonl")
    per_agent: dict[str, int] = {}
    total = 0
    try:
        with open(path, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    entry = json.loads(line)
                except ValueError:
                    continue
                if entry.get("method") != "POST":
                    continue
                total += 1
                per_agent[entry["agent"]] = per_agent.get(entry["agent"], 0) + 1
    except OSError:
        pass

    steps = {
        step: _read(_step_path(run_dir, step)) is not None for _, step in STEPS
    }
    return {"posts": total, "perAgent": per_agent, "stepFiles": steps, "log": path}


def drive(run_dir: str, request_text: str | None, hold: str | None) -> int:
    os.makedirs(os.path.join(run_dir, "steps"), exist_ok=True)
    for agent, step in STEPS:
        existing = _read(_step_path(run_dir, step))
        if existing is not None:
            print(f"[{step}] result already on disk — not called again", flush=True)
            continue

        prompt = request_text if request_text else json.load(
            open(os.path.join(run_dir, "request.json"), encoding="utf-8")
        )["request"]
        print(f"[{step}] calling {agent}", flush=True)
        try:
            response = _call(agent, prompt)
        except RuntimeError as error:
            print(f"[{step}] FAILED: {error}", flush=True)
            _write(os.path.join(run_dir, "status.json"), {"failedAt": step, "error": str(error)})
            return 3

        if hold == step:
            # The response is in hand; the process is held here so a kill lands inside
            # the step rather than between two of them.
            print(f"[{step}] HOLDING before the result is recorded", flush=True)
            marker = os.path.join(run_dir, "holding")
            with open(marker, "w", encoding="utf-8") as handle:
                handle.write(f"{step} pid={os.getpid()}\n")
            while os.path.exists(marker):
                time.sleep(0.05)

        _write(
            _step_path(run_dir, step),
            {"agent": agent, "step": step, "output": response.get("output_text", ""), "at": time.time()},
        )
        print(f"[{step}] recorded", flush=True)

    _write(
        os.path.join(run_dir, "status.json"),
        {"finished": True, "at": time.time(), "atIso": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())},
    )
    print("run finished", flush=True)
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="driver")
    commands = parser.add_subparsers(dest="command", required=True)

    run = commands.add_parser("run")
    run.add_argument("--run-dir", required=True)
    run.add_argument("--request", default=None)

    resume = commands.add_parser("resume")
    resume.add_argument("--run-dir", required=True)

    listing = commands.add_parser("count")
    listing.add_argument("--run-dir", required=True)

    hold_holder = run
    hold_holder.add_argument("--hold-step", default=None)

    args = parser.parse_args(argv)

    if args.command == "count":
        print(json.dumps(count(args.run_dir), indent=1, sort_keys=True))
        return 0

    if args.command == "resume":
        before = count(args.run_dir)
        if not any(before["stepFiles"].values()):
            print("driver.py: nothing to resume — no step result on disk", flush=True)
            return 4
        return drive(args.run_dir, None, None)

    os.makedirs(args.run_dir, exist_ok=True)
    if args.request is not None:
        _write(os.path.join(args.run_dir, "request.json"), {"request": args.request})
    return drive(args.run_dir, args.request, args.hold_step)


if __name__ == "__main__":
    raise SystemExit(main())
