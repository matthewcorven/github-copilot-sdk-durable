#!/usr/bin/env python3
"""Take a reading of the running container's store *before* the fault, from outside it.

A fault that lands mid-write can leave a store an outside reader cannot parse; more
importantly, a fault that lands *after* the application already recorded the work is not a
fault. So the harness needs to know the moment the container reached the injection point,
and it needs that reading to come from another process — the container's own log is the
application's account of itself.

This script does the two-step:

1. wait for a marker file the container's own command writes when it reaches the step
   (the marker is read through the session volume, a named Docker volume, so the harness
   never enters the container to inject);
2. read the store file over the same volume and record its SHA-256, its length, and the
   task ids/statuses it holds, as the *before* reading.

If the container has already exited when the marker appears, the store may be mid-flight;
that is recorded rather than guessed at.

Usage:
    fence.py --wait MARKER --store PATH [--timeout SECONDS] [--out FILE]

This is a standalone reader, not a step in the sweep: `run-all.sh` does not call it, and no
fault script invokes it. It is kept because a reader that summarises the store's shape from
outside the layer is what a future fault would need, and because the alternative reading of
it - "the harness takes its before-image through this" - would be false.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import time


def digest(path: str) -> dict:
    try:
        with open(path, "rb") as handle:
            body = handle.read()
    except OSError:
        return {"path": path, "bytes": 0, "sha256": "no-file", "exists": False}
    return {
        "path": path,
        "bytes": len(body),
        "sha256": hashlib.sha256(body).hexdigest(),
        "exists": True,
    }


def summary(path: str) -> dict:
    """What a reader can say about the store without running the layer.

    Deliberately shallow: the JSONL wire format is read line by line and the task records
    counted, because importing `durable` here would take the store's write lock and change
    the thing being measured. The shape is the layer's own — ``{"type": ..., "payload": ...}``
    with tasks under ``type: "task"``.
    """
    counts: dict[str, int] = {}
    tasks: list[dict] = []
    try:
        with open(path, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    entry = json.loads(line)
                except ValueError:
                    counts["unparseable"] = counts.get("unparseable", 0) + 1
                    continue
                kind = str(entry.get("type") or "?")
                counts[kind] = counts.get(kind, 0) + 1
                payload = entry.get("payload") or {}
                if kind == "task" and isinstance(payload, dict) and payload.get("id"):
                    tasks.append(
                        {
                            "id": payload["id"],
                            "name": payload.get("name"),
                            "status": payload.get("status"),
                            "waitingOn": payload.get("waitingOn"),
                            "restarts": payload.get("restarts"),
                        }
                    )
    except OSError:
        pass
    return {"types": counts, "tasks": tasks}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="fence")
    parser.add_argument("--wait", required=True)
    parser.add_argument("--store", required=True)
    parser.add_argument("--timeout", type=float, default=120.0)
    parser.add_argument("--out", default=None)
    args = parser.parse_args(argv)

    started = time.time()
    while not os.path.exists(args.wait):
        if time.time() - started > args.timeout:
            print(f"fence: '{args.wait}' never appeared within {args.timeout:.0f}s", file=sys.stderr)
            return 3
        time.sleep(0.02)

    reading = {
        "waitedSeconds": round(time.time() - started, 3),
        "marker": args.wait,
        "store": digest(args.store),
        "storeSummary": summary(args.store),
        "containerMayHaveExited": os.path.exists(args.wait + ".exited"),
    }
    text = json.dumps(reading, indent=1, sort_keys=True)
    if args.out:
        os.makedirs(os.path.dirname(args.out), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as handle:
            handle.write(text + "\n")
    print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
