"""Forge the half-written store design point 3 exists to rule out.

A completed run's store, with one turn's specialist output removed: the `band-f.specialist-output`
document version is dropped, and so is the `produced` control record that says it existed. What is
left is a store that holds a committed delegation for that turn and nothing the specialist produced
for it — the state a process that died between "control moved" and "the output committed" would leave
if the two had not been written together.

The point of the forgery is `verify`. A check that skips a turn whose output is absent passes this
store, and that is exactly the failure the variant rules out; the checks in claims 2 and 4 therefore
fail on it rather than skipping it. `show` prints `output: (none)` for the turn, which is the same
fact stated a second way.

Usage: forge-missing-output.py <source-store.jsonl> <target-store.jsonl> <turn>

Written with `json`, not the layer: a forgery has to be able to produce a shape the layer would never
write, or it is not a control.
"""

import json
import sys

source, target, turn = sys.argv[1], sys.argv[2], int(sys.argv[3])

records = [json.loads(line) for line in open(source)]
owner = f"band-f.delegation/{turn}"

# The entry the turn's output was committed under. Both records share it, so removing one without the
# other would leave a store the layer could not have produced by stopping between two commits.
output_entry = None
for record in records:
    payload = record.get("payload") or {}
    if record.get("type") != "document-version":
        continue
    if payload.get("kind") != "band-f.specialist-output" or payload.get("ownerId") != owner:
        continue
    output_entry = payload.get("entryId")

if output_entry is None:
    raise SystemExit(f"store has no band-f.specialist-output for turn {turn}")

kept = []
removed = {"document-version": 0, "entry-record": 0, "entry": 0}
for record in records:
    payload = record.get("payload") or {}
    kind = record.get("type")

    if kind == "document-version" and payload.get("kind") == "band-f.specialist-output" \
            and payload.get("ownerId") == owner:
        removed["document-version"] += 1
        continue

    if kind == "entry-record" and payload.get("id") == output_entry:
        removed["entry-record"] += 1
        continue

    if kind == "entry" and payload.get("entryId") == output_entry:
        removed["entry"] += 1
        continue

    kept.append(record)

# The `produced` control record for this turn is a document version like any other; find it by the
# event it carries, and drop only that one.
wire_removed = 0
final = []
for record in kept:
    payload = record.get("payload") or {}
    value = payload.get("value") or {}
    if record.get("type") == "document-version" \
            and payload.get("kind") == "band-f.wire" \
            and payload.get("ownerId") == owner \
            and value.get("event") == "produced":
        wire_removed += 1
        continue
    final.append(record)

if wire_removed == 0:
    raise SystemExit(f"store has no produced control record for turn {turn}")

with open(target, "w") as handle:
    for record in final:
        handle.write(json.dumps(record, separators=(",", ":")) + "\n")

print(f"forged: removed turn {turn}'s specialist output and its produced record")
print(f"  entry {output_entry}: {removed}")
print(f"  produced control records removed: {wire_removed}")
print(f"  records: {len(records)} -> {len(final)}")
