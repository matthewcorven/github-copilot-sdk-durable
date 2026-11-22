"""Stages a target for the fault harness: the band's template, with more of the same defect.

The band's template is twelve checkable items, and on this machine one pass of the baseline's
unbounded prompt finishes all twelve. A fault about *iteration boundaries* then has nothing to stop at
on the baseline: there is no second iteration. This script grows the same job — sections 11..N with the
same missing H1, and a plan line for each — so both variants have several iterations to be faulted
between.

It is not a change to either variant and not a change to `baseline/target/`. The added files carry the
only defect the template's own sections carry (`docs/section-NN.md` has no H1), and each gets a plan
line in the template's wording, so `baseline/verify.sh` covers them without being modified: it asserts an
H1 in every `docs/section-*.md` and that no plan item is open.

    stage.py <target-dir> <total-sections>

Existing files are left exactly as the template wrote them.
"""

import os
import pathlib
import sys

BODY = "Content for section {n}, taken from the notes.\n"


def main():
    if len(sys.argv) != 3:
        print("usage: stage.py <target-dir> <total-sections>", file=sys.stderr)
        return 2
    target = pathlib.Path(sys.argv[1])
    total = int(sys.argv[2])
    docs = target / "docs"
    if not (docs / "section-01.md").exists():
        print("stage.py: %s is not a band B target (no docs/section-01.md)" % target, file=sys.stderr)
        return 2

    plan_path = target / "PLAN.md"
    plan = plan_path.read_text()
    added = []
    for n in range(11, total + 1):
        path = docs / ("section-%02d.md" % n)
        if path.exists():
            continue
        # No H1: exactly the defect sections 01-10 carry.
        path.write_text(BODY.format(n=n))
        added.append("- [ ] `docs/section-%02d.md`: add the missing H1" % n)

    if added:
        if not plan.endswith("\n"):
            plan += "\n"
        plan += "\n".join(added) + "\n"
        plan_path.write_text(plan)

    print("staged: %d section(s) added, %d plan item(s) added, target %s"
          % (len(added), len(added), target))
    return 0


if __name__ == "__main__":
    sys.exit(main())
