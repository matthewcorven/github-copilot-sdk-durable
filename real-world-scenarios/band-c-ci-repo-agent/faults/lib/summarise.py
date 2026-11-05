#!/usr/bin/env python3
"""Render the Band C fault matrix from the result files, and fail if a claim has no result.

Same shape as Band A's `lib/summarise.py`, with two Band C additions:

  * the sixth fault, `cooldown-desync`, whose result carries an `observations` object (the duplicate
    counts) — rendered as its own table, because a single outcome word cannot carry "thirteen PRs got
    two comments";
  * a stricter missing-result check: Band C runs every fault against both variants and every fault
    has a control, so a missing control is a missing result too.

The table the band README quotes is generated from the checked-in JSON and nothing else, so a re-run
cannot leave the README describing a result that no longer exists.
"""

from __future__ import annotations

import datetime
import json
import os
import sys

FAULTS = [
    "kill-mid-turn",
    "restart-after-checkpoint",
    "duplicate-delivery",
    "concurrent-clients",
    "store-outage",
    "cooldown-desync",
]
VARIANTS = ["baseline", "durable"]


def control_name(fault: str) -> str:
    return fault + "-not-injected" if fault == "concurrent-clients" else fault + "-control"


def load(results_dir: str, variant: str, name: str):
    path = os.path.join(results_dir, variant, name + ".json")
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def run_started(status_file: str):
    """When the suite that produced the status file started.

    `run-all.sh` writes `started <iso8601>` as the status file's first line, and that is the reference. A
    result whose `recordedAt` predates it is not from this run: it is a left-over the script has not
    replaced yet, which would otherwise read as a full matrix row describing a run that did not happen.
    Without the line (a hand-run single script, or an older status file) the file's mtime is used, which
    is weaker but never *later* than the results it precedes.
    """
    try:
        with open(status_file, encoding="utf-8") as handle:
            first = handle.readline().split()
        if len(first) == 2 and first[0] == "started":
            return datetime.datetime.fromisoformat(first[1])
    except (OSError, ValueError):
        pass
    try:
        return datetime.datetime.fromtimestamp(os.path.getmtime(status_file), datetime.timezone.utc)
    except OSError:
        return None


def stale(doc, started) -> str | None:
    if doc is None or started is None:
        return None
    stamp = doc.get("recordedAt")
    if not stamp:
        return "no recordedAt"
    try:
        recorded = datetime.datetime.fromisoformat(stamp)
    except ValueError:
        return "unreadable recordedAt %r" % stamp
    if recorded.tzinfo is None:
        recorded = recorded.replace(tzinfo=datetime.timezone.utc)
    if recorded < started:
        return "recorded %s, before this suite started at %s" % (stamp, started.isoformat())
    return None


def short(value, limit=110):
    value = (value or "").replace("|", "\\|").replace("\n", " ")
    return value if len(value) <= limit else value[: limit - 1] + "…"


def label(outcome: str | None) -> str:
    text = outcome or ""
    return text if text.startswith("control") else "control: " + text


def main() -> int:
    results_dir, status_file = sys.argv[1:3]
    problems: list[str] = []
    started = run_started(status_file)
    checked: dict[str, object] = {}

    def check(doc, variant: str, name: str):
        """A stale result is a problem, not a row: it describes a run that did not happen.

        The same document is read by several sections, so the problem is recorded once.
        """
        note = stale(doc, started)
        if note:
            problem = f"{variant} / {name}: stale result — {note}"
            if problem not in problems:
                problems.append(problem)
        return doc

    print("## Fault matrix — outcome per fault per variant")
    print()
    print("| Fault | baseline | durable |")
    print("| --- | --- | --- |")
    for fault in FAULTS:
        row = []
        for variant in VARIANTS:
            doc = check(load(results_dir, variant, fault), variant, fault)
            if doc is None:
                problems.append(f"{variant} / {fault}: no result file")
                row.append("**missing**")
            else:
                row.append("**%s**" % doc.get("outcome"))
        print(f"| `{fault}` | {row[0]} | {row[1]} |")
    print()

    print("## The headline: `cooldown-desync` in numbers")
    print()
    print("| Variant | run 2 comments | run 3 posted | run 3 adopted | run 3 duplicated (byte-level) | run 3 resumed | guard-disabled arm: posted / duplicated |")
    print("| --- | --- | --- | --- | --- | --- | --- |")
    for variant in VARIANTS:
        doc = check(load(results_dir, variant, "cooldown-desync"), variant, "cooldown-desync")
        if doc is None or "observations" not in doc:
            problems.append(f"{variant} / cooldown-desync: no observations")
            print(f"| {variant} | **missing** | | | | |")
            continue
        o = doc["observations"]
        print(
            "| %s | %d | %d | %d | %d | %s | %s |"
            % (
                variant,
                o.get("run2_comments_posted", 0),
                o.get("run3_comments_posted", 0),
                o.get("run3_comments_adopted", 0),
                o.get("run3_comments_duplicated", 0),
                "yes" if o.get("run3_resumed") else "no",
                "%d / %d" % (o.get("guard_disabled_posted", 0), o.get("guard_disabled_duplicated", 0))
                if o.get("guard_disabled_posted") or o.get("guard_disabled_duplicated")
                else "not run (baseline has no guard to disable)",
            )
        )
    print()
    for variant in VARIANTS:
        doc = check(load(results_dir, variant, "cooldown-desync"), variant, "cooldown-desync")
        if doc and doc.get("observations", {}).get("prs_doubled"):
            print(
                "- %s, PRs with two comments: %s"
                % (variant, ", ".join("#%d" % n for n in doc["observations"]["prs_doubled"]))
            )
    print()

    print("## What each result observed")
    print()
    for fault in FAULTS:
        print(f"### `{fault}`")
        print()
        print("| Variant | Outcome | Phase | Detail |")
        print("| --- | --- | --- | --- |")
        for variant in VARIANTS:
            doc = check(load(results_dir, variant, fault), variant, fault)
            if doc is None:
                print(f"| {variant} | **missing** | | |")
                continue
            print(
                "| %s | **%s** | %s | %s |"
                % (variant, doc.get("outcome"), short(doc.get("phase"), 80), (doc.get("detail") or "").replace("|", "\\|"))
            )
        print()

    print("## Before/after controls")
    print()
    print("Each control runs the same steps with the injection removed. The fault and its control differ only")
    print("in whether the fault happened, so a difference between them is attributable to it.")
    print()
    print("| Fault | Variant | Control outcome | Fault outcome | Control phase | Fault phase |")
    print("| --- | --- | --- | --- | --- | --- |")
    for fault in FAULTS:
        for variant in VARIANTS:
            doc = check(load(results_dir, variant, fault), variant, fault)
            control = check(load(results_dir, variant, control_name(fault)), variant, control_name(fault))
            if control is None:
                problems.append(f"{variant} / {control_name(fault)}: no control result file")
            if doc is None or control is None:
                continue
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

    print("## Harness status")
    print()
    if os.path.exists(status_file):
        for line in open(status_file, encoding="utf-8"):
            parts = line.split()
            if parts[:1] == ["started"]:
                continue
            if len(parts) == 3:
                variant, name, status = parts
                print("- `%s` %s: exit %s%s" % (variant, name, status, "" if status == "0" else " — **harness error**"))
                if status != "0":
                    problems.append(f"{variant} / {name}: harness exited {status}")
    print()

    print("## Not exercised, and why")
    print()
    any_missing = False
    for variant in VARIANTS:
        for fault in FAULTS:
            doc = check(load(results_dir, variant, fault), variant, fault)
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
