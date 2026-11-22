"""Counts the specialist tool results a baseline checkpointed conversation holds.

The baseline's framework checkpoints carry the shared conversation, and a tool result appears in it
as a `functionResult` content item. A re-delivered request that the port replays runs the tool again,
so the count is the observation for `duplicate-delivery`: it is read from the checkpoint the framework
wrote, not from the port's log.
"""

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import holder  # noqa: E402


def tool_results(document):
    for key, value in (document.get("stateData") or {}).items():
        if not str(key).endswith("SharedState"):
            continue
        history = (((value or {}).get("value") or {}).get("conversation") or {}).get("history") or []
        results = []
        for message in history:
            for content in message.get("contents") or []:
                if content.get("$type") == "functionResult" and content.get("result") is not None:
                    # The framework's own handoff handshake answers "Transferred." from a
                    # `handoff_to_*` call; that is routing, not the specialist's domain tool.
                    if str(content.get("result")) == "Transferred.":
                        continue
                    results.append(str(content.get("result")))
        return results
    return []


def newest(directory):
    """The newest checkpoint document, read from the last index entry only.

    The index is append-ordered, so the last entry is the newest document; reading one file rather
    than every checkpoint keeps this cheap enough to poll for an injection point.
    """
    index = os.path.join(directory, "index.jsonl")
    last = None
    try:
        with open(index, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if line:
                    last = line
    except OSError:
        return None
    if not last:
        checkpoints = holder._baseline_checkpoints(directory)
        return checkpoints[-1][1] if checkpoints else None
    try:
        entry = json.loads(last)
    except ValueError:
        return None
    name = entry.get("fileName")
    if not name:
        return None
    try:
        return json.load(open(os.path.join(directory, name), encoding="utf-8"))
    except (OSError, ValueError):
        return None


def main(argv):
    if len(argv) < 3 or argv[1] not in ("count", "list", "user-turns"):
        print("usage: tool_results.py <count|list|user-turns> <checkpoint-dir>", file=sys.stderr)
        return 2
    document = newest(argv[2])
    if document is None:
        print(0 if argv[1] == "count" else "[]")
        return 0
    results = tool_results(document)
    if argv[1] == "count":
        print(len(results))
    elif argv[1] == "list":
        print(json.dumps(results, indent=2))
    else:
        turns = []
        for key, value in (document.get("stateData") or {}).items():
            if not str(key).endswith("SharedState"):
                continue
            history = (((value or {}).get("value") or {}).get("conversation") or {}).get("history") or []
            for message in history:
                if message.get("role") == "user":
                    text = " ".join(c.get("text") or "" for c in message.get("contents") or [] if c.get("$type") == "text")
                    if text:
                        turns.append(text)
        print(json.dumps(turns, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
