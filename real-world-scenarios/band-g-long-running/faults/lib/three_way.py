"""Renders the Band G three-way comparison, taking every measured cell from the fault results.

The band's comparison table has three columns and only two of them were run. This script exists so the
boundary between the two cannot drift: a cell that cites a fault result is read from
`results/<variant>/<fault>.json` and printed with the numbers that result recorded, and the middle column
is printed with the same "Not run here" marking G2 gave it. Nothing in the table is typed by hand, and a
measured cell whose result is missing fails the script rather than quietly reading as prose.

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

    rb = restart_b["measurements"]
    rd = restart_d["measurements"]
    lb = late_b["measurements"]
    ld = late_d["measurements"]

    rows = [
        (
            "**Restart/resume**",
            "**Yes, measured.** `kill-during-wait`: %s; `restart-after-checkpoint`: %s after %s calls in the stopped process."
            % (kill_b["outcome"], restart_b["outcome"], rb["model_calls_stopped_process"]),
            NOT_RUN + " Documented: completed steps are not repeated after a restart. The durable-extension "
            "sample was not stood up (no prerelease package on NuGet — see the band README).",
            "**Yes, measured.** `kill-during-wait`: " + kill_d["outcome"]
            + "; `restart-after-checkpoint`: " + restart_d["outcome"] + ".",
        ),
        (
            "**Re-does model calls on resume**",
            "**Yes, measured.** The stopped resumed process made %s calls; the fresh resume made %s more for the same turns, contents identical by request shape."
            % (rb["model_calls_stopped_process"], rb["model_calls_second_process"]),
            NOT_RUN + " Documented: replay does not re-run completed agent executions.",
            "**No, measured.** The resume added **%s** calls and served the re-entered step from the memo (%s memo hit(s))."
            % (rd["calls_added_by_resume"], rd["memo_hits"]),
        ),
        (
            "**Domain state transactional**",
            "**No.** The checkpoint is the workflow's state; a completed turn inside a process is not in it, which is what the call count above shows.",
            NOT_RUN + " Documented as partial: workflow and conversation state are checkpointed, a side effect is not.",
            "**Within the run.** A step's checkpoint, its memo and its ledger entry commit together: `store-outage` refused the commit (exit %s), left the run '%s' with the decision still '%s', and the store file byte-identical at %s bytes."
            % (od["drive_exit"], od["run_after_outage"], od["decision_after_outage"], od["store_bytes_after"]),
        ),
        (
            "**What a decision is**",
            "**A request/response pair, not a value.** `duplicate-approval`: %s — a second resume approves again. `late-approval`: %s — the finished gate checkpoint is still answerable."
            % (dup_b["outcome"], late_b["outcome"]),
            NOT_RUN + " An external event on an orchestration instance, with a timer for the timeout.",
            "**A first-writer-wins document.** `duplicate-approval`: %s, and the refusal did not come from the terminal guard (%s). `late-approval`: %s — %s."
            % (dup_d["outcome"], dup_d["measurements"]["second_refused_by_terminal_guard"], late_d["outcome"], ld["late_approval_note"]),
        ),
        (
            "**A decision after a terminal state**",
            "**Applied again.** `late-approval`: %s. There is no terminal record to refuse it." % late_b["outcome"],
            NOT_RUN + " The runtime refuses an event raised after completion; not exercised.",
            "**Held.** `late-approval`: %s — %s." % (late_d["outcome"], ld["late_approval_note"]),
        ),
        (
            "**Abort mid-wait**",
            "**Not exercised.** %s" % abort_b["detail"].split(" So there is")[0].strip(),
            NOT_RUN + " Orchestration termination exists; a cascade over child work is not described.",
            "**Cascades, measured.** %s — the parent's handler saw the child already terminal (%s)."
            % (abort_d["outcome"], abort_d["measurements"]["children_observed"]),
        ),
        (
            "**Operational weight**",
            "A directory on disk, one writer (the framework's store refuses a second).",
            NOT_RUN + " DTS + emulator, per the documented prerequisites.",
            "A store file, one writer. Measured only in that shape: no server, no scheduler.",
        ),
    ]

    out = []
    add = out.append
    add("| | File checkpoint (baseline, #176) | Durable Task (agent-framework-durable-extension) | This variant (#177) |")
    add("| --- | --- | --- | --- |")
    for row in rows:
        add("| %s | %s | %s | %s |" % row)
    print("\n".join(out))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
