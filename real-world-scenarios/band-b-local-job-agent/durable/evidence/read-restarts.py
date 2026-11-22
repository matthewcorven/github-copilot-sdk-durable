"""The restart count on the record, read after a resume has run."""
import sys

from _store import records, store_path


def main(flow: str) -> int:
    last = None
    for record in records(store_path(flow)):
        if record.get("type") == "task":
            last = record["payload"]
    if last is None:
        print(f"no task record for {flow}")
        return 1

    print("restarts on the record after the resume:", last["restarts"])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
