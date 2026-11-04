# Band G baseline — captured restart/resume output

Both processes were run for real on 2026-11-04 against an OpenAI-compatible endpoint
(`OPENAI_BASE_URL=https://openrouter.ai/api/v1`, `OPENAI_MODEL=openai/gpt-4o`,
`OPENAI_API_KEY` set from the environment). The two blocks below are each program's stdout, pasted
from the shell capture without edits. stderr was empty for both processes: `run.sh` and `resume.sh`
build first and then run with `--no-build`, so compiler warnings do not reach this output.

Machine: macOS, `dotnet` 10.0.402 at `/opt/homebrew/bin/dotnet`.
Packages: `Microsoft.Agents.AI.Workflows` 1.27.0, `Microsoft.Agents.AI.OpenAI` 1.27.0.

## The two commands

```sh
export OPENAI_API_KEY=...                            # the environment's key
export OPENAI_BASE_URL=https://openrouter.ai/api/v1
export OPENAI_MODEL=openai/gpt-4o

CKPT="$(./run.sh | tail -1)"    # process 1: stops at the gate and exits
./resume.sh "$CKPT"             # process 2: resumes, approves, completes, removes the scratch dir
```

`run.sh` prints the checkpoint directory as its final line; that is the only value passed from
process 1 to process 2. Both processes exited 0. `resume.sh` removed the directory on exit, as
designed — that is why the paths below no longer exist.

## Process 1 — `./run.sh`

The workflow reached the plan-review request in the orchestrator's first superstep, checkpointed the
pending request, printed the gate checkpoint id and the request ids it contains, and exited. No
researcher or writer turn had run.

```
checkpoint directory: /var/folders/g2/v7hw2fhs2mjd_5dxggk52cn00000gn/T/band-g-checkpoints.MPN9vk

=== Stage 1: run until plan review request (checkpointing active) ===
  event: WorkflowStartedEvent
  event: SuperStepStartedEvent
  event: ExecutorInvokedEvent MagenticOrchestrator
  event: ExecutorCompletedEvent
  event: ExecutorInvokedEvent MagenticOrchestrator
  event: MagenticPlanCreatedEvent
  event: RequestInfoEvent ccb49e31b0114a96855a9df96926b2f2
Captured plan review request: ccb49e31b0114a96855a9df96926b2f2
  event: ExecutorCompletedEvent
  event: SuperStepCompletedEvent
Using checkpoint cca1a13585ad4530a3ec91cd1514c495 at session ce5ab1cc774940af916d08cf5d632165
Pending plan-review requests persisted in checkpoint: ['ccb49e31b0114a96855a9df96926b2f2']
Checkpoint id recorded in /var/folders/g2/v7hw2fhs2mjd_5dxggk52cn00000gn/T/band-g-checkpoints.MPN9vk/gate.json

=== Process exits at the gate; approval is supplied by resume.sh ===
  checkpoint store: ce5ab1cc774940af916d08cf5d632165_cca1a13585ad4530a3ec91cd1514c495%2Ejson (18801 bytes)
  checkpoint store: gate.json (145 bytes)
  checkpoint store: index.jsonl (229 bytes)

/var/folders/g2/v7hw2fhs2mjd_5dxggk52cn00000gn/T/band-g-checkpoints.MPN9vk
```

stderr: (empty)

## Process 2 — `./resume.sh <checkpoint-directory>`

A new process, with a new checkpoint store, checkpoint manager, execution environment and workflow.
It restored the gate checkpoint, the pending request was re-emitted with the same request id, the
approval was supplied, and the workflow ran to its final answer. The last lines name a checkpoint id
that is not the gate checkpoint, so checkpointing continued past the gate.

```
checkpoint directory: /var/folders/g2/v7hw2fhs2mjd_5dxggk52cn00000gn/T/band-g-checkpoints.MPN9vk

=== Stage 2: resume from checkpoint and approve plan ===
Resuming from checkpoint cca1a13585ad4530a3ec91cd1514c495 (session ce5ab1cc774940af916d08cf5d632165)
  event: RequestInfoEvent ccb49e31b0114a96855a9df96926b2f2
Resumed plan review request: ccb49e31b0114a96855a9df96926b2f2 (matches the recorded request)
Approving plan and continuing to completion.
  event: WorkflowStartedEvent
  event: SuperStepStartedEvent
  event: ExecutorInvokedEvent MagenticOrchestrator
  event: MagenticProgressLedgerUpdatedEvent
  event: ExecutorCompletedEvent
  event: SuperStepCompletedEvent
  event: SuperStepStartedEvent
  event: ExecutorInvokedEvent ResearcherAgent_ResearcherAgent
  event: ExecutorCompletedEvent
  event: ExecutorInvokedEvent ResearcherAgent_ResearcherAgent
  event: ExecutorCompletedEvent
  event: SuperStepCompletedEvent
  event: SuperStepStartedEvent
  event: ExecutorInvokedEvent MagenticOrchestrator
  event: ExecutorCompletedEvent
  event: ExecutorInvokedEvent MagenticOrchestrator
  event: MagenticProgressLedgerUpdatedEvent
  event: ExecutorCompletedEvent
  event: SuperStepCompletedEvent
  event: SuperStepStartedEvent
  event: ExecutorInvokedEvent WriterAgent_WriterAgent
  event: ExecutorCompletedEvent
  event: ExecutorInvokedEvent WriterAgent_WriterAgent
  event: ExecutorCompletedEvent
  event: ExecutorInvokedEvent WriterAgent_WriterAgent
  event: ExecutorCompletedEvent
  event: SuperStepCompletedEvent
  event: SuperStepStartedEvent
  event: ExecutorInvokedEvent MagenticOrchestrator
  event: ExecutorCompletedEvent
  event: ExecutorInvokedEvent MagenticOrchestrator
  event: MagenticProgressLedgerUpdatedEvent
  event: WorkflowOutputEvent
  event: ExecutorCompletedEvent
  event: SuperStepCompletedEvent
  event: SuperStepStartedEvent
  event: ExecutorInvokedEvent ResearcherAgent_ResearcherAgent
  event: ExecutorCompletedEvent
  event: SuperStepCompletedEvent

=== Final Answer ===
Certainly! Here is the internal brief designed to guide the collaboration between your research and implementation teams for launching the beta feature of data-driven email summarization:

---

### Internal Brief: Collaborating for Beta Launch of Email Summarization Feature

**Introduction:**

To launch a successful beta version of our data-driven email summarization feature, effective collaboration between our research and implementation teams is essential. This document outlines the key milestones, potential risks, and suggested communication cadence to ensure a smooth and efficient project execution.

**Key Milestones:**

1. **Goal Definition (Weeks 1-2):** Establish clear objectives and scope for the summarization feature. Ensure alignment on requirements with stakeholders.
   
2. **Research and Model Selection (Weeks 3-5):** Evaluate and choose appropriate NLP models, considering options such as BERT or GPT-3, and decide whether to use extractive or abstractive summarization techniques.

3. **Development and Testing (Weeks 6-10):** Implement chosen models using libraries like NLTK, SpaCy, and Transformers, followed by rigorous internal testing.

4. **Beta Launch Preparation (Weeks 11-12):** Finalize internal testing and prepare user guides. Launch the beta version to a select user group to gather initial feedback.

5. **Feedback Analysis and Refinement (Weeks 13-14):** Gather and analyze feedback, making necessary refinements to the feature.

6. **Public Launch Preparation (Weeks 15-16):** Plan a marketing strategy, ensure system scalability, and complete end-user documentation.

**Potential Risks:**

- **Security and Privacy:** Managing potential vulnerabilities during early releases.
- **Technical Debt:** Risk of accumulating unresolved issues.
- **Market Misalignment:** Ensuring feature relevancy and user engagement.
- **User Experience Issues:** Addressing negative feedback from initial features.

**Communication Cadence:**

- **Weekly Meetings:** Conduct weekly team meetings to discuss updates and next steps.
- **Bi-weekly Sprint Reviews:** Assess sprint outcomes and incorporate feedback.
- **Monthly Stakeholder Updates:** Provide progress reports to stakeholders to ensure project alignment.

By following this structured plan, we aim to successfully collaborate and deliver an optimized email summarization feature to our users.

--- 

If you have any further adjustments or questions, feel free to ask!
Checkpoint after approval: a0a08b4e8da84e42b717f242be83bf26 (was cca1a13585ad4530a3ec91cd1514c495 at the gate)
  checkpoint store: ce5ab1cc774940af916d08cf5d632165_8396a5c14ef74c05b248f2122da31f22%2Ejson (41910 bytes)
  checkpoint store: ce5ab1cc774940af916d08cf5d632165_999dcf1a6f1841a1b77f819ae59f46d6%2Ejson (34452 bytes)
  checkpoint store: ce5ab1cc774940af916d08cf5d632165_a0a08b4e8da84e42b717f242be83bf26%2Ejson (41127 bytes)
  checkpoint store: ce5ab1cc774940af916d08cf5d632165_cca1a13585ad4530a3ec91cd1514c495%2Ejson (18801 bytes)
  checkpoint store: ce5ab1cc774940af916d08cf5d632165_d58728ada6134111a5e4f5d3d09222bc%2Ejson (40062 bytes)
  checkpoint store: ce5ab1cc774940af916d08cf5d632165_e429e7f725dc4d30a9c92b3b19f705ef%2Ejson (20567 bytes)
  checkpoint store: ce5ab1cc774940af916d08cf5d632165_e52d846b67ec48b2bfac44145ab0b904%2Ejson (28099 bytes)
  checkpoint store: gate.json (145 bytes)
  checkpoint store: index.jsonl (1939 bytes)
```

stderr: (empty)

## What the capture shows

| Claim | Where it is visible |
| --- | --- |
| Process 1 stops at a human gate without doing the task | `Captured plan review request: …` then `Using checkpoint …` then the process ends. Process 1's event trace contains no participant turn, and there is no final answer in its output. |
| The gate checkpoint is on disk and holds the pending request | `Pending plan-review requests persisted in checkpoint: ['…']`, read back out of the checkpoint file, plus the `checkpoint store:` listing that names the file. |
| The checkpoint id crosses the process boundary | `run.sh` prints the directory; `gate.json` in it carries the session id, checkpoint id and request id; `resume.sh` takes that directory as its argument. |
| Process 2 resumes rather than restarting | `Resuming from checkpoint … (session …)`, then `Resumed plan review request: … (matches the recorded request)` — the same request id process 1 recorded. |
| The pre-gate work is not redone | Process 1 never invoked `ResearcherAgent` or `WriterAgent`. Process 2's trace after approval resumes at the orchestrator (`MagenticProgressLedgerUpdatedEvent`) with the restored task ledger and plan, then runs the participants. The manager does not regenerate the plan. |
| Checkpointing continues after approval | `Checkpoint after approval: <new id> (was <gate id> at the gate)`. |

## Not captured

Nothing was skipped: both processes ran to their own completion against a live model endpoint.

Three caveats, so the band does not read more into this than is there:

- The ids and the final-answer text are from this run only; a rerun produces new ids and different
  prose. What is stable is the shape: the request id recorded at the gate equals the request id
  re-emitted on resume, and the post-approval checkpoint id differs from the gate checkpoint id.
- The endpoint is a hosted gateway in front of an OpenAI model, not `api.openai.com` directly. The
  orchestration and checkpoint code paths are the same; only the HTTP endpoint differs.
- With `openai/gpt-4o-mini` the same code path resumed and completed as well, but the manager hit its
  `max_round_count` of 10 and the workflow's output was
  `Task execution stopped due to hitting the maximum round count limit.` That is the model needing
  more coordination rounds, not a resume failure. The capture above uses `openai/gpt-4o`, which
  converged inside the cap.

## Reproduction

```sh
cd real-world-scenarios/band-g-long-running
export OPENAI_API_KEY=...  OPENAI_BASE_URL=...  OPENAI_MODEL=...
CKPT="$(./run.sh | tail -1)"
./resume.sh "$CKPT"
```
