"""Per-conversation document history: which checkpoint kinds and admissions a store holds.

A checkpoint 'turn' means the turn finished and was recorded; a history that stops at 'tool' means
it did not. An admission still 'admitted' means the prompt was sent and never settled.
"""
import sys

from _store import records, store_path

KINDS = ("durable.checkpoint", "durable.admission")


def main(flow: str) -> int:
    seen = {}
    for record in records(store_path(flow)):
        if record.get("type") != "document-version":
            continue
        payload = record["payload"]
        kind = payload.get("kind")
        if kind not in KINDS:
            continue
        value = payload.get("value") or {}
        seen.setdefault((payload.get("ownerId"), kind), []).append(
            value.get("kind") if kind == "durable.checkpoint" else value.get("state"))

    for (owner, kind) in sorted(seen):
        print(f"{owner} {kind}: {seen[(owner, kind)]}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
