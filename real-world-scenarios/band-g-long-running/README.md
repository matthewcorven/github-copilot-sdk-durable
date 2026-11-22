# Band G baseline — Magentic orchestration with a human plan-review gate

This directory reproduces the restart/resume behaviour of the reference implementation for Band G
(long-running operations): `microsoft/agent-framework`'s `magentic_checkpoint.py`. It is the baseline
side of the comparison; the Durable variant is the band's own work issue.

**The baseline works.** It checkpoints the workflow, stops for human review, survives the process
ending, and finishes in a new process without repeating the pre-gate work. The evidence file under
`evidence/` is our reproduction of that, not a quotation of the sample's README. Any comparison the
band publishes has to treat restart/resume as a tie unless it shows a concrete difference.

## What is here

| Path | What it is |
| --- | --- |
| `upstream/` | The reference sample, its sibling, and the repository's `LICENSE`, byte-identical to a pinned commit. Hashes in `upstream/PROVENANCE.md`. |
| `baseline/BaselineCheckpoint.cs` | The C# port. Single file-based app, two modes: `run` and `resume`. |
| `run.sh` | Process 1: run to the gate, record the checkpoint id, exit. |
| `resume.sh` | Process 2: resume from the recorded checkpoint, approve, complete. |
| `evidence/baseline-restart-resume.md` | Captured stdout of both processes, with the commands that produced them. |
| `faults/` | Six faults against this baseline and against the Durable variant, with controls and model-call counts. See **Fault evidence** below, or `faults/README.md`. |

## Pinned source

| Field | Value |
| --- | --- |
| Repository | `microsoft/agent-framework` |
| Commit | `cf734bc4692ccfc944496a5b3182b3d65483c44d` |
| License | MIT |
| Sample | `python/samples/03-workflows/orchestrations/magentic_checkpoint.py` |
| Sibling | `python/samples/03-workflows/orchestrations/handoff_with_tool_approval_checkpoint_resume.py` |

The vendored files are byte-identical to that commit: `git hash-object` returns the blob ids the
GitHub API reports for the same paths, and a re-fetch reproduces the same SHA-256 sums.

## Which shape was chosen, and why

**Magentic, not Handoff.** The Python sample orchestrates three agents — a Magentic manager, a
researcher and a writer — and gates on the manager's plan via `MagenticPlanReviewRequest`. The .NET
port of that orchestration ships in the same framework (`MagenticWorkflowBuilder`,
`RequirePlanSignoff`, `MagenticPlanReviewRequest.Approve()`), so the port does not have to
re-implement the manager/worker loop or its planning prompts. It builds the real Magentic workflow
with the framework's own builder and drives it through the framework's own checkpoint manager.

Nothing is hand-rolled: the manager, the participant selection, the task and progress ledgers, the
plan-review request and the checkpoint objects all come from `Microsoft.Agents.AI.Workflows`
1.27.0, the .NET counterpart of the Python package the sample imports. The Handoff sample is
vendored as a sibling reference but is not ported; Band F (#171) covers the Handoff shape.

## What the port preserves

| Baseline behaviour | In the port |
| --- | --- |
| Manager plus two participants (researcher, writer) | Same three agents, same names and instructions as the sample |
| Plan review enabled before execution (`enable_plan_review=True`) | `RequirePlanSignoff(true)` |
| Rounds and stalls capped at 10 and 3 | `WithMaxRounds(10)`, `WithMaxStalls(3)` |
| File-backed checkpoint storage (`FileCheckpointStorage`) | `FileSystemJsonCheckpointStore` — the framework's own file-backed store, one JSON file per checkpoint plus `index.jsonl` |
| Checkpoint written at the superstep that raises the plan-review request | The checkpoint-before-gate point; `run.sh` prints its id and the pending request ids it contains |
| Resume by checkpoint id in a new run (`workflow.run(checkpoint_id=... )`) | `ResumeStreamingAsync(workflow, new CheckpointInfo(sessionId, checkpointId))` |
| Approval supplied to the re-emitted request | `MagenticPlanReviewRequest.Approve()` wrapped in the request's own `ExternalResponse` |
| Checkpointing continues after approval | The checkpoint-after-approval point; `resume.sh` prints the newest checkpoint id and shows that it differs from the gate one |

## What the port drops

- **The `.env` file and Azure CLI credential.** The sample authenticates with `AzureCliCredential`
  against a Foundry project. The port takes an OpenAI-compatible endpoint from `OPENAI_API_KEY`,
  `OPENAI_BASE_URL` and `OPENAI_MODEL`, so no Azure subscription is needed. This is a provider
  substitution only; the orchestration and the checkpointing are unchanged.
- **Stage 3 of the sample** — resuming again from a post-plan checkpoint. The two-process flow that
  Band G needs is process 1 to the gate and process 2 to completion. The port exposes the
  post-approval checkpoint id (and shows it is different from the gate checkpoint); it does not
  start a third process from it.
- **Clearing the checkpoint directory on startup.** The sample deletes `*.json` in its checkpoint
  directory so each invocation starts clean. Here the directory is a scratch directory created by
  `run.sh`, which is a stronger version of the same guarantee, and `resume.sh` must not delete it.

## Fault evidence

`faults/` runs six faults against both variants and records one JSON result per fault per variant, plus a
control for each. Nothing in that directory changes the baseline; it is driven through `run`, `resume`, and
the OpenAI-compatible endpoint the port already reads.

| Fault | Injection, baseline | Injection, durable | baseline | durable |
| --- | --- | --- | --- | --- |
| `kill-during-wait` | `kill -9` inside the **manager's first model call**, before any gate checkpoint exists | `kill -9` of the **process blocked in the wait** for the gate's verdict | **lost** | **wait survived** |
| `restart-after-checkpoint` | `SIGTERM` inside the **writer's model call**, after the plan and research turns completed | the process leaves itself **after the write step's turn, before its checkpoint commits** | **resumed, completed work re-done** | **resumed, no calls repeated** |
| `duplicate-approval` | a **second `resume`** against the same gate checkpoint | a **second `approve`** against a live gate | **doubled** | **second delivery refused** |
| `late-approval` | `resume` **after the run already finished** | `approve` **after the gate's durable deadline** passed | **applied again** | **terminal state held** |
| `abort-mid-wait` | — none: the port has no abort to inject | the variant's **abort verb**, on a waiting parent | **not exercised** | **cancelled** |
| `store-outage` | `chflags uchg` on the **checkpoint directory** while a resume commits | `chflags uchg` on the **store file** while the run drives a decision | **failed safely, gate still answerable** | **failed safely** |

**The injections are not the same test on the two sides, and the table says so by column**, not only by
outcome: `kill-during-wait` is a kill inside a model call for the baseline and a kill of a blocked process
for the Durable variant, because the baseline has no durable wait to be in. Reading `lost` against
`wait survived` as one measurement would be reading two different faults, and the injection column is
there so nobody does.

Two of those six are not a loss for the baseline and should not be read as one: it fails cleanly on a store
outage and it has nothing to abort. One is a tie in outcome and not in mechanism: both resume, and the
count is what differs. The counts are in `faults/results/` and rendered in `faults/results/summary.md`.

### The three-way comparison, with the measured cells

Every measured cell below is read from `faults/results/` by `faults/lib/three_way.py`, so it cannot drift
from the run. The middle column is **measured**, against the vendor's Durable Task extension on the DTS
emulator (`extension/`), and it carries two stated residuals rather than filled cells: its executors call no
model, so there is no call count for it, and its ports live inside one orchestration, so "children cancelled"
is not observable. G2 and the first G3 draft recorded this column as *not run* because they believed no
prerelease package existed; that was wrong, and `extension/README.md` records the correction.

| | File checkpoint (baseline, #176) | Durable Task extension on DTS (`extension/`) | This variant (#177) |
| --- | --- | --- | --- |
| **Restart/resume** | **Yes, measured.** `kill-during-wait`: lost; `restart-after-checkpoint`: resumed, completed work re-done after 2 calls in the stopped process. | **Yes, measured.** `kill-during-wait`: wait survived; `restart-after-checkpoint`: resumed — a fresh process, its own worker, finished the parked run (status after: Running). | **Yes, measured.** `kill-during-wait`: wait survived; `restart-after-checkpoint`: resumed, no calls repeated. |
| **Re-does model calls on resume** | **Yes, measured.** The stopped resumed process made 2 calls; the fresh resume made 2 more for the same turns. | **Not measurable here.** The vendored sample's executors call no model, so there is no call to count. The band's call-count comparison therefore has no number for this column, and none is borrowed. | **No, measured.** The resume added **0** calls and served the re-entered step from the memo (1 memo hit(s)). |
| **Domain state transactional** | **No.** The checkpoint is the workflow's state; a completed turn inside a process is not in it, which is what the call count above shows. | **In the substrate, and only as durable as it.** Stopping the process mid-run lost nothing (`kill-during-wait`: wait survived). Stopping the *scheduler* lost the run: `store-outage`: lost with the scheduler — the instance did not exist after a restart. State is the scheduler's, and on the emulator that also means it is the emulator's lifetime. | **Within the run.** A step's checkpoint, its memo and its ledger entry commit together: `store-outage` refused the commit (exit 70), left the run 'waiting' with the decision still 'alice approved', and the store file identical in size before and after (31903 -> 31903 bytes): a refused commit left no partial write. The exact byte count is run-variable; its equality is not. |
| **What a decision is** | **A request/response pair, not a value.** `duplicate-approval`: doubled — a second resume approves again. `late-approval`: applied again — the finished gate checkpoint is still answerable. | **An event on the orchestration instance.** `duplicate-approval`: second delivery did not run the next step twice — the second event is recorded but schedules no second step. `late-approval`: terminal state held. | **A first-writer-wins document.** `duplicate-approval`: second delivery refused, and the refusal did not come from the terminal guard (0). `late-approval`: terminal state held — refused: the workflow is Terminal, so this decision is late. |
| **A decision after a terminal state** | **Applied again.** `late-approval`: applied again. There is no terminal record to refuse it. | **Not moved.** `late-approval`: terminal state held — a late answer delivered by a live worker completed 1 step(s) against 1 at the gate. | **Held.** `late-approval`: terminal state held — refused: the workflow is Terminal, so this decision is late. |
| **Abort mid-wait** | **Not exercised.** The baseline's port advertises exactly two modes, 'run' and 'resume' — checked in this run, where an unknown mode answers 'Use run or resume'. | **Stops, with no tree to cascade.** `abort-mid-wait`: cancelled — the run was terminated and an answer delivered by a live worker completed 1 step(s) against 1 at the gate, i.e. the step after the port did not run. There is no child work: the ports live inside one orchestration. | **Cascades, measured.** cancelled — the parent's handler saw the child already terminal (t59=Terminal:aborted). |
| **Operational weight** | A directory on disk, one writer (the framework's store refuses a second). | **The heaviest, and the least forgiving.** A scheduler (or its emulator) is a prerequisite even for one run. Stopping it under a live run did not fail over: the run was gone when the scheduler came back (`store-outage`: lost with the scheduler). That is the cost the other two columns do not have — their store is a file this harness can move and restore. | A store file, one writer. Measured only in that shape: no server, no scheduler. |

Read the middle column as **one sample, measured**: it is the vendor's human-in-the-loop workflow, the right
shape (a human gate over a durable wait) but not the same workflow as the other two columns. Its two residuals
— no model-call count, no observable child cascade — are holes in the comparison, not wins for this variant.

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| .NET SDK | 10.0.402 | Must resolve to `/opt/homebrew/bin/dotnet`. File-based apps (`dotnet run file.cs`) need .NET 10. |
| An OpenAI-compatible endpoint | — | `OPENAI_API_KEY` is required. `OPENAI_BASE_URL` defaults to `https://api.openai.com/v1`; any compatible gateway works. `OPENAI_MODEL` defaults to `gpt-4o`, which is what the captured evidence used; `gpt-4o-mini` needed more than the sample's 10 coordination rounds to converge. |
| Network access to NuGet | — | The two `Microsoft.Agents.AI.*` packages are restored on first run. |

No Azure subscription, no `az login`, no Foundry project.

## Two-process mechanics

```sh
export OPENAI_API_KEY=...
export OPENAI_BASE_URL=https://openrouter.ai/api/v1   # optional
export OPENAI_MODEL=openai/gpt-4o                     # optional, this is the default

CKPT="$(./run.sh | tail -1)"    # process 1 stops at the gate and exits
./resume.sh "$CKPT"             # process 2 resumes and finishes
```

`run.sh` prints the checkpoint directory as its last line, so the two processes can be chained
without any other channel between them. They share exactly two things:

1. The scratch checkpoint directory, which holds the framework's checkpoint files and a `gate.json`
   record with the session id, checkpoint id and request id.
2. The model endpoint and model name, because both processes rebuild the same three agents before
   touching the checkpoint. Their agent ids are fixed strings (`MagenticManager`, `ResearcherAgent`,
   `WriterAgent`) for the same reason: the checkpoint records executor ids, and a resumed graph with
   different ids is rejected as incompatible.

Nothing else crosses: process 2 constructs a new store, checkpoint manager, execution environment and
workflow. The only in-memory state it has is the checkpoint the framework reads from disk.

The checkpoint directory is a scratch directory under `$TMPDIR` (or `/tmp`). Process 1 has to leave
it behind — that is the handoff — so `resume.sh` is what removes it, on exit, unless `KEEP=1`. If
`run.sh` fails it removes its own directory, since there is nothing to resume from. Nothing is ever
written inside the repository.

### Scratch directories

Neither script deletes a directory it did not create. The rules below cover every path, and they
hold whether the run succeeds or fails:

| Rule | Where it is enforced |
| --- | --- |
| `run.sh` writes an empty `.band-g-scratch` marker into every checkpoint directory it creates, and removes only directories it created. | `run.sh` |
| `resume.sh` removes a checkpoint directory only when that marker is present. Otherwise it prints `not removing non-scratch directory: <path>` and leaves the directory and its contents alone. | `resume.sh` |
| A pre-populated `CHECKPOINT_DIR` is refused, not emptied: `run.sh` exits 2 with a message and does not touch the path. A path that does not exist is created and marked; an existing *empty* directory is used as it is and left unmarked, so it is never removed later. A directory holding only the marker — left by a failed run with `KEEP=1` — is recognised as this script's own and reused. | `run.sh` |

Removal runs on both outcomes of the resume itself: a real resume failure removes a marked
directory just as a success does. The one exception is a **build failure in `resume.sh`**, which
leaves the directory in place, because the build is the retryable part (missing package, no network)
and there is nothing wrong with the checkpoint. That matches `run.sh`, which keeps its scratch
directory when it fails before reaching the gate. `KEEP=1` still wins in both scripts: it keeps the
directory even when it is marked scratch.

The marker is an empty dotfile. The checkpoint store never reads it, but the framework's listing
uses `DirectoryInfo.EnumerateFiles()`, so a capture taken with this change would show an extra
`checkpoint store: .band-g-scratch (0 bytes)` line where the evidence file shows three entries. That
line is the marker, not a checkpoint.

The rule matters because `CHECKPOINT_DIR` is a caller-supplied path, and `gate.json` alone is not a
provenance check: any directory an author writes a `gate.json` into would otherwise satisfy
`resume.sh` and be deleted on exit. Pointing either script at a hand-written directory is now safe —
the directory survives, with a message saying why.

`./run.sh --help` and `./resume.sh --help` print the same rules from the script headers.

### Exit codes

| Code | `run.sh` | `resume.sh` |
| --- | --- | --- |
| 0 | Reached the gate; the checkpoint directory is the last line of stdout. | The workflow completed. |
| 1 | `OPENAI_API_KEY` unset, or the build failed — nothing to resume from, so the scratch directory is removed unless `KEEP=1`. | `OPENAI_API_KEY` unset, or the build failed — the checkpoint directory is left in place for a retry. |
| 2 | Bad usage, or a `CHECKPOINT_DIR` that exists and is not an empty/scratch directory. The path is not touched. | Missing argument, or no `gate.json` in the directory. The directory is not touched. |
| other | The workflow's own status; the scratch directory is removed unless `KEEP=1`. | The workflow's own status; a marked directory is removed, an unmarked one is kept. |

## What was executed

Both processes were run against an OpenAI-compatible endpoint (`openai/gpt-4o` through a hosted
gateway) and both exited 0. The full captured output of each is in
`evidence/baseline-restart-resume.md`:

- Process 1 exited 0 at the gate, after printing the plan-review request id, the gate checkpoint id,
  and the pending request ids read back out of the checkpoint file.
- Process 2 exited 0 after approving and running the workflow to its final answer, and printed a
  post-approval checkpoint id different from the gate checkpoint id.

An earlier run of the same code with `openai/gpt-4o-mini` also resumed and completed, but the
manager reached `max_round_count` and the workflow's output was
`Task execution stopped due to hitting the maximum round count limit.` That is the model running out
of coordination rounds, not a resume failure; the evidence file says which model each capture used.

## What the baseline does well

- It stops cleanly at a human gate and exits. The process does not have to be kept alive, and no
  server or worker pool is involved.
- It resumes from a serialized checkpoint in a new process, re-emits the pending request with the
  same request id, accepts the response, and continues from that superstep rather than from the
  beginning. The pre-gate model calls are not repeated: the researcher and writer have not run yet
  when the gate is reached, and after resume the manager continues its own ledger instead of
  replanning.
- It keeps checkpointing after the gate, so a later restart can pick up from a later point.
- It is a library feature of the orchestration builder, not application code: enabling it is one
  builder call.

## What it does not do

These are observations from building and running the port, not criticisms of the sample:

- There is no retry, no timeout and no backoff around the model calls. A transient provider error
  fails the process and leaves the last checkpoint as the restart point.
- The store is single-process by construction: `FileSystemJsonCheckpointStore` opens `index.jsonl`
  with `FileShare.None` and throws `The store at '<dir>' is already in use by another process` if a
  second process opens it. That is acceptable for the pause/resume shape and rules out concurrent
  workers on one checkpoint directory.
- The checkpoint holds the workflow's own state as JSON. Resuming requires the same workflow graph
  (same executor ids, same edges) and the same package version; a different graph is rejected with
  `The specified checkpoint is not compatible with the workflow associated with this runner.`
- The gate is a single request/response pair. There is no queue of human tasks and no notion of who
  is allowed to answer.
