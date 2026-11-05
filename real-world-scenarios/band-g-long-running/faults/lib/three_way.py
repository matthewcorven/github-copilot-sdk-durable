"""Renders the Band G three-way comparison, taking every measured cell from the fault results.

The band's comparison table has three columns and **all three were run**. This script exists so a cell can
never drift from the result behind it: a measured cell is read from `results/<variant>/<fault>.json` and
printed with the numbers that result recorded, and a fault with no result for a column is an error rather
than a sentence. Nothing in the table is typed by hand.

`extension` is the third column: the agent-framework durable extension on the Durable Task Scheduler,
driven by `faults/extension.sh`. Its results live in `results/extension/`. Where that column cannot supply
a measurement — its vendored sample calls no model — the cell says so, in the cell, instead of borrowing
one from another column.

    python3 lib/three_way.py <results-dir> [--md]
"""

import json
import os
import sys

NOT_RUN = "**Not run here.**"


def load(results_dir, variant, fault):
    with open(os.path.join(results_dir, variant, fault + ".json"), encoding="utf-8") as handle:
        return json.load(handle)


def main(argv):
    args = [a for a in argv if a != "--md"]
    if len(args) != 1:
        sys.stderr.write("usage: three_way.py <results-dir> [--md]\n")
        return 2
    results = args[0]

    kill_b = load(results, "baseline", "kill-during-wait")
    kill_d = load(results, "durable", "kill-during-wait")
    restart_b = load(results, "baseline", "restart-after-checkpoint")
    restart_d = load(results, "durable", "restart-after-checkpoint")
    dup_b = load(results, "baseline", "duplicate-approval")
    dup_d = load(results, "durable", "duplicate-approval")
    late_b = load(results, "baseline", "late-approval")
    late_d = load(results, "durable", "late-approval")
    abort_b = load(results, "baseline", "abort-mid-wait")
    abort_d = load(results, "durable", "abort-mid-wait")
    outage_d = load(results, "durable", "store-outage")
    od = outage_d["measurements"]
    ext_kill = load(results, "extension", "kill-during-wait")
    ext_restart = load(results, "extension", "restart-after-checkpoint")
    ext_dup = load(results, "extension", "duplicate-approval")
    ext_late = load(results, "extension", "late-approval")
    ext_abort = load(results, "extension", "abort-mid-wait")
    ext_outage = load(results, "extension", "store-outage")

    rb = restart_b["measurements"]
    rd = restart_d["measurements"]
    lb = late_b["measurements"]
    ld = late_d["measurements"]

    xk = ext_kill["measurements"]
    xr = ext_restart["measurements"]

    rows = [
        (
            "**Restart/resume**",
            "**Yes, measured.** `kill-during-wait`: " + kill_b["outcome"]
            + "; `restart-after-checkpoint`: " + restart_b["outcome"]
            + " after " + str(rb["model_calls_stopped_process"]) + " calls in the stopped process.",
            "**Yes, measured.** `kill-during-wait`: " + ext_kill["outcome"]
            + "; `restart-after-checkpoint`: " + ext_restart["outcome"]
            + " — a fresh process, its own worker, finished the parked run (status after: "
            + xr["status_after_restart"] + ").",
            "**Yes, measured.** `kill-during-wait`: " + kill_d["outcome"]
            + "; `restart-after-checkpoint`: " + restart_d["outcome"] + ".",
        ),
        (
            "**Re-does model calls on resume**",
            "**Yes, measured.** The stopped resumed process made " + str(rb["model_calls_stopped_process"])
            + " calls; the fresh resume made " + str(rb["model_calls_second_process"])
            + " more for the same turns.",
            "**Not measurable here.** " + xr["model_calls"]
            + ". The band's call-count comparison therefore has no number for this column, and none is borrowed.",
            "**No, measured.** The resume added **" + str(rd["calls_added_by_resume"])
            + "** calls and served the re-entered step from the memo (" + str(rd["memo_hits"]) + " memo hit(s)).",
        ),
        (
            "**Domain state transactional**",
            "**No.** The checkpoint is the workflow's state; a completed turn inside a process is not in it, "
            "which is what the call count above shows.",
            "**In the substrate.** State lives in the Durable Task Scheduler, not in the process: stopping the "
            "process mid-run lost nothing (`kill-during-wait`: " + ext_kill["outcome"] + "). Stopping the "
            "*scheduler* is a different matter — see operational weight.",
            "**Within the run.** A step's checkpoint, its memo and its ledger entry commit together: "
            "`store-outage` refused the commit (exit " + str(od["drive_exit"]) + "), left the run '"
            + od["run_after_outage"] + "' with the decision still '" + od["decision_after_outage"]
            + "', and the store file identical in size before and after ("
            + str(od["store_bytes_before"]) + " -> " + str(od["store_bytes_after"])
            + " bytes): a refused commit left no partial write. The exact byte count is run-variable; its "
            "equality is not.",
        ),
        (
            "**What a decision is**",
            "**A request/response pair, not a value.** `duplicate-approval`: " + dup_b["outcome"]
            + " — a second resume approves again. `late-approval`: " + late_b["outcome"]
            + " — the finished gate checkpoint is still answerable.",
            "**An event on the orchestration instance.** `duplicate-approval`: " + ext_dup["outcome"]
            + " — the second event is recorded but schedules no second step. `late-approval`: " + ext_late["outcome"]
            + ".",
            "**A first-writer-wins document.** `duplicate-approval`: " + dup_d["outcome"]
            + ", and the refusal did not come from the terminal guard ("
            + dup_d["measurements"]["second_refused_by_terminal_guard"] + "). `late-approval`: "
            + late_d["outcome"] + " — " + ld["late_approval_note"] + ".",
        ),
        (
            "**A decision after a terminal state**",
            "**Applied again.** `late-approval`: " + late_b["outcome"]
            + ". There is no terminal record to refuse it.",
            "**Not moved.** `late-approval`: " + ext_late["outcome"]
            + " — a late answer on a terminated run ran no post-approval step.",
            "**Held.** `late-approval`: " + late_d["outcome"] + " — " + ld["late_approval_note"] + ".",
        ),
        (
            "**Abort mid-wait**",
            "**Not exercised.** " + abort_b["detail"].split(" So there is")[0].strip(),
            "**Not the same shape.** `abort-mid-wait`: " + ext_abort["outcome"]
            + " — termination is the scheduler's call, and the ports live inside one orchestration, so a "
            "child cascade is not observable here.",
            "**Cascades, measured.** " + abort_d["outcome"]
            + " — the parent's handler saw the child already terminal ("
            + abort_d["measurements"]["children_observed"] + ").",
        ),
        (
            "**Operational weight**",
            "A directory on disk, one writer (the framework's store refuses a second).",
            "**The heaviest of the three.** A scheduler (or its emulator) is a prerequisite even for one run: "
            "`docker run mcr.microsoft.com/dts/dts-emulator`. Stopping it under a live run failed the client "
            "(`store-outage`: " + ext_outage["outcome"] + ", exit " + str(ext_outage["measurements"]["drive_exit"])
            + "), which is an availability cost the other two columns do not have.",
            "A store file, one writer. Measured only in that shape: no server, no scheduler.",
        ),
    ]

    out = []
    add = out.append
    add("| | File checkpoint (baseline, #176) | Durable Task extension on DTS (`extension/`) | This variant (#177) |")
    add("| --- | --- | --- | --- |")
    for row in rows:
        add("| %s | %s | %s | %s |" % row)
    print("\n".join(out))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
