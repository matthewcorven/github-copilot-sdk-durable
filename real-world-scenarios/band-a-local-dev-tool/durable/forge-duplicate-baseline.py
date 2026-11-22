"""Forge the duplicate-baseline shape the reconciliation has to survive.

A resumed process replays a session, so the same `turn:<messageId>` checkpoint is committed more
than once. That makes the baseline the task recorded ambiguous if it is matched by position: the
first occurrence and the last occurrence are in different places, and a turn that sits between them
looks like a later candidate when it is really an earlier one being replayed.

This rewrites an existing store into exactly that shape:

  [answer turn A] [baseline B] [turn A again]

Anchoring on the first B puts A after the anchor, so the candidate set is {A} and the guard
"resolves" a turn the task had already consumed. Anchoring on the last B puts A behind it, so the
candidate set is empty and the guard refuses.
"""

import json
import sys

source, target = sys.argv[1], sys.argv[2]

records = [json.loads(line) for line in open(source)]


def event_id(record):
    payload = record.get("payload") or {}
    return (payload.get("value") or {}).get("eventId")


def is_checkpoint(record, kind):
    payload = record.get("payload") or {}
    if payload.get("kind") != "durable.checkpoint":
        return False
    return (payload.get("value") or {}).get("kind") == kind


turns = {}
for record in records:
    if is_checkpoint(record, "turn"):
        turns.setdefault(event_id(record), record)

if len(turns) < 2:
    raise SystemExit(f"need at least two distinct turn checkpoints, saw {sorted(turns)}")

earlier, baseline = sorted(turns)[0], sorted(turns)[1]

out = []
for record in records:
    payload = record.get("payload") or {}
    if payload.get("kind") == "durable.checkpoint":
        continue
    if record.get("type") == "task":
        continue
    out.append(json.dumps(record) + "\n")

for index, turn in enumerate([earlier, baseline, earlier]):
    clone = json.loads(json.dumps(turns[turn]))
    clone["seq"] = 9000 + index
    out.append(json.dumps(clone) + "\n")

task = next(record for record in records if record.get("type") == "task")
payload = task["payload"]
payload["status"] = 1
payload.pop("outcome", None)
payload["checkpoint"] = {
    "phase": "run",
    "enquiryPhase": "searching",
    "turns": 1,
    "seenCheckpoint": baseline,
    "inTurn": True,
}
task["seq"] = 9500
task["payload"] = payload
out.append(json.dumps(task) + "\n")

open(target, "w").write("".join(out))
print(f"  forged: {earlier} / {baseline} / {earlier} -- the baseline is recorded twice")
