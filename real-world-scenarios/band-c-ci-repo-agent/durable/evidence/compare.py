"""Compare the two variants' outputs, and prove the shared code has not drifted.

Two checks, both against artefacts rather than against a claim:

1. `--outputs` diffs the four output documents one baseline run and one durable run produced from
   the same fixture and the same clock. This is requirement 1 of the issue: "both variants produce
   the same comments for the same fixture".

2. `--code` re-reads both programs and compares the phase-2-to-phase-7 functions a line at a time.
   The claim "the triage logic is the baseline's, moved" is only worth anything if it is checked;
   this is the check, and it fails if the durable variant's copy is edited.

Usage:  python3 compare.py --outputs <baseline-run-dir> <durable-run-dir>
        python3 compare.py --code
"""

from __future__ import annotations

import difflib
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
DURABLE = HERE.parent
BAND = DURABLE.parent
BASELINE = BAND / "baseline" / "PrTriageBaseline.cs"
TRIAGE = DURABLE / "Triage.cs"

# The functions that must be identical, in both files, with the marker that ends each run of them.
SECTIONS = [
    ("classification, risk, scoring, recommendation",
     "static string Classify(PullRequest pr)", "// ─── Batching and labels"),
    ("batching and labels",
     "static List<Batch> Batch(List<Assessment> assessments)", "// ─── Output documents"),
    ("comment and report",
     "static string Comment(Assessment a)", "// ─── State payload"),
]

OUTPUTS = ["comments", "labels.json", "triage-report.md", "triage-data.json"]


def normalise(block: str) -> list[str]:
    """A block's significant lines, with the class-body indent and access modifiers removed.

    Blank lines are dropped and the four-space indent of a method inside a class is removed, so the
    comparison is about what the code does. `static` becomes `public static` in the durable variant
    because the method is called from another type, and that is the *only* difference this
    normalisation hides — it is stated here rather than left for a reader to discover. Nothing else
    is normalised: a changed identifier, a reordered condition or a different constant all show up,
    and `--code` fails on them.
    """
    lines = []
    for line in block.split("\n"):
        if not line.strip():
            continue
        if line.startswith("    "):
            line = line[4:]
        if line.startswith("public static "):
            line = "static " + line[len("public static "):]
        lines.append(line)
    return lines


def section(text: str, start: str, end: str) -> list[str]:
    index = text.index(start)
    stop = text.index(end, index)
    return normalise(text[index:stop])


def compare_code() -> int:
    baseline_text = BASELINE.read_text()
    triage_text = TRIAGE.read_text()
    failures = 0

    for name, start, end in SECTIONS:
        # `Report` ends at the state-payload banner in the baseline and at the paths banner in the
        # durable variant, because BuildState does not exist there.
        if start.startswith("static string Comment"):
            baseline_stop = end
            durable_stop = "// ─── Paths"
        else:
            baseline_stop = durable_stop = end

        left = section(baseline_text, start, baseline_stop)
        right = section(triage_text, start, durable_stop)
        if left == right:
            print(f"identical  {name:<45} {len(left):>3} significant lines")
            continue

        failures += 1
        print(f"DIFFERENT  {name}")
        for line in difflib.unified_diff(left, right, "baseline", "durable", lineterm="", n=1):
            print(f"  {line}")

    return 1 if failures else 0


def compare_outputs(baseline_dir: str, durable_dir: str) -> int:
    left = pathlib.Path(baseline_dir)
    right = pathlib.Path(durable_dir)
    failures = 0

    for name in OUTPUTS:
        a, b = left / name, right / name
        if not a.exists() or not b.exists():
            print(f"MISSING   {name}: baseline={a.exists()} durable={b.exists()}")
            failures += 1
            continue

        if a.is_dir():
            names_a = sorted(path.name for path in a.glob("*.md"))
            names_b = sorted(path.name for path in b.glob("*.md"))
            if names_a != names_b:
                print(f"DIFFERENT {name}/: {len(names_a)} vs {len(names_b)} file(s)")
                failures += 1
                continue

            changed = [n for n in names_a if (a / n).read_bytes() != (b / n).read_bytes()]
            if changed:
                print(f"DIFFERENT {name}/: {len(changed)} of {len(names_a)} file(s) differ: "
                      + ", ".join(changed))
                failures += 1
            else:
                print(f"identical  {name}/ ({len(names_a)} files)")
            continue

        if a.read_bytes() == b.read_bytes():
            print(f"identical  {name} ({a.stat().st_size} bytes)")
        else:
            print(f"DIFFERENT  {name}")
            failures += 1

    return 1 if failures else 0


def main(argv: list[str]) -> int:
    if "--code" in argv:
        return compare_code()
    if "--outputs" in argv:
        rest = [a for a in argv[1:] if not a.startswith("--")]
        if len(rest) != 2:
            print("usage: compare.py --outputs <baseline-run-dir> <durable-run-dir>", file=sys.stderr)
            return 2
        return compare_outputs(rest[0], rest[1])
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
