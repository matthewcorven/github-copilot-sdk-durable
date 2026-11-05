"""The task record at the end of a flow: memo keys, restarts, checkpoint."""
import json
import sys

from _store import records, store_path


def main(flow: str) -> int:
    path = store_path(flow)
    last = None
    for record in records(path):
        if record.get("type") == "task":
            last = record["payload"]
    if last is None:
        print(f"no task record in {path}")
        return 1

    print("memo keys:", sorted(last["memos"].keys()))
    print("restarts:", last.get("restarts"))
    print("checkpoint:", json.dumps(last["checkpoint"], indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
