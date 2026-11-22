"""Read either Band C variant's committed state without going through the program that wrote it.

The baseline keeps its state in one JSON blob on the git branch `memory/pr-triage`; the Durable variant
keeps it in a Durable JSONL store. A fault's observation has to be checkable from those artefacts and
not from what a process printed — after a `kill -9` there may be no process left to ask — so this
reader opens each one directly and answers the same questions of both:

    state  <variant> <path> <field>   one field of the committed run state (phase, startedAt, triaged)
    triaged <variant> <path>          the PR numbers the committed run triaged, comma separated
    runs   <variant> <path>           how many runs the state remembers (records, for the baseline)
    exists <variant> <path>           whether the state exists at all
    comments <dirname>                how many `pr-NNN.md` comment files a run directory holds
    wrote <run-log>                   the PR numbers a run's log says it wrote a comment for
    overlap <before-dir> <after-dir>  comments a second run wrote that the first had already written
    snapshot <dir>... <out.json>      record each posted comment's bytes (a digest per PR); several
                                      comment directories may be given and are merged, so a baseline
                                      whose two runs use two output directories is still one view
    duplicates <snapshot.json> <run-log> <comments-dir>
                                      PRs this run wrote whose content the snapshot already held — the
                                      same comment posted twice, byte for byte

`path` is the bare repository for `baseline` and the store file for `durable`.

The two shapes are read separately and deliberately: the baseline's `pr-triage-latest.json` is a whole
snapshot written at the end of a run, so "the state" is one document; the Durable store is a log of
committed versions, so "the state" is the last committed value of a document kind. Reporting both under
one command is what lets a fault script write one sentence about both variants.
"""

from __future__ import annotations

import json
import pathlib
import re
import subprocess
import sys

RUN_KIND = "band-c.run"


# ─── Baseline: the blob on the state branch ──────────────────────────────────────────────────────


def _git(repo: str, *args: str) -> str | None:
    result = subprocess.run(
        ["git", "--git-dir", repo, *args],
        capture_output=True,
        text=True,
        env={
            "GIT_TERMINAL_PROMPT": "0",
            "GIT_CONFIG_NOSYSTEM": "1",
            "PATH": "/usr/bin:/bin:/usr/local/bin:/opt/homebrew/bin",
        },
    )
    return result.stdout if result.returncode == 0 else None


def baseline_state(repo: str) -> dict | None:
    if not pathlib.Path(repo, "objects").is_dir():
        return None
    commit = _git(repo, "rev-parse", "--verify", "--quiet", "refs/heads/memory/pr-triage")
    if not commit or not commit.strip():
        return None
    blob = _git(repo, "show", f"{commit.strip()}:pr-triage-latest.json")
    if blob is None:
        return None
    try:
        return json.loads(blob)
    except ValueError:
        return {"__unparseable__": blob[:200]}


def baseline_runs(repo: str) -> int:
    """How many commits the state branch carries: the baseline's only history."""
    log = _git(repo, "rev-list", "--count", "refs/heads/memory/pr-triage")
    try:
        return int((log or "0").strip() or 0)
    except ValueError:
        return 0


# ─── Durable: the JSONL store ───────────────────────────────────────────────────────────────────


def durable_records(store: str) -> list[dict]:
    file = pathlib.Path(store)
    if not file.exists():
        return []
    out = []
    for line in file.read_text(errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            out.append(json.loads(line))
        except ValueError:
            # A torn tail is an observation, not a reader's error: the caller counts readable records.
            out.append({"type": "__unparseable__"})
    return out


def durable_run(store: str) -> dict | None:
    latest = None
    for record in durable_records(store):
        if record.get("type") != "document-version":
            continue
        payload = record.get("payload") or {}
        if payload.get("kind") == RUN_KIND:
            latest = payload.get("value") or {}
    return latest


def durable_ledger(store: str) -> list[dict]:
    latest = None
    for record in durable_records(store):
        if record.get("type") != "document-version":
            continue
        payload = record.get("payload") or {}
        if payload.get("kind") == "band-c.comments":
            latest = payload.get("value") or {}
    return list((latest or {}).get("comments") or [])


def durable_task(store: str) -> dict | None:
    states = [
        record.get("payload") or {}
        for record in durable_records(store)
        if record.get("type") == "task"
        and str((record.get("payload") or {}).get("name", "")).startswith("band-c.")
    ]
    return states[-1] if states else None


# ─── The Phase 6 effect: one file per commented pull request ─────────────────────────────────────


def comment_numbers(output_dir: pathlib.Path) -> list[int]:
    directory = pathlib.Path(output_dir, "comments")
    if not directory.is_dir():
        return []
    numbers = []
    for path in directory.glob("pr-*.md"):
        try:
            numbers.append(int(path.stem.split("-", 1)[1]))
        except (IndexError, ValueError):
            continue
    return sorted(numbers)


def wrote(log_path: str) -> list[int]:
    """The PRs a run says it posted a comment for, in the order it posted them."""
    file = pathlib.Path(log_path)
    if not file.exists():
        return []
    # Matched by regex, not by a literal prefix: the column width is a formatting detail of the
    # program under test, and a hard-coded run of spaces silently matched nothing when it changed.
    # The size suffix is optional: the durable variant prints `(448 bytes)` and the baseline prints the
    # path alone. Requiring it matched the baseline's whole log as zero comments, which is the same
    # silent-zero failure as a wrong run of spaces — so it is optional and the PR number is what matters.
    pattern = re.compile(r"^\s*wrote:\s+comments/pr-(\d+)\.md\b")
    numbers = []
    for line in file.read_text(errors="replace").splitlines():
        match = pattern.match(line)
        if match:
            numbers.append(int(match.group(1)))
    return numbers


def snapshot(*directories) -> dict[str, str]:
    """A digest per posted comment, so a later write can be compared byte for byte.

    Known limits of this measure, stated because a duplicate count that quietly misses cases is worse
    than none:

    * Only *file* comments are measured. A path that is a directory is a killed process's residue —
      the open and the create are two syscalls, so a `kill -9` between them can leave a directory where
      the file would be — and it is skipped, not counted.
    * More than one directory may be given; later ones win on a collision. The baseline's two runs write
      to two directories and a resume rewrites its original one, so the union is "what the repository
      already had".
    """
    import hashlib

    out: dict[str, str] = {}
    for directory in directories:
        comments = pathlib.Path(directory, "comments")
        if not comments.is_dir():
            continue
        for path in sorted(comments.glob("pr-*.md")):
            if not path.is_file():
                continue
            try:
                number = int(path.stem.split("-", 1)[1])
            except (IndexError, ValueError):
                continue
            out[str(number)] = hashlib.sha256(path.read_bytes()).hexdigest()
    return out


def duplicates(before_path: str, log_path: str, *directories) -> list[int]:
    """PRs a run wrote whose identical bytes were already on disk.

    This is the "the same comment posted twice" measure, and it is deliberately stricter than a count of
    files: a file is written once per PR and adopted files are rewritten with the same content, so a
    file count cannot see a duplicate. What it asks is "did this run *write* pr-N, and is what it wrote
    exactly what pr-N already held?" — which is true only when the comment was posted twice.
    """
    import json

    before = json.loads(pathlib.Path(before_path).read_text()) if pathlib.Path(before_path).exists() else {}
    after = snapshot(*[pathlib.Path(item) for item in directories])
    written = set(wrote(log_path))
    return sorted(n for n in written if str(n) in before and after.get(str(n)) == before[str(n)])


# ─── One vocabulary over both ───────────────────────────────────────────────────────────────────


def committed(variant: str, path: str) -> dict | None:
    if variant == "baseline":
        return baseline_state(path)
    if variant == "durable":
        return durable_run(path)
    raise SystemExit(f"unknown variant '{variant}'")


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(f"usage: {argv[0]} <state|triaged|runs|exists|ledger|task|comments> ...", file=sys.stderr)
        return 2

    command = argv[1]

    if command == "wrote":
        if len(argv) < 3:
            print("usage: store.py wrote <run-log>", file=sys.stderr)
            return 2
        numbers = wrote(argv[2])
        print(",".join(str(n) for n in numbers))
        return 0

    if command == "overlap":
        if len(argv) < 4:
            print("usage: store.py overlap <before-dir> <after-dir>", file=sys.stderr)
            return 2
        before = comment_numbers(pathlib.Path(argv[2]))
        after = comment_numbers(pathlib.Path(argv[3]))
        shared = [n for n in after if n in before]
        print(",".join(str(n) for n in shared))
        return 0

    if command == "snapshot":
        if len(argv) < 4:
            print("usage: store.py snapshot <dir>... <out.json>", file=sys.stderr)
            return 2
        import json

        merged = snapshot(*[pathlib.Path(item) for item in argv[2:-1]])
        pathlib.Path(argv[-1]).write_text(json.dumps(merged, indent=1) + "\n")
        return 0

    if command == "duplicates":
        if len(argv) < 5:
            print("usage: store.py duplicates <snapshot.json> <run-log> <comments-dir>...", file=sys.stderr)
            return 2
        for number in duplicates(argv[2], argv[3], *[pathlib.Path(item) for item in argv[4:]]):
            print(number)
        return 0

    if command == "comments":
        if len(argv) < 3:
            print("usage: store.py comments <run-output-dir>", file=sys.stderr)
            return 2
        directory = pathlib.Path(argv[2], "comments")
        print(len(list(directory.glob("*.md"))) if directory.is_dir() else 0)
        return 0

    if len(argv) < 3:
        print(f"usage: {argv[0]} {command} <baseline|durable> <path> [field]", file=sys.stderr)
        return 2

    if command in ("ledger", "task"):
        if len(argv) < 3:
            print(f"usage: {argv[0]} {command} <store>", file=sys.stderr)
            return 2
        path = argv[2]
        if command == "ledger":
            for entry in durable_ledger(path):
                print(f"#{entry.get('number')} postedAt={entry.get('postedAt')} bytes={entry.get('bytes')}")
            return 0
        task = durable_task(path)
        if task is None:
            print("(no task record)")
            return 0
        checkpoint = task.get("checkpoint") or {}
        print(f"id={task.get('id')} status={task.get('status')} phase={checkpoint.get('phase')} "
              f"attempts={checkpoint.get('attempts')} commented={len(checkpoint.get('commented') or [])} "
              f"memos={len(task.get('memos') or {})} outcome={(task.get('outcome') or {}).get('status')}")
        return 0

    if len(argv) < 4:
        print(f"usage: {argv[0]} {command} <baseline|durable> <path> [field]", file=sys.stderr)
        return 2

    variant, path = argv[2], argv[3]
    state = committed(variant, path)

    if command == "exists":
        print("yes" if state is not None else "no")
        return 0

    if command == "runs":
        if variant == "baseline":
            print(baseline_runs(path))
        else:
            print(int((state or {}).get("generation") or 0))
        return 0

    if state is None:
        print("")
        return 0

    if command == "triaged":
        numbers = []
        if variant == "baseline":
            for pr in (state.get("selectedCandidates") or []):
                if isinstance(pr, dict) and "number" in pr:
                    numbers.append(pr["number"])
                elif isinstance(pr, int):
                    numbers.append(pr)
        elif variant == "durable":
            for pr in (state.get("commented") or []):
                numbers.append(pr)
        else:
            raise SystemExit(f"unknown variant '{variant}'")
        print(",".join(str(n) for n in numbers))
        return 0

    if command == "state":
        field = argv[4] if len(argv) > 4 else ""
        if variant == "baseline":
            # The baseline's blob has no phase field: the run either wrote the whole snapshot at the
            # end of Phase 8 or wrote nothing, so the closest honest answer is the run's own record
            # being present, which only happens when the run finished.
            if field == "phase":
                print("done (the state snapshot only exists once Phase 8 has committed)")
                return 0
            field = {
                "startedAt": "run.startedAt",
                "triaged": "summary.triaged",
                "inCooldown": "summary.inCooldown",
                "repository": "run.repository",
                "commented": "summary.triaged",
            }.get(field, field)
        value = state
        for part in [p for p in field.split(".") if p]:
            if isinstance(value, list):
                try:
                    value = value[int(part)]
                except (ValueError, IndexError):
                    value = None
            elif isinstance(value, dict):
                value = value.get(part)
            else:
                value = None
        print("" if value is None else value)
        return 0

    print(f"unknown command '{command}'", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
