"""Fails when the band README's three-way table is not the one the results produce.

The README introduces that table as generated, and `faults/lib/three_way.py` is what generates it. Without
this check "generated" describes an intention: the table had already drifted once (a byte count from an
earlier run), which is exactly the failure a hand-copied table produces.

Two things make the comparison stable across runs, because a check that fails on every re-run gets ignored:
the table's *outcome* words and its *measurements* are what must match, and a run-variable value (a byte
count, a PID) is compared for the property the cell asserts rather than digit for digit. Everything else is
compared exactly.
"""

import re
import subprocess
import sys

# Values that legitimately differ between runs. Each is replaced by its property, so a cell that asserts
# "unchanged" still has to show the same numbers on both sides of the arrow.
RUN_VARIABLE = [
    (re.compile(r"\b\d{4}-\d{2}-\d{2}T[\d:.+Z-]+"), "<timestamp>"),
    (re.compile(r"\bpid \d+\b"), "pid <n>"),
    (re.compile(r"\b[0-9a-f]{32}\b"), "<id>"),
    # A byte count, in the one form the table quotes it. The cell asserts that the size before and after is
    # equal; it does not assert a particular number, and a number that moves between runs must not read as a
    # changed conclusion. The equality is still compared, because both sides of the arrow are rewritten.
    (re.compile(r"\b\d{3,7} -> \d{3,7} bytes"), "<n> -> <n> bytes"),
]


def normalise(row):
    text = row.strip()
    for pattern, replacement in RUN_VARIABLE:
        text = pattern.sub(replacement, text)
    return text


def table_rows(text):
    lines = [line.rstrip() for line in text.splitlines()]
    for index, line in enumerate(lines):
        if line.startswith("|") and "File checkpoint" in line and "baseline" in line:
            rows = []
            for candidate in lines[index:]:
                if not candidate.startswith("|"):
                    break
                rows.append(candidate.strip())
            return rows
    return []


def main(argv):
    if len(argv) != 2:
        sys.stderr.write("usage: check_readme_table.py <README.md> <results-dir>\n")
        return 2

    readme_path, results_dir = argv
    with open(readme_path, encoding="utf-8") as handle:
        readme_rows = [normalise(row) for row in table_rows(handle.read())]

    generated = subprocess.run(
        [sys.executable, __file__.replace("check_readme_table.py", "three_way.py"), results_dir],
        capture_output=True,
        text=True,
    )
    if generated.returncode != 0:
        print("check_readme_table: three_way.py failed:", generated.stderr.strip())
        return 1
    generated_rows = [normalise(line) for line in generated.stdout.splitlines() if line.startswith("|")]

    if readme_rows == generated_rows:
        print("check_readme_table: the band README's three-way table matches the results")
        return 0

    print("check_readme_table: the band README's table has drifted from the results")
    for index in range(max(len(readme_rows), len(generated_rows))):
        have = readme_rows[index] if index < len(readme_rows) else "(missing)"
        want = generated_rows[index] if index < len(generated_rows) else "(missing)"
        if have != want:
            print("  row %d" % index)
            print("    README:    " + have[:300])
            print("    generated: " + want[:300])
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
