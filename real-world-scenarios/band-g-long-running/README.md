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

Neither script deletes a directory it did not create. Three rules cover it, and they hold whether
the run succeeds or fails:

| Rule | Where it is enforced |
| --- | --- |
| `run.sh` writes an empty `.band-g-scratch` marker into every checkpoint directory it creates, and removes only directories it created. | `run.sh` |
| `resume.sh` removes a checkpoint directory only when that marker is present. Otherwise it prints `not removing non-scratch directory: <path>` and leaves the directory and its contents alone. | `resume.sh` |
| A pre-populated `CHECKPOINT_DIR` is refused, not emptied: `run.sh` exits 2 with a message and does not touch the path. A path that does not exist is created and marked; an existing *empty* directory is used as it is and left unmarked, so it is never removed later. | `run.sh` |

`KEEP=1` still wins in both scripts: it keeps the directory even when it is marked scratch. The
marker is a plain file, so it does not disturb the checkpoint store, which only reads the framework's
own `*.json` / `index.jsonl` files.

The rule matters because `CHECKPOINT_DIR` is a caller-supplied path, and `gate.json` alone is not a
provenance check: any directory an author writes a `gate.json` into would otherwise satisfy
`resume.sh` and be deleted on exit. Pointing either script at a hand-written directory is now safe —
the directory survives, with a message saying why.

`./run.sh --help` and `./resume.sh --help` print the same rules from the script headers.

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
