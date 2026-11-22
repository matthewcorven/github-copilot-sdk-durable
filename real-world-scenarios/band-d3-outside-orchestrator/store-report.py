#!/usr/bin/env python3
"""Read a D3 store and print what it records, as JSON.

    ./store-report.py <store.jsonl>

Used by the evidence scripts so a claim about the store is read out of the store,
not retyped from a terminal. Three things it reports:

  task        the task's id, status, restart count and checkpoint phase
  memos       every memo key with the step and agent identity that produced it
  result      the terminal outcome's result, unwrapped

The outcome is stored as `{status, result, error}` for a terminal task and as the
store's own shape otherwise, so `result` is unwrapped rather than assumed.
"""
import json
import sys


def main(path):
    records = [json.loads(line) for line in open(path) if line.strip()]
    tasks = [r["payload"] for r in records if r.get("type") == "task"]
    if not tasks:
        raise SystemExit(f"{path}: no task records")

    # The store appends a task record each time its state changes, so the last one is
    # the current state and `restarts` is read from it rather than summed.
    task = tasks[-1]
    outcome = task.get("outcome")

    result = None
    if isinstance(outcome, dict):
        if "result" in outcome:
            result = outcome["result"]
        elif outcome.get("status") == "completed":
            result = outcome
    elif isinstance(outcome, str):
        try:
            parsed = json.loads(outcome)
            result = parsed.get("result", parsed)
        except json.JSONDecodeError:
            result = {"raw": outcome}

    memos = {}
    for key, value in (task.get("memos") or {}).items():
        settled = value if isinstance(value, dict) else json.loads(value)
        memos[key] = {
            "step": settled.get("step"),
            "agentIdentity": settled.get("agentIdentity"),
            "outputChars": len(settled.get("output") or ""),
        }

    report = {
        "task": {
            "id": task.get("id"),
            "name": task.get("name"),
            "status": task.get("status"),
            "restarts": task.get("restarts"),
            "phase": (task.get("checkpoint") or {}).get("phase"),
        },
        "memos": dict(sorted(memos.items())),
        "result": result,
    }
    json.dump(report, sys.stdout, indent=1)
    print()


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: store-report.py <store.jsonl>")
    main(sys.argv[1])
