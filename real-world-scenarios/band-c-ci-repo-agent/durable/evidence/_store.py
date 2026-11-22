"""Read a Band C Durable JSONL store without going through the program under test.

The evidence has to be checkable from the store's own records, not from what the run printed.
Everything here reads `store.jsonl` line by line and reconstructs the task record and the comment
ledger from the records themselves, so a claim like "the memo was on the record" can be checked by
something that has no opinion about it.
"""

from __future__ import annotations

import json
import pathlib
import sys


def records(path: str) -> list[dict]:
    file = pathlib.Path(path)
    if not file.exists():
        raise SystemExit(f"{path}: no such store — the run that should have created it did not")
    return [json.loads(line) for line in file.read_text().splitlines() if line.strip()]


def task_states(path: str) -> list[dict]:
    """Every committed state of the triage task, oldest first.

    The JSONL provider appends a `task` record whenever the task's serialized form changed, so this
    is the task's whole history: status, checkpoint and memos after each commit.
    """
    out = []
    for record in records(path):
        if record.get("type") == "task":
            payload = record.get("payload") or {}
            if str(payload.get("name", "")).startswith("band-c."):
                out.append(payload)
    return out


def final_task(path: str) -> dict:
    states = task_states(path)
    if not states:
        raise SystemExit(f"{path}: no band-c task record")
    return states[-1]


def memos(path: str) -> dict:
    return final_task(path).get("memos") or {}


def ledger(path: str) -> dict:
    """The latest committed value of the comment ledger document."""
    latest = None
    for record in records(path):
        if record.get("type") == "document-version":
            payload = record.get("payload") or {}
            if payload.get("kind") == "band-c.comments":
                latest = payload.get("value") or {}
    return latest or {}


def run_state(path: str) -> dict:
    """The latest committed value of the run record for `fixture/durable`."""
    latest = None
    for record in records(path):
        if record.get("type") == "document-version":
            payload = record.get("payload") or {}
            if payload.get("kind") == "band-c.run":
                latest = payload.get("value") or {}
    return latest or {}


def pull_request_states(path: str) -> dict[int, dict]:
    """The latest committed assessment of every pull request, by number."""
    latest: dict[int, dict] = {}
    for record in records(path):
        if record.get("type") == "document-version":
            payload = record.get("payload") or {}
            if payload.get("kind") == "band-c.pr":
                value = payload.get("value") or {}
                if "number" in value:
                    latest[int(value["number"])] = value
    return latest


def document_versions(path: str, kind: str) -> int:
    """How many versions of a document kind were committed."""
    return sum(
        1
        for record in records(path)
        if record.get("type") == "document-version"
        and (record.get("payload") or {}).get("kind") == kind
    )


def main(argv: list[str]) -> int:
    if len(argv) < 3:
        print("usage: _store.py <command> <store.jsonl>", file=sys.stderr)
        print(
            "commands: task, memos, ledger, run, prs, versions",
            file=sys.stderr,
        )
        return 2

    command, path = argv[1], argv[2]
    if command == "task":
        for index, state in enumerate(task_states(path)):
            checkpoint = state.get("checkpoint") or {}
            print(
                f"{index:>2} status={state.get('status')} "
                f"phase={checkpoint.get('phase')} "
                f"attempts={checkpoint.get('attempts')} "
                f"generation={checkpoint.get('generation')} "
                f"commented={len(checkpoint.get('commented') or [])} "
                f"memos={len(state.get('memos') or {})} "
                f"outcome={(state.get('outcome') or {}).get('status')}"
            )
        return 0

    if command == "memos":
        for key in sorted(memos(path)):
            print(key)
        return 0

    if command == "ledger":
        entries = ledger(path).get("comments") or []
        print(f"ledger entries: {len(entries)}")
        for entry in entries:
            print(f"  #{entry.get('number')} digest={entry.get('digest')} "
                  f"postedAt={entry.get('postedAt')} run={entry.get('runId')} bytes={entry.get('bytes')}")
        return 0

    if command == "run":
        state = run_state(path)
        for key in ("phase", "repository", "startedAt", "generation", "attempts", "resumedFrom"):
            print(f"{key}: {state.get(key)}")
        print(f"commented: {state.get('commented')}")
        return 0

    if command == "prs":
        for number, state in sorted(pull_request_states(path).items()):
            print(f"#{number} stage={state.get('stage')} digest={state.get('inputDigest')} "
                  f"{state.get('category')}/{state.get('risk')} {state.get('action')} "
                  f"gen={state.get('generation')}")
        return 0

    if command == "versions":
        for kind in ("band-c.run", "band-c.pr", "band-c.comments"):
            print(f"{kind}: {document_versions(path, kind)} committed version(s)")
        return 0

    print(f"unknown command '{command}'", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
