"""Assembles the Band G Durable variant's evidence file from the recordings `evidence.sh` wrote.

Every console block in the output is one whole recording: the command, its entire output, and its
exit status. Nothing is retyped beside its output and nothing is stitched together, which is checked
rather than promised — a recording that does not end with an `exit=` line, or a recording the report
needs but cannot find, fails the run and no file is written.

Usage:
  render.py --recordings <dir> --out <file> --worktree <path> --dotnet <version> --runtime <path>
"""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

CONSOLE = "console"

# One entry per block the report shows, in the order it shows them. A block names the recording it
# lifts, so a missing or empty recording is a failure here rather than a gap in the file.
BLOCKS = [
    ("1", "Two-process restart/resume", [
        ("Process 1 — run to the gate and exit", "happy-start"),
        ("Process 2 — approve and finish", "happy-approve"),
        ("The store's view of the finished run", "happy-inspect"),
    ]),
    ("2", "A resume does not re-call the model for a step already done", [
        ("Process 1 variant — leave the process before the write step commits", "memo-start"),
        ("The store after the kill: three steps on disk", "memo-after-kill"),
        ("Process 2 variant — resume, hitting the memo", "memo-resume"),
        ("Approve and finish", "memo-finish"),
    ]),
    ("2a", "The control: the same flow with the memo bypassed", [
        ("Process 1 control — the same kill, in the same place", "memo-control-start"),
        ("Process 2 control — the resume re-calls the step the store already held", "memo-control-resume"),
        ("The control reaches the same gate", "memo-control-at-gate"),
        ("The control finishes on the same verdict", "memo-control-finish"),
    ]),
    ("3", "A duplicate approval cannot overwrite the first", [
        ("Process 1 — to the gate", "dup-start"),
        ("Process 2 — approve and finish", "dup-approve"),
        ("Process 3 — a second approval from a different reviewer", "dup-duplicate"),
        ("The decision on file is still the first one", "dup-inspect"),
    ]),
    ("4", "A late decision cannot overwrite a verdict", [
        ("Process 1 — a four-second window, then exit at the gate", "late-start"),
        ("Process 2 — started after the window has passed: the gate wakes at once and times out", "late-expire"),
        ("The store: a timeout, and a run that ended on it", "late-inspect"),
        ("An approval after the timeout", "late-approve-after-timeout"),
        ("The verdict is still the timeout", "late-after-timeout-inspect"),
        ("A decision after the run finished", "late-approve-after-finish"),
    ]),
    ("5", "Aborting mid-wait cascades to the children", [
        ("Process 1 — to the gate", "abort-start"),
        ("The run before the abort: parent Waiting, gate Running", "abort-before"),
        ("Abort the parent", "abort-do"),
        ("Both ends terminal, and the parent's verdict is an abort", "abort-after"),
        ("An approval after the abort", "abort-approve-late"),
    ]),
    ("6", "The before/after control for the layer's re-entry wait", [
        ("Process 1 of the control: the same run, to the same gate", "control-start"),
        ("Process 2 of the control: approve — with the wait removed, the parent reads the gate early", "control-resume"),
        ("The control's store: the gate finished approved, and the parent failed anyway", "control-inspect"),
    ]),
]


def read(recordings: pathlib.Path, name: str) -> str:
    path = recordings / f"{name}.txt"
    if not path.is_file():
        raise SystemExit(f"render.py: no recording '{name}' in {recordings}")
    text = path.read_text(encoding="utf-8")
    if not text.strip():
        raise SystemExit(f"render.py: the recording '{name}' is empty")
    if not re.search(r"(?m)^exit=\d+$", text):
        raise SystemExit(f"render.py: the recording '{name}' does not end with an exit status")
    return text.rstrip("\n")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--recordings", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--worktree", required=True)
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--runtime", required=True)
    args = parser.parse_args(argv[1:])

    recordings = pathlib.Path(args.recordings)
    out = pathlib.Path(args.out)
    worktree = pathlib.Path(args.worktree)

    # Read every block first: a missing recording must fail before anything is written.
    body: list[str] = []
    for number, title, blocks in BLOCKS:
        body.append(f"## {number}. {title}")
        body.append("")
        for caption, recording in blocks:
            text = read(recordings, recording)
            # The absolute worktree path would otherwise appear in every command line; the report is
            # read beside the scenario, so a relative path is what a reader can retype.
            text = text.replace(str(worktree) + "/", "")
            body.append(f"**{caption}.**")
            body.append("")
            body.append(f"```{CONSOLE}")
            body.append(text)
            body.append("```")
            body.append("")

    offline = (recordings / "control-inspect.txt").is_file()
    if not offline:
        raise SystemExit("render.py: the control recordings are missing; the report would claim a "
                         "before/after it does not show")

    header = f"""# Band G Durable variant — a durable approval gate across processes

Captured output of `real-world-scenarios/band-g-long-running/durable/` (issue #177), run on this
machine. **Every console block below is a whole recording**: the command, its output, and its exit
status, written by `durable/evidence.sh` and lifted verbatim by `durable/evidence/render.py` — the
script that wrote this file. A block is one invocation, not a command retyped beside output.

That invariant is enforced rather than promised: `render.py` fails without writing the file when a
recording is missing, empty, or does not end with an `exit=` line. Re-run `./evidence.sh` to
regenerate.

| Field | Value |
| --- | --- |
| Worktree | `{worktree.name}` |
| `dotnet` | `{args.dotnet}` |
| Runtime | `{args.runtime.replace(str(worktree) + "/", "")}` — the repository's recorded runtime |
| Model | `replay-model`; each answer is supplied by the scenario (`--deterministic`) |
| Driver | `durable/band-g-durable.cs` |
| Runners | `durable/run.sh` (process 1), `durable/resume.sh` (every later process) |
| Recorder | `durable/evidence.sh` |
| Assembler | `durable/evidence/render.py` |

## What the model is, and what the call count therefore proves

**What a turn is.** Every "model call" in these recordings is a real Copilot SDK turn through the
durable harness: a conversation is created, the prompt is admitted to the durable store, the prompt
crosses the runtime boundary, an assistant message comes back, and the turn is checkpointed. Nothing
in that path is the scenario's.

**What the scenario supplies.** Only the assistant text. `--deterministic` replaces what the model
said after the turn returns, so a run needs no endpoint and two runs of the same prompt are
identical. `--live` is its explicit counterpart: it takes whatever the configured runtime answered,
so a run against a real model differs only in which bytes come back. Neither flag changes the turn,
the memo, or the count.

**So the counter proves** how many turns this run of the workflow issued, and — because the count and
the memo commit together — that a resumed process issued none for a step whose memo the store already
held. It does **not** prove anything about model quality, and it is not evidence that a provider was
reachable. The alternative was a run nobody could repeat, which would have made the memoization claim
unrepeatable rather than merely unproven.

## The five requirements, and where each is shown

| Requirement | Where it is demonstrated |
| --- | --- |
| **1. The gate is a task waiting on an external event with a durable deadline, not a blocking read.** | §1. The workflow commits `TaskCommit.Waiting` on a child gate task and returns; the child commits its deadline once, at open. Process 1 exits while the parent is `Waiting` and the gate is `Running`, and process 2 drives both. §4 leaves the run at its gate with a four-second window, then opens the store in a **fresh** process
after that window has passed: the gate wakes at once, records a timeout, and the run ends on it. |
| **2. Pre-gate work is memoized, proven by counting calls.** | §2 and §2a. The variant resumes without re-calling a step the store already held (`3 calls ..., 1 memo hits`), and the same flow with the memo bypassed re-calls it (`4 calls [plan, research, write, write], 0 memo hits`). Both halves are the same script. |
| **3. A decision cannot be overwritten by a late wakeup or a duplicate approval.** | §3 approves a **live** gate twice and the second write is refused by the decision document's own first-writer-wins rule (`ignored: a decision is already on file`), with a `rejects` assertion that the refusal did **not** come from the terminal check. §4a leaves a gate to time out in a fresh process and shows a later approval cannot turn the timeout into an approval. §4b shows a decision arriving after the run finished refused as late — the same guard, a different route, and the capture says which. |
| **4. Aborting mid-wait cascades, and terminal state is final.** | §5. The parent is `Waiting` and its gate is `Running`; the abort leaves both terminal. The ordering is recorded rather than inferred: the parent's abort handler reads its child at the moment it runs and the capture shows `t59 was Terminal (aborted) when the abort handler ran` — so the child was already settled, which "both ended terminal" alone would not establish. An approval afterwards is refused. |
| **5. `lib/Durable.cs` is used, not copied.** | `durable/band-g-durable.cs` has `#:project ../../../lib/Copilot.Durable.csproj` and no store, task runner, or memo implementation of its own. The scenario's own code is the workflow definition, the gate, and the model ledger. |

## The layer change this needed, and its control

Two gaps had to be fixed for requirement 1 to hold at all. Both were reproduced first, and both are
in the same commit as a regression test that fails without the change.

1. **`ResumeAsync` skipped a task left `Waiting`.** It re-entered `Pending` and `Running` only, while
   `lib/ts/durable.ts` and `lib/py/durable.py` both included `Waiting`. A parent suspended on its
   children was therefore never resumed after a restart: the children finished, the parent stayed
   `Waiting`, and nothing raised an error.
2. **A task re-entered while `Waiting` ran its next phase without waiting.** The recorded wait was
   honoured only after a commit made inside the same process. A parent re-entered from a checkpoint it
   committed before the process ended started its post-gate phase immediately — reading a child that
   had not run. §6 is this one: with the wait removed the gate still ends `approved` and the parent
   still fails with `the gate produced no verdict`, because it read the gate before the gate finished.

The `Waiting` filter was already right in TypeScript and Python, so the first fix brings C# into
line. The second is new in all three: the recorded wait is recovered on re-entry in C#, TypeScript, and
Python alike, in one commit, because a semantic change to the layer that lands in one port and not the
others is a cross-port discrepancy rather than a fix. The wire format did not change — a status is
already persisted and already round-trips — so a store written before the change still reads.

## Running it

```sh
cd real-world-scenarios/band-g-long-running/durable
./run.sh                      # process 1: to the gate, then exit
APPROVE=1 ./resume.sh         # process 2: approve, then to completion
./evidence.sh                 # every flow above, then this file
```

A run needs no model endpoint. `DURABLE_RUNTIME_PATH` selects a real runtime when there is one; the
scripts build the repository's recorded runtime when there is not.

## What this does not show

- **A live model.** No endpoint was configured for these captures, so every answer is the scenario's.
  The turns are real; the words are not a model's.
- **Concurrency across processes.** One store has one writer, and a second process opening it while
  the first holds it is refused rather than serialized. The two-process shape here is sequential by
  construction, which is what the baseline's pause/resume shape needs.
- **A gate queue.** There is one approval per run and no notion of who is allowed to answer it. The
  decision document records who did.
- **The middle column, run.** §"Three-way comparison" in the band's README states exactly what was
  and was not executed for the Agent Framework durable extension.

---

"""

    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(header + "\n".join(body) + "\n", encoding="utf-8")
    print(f"wrote {out} ({len(body)} lines of body)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
