import json, sys

lines = []
turns = []
for line in open(sys.argv[1]):
    record = json.loads(line)
    payload = record.get("payload") or {}
    if payload.get("kind") == "durable.checkpoint":
        value = payload.get("value") or {}
        if value.get("kind") == "turn":
            turns.append(value.get("eventId"))
    if record.get("type") == "task" and payload.get("status") == 4:
        payload["status"] = 1
        payload.pop("outcome", None)
        payload["checkpoint"] = {
            "phase": "run", "enquiryPhase": "searching", "turns": 1,
            "seenCheckpoint": turns[0] if turns else "turn:none", "inTurn": True,
        }
        record["payload"] = payload
        line = json.dumps(record) + "\n"
    lines.append(line)
open(sys.argv[2], "w").write("".join(lines))
print("  forged a store whose baseline precedes %d distinct turn checkpoint(s)" % len(set(turns)))
