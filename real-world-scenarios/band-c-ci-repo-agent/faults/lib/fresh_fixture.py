"""Write a copy of the PR fixture with a new capture clock.

The fixture's dates are relative — `openedMinutesAgo` / `updatedMinutesAgo` measured from the file's own
`capturedAt` — so moving `capturedAt` moves every PR's age with it and the cooldown filter answers the
same way on any date. `cooldown-desync` needs that, because its three runs are at three different
instants: run 2 must *not* be inside the per-PR cooldown (it has to post comments for the fault to be
about its state write) while its previous run must be inside the run gate (it has to be skipped). Both
are true only when the fixture is captured at the instant the run reads it.

Usage: fresh_fixture.py <source-fixture> <now-iso8601> <destination>
"""

from __future__ import annotations

import json
import pathlib
import sys


def main(argv: list[str]) -> int:
    if len(argv) != 4:
        print("usage: fresh_fixture.py <source> <now> <destination>", file=sys.stderr)
        return 2
    source, now, destination = argv[1], argv[2], argv[3]
    document = json.loads(pathlib.Path(source).read_text())
    document["capturedAt"] = now
    pathlib.Path(destination).write_text(json.dumps(document, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
