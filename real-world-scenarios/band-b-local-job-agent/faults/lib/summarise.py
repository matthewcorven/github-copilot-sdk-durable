"""Renders the fault matrix from the result files, and checks that every claim has one.

The table is the only place the band README's numbers come from. It is generated rather than typed so a
re-run cannot leave the README describing a result that no longer exists, and it exits non-zero on any of
four things:

  * a variant has no result for a fault — a missing file is indistinguishable from a fault nobody ran;
  * a result's `harness.script` is not the script that owns that fault — stale or hand-edited files would
    otherwise render as evidence;
  * a result's `harness.commit` is not the checked-out commit — the numbers would describe different code
    from the one being read;
  * a fault's outcome is one the vocabulary does not define — a sentence invented at the point of writing
    is how a result ends up contradicting its own evidence.

Every number printed here is read from a result file. The prose is fixed text.
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
EXTRA = ["run-equivalence"]

# Every outcome word a result may use. A word not here is a result that has invented a claim, and it
# fails rather than rendering. Controls are prefixed `control` and are never read as the fault's.
OUTCOMES = {
    "completed",
    "resumed and finished",
    "resumed but did not finish",
    "resumed after the kill",
    "refused to resume the in-flight iteration",
    "refused to resume the dead turn",
    "stopped inside an iteration's turn",
    "restarted from the plan",
    "lost its position",
    "doubled",
    "not doubled",
    "refused the second client",
    "not corrupted",
    "corrupted",
    "failed safely",
    "failed safely, without advancing",
    "continued past the outage",
    "failed unsafely",
    "identical",
    "differs",
    "not comparable: two clean runs differed",
    "not comparable: a second pass has nothing left to do",
    "not resumed: the interruption landed in the in-flight turn",
    "comparator failed its self-check",
    "control did not finish",
    "control: one delivery",
    "control: the store stayed",
    "control: serialised, not corrupted",
    "control: corrupted without overlap",
    "not exercised",
}


def owning_script(name):
    for fault in FAULTS + EXTRA:
        if name == fault or name == fault + "-control" or name == fault + "-not-injected":
            return fault + ".sh"
    return None


def load(results_dir, variant, fault):
    path = os.path.join(results_dir, variant, fault + ".json")
    try:
        return json.load(open(path, encoding="utf-8"))
    except (OSError, ValueError):
        return None


def short(value, limit=110):
    value = (value or "").replace("|", "\\|").replace("\n", " ")
    return value if len(value) <= limit else value[: limit - 1] + "…"


def label(outcome):
    text = outcome or ""
    return text if text.startswith("control") else text


def check(name, variant, doc, checkout):
    found = []
    expected = owning_script(name)
    recorded = (doc.get("harness") or {}).get("script") or ""
    if expected and recorded and recorded != expected:
        found.append(
            "%s / %s: harness.script is %r but the fault %r is owned by %r"
            % (variant, name, recorded, name, expected)
        )
    elif expected and not recorded:
        found.append("%s / %s: harness.script is missing; the result cannot be attributed to a script" % (variant, name))
    commit = (doc.get("harness") or {}).get("commit") or ""
    if commit and checkout and commit != checkout:
        found.append(
            "%s / %s: recorded at %s but the checkout is %s; the numbers describe different code"
            % (variant, name, commit[:12], checkout[:12])
        )
    outcome = doc.get("outcome") or ""
    if outcome not in OUTCOMES:
        found.append("%s / %s: outcome %r is not one of the defined outcomes" % (variant, name, outcome))
    return found


def main():
    results_dir, status_file = sys.argv[1:3]
    checkout = os.environ.get("FAULTS_COMMIT") or (sys.argv[3] if len(sys.argv) > 3 else "")
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
                problems.extend(check(fault, variant, doc, checkout))
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
                % (variant, doc.get("outcome"), short(doc.get("phase"), 80), (doc.get("detail") or "").replace("|", "\\|"))
            )
        print()

    print("## The before/after controls")
    print()
    print("Each control runs the same steps with the injection removed. The fault and its control differ only")
    print("in whether the fault happened, so a difference between them belongs to it.")
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
            problems.extend(check(name, variant, control, checkout))
            print(
                "| `%s` | %s | %s | %s | %s | %s |"
                % (
                    fault,
                    variant,
                    label(control.get("outcome")),
                    doc.get("outcome"),
                    short(control.get("phase"), 50),
                    short(doc.get("phase"), 50),
                )
            )
    print()

    print("## The extra evidence: a run that was interrupted and continued")
    print()
    print("| Variant | Outcome | Detail |")
    print("| --- | --- | --- |")
    for variant in VARIANTS:
        doc = load(results_dir, variant, "run-equivalence")
        if doc is None:
            problems.append("%s / run-equivalence: no result file" % variant)
            print("| %s | **missing** | |" % variant)
            continue
        problems.extend(check("run-equivalence", variant, doc, checkout))
        print("| %s | **%s** | %s |" % (variant, doc.get("outcome"), (doc.get("detail") or "").replace("|", "\\|")))
    print()

    print("## Provenance")
    print()
    if checkout:
        print("Checked out at `%s`; every result's `harness.commit` is compared against it." % checkout[:12])
    else:
        print("No checkout commit was supplied, so results were not checked for staleness.")
    print()

    print("## Harness status")
    print()
    if os.path.exists(status_file):
        for line in open(status_file, encoding="utf-8"):
            parts = line.split()
            if len(parts) == 3:
                variant, name, status = parts
                print("- `%s` %s: exit %s%s" % (variant, name, status, "" if status == "0" else " — **harness error**"))
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
