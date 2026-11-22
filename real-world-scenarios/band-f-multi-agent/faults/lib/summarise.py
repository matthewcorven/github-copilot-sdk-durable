"""Renders the Band F fault matrix from the result files, and fails when a claim has no result.

The table in the band README is the only place this band's numbers come from. It is generated rather
than typed so a re-run cannot leave the README describing a result that no longer exists, and it
fails (non-zero) when a fault is missing a result for a variant: a missing file is indistinguishable
from a fault nobody ran.

Every fault row carries the post-recovery holder, because that is the assertion the issue makes: a
result that only says "the run finished" is not evidence for this band.
"""

import json
import os
import sys

FAULTS = [
    "kill-mid-delegation",
    "restart-after-checkpoint",
    "duplicate-delivery",
    "competing-managers",
    "store-outage",
]
VARIANTS = ["baseline", "durable"]


def load(results_dir, variant, fault):
    path = os.path.join(results_dir, variant, fault + ".json")
    try:
        return json.load(open(path, encoding="utf-8"))
    except (OSError, ValueError):
        return None


def short(value, limit=110):
    value = " ".join(str(value or "").split())
    value = value.replace("|", "\\|")
    return value if len(value) <= limit else value[: limit - 1] + "…"


def holder(doc):
    return doc.get("holder") or "—"


def control_name(fault):
    return fault + "-control"


def main():
    results_dir, status_file = sys.argv[1:3]
    problems = []

    print("## Fault matrix — outcome, and who held control after recovery")
    print()
    print("| Fault | Variant | Outcome | Holder after recovery |")
    print("| --- | --- | --- | --- |")
    for fault in FAULTS:
        for variant in VARIANTS:
            doc = load(results_dir, variant, fault)
            if doc is None:
                problems.append("%s / %s: no result file" % (variant, fault))
                print("| `%s` | %s | **missing** | |" % (fault, variant))
                continue
            print("| `%s` | %s | **%s** | `%s` |" % (fault, variant, doc.get("outcome"), holder(doc)))
    print()

    print("## What each result observed")
    print()
    for fault in FAULTS:
        print("### `%s`" % fault)
        print()
        print("| Variant | Outcome | Holder | Detail |")
        print("| --- | --- | --- | --- |")
        for variant in VARIANTS:
            doc = load(results_dir, variant, fault)
            if doc is None:
                print("| %s | **missing** | | |" % variant)
                continue
            print(
                "| %s | **%s** | `%s` | %s |"
                % (variant, doc.get("outcome"), holder(doc), short(doc.get("detail"), 400))
            )
        print()

    print("## Before/after controls")
    print()
    print("Each control is the same script with the injection removed. The fault and its control differ")
    print("only in whether the fault happened, so a difference between them is attributable to it. A")
    print("control that reaches the same outcome as its fault would prove nothing, and is called out.")
    print()
    print("| Fault | Variant | Control outcome | Control holder | Fault outcome | Fault holder | Holder at injection: control / fault |")
    print("| --- | --- | --- | --- | --- | --- | --- |")
    for fault in FAULTS:
        for variant in VARIANTS:
            doc = load(results_dir, variant, fault)
            control = load(results_dir, variant, control_name(fault))
            if doc is None or control is None:
                problems.append("%s / %s: no control result file" % (variant, fault))
                continue
            # A control proves nothing when it is indistinguishable from its fault: same outcome, same
            # post-recovery holder, and the same state at the injection point. Comparing outcomes alone
            # would flag a pair that differs exactly in the commit the fault stopped after, which is the
            # variable `restart-after-checkpoint` is built on.
            same = (
                doc.get("outcome") == control.get("outcome")
                and doc.get("holder") == control.get("holder")
                and (doc.get("holderAtFault") or "") == (control.get("holderAtFault") or "")
            )
            print(
                "| `%s` | %s | %s | `%s` | %s | `%s`%s | control: %s / fault: %s |"
                % (
                    fault,
                    variant,
                    control.get("outcome"),
                    holder(control),
                    doc.get("outcome"),
                    holder(doc),
                    " **(indistinguishable — proves nothing)**" if same else "",
                    control.get("holderAtFault") or "—",
                    doc.get("holderAtFault") or "—",
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
                print("- `%s` / `%s`: %s" % (variant, fault, short(doc.get("detail"), 600)))
    if not any_missing:
        print("- none")
    print()

    print("## Produced at")
    print()
    commits = {}
    for variant in VARIANTS:
        for fault in FAULTS:
            doc = load(results_dir, variant, fault)
            if doc:
                commit = (doc.get("harness") or {}).get("commit") or "?"
                commits.setdefault(commit, []).append("%s/%s" % (variant, fault))
    for commit, names in commits.items():
        print("- `%s` — %d result(s)" % (commit, len(names)))
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
