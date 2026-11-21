#!/usr/bin/env python3
"""Render the Band D4 fault matrix from the checked-in results, and refuse a dishonest one.

Three things this file will not do, because a matrix that does any of them is worse than no
matrix:

1. **Render a column the run did not produce.** A missing result is `missing` in the table
   and a non-zero exit, not a blank cell a reader can skim past.
2. **Let `platform` and `application` blur.** The table has one column for what the platform
   did and one for what the application did, and each is read from its own block of the
   result. Neither is inferred from the other.
3. **Upgrade "documented" to "reproduced".** The platform rows are rendered from the results'
   own `platform.behaviours`, whose only legal statuses are `documented, not exercised here`
   and `reproduced here` — and `lib/result.py` refuses the latter without a live platform
   call, which no result in this directory has.

Usage: summarise.py RESULTS_DIR [--md]
"""

from __future__ import annotations

import datetime
import json
import os
import sys

FAULTS = [
    "client-dies-mid-run",
    "container-restart",
    "duplicate-delivery",
    "concurrent-clients",
    "store-outage",
    "agent-call-failure",
]
VARIANTS = ["baseline", "durable-inside", "durable-outside"]
CONTROL_SUFFIX = "-control"


def load(results_dir: str, variant: str, name: str):
    path = os.path.join(results_dir, variant, name + ".json")
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def short(text, limit=120) -> str:
    text = (text or "").replace("|", "\\|").replace("\n", " ")
    return text if len(text) <= limit else text[: limit - 1] + "…"


def plain(text) -> str:
    return str(text if text is not None else "").replace("|", "\\|").replace("\n", " ")


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:]
    markdown = "--md" in args
    args = [item for item in args if item != "--md"]
    results_dir = args[0]

    problems: list[str] = []
    table: dict[tuple[str, str], dict | None] = {}

    for fault in FAULTS:
        for variant in VARIANTS:
            doc = load(results_dir, variant, fault)
            table[(fault, variant)] = doc
            if doc is None:
                problems.append(f"{variant}/{fault}: no result file")

    def cell(fault: str, variant: str, key: str) -> str:
        doc = table.get((fault, variant))
        if doc is None:
            return "**missing**"
        return plain(doc.get(key) or "")

    out: list[str] = []
    add = out.append

    add("## The fault matrix — what the platform did, and what the application did")
    add("")
    add(
        "Every cell comes from a result file's own block: `platform` for the first column, "
        "`application` for the second. Nothing here is typed by hand, and no cell borrows the "
        "other's evidence."
    )
    add("")
    add("| Fault | Variant | Platform | Application |")
    add("| --- | --- | --- | --- |")
    for fault in FAULTS:
        for variant in VARIANTS:
            doc = table.get((fault, variant))
            if doc is None:
                add(f"| `{fault}` | {variant} | **missing** | **missing** |")
                continue
            platform = doc.get("platform") or {}
            live = "live Foundry: yes" if platform.get("live_foundry_called") else "live Foundry: **no**"
            add(
                f"| `{fault}` | {variant} | {live} — {short(platform.get('what_the_platform_did'), 90)} "
                f"| **{plain(doc.get('outcome'))}** |"
            )
    add("")

    add("## The four documented platform behaviours")
    add("")
    add(
        "Each result carries all four. No fault in this directory reached Foundry, so every row is "
        "`documented, not exercised here` — quoted from D1's captures and the documentation, never "
        "reported as reproduced."
    )
    add("")
    add("| Behaviour | Status | Source | Spoken to by |")
    add("| --- | --- | --- | --- |")
    behaviours: dict[str, dict] = {}
    spoken: dict[str, list[str]] = {}
    for fault in FAULTS:
        for variant in VARIANTS:
            doc = table.get((fault, variant))
            if doc is None:
                continue
            for entry in (doc.get("platform") or {}).get("behaviours") or []:
                behaviours.setdefault(entry["id"], entry)
                if entry.get("spoken_to_by_this_fault"):
                    spoken.setdefault(entry["id"], []).append(f"{variant}/{fault}")
    for behaviour_id in ["session-persistence", "idle-timeout", "foreground-cancelled", "no-replay"]:
        entry = behaviours.get(behaviour_id)
        if entry is None:
            problems.append(f"no result carries the platform behaviour '{behaviour_id}'")
            add(f"| `{behaviour_id}` | **missing** | | |")
            continue
        add(
            f"| `{behaviour_id}` | **{plain(entry.get('status'))}** | {short(entry.get('source'), 70)} "
            f"| {', '.join(spoken.get(behaviour_id, [])) or '—'} |"
        )
    add("")

    add("## What each row does and does not show")
    add("")
    for fault in FAULTS:
        add(f"### `{fault}`")
        add("")
        add("| Variant | Outcome | Detail |")
        add("| --- | --- | --- |")
        for variant in VARIANTS:
            doc = table.get((fault, variant))
            if doc is None:
                add(f"| {variant} | **missing** | |")
                continue
            add(
                f"| {variant} | **{plain(doc.get('outcome'))}** "
                f"| {short((doc.get('application') or {}).get('outcome_detail'), 400)} |"
            )
        add("")

    add("## Before/after controls")
    add("")
    add(
        "Each control runs the same steps with the injection removed, so a difference between the "
        "pair belongs to the fault. A control is named `control: …` in its own outcome so it is "
        "never read as the fault's."
    )
    add("")
    add("| Fault | Variant | Control outcome | Fault outcome |")
    add("| --- | --- | --- | --- |")
    controls_seen = 0
    controls_missing: list[str] = []
    for fault in FAULTS:
        for variant in VARIANTS:
            control = load(results_dir, variant, fault + CONTROL_SUFFIX)
            doc = table.get((fault, variant))
            if control is None:
                controls_missing.append(f"{variant}/{fault}{CONTROL_SUFFIX}")
                continue
            controls_seen += 1
            add(
                f"| `{fault}` | {variant} | {plain(control.get('outcome'))} "
                f"| {plain((doc or {}).get('outcome'))} |"
            )
    for name in controls_missing:
        add(f"| — | {name} | **missing** | |")
    add("")
    add(
        f"Controls present: **{controls_seen}**. A row without one says so rather than being "
        "quietly absent."
    )
    add("")

    add("## Not exercised, and why")
    add("")
    any_not_exercised = False
    for fault in FAULTS:
        for variant in VARIANTS:
            doc = table.get((fault, variant))
            if doc and str(doc.get("outcome", "")).startswith("not exercised"):
                any_not_exercised = True
                add(f"- `{variant}` / `{fault}`: {short((doc.get('application') or {}).get('outcome_detail'), 400)}")
    if not any_not_exercised:
        add("- none")
    add("")

    text = "\n".join(out)
    print(text)

    if problems:
        print("## Problems")
        print()
        for problem in problems:
            print("- " + problem)
        return 2
    if markdown:
        with open(os.path.join(results_dir, "summary.md"), "w", encoding="utf-8") as handle:
            handle.write(text + "\n")
    else:
        with open(os.path.join(results_dir, "summary.txt"), "w", encoding="utf-8") as handle:
            handle.write(text + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
