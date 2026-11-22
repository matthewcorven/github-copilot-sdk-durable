"""The mutation that produces the before/after control for the re-entry wait.

`lib/Durable.cs` recovers a task's recorded wait when a process re-enters it, so a parent that was
`Waiting` on its children finishes them before its next phase runs. The scenario's evidence removes
that guard, rebuilds, and replays a two-process flow; the parent's post-gate phase then reads a gate
that has not finished, and the run fails instead of completing.

This script does exactly one thing, and refuses rather than guessing:

  exit 0  the guard was found and removed
  exit 1  the guard is not in the file, or the file has no other difference left to make
  exit 2  bad usage

Usage: remove-reentry-wait.py <path-to-Durable.cs>
"""

import pathlib
import sys

GUARD = """        if (Store.Task(taskId)?.Status == TaskStatus.Waiting)
        {
            await WaitForChildrenAsync(taskId, record.WaitingOn, cancellationToken);
        }

"""


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2

    path = pathlib.Path(argv[1])
    text = path.read_text(encoding="utf-8")
    if GUARD not in text:
        print(
            f"{path}: the re-entry wait is not present, so there is nothing to remove. "
            "The control cannot be built from this checkout.",
            file=sys.stderr,
        )
        return 1

    mutated = text.replace(GUARD, "", 1)
    if mutated == text:
        print(f"{path}: the removal changed nothing; refusing to report a mutation.", file=sys.stderr)
        return 1

    path.write_text(mutated, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
