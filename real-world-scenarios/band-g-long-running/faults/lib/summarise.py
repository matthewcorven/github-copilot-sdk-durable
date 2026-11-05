"""Renders the Band G fault matrix from the result files, and fails when a result is missing.

The tables in `faults/README.md` and in the PR body are generated from `results/*.json`, never typed. That
is what makes "the checked-in results are what the scripts emit" checkable: re-running the harness
regenerates the same two files, and a fault with no result is a non-zero exit rather than a gap a reader
has to notice.

Two renderings, one function: plain text (the default) and Markdown (`--md`). Both list every fault for
every variant, every control, every model-call count, and every `not exercised` with its reason.
"""

import json
import os
import sys

FAULTS = [
    ("kill-during-wait", "whether the wait survives"),
    ("restart-after-checkpoint", "resume is exact, model calls not repeated"),
    ("duplicate-approval", "the decision is applied once"),
    ("late-approval", "the terminal state is not overwritten"),
    ("abort-mid-wait", "children cancelled, state final"),
    ("store-outage", "no half-committed decision"),
]
VARIANTS = ["baseline", "durable"]


def load(results_dir, variant, name):
    path = os.path.join(results_dir, variant, name + ".json")
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def esc(value, limit=None):
    text = (value or "").replace("|", "\\|").replace("\n", " ").strip()
    if limit and len(text) > limit:
        return text[: limit - 1] + "…"
    return text


# Every measurement a result may carry, with the label it is printed under. The order is the order a
# reader needs: the process that was faulted, what the next process cost, and what the run ended with.
LABELS = [
    ("model_calls_at_gate", "calls at the gate"),
    ("model_calls_process_1", "calls, process 1"),
    ("model_calls_held_process", "calls, faulted process"),
    ("model_calls_before_kill", "calls before the kill"),
    ("model_calls_ontime", "calls, on-time process"),
    ("model_calls_drive", "calls, process blocked by the fault"),
    ("model_calls_first", "calls, first delivery"),
    ("model_calls_second", "calls, second delivery"),
    ("model_calls_process_2", "calls, process 2"),
    ("model_calls_second_process", "calls, second process"),
    ("model_calls_stopped_process", "calls, stopped process"),
    ("model_calls_after_resume", "calls, resume process"),
    ("model_calls_late", "calls, late process"),
    ("model_calls_after", "calls after"),
    ("model_calls_after_deadline", "calls after the deadline"),
    ("model_calls_after_outage", "calls after the outage"),
    ("model_calls_after_abort", "calls after the abort"),
    ("model_calls_after_finish", "calls after finishing"),
    ("model_calls_end", "calls, end"),
    ("calls_added_by_resume", "calls added by the resume"),
    ("memo_hits", "memo hits"),
    ("gate_window_seconds", "gate window (s)"),
    ("gate_verdict_after_deadline", "gate verdict after the deadline"),
    ("gate_verdict_after_abort", "gate verdict after the abort"),
    ("workflow_verdict_after_abort", "workflow verdict after the abort"),
    ("children_observed", "children, as the abort handler saw them"),
    ("child_terminal_when_parent_concluded", "child already terminal"),
    ("decision_after_first", "decision after the first delivery"),
    ("decision_after_second", "decision after the second delivery"),
    ("decision_after_late", "decision after the late approval"),
    ("decision_after_abort", "decision after the abort"),
    ("decision_after_outage", "decision after the outage"),
    ("decision_ontime", "decision, on time"),
    ("late_approval_note", "the late approval was told"),
    ("second_refused_by_terminal_guard", "refused by the terminal guard"),
    ("second_delivery_ran", "second delivery approved"),
    ("late_approved_again", "late approval re-ran the run"),
    ("run_after_late", "run after the late approval"),
    ("run_after_abort", "run after the abort"),
    ("run_after_outage", "run after the outage"),
    ("run_after_ontime", "run after the on-time approval"),
    ("run_after_finish", "run at the end"),
    ("run_final", "run, final"),
    ("verdict_after_late", "verdict after the late approval"),
    ("verdict_after_outage", "verdict after the outage"),
    ("verdict_ontime", "verdict, on time"),
    ("verdict", "verdict"),
    ("outcome_after_late", "outcome after the late approval"),
    ("drive_exit", "exit, faulted process"),
    ("ontime_exit", "exit, on-time process"),
    ("late_exit", "exit, late process"),
    ("resume_exit", "exit, resume process"),
    ("fresh_inspect_exit", "exit, fresh inspect"),
    ("checkpoints_at_gate", "checkpoints at the gate"),
    ("checkpoints_after", "checkpoints after"),
    ("checkpoints_after_first", "checkpoints after the first delivery"),
    ("checkpoints_after_second", "checkpoints after the second delivery"),
    ("checkpoints_after_ontime", "checkpoints after the on-time run"),
    ("post_approval_checkpoint_after", "post-approval checkpoint after"),
    ("gate_checkpoint_after", "gate checkpoint after"),
    ("gate_reapproved_after_outage", "gate re-approved after the outage"),
    ("store_bytes_before", "store bytes before"),
    ("store_bytes_after", "store bytes after"),
    ("reason_checked_by", "reason checked by"),
    ("steps_after_resume", "steps in the ledger after the resume"),
    ("model_calls_after_approve", "calls after the approval"),
]


def calls(doc):
    """The measurements a result carries, as the phrase the table prints. The raw JSON is the source."""
    measurements = (doc or {}).get("measurements") or {}
    parts = []
    for key, label in LABELS:
        if key not in measurements:
            continue
        value = measurements[key]
        if value is None or value == "":
            continue
        parts.append("%s: %s" % (label, value))
    for key, value in measurements.items():
        if key not in dict(LABELS) and value not in (None, ""):
            parts.append("%s: %s" % (key, value))
    return "; ".join(parts) if parts else "—"


def render(results_dir, status_file, markdown):
    problems = []
    lines = []
    add = lines.append

    if markdown:
        add("## Fault matrix — outcome per fault per variant")
    else:
        add("Fault matrix — outcome per fault per variant")
    add("")
    if markdown:
        add("| Fault | Records | baseline | durable |")
        add("| --- | --- | --- | --- |")
    for fault, records in FAULTS:
        row = []
        for variant in VARIANTS:
            doc = load(results_dir, variant, fault)
            row.append("**%s**" % doc["outcome"] if doc else "**missing**")
        if markdown:
            add("| `%s` | %s | %s | %s |" % (fault, records, row[0], row[1]))
    add("")

    if markdown:
        add("## Model calls per process, as measured")
        add("")
        add("| Fault | Variant | Calls |")
        add("| --- | --- | --- |")
    for fault, _ in FAULTS:
        for variant in VARIANTS:
            doc = load(results_dir, variant, fault)
            if doc is None:
                continue
            if markdown:
                add("| `%s` | %s | %s |" % (fault, variant, esc(calls(doc), 160)))
    if markdown:
        add("")

    if markdown:
        add("## What each result observed")
        add("")
    for fault, _ in FAULTS:
        add(("### " if markdown else "") + "`%s`" % fault)
        add("")
        for variant in VARIANTS:
            doc = load(results_dir, variant, fault)
            if doc is None:
                add("- %s: **missing**" % variant)
                problems.append("%s / %s: no result file" % (variant, fault))
                continue
            if markdown:
                add("| Variant | Outcome | Phase | Calls | Detail |")
                add("| --- | --- | --- | --- | --- |")
                add("| %s | **%s** | %s | %s | %s |" % (
                    variant, doc["outcome"], esc(doc.get("phase"), 70), esc(calls(doc), 60), esc(doc.get("detail"))))
            else:
                add("- %s: %s" % (variant, doc["outcome"]))
                add("  phase:  %s" % doc.get("phase"))
                add("  calls:  %s" % calls(doc))
                add("  detail: %s" % doc.get("detail"))
        add("")

    if markdown:
        add("## Before/after controls")
        add("")
        add("Each control runs the same steps with the injection removed, so the pair differs only in whether")
        add("the fault happened. `abort-mid-wait` has no control: the control for an abort is a run that was")
        add("never aborted, which the band's own two-process evidence already covers — and its result says so.")
        add("")
        add("| Fault | Variant | Control outcome | Fault outcome |")
        add("| --- | --- | --- | --- |")
    for fault, _ in FAULTS:
        for variant in VARIANTS:
            doc = load(results_dir, variant, fault)
            control = load(results_dir, variant, fault + "-control")
            if doc is None or control is None:
                continue
            if markdown:
                add("| `%s` | %s | %s | %s |" % (fault, variant, control["outcome"], doc["outcome"]))
            else:
                add("- %s / %s: control %s, fault %s" % (variant, fault, control["outcome"], doc["outcome"]))
    if markdown:
        add("")

    if markdown:
        add("## Not exercised, and why")
        add("")
    any_missing = False
    for variant in VARIANTS:
        for fault, _ in FAULTS:
            doc = load(results_dir, variant, fault)
            if doc and doc.get("outcome") == "not exercised":
                any_missing = True
                add("- `%s` / `%s`: %s" % (variant, fault, doc.get("detail")))
    if not any_missing:
        add("- none")
    add("")

    if markdown:
        add("## Harness status")
        add("")
    if os.path.exists(status_file):
        for line in open(status_file, encoding="utf-8"):
            parts = line.split()
            if len(parts) == 3:
                variant, name, status = parts
                add("- `%s` %s: exit %s%s" % (variant, name, status, "" if status == "0" else " — **harness error**"))
    add("")

    for line in lines:
        print(line)

    if problems:
        print("Problems")
        for problem in problems:
            print("- " + problem)
        return 2 if not markdown else 0
    return 0


def main(argv):
    markdown = "--md" in argv
    args = [argument for argument in argv if argument != "--md"]
    if len(args) != 2:
        sys.stderr.write("usage: summarise.py <results-dir> <status-file> [--md]\n")
        return 2
    return render(args[0], args[1], markdown)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
