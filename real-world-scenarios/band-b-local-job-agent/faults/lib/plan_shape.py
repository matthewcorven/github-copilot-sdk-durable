"""Reports whether a `PLAN.md` is still a plan after two processes wrote to it.

The template's plan has exactly one `Status:` line and a checklist whose items are `- [ ]` or `- [x]`.
A file that two writers interleaved can be many things; this says which of them it is, without
attempting to repair it.

    plan_shape.py <PLAN.md>

Prints `missing`, `intact`, or `damaged(bad_lines=N,status_lines=M)`.
"""

import sys


def main():
    try:
        lines = open(sys.argv[1], encoding="utf-8").read().split("\n")
    except OSError:
        print("missing")
        return 0
    bad = [
        line
        for line in lines
        if line.startswith("- [") and not (line.startswith("- [ ]") or line.startswith("- [x]"))
    ]
    statuses = sum(1 for line in lines if line.startswith("Status:"))
    if bad or statuses != 1:
        print("damaged(bad_lines=%d,status_lines=%d)" % (len(bad), statuses))
    else:
        print("intact")
    return 0


if __name__ == "__main__":
    sys.exit(main())
