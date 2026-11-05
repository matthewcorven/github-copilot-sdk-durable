"""Shared reader for the evidence recordings: opens a flow's JSONL store."""
import json
import os


def store_path(flow: str) -> str:
    root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", ".."))
    return os.path.join(root, ".scratch", "band-b-evidence", flow, "target", ".durable", "store.jsonl")


def records(path):
    with open(path) as handle:
        for line in handle:
            yield json.loads(line)
