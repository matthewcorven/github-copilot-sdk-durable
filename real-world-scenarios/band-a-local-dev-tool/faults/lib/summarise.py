"""Renders the fault matrix from the result files, and checks that every claim has one.

The table is the only place the band README's numbers come from. It is generated rather than typed so a
re-run cannot leave the README describing a result that no longer exists, and it fails (non-zero) when a
fault is missing a result for a variant: a missing file is indistinguishable from a fault nobody ran.
"""

import glob
import json
import os
import sys

FAULTS = [
    "kill-mid-turn",
    "restart-after-checkpoint",
    "duplicate-delivery",
    "concurrent-clients",
    "store-outage",
]
VARIANTS = ["baseline", "durable"]


def load(results_dir, variant, fault):
    path = os.path.join(results_dir, variant, fault + ".json")
    try:
        return json.load(open(path, encoding="utf-8"))
    except (OSError, ValueError):
        return None


def short(value, limit=96):
    value = (value or "").replace("|", "\\|").replace("\n", " ")
    return value if len(value) <= limit else value[: limit - 1] + "…"


def label(name, outcome):
    """Controls are labelled in the table, so a control's outcome is never read as the fault's."""
    if name.endswith("-control") or name.endswith("-not-injected"):
        text = outcome or ""
        return text if text.startswith("control") else "control: " + text
    return outcome


def main():
    results_dir, status_file = sys.argv[1:3]
    problems = []

    print("## Fault matrix — outcome per fault per variant")
    print()
    print("| Fault | baseline | durable |")
    print("| --- | --- | --- |")
    for fault in FAULTS:
        row = []
        for variant in VARIANTS:
            doc = load(results_dir, variant, fault)
            if doc is None:
                problems.append("%s / %s: no result file" % (variant, fault))
                row.append("**missing**")
            else:
                row.append("**%s**" % (doc.get("outcome")))
        print("| `%s` | %s | %s |" % (fault, row[0], row[1]))
    print()

    print("## What each result observed")
    print()
    for fault in FAULTS:
        print("### `%s`" % fault)
        print()
        print("| Variant | Outcome | Phase | Detail |")
        print("| --- | --- | --- | --- |")
        for variant in VARIANTS:
            doc = load(results_dir, variant, fault)
            if doc is None:
                print("| %s | **missing** | | |" % variant)
                continue
            print(
                "| %s | **%s** | %s | %s |"
                % (
                    variant,
                    doc.get("outcome"),
                    short(doc.get("phase"), 80),
                    (doc.get("detail") or "").replace("|", "\\|"),
                )
            )
        print()

    print("## Before/after controls")
    print()
    print("Each control runs the same steps with the injection removed. The fault and its control are")
    print("different only in whether the fault happened, so a difference between them is attributable to it.")
    print()
    print("| Fault | Variant | Control outcome | Fault outcome | Control phase | Fault phase |")
    print("| --- | --- | --- | --- | --- | --- |")
    for fault in FAULTS:
        for variant in VARIANTS:
            doc = load(results_dir, variant, fault)
            control = None
            for name in (fault + "-control", fault + "-not-injected"):
                control = control or load(results_dir, variant, name)
            if doc is None or control is None:
                continue
            print(
                "| `%s` | %s | %s | %s | %s | %s |"
                % (
                    fault,
                    variant,
                    label(fault + "-control", control.get("outcome")),
                    doc.get("outcome"),
                    short(control.get("phase"), 50),
                    short(doc.get("phase"), 50),
                )
            )
    print()

    print("## Harness status")
    print()
    if os.path.exists(status_file):
        for line in open(status_file, encoding="utf-8"):
            parts = line.split()
            if len(parts) == 3:
                variant, name, status = parts
                print(
                    "- `%s` %s: exit %s%s"
                    % (variant, name, status, "" if status == "0" else " — **harness error**")
                )
    print()

    print("## Not exercised, and why")
    print()
    any_missing = False
    for variant in VARIANTS:
        for fault in FAULTS:
            doc = load(results_dir, variant, fault)
            if doc and doc.get("outcome") == "not exercised":
                any_missing = True
                print("- `%s` / `%s`: %s" % (variant, fault, doc.get("detail")))
    if not any_missing:
        print("- none")
    print()

    if problems:
        print("## Problems")
        print()
        for problem in problems:
            print("- " + problem)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
