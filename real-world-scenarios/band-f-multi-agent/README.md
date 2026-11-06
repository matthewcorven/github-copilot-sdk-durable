# Band F baseline — handoff orchestration with a triage agent and three specialists

This directory reproduces the delegation shape of the reference implementation for Band F
(multi-agent orchestration): `microsoft/agent-framework`'s `handoff_simple.py`. It is the baseline
side of the comparison; the Durable variant is F2 (#172), and the fault evidence is F3 (#173) —
`faults/`, including `faults/README.md`, which is the fault matrix and its reasoning.

**The baseline delegates for real.** One agent decides that another should take over, and the
conversation moves to that agent. The evidence file under `evidence/` is our reproduction of that
against a live endpoint — the transcript and the control-transfer log — not a quotation of the
sample's README. Any comparison the band publishes has to treat "control transferred" as something
the baseline already does.

**The sample does not checkpoint.** `grep -ci checkpoint` returns 0 for both vendored Python files:
`handoff_simple.py` builds a handoff workflow and drives a request/response loop, with no checkpoint
manager and no file-backed store. Everything this directory says about checkpointing is about the
port's own addition, not about the reference. The distinction matters for F2: the reference is a
delegation baseline, not a durable-execution baseline, and the comparison must not be framed as
"the sample already does this and we do it better".

## What is here

| Path | What it is |
| --- | --- |
| `upstream/` | The reference sample, its autonomous sibling, and the repository's `LICENSE`, byte-identical to a pinned commit. Hashes in `upstream/PROVENANCE.md`. |
| `baseline/BaselineHandoff.cs` | The C# port. Single file-based app, two modes: `run` and `resume`. |
| `run.sh` | Runs the workflow with the sample's scripted user, prints the control-transfer trace, writes the log. `--resume <dir>` continues from a checkpoint directory instead; `--clean` removes a scratch directory after a successful run. |
| `evidence/run-stdout.txt` | Captured stdout of a real run: the delegation trace and the final conversation. |
| `evidence/control-transfer.jsonl` | The same run's machine-readable control-transfer log, one record per transition. |
| `evidence/resume-stdout.txt` | Captured stdout of a resume against the same directory, naming the agent that held control. |
| `evidence/endpoint-required.txt` | What `run.sh` does when no endpoint is configured. |
| `faults/` | The F3 fault harness: five faults against both variants, each result naming the post-recovery controlling agent. See `faults/README.md`. |

## Pinned source

| Field | Value |
| --- | --- |
| Repository | `microsoft/agent-framework` |
| Commit | `cf734bc4692ccfc944496a5b3182b3d65483c44d` |
| License | MIT |
| Sample | `python/samples/03-workflows/orchestrations/handoff_simple.py` |
| Sibling | `python/samples/03-workflows/orchestrations/handoff_autonomous.py` |

The vendored files are byte-identical to that commit: `git hash-object` returns the blob ids the
GitHub API reports for the same paths, and a re-fetch reproduces the same SHA-256 sums.

## Which sample was chosen, and why

**`handoff_simple.py`, not `handoff_autonomous.py`.** The simple sample is not too thin for this
band's purpose, and it is the better baseline for two reasons.

First, it demonstrates exactly the thing Band F is about: control transfer between agents that each
own a different capability. The triage agent receives the user's request and routes it; the
specialists own a refund tool, an order-status tool and a return tool respectively. A turn in the
middle of the captured run moves `triage_agent → return_agent → refund_agent → return_agent` — three
distinct agents and four steps of control in one user turn, each step a real framework executor. That
is not two agents chatting: the framework's own handoff tool call is what moves the turn.

Second, its shape is the one the rest of the band needs to fault. The conversation is a single user
turn at a time with a single agent holding control at any moment, which is what makes "who held
control when the process died?" a well-posed question. `handoff_autonomous.py` adds an autonomous
loop per agent (`.with_autonomous_mode()`), which is a useful variant but a *different* question: it
is about one agent iterating, not about the control transfer itself. It is vendored as a sibling
reference and is not ported.

The one thing the simple sample lacks for our purpose is a turn that reaches the third specialist.
It ships three scripted responses, and none of them asks about an order's status, so `order_agent`
never takes control. The port inserts one extra message before the sample's closing turn. The
sample's three responses are used verbatim and in their original order; the insertion is the port's
only change to the script, and it is placed before "thanks" because the sample's termination
condition ends the conversation on that closing message:

| # | Message | Source |
| --- | --- | --- |
| 0 | `Hello, I need assistance with my recent purchase.` | the sample's initial message |
| 1 | `My order 1234 arrived damaged and the packaging was destroyed. I'd like to return it.` | the sample, response 1 |
| 2 | `Please also process a refund for order 1234.` | the sample, response 2 |
| 3 | `One more thing: what is the status of order 1234?` | **added by the port** |
| 4 | `Thanks for resolving this.` | the sample, response 3 |

## What the port preserves

| Baseline behaviour | In the port |
| --- | --- |
| One triage/coordinator agent plus three specialists (refund, order, return) | Same four agents, same names (`triage_agent`, `refund_agent`, `order_agent`, `return_agent`) and the sample's instructions |
| One tool per specialist, with the sample's exact result strings | `process_refund`, `check_order_status`, `process_return` via `AIFunctionFactory`, same messages |
| Tools require no approval (`approval_mode="never_require"`) | Plain `AIFunction` — the .NET default |
| Mesh handoff topology: participants are wired to each other, triage is the start agent | `new HandoffWorkflowBuilder(triage).AddParticipants([refund, order, returns])`, which produces the same all-to-all wiring |
| Handoff is the framework's own mechanism: a `handoff_to_<target>` function call from the current agent | Same mechanism; the port reads the framework's `AgentResponseUpdateEvent` for that call rather than hand-rolling routing |
| Scripted user responses instead of interactive input | The same three responses, verbatim and in order, with one extra turn inserted before the last (see above) |
| Per-service-call history persistence | `ChatClientAgent` sessions, which is what the .NET handoff executor keeps |
| Termination when the last message contains "welcome" | The same predicate on `WithTerminationCondition`, and the same observable effect: the scripted loop stops instead of asking for another user turn |
| A run that stops when an agent asks for user input | Same: the run yields when the workflow goes idle with the specialist's question |

## What the port adds

The sample has no checkpoint manager and no observation of its own. Three additions, none of which
changes the orchestration:

### Checkpointing

`FileSystemJsonCheckpointStore` — the framework's file-backed store, one JSON file per checkpoint plus
`index.jsonl` — with `CheckpointManager.CreateJson` and
`InProcessExecution.Default.WithCheckpointing(...)`. It is enabled on every run, and `resume` mode
reopens the newest checkpoint in the directory. **This is not in the sample**: `handoff_simple.py`
never mentions a checkpoint. It is the port's addition, permitted by the issue so that the baseline
can be faulted on the same axis as the Durable variant. A comparison must attribute it to the port.

### The control-transfer log

The issue asks for the delegation to be observable, so that a later fault run can assert "resumed at
agent B, not agent A". Two parts:

1. **A console trace.** Every agent executor invocation prints `[control] <agent> holds control`;
   every framework handoff call prints `[control] handoff requested by <agent> (<function>)`; every
   turn ends with `[control] handoff <from> -> <to>` lines for the transitions that actually happened.
   The executor id is the framework's (`<agent.Id>_<agent.Name>`), and it is mapped back to the
   stable agent id the sample uses.
2. **A JSONL log** at `$CONTROL_LOG` (default `$CHECKPOINT_DIR/control-transfer.jsonl`). One record
   per observed event:

| `Event` | Fields that matter | Meaning |
| --- | --- | --- |
| `session` | `SessionId` | The workflow session this run belongs to. |
| `control` | `Agent` | That agent's executor was invoked; it held control for this step. |
| `handoff_requested` | `Agent`, `Function` | The framework recorded a `handoff_to_*` call from that agent. |
| `handoff` | `From`, `Agent` | Control moved from one agent to another within a user turn. |
| `waiting` | `Checkpoint` (request id) | The workflow is waiting on an external request. |
| `turn_end` | `Turn`, `Agent`, `Checkpoint` | The user turn finished; this agent held control and this checkpoint id is the newest. |
| `resumed` | `Turn`, `SessionId`, `Agent` | This process resumed; the named agent held control at the checkpoint it resumed from. |
| `terminated` | `Turn` | The sample's termination condition fired after this turn; the loop stopped. |

The `turn_end` record is the assertion the fault band needs: it pairs *who held control* with *the
checkpoint id written at that moment*. F3 can kill the process, resume from that checkpoint, and
compare the `resumed` record against it.

`resume` mode is what makes that testable now. It opens the newest checkpoint in the directory,
skips the user turns already recorded as complete, and prints which agent held control. A resume of
a finished run therefore does no model work and says so:

```
[control] resumed with triage_agent holding control; skipping 5 completed turn(s)
```

## What the port drops

- **The `.env` file and Azure CLI credential.** The sample authenticates with `AzureCliCredential`
  against a Foundry project. The port takes an OpenAI-compatible endpoint from `OPENAI_API_KEY`,
  `OPENAI_BASE_URL` and `OPENAI_MODEL`, so no Azure subscription is needed. This is a provider
  substitution only; the orchestration is unchanged.
- **Interactive input.** The sample also uses scripted responses, so nothing is lost; the port keeps
  them and does not add a console loop.
- **`require_per_service_call_history_persistence=True`.** In the Python sample this asks the client
  to persist per-service-call history. The .NET agent keeps its history in the session the handoff
  executor holds, which is the equivalent behaviour on this side, but it is not a flag the port
  sets — the client owns it.

Nothing else is dropped. In particular the termination condition is implemented, not skipped (see
"What the port preserves").

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| .NET SDK | 10.0.402 | Must resolve to `/opt/homebrew/bin/dotnet`. File-based apps (`dotnet run file.cs`) need .NET 10. |
| An OpenAI-compatible endpoint | — | `OPENAI_API_KEY` is required. `OPENAI_BASE_URL` defaults to `https://api.openai.com/v1`; any compatible gateway works. `OPENAI_MODEL` defaults to `gpt-4o`; the captured evidence used `openai/gpt-4o-mini` through OpenRouter. |
| Network access to NuGet | — | The two `Microsoft.Agents.AI.*` packages are restored on first run. |

No Azure subscription, no `az login`, no Foundry project.

Without `OPENAI_API_KEY`, `run.sh` exits 1 before building anything and prints the three variables it
understands; `evidence/endpoint-required.txt` is that output. No run is claimed without one.

## Commands

```sh
export OPENAI_API_KEY=...
export OPENAI_BASE_URL=https://openrouter.ai/api/v1   # optional
export OPENAI_MODEL=openai/gpt-4o-mini                # optional, default is gpt-4o

./run.sh                                # run, print the control trace, print the directory
CKPT="$(./run.sh | tail -1)"            # or capture the directory for a later resume
./run.sh --resume "$CKPT"               # resume from the newest checkpoint in that directory
```

`run.sh` prints the checkpoint directory as its last line and **keeps it**, on success and on
failure, because it is the handoff artifact the resume needs — the two-line recipe above is the
supported path. `--clean` (or `CLEAN=1`) removes a directory `run.sh` created once the run has
succeeded. A directory the script did not create (an existing empty one, or a caller-supplied
`CHECKPOINT_DIR`) is never deleted. `./run.sh --help` prints the same rules from the script header.

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | The workflow completed. The checkpoint directory is the last line of stdout and is kept. |
| 1 | `OPENAI_API_KEY` unset, the build failed, or the workflow reported an error. The directory is kept so the failure can be read back. |
| 2 | Bad usage, or a `CHECKPOINT_DIR` that exists and is not empty and was not created by this script. The path is not touched. |

## What was executed

`./run.sh` was run for real on 2026-11-05 against `https://openrouter.ai/api/v1` with
`OPENAI_MODEL=openai/gpt-4o-mini`, and exited 0. `evidence/run-stdout.txt` is its stdout, pasted
unedited; stderr was empty. The delegation it shows:

```
- User: My order 1234 arrived damaged and the packaging was destroyed. I'd like to return it.
[control] triage_agent holds control
[control] handoff requested by triage_agent (handoff_to_3)
[control] return_agent holds control
[control] handoff requested by return_agent (handoff_to_2)
[control] refund_agent holds control
[control] handoff requested by refund_agent (handoff_to_3)
[control] return_agent holds control
...
[control] handoff triage_agent -> return_agent
[control] handoff return_agent -> refund_agent
[control] handoff refund_agent -> return_agent
```

Three distinct agents held control in that one user turn: `triage_agent`, then `return_agent`, then
`refund_agent`, then `return_agent` again — four steps, three agents. Later turns show the
coordinator routing to the other two specialists:

```
- User: Please also process a refund for order 1234.
[control] triage_agent holds control
[control] handoff requested by triage_agent (handoff_to_1)
[control] refund_agent holds control
[control] handoff triage_agent -> refund_agent

- User: One more thing: what is the status of order 1234?
[control] triage_agent holds control
[control] handoff requested by triage_agent (handoff_to_2)
[control] order_agent holds control
[control] handoff triage_agent -> order_agent
```

All four agents therefore took control at least once in this run, and the last line shows the
sample's termination condition doing its job:

```
- User: Thanks for resolving this.
[control] triage_agent holds control
[control] termination condition fired on turn 4 (the agent's closing message contains "welcome")
```

`evidence/control-transfer.jsonl` is the same run's log. Its `turn_end` records pair the holder with
the checkpoint written at that point, and `terminated` records where the loop stopped:

| Turn | Agent holding control at turn end | Checkpoint |
| --- | --- | --- |
| 0 | `triage_agent` | `91ec9f2d37f24c389dea6bbf00b3bd77` |
| 1 | `return_agent` | `eeb961d9efc549eb820180bf6bc8596a` |
| 2 | `refund_agent` | `2de2a2d5ea9e4f51abbc026086cfe9f0` |
| 3 | `order_agent` | `6b3b33e50d364f2c8c5c27c739580e4d` |
| 4 | `triage_agent` | `b21f3875b5f04a33963600ea9375f9c5` |

The chained recipe from §Commands was then run against that directory, exactly as written, and both
halves exited 0:

```sh
CKPT="$(./run.sh | tail -1)"
./run.sh --resume "$CKPT"
```

```
=== Resuming from checkpoint b21f3875b5f04a33963600ea9375f9c5 (session bf5854168f7642e19a964b948b12893c) ===
[control] session bf5854168f7642e19a964b948b12893c
[control] resumed with triage_agent holding control; skipping 5 completed turn(s)
```

That is a restore of the workflow's own state — the resumed process rebuilt the four agents, opened
the checkpoint, and read back that `triage_agent` was the last holder. It made no model call,
because every scripted turn was already recorded as complete. `evidence/resume-stdout.txt` is that
output.

Model behaviour is not deterministic: the same code on an earlier run sent `return_agent` to
`refund_agent` and back, and on another resumed at `order_agent`. The captured evidence is one run,
not a fixed script.

## What the baseline does well

- Handoff is a framework feature, not application code: declaring the participants and the start
  agent is the whole orchestration. The handoff tools, the routing switch, the per-agent session and
  the mesh edges are generated.
- It keeps each agent's session, so a specialist that regains control continues its own reasoning
  rather than restarting the conversation.
- The topology is the sample's: any participant may hand off to any other, so control can move
  specialist-to-specialist without going back through the coordinator (the captured run does exactly
  that).
- It stops when the conversation has closed, on a one-line predicate over the last message. That is
  a smaller mechanism than a plan or a completion signal, and it is enough for the sample's purpose.

Everything about checkpointing belongs to the port, not to this list. The sample has no checkpoint
manager; `FileSystemJsonCheckpointStore`, `CheckpointManager`, the `resume` mode and the `turn_end`
checkpoint ids are all additions made here.

## What it does not do

These are observations from building and running the port, not criticisms of the sample:

- **The checkpoint is the workflow's state, not the domain's.** A checkpoint restores who was
  speaking and what the conversation was; it does not know that a return was initiated for order
  1234 as a fact separate from the transcript. F2 is where that distinction gets tested. The
  checkpoint is also the port's, so this is a limit of *this baseline*, not of the reference.
- **Nothing is transactional.** The specialist's tool runs inside the model call; if the process
  dies after the tool result but before the next checkpoint, the tool's effect is not recorded
  anywhere. The port's own log is appended outside the checkpoint for the same reason, and it is
  evidence, not a commit record.
- **The store is single-process by construction.** `FileSystemJsonCheckpointStore` opens
  `index.jsonl` with `FileShare.None` and throws if a second process opens it. That is acceptable for
  a sequential handoff and rules out two coordinators sharing one checkpoint directory.
- **The termination condition only governs autonomous continuation.** In 1.27.0 the predicate is
  evaluated by the framework to decide whether to loop an agent again; it does not end a
  non-autonomous workflow. The sample's own effect — its request/response loop stops instead of
  asking for another turn — is reproduced by the port's scripted loop, which stops when the last
  agent message contains "welcome". So the *effect* matches the sample, but the framework-level
  condition is not what produces it here.
- **The handoff log is derived, not authoritative.** It is written by the port, so it is only as
  complete as the events the framework emits. `turn_end` names the newest checkpoint id observed
  during that turn; it does not prove which checkpoint the *next* turn started from.
- **There is no retry, timeout or backoff around the model calls.** A transient provider error fails
  the process and leaves the last checkpoint as the restart point.

## What Durable adds over a workflow checkpoint file?

The question this band exists to answer, and it has **two** answers because there are two baselines.
The published sample has no checkpointing at all, so a comparison against "the sample" and a
comparison against "the sample plus the port's checkpoint store" are comparisons against different
things. They are answered separately below and must not be quoted as one.

### Part one — against the port's checkpoint store

This is the interesting comparison, because the baseline here really does have a workflow checkpoint
file: `FileSystemJsonCheckpointStore` writes one JSON file per checkpoint plus `index.jsonl`, and the
port's `resume` mode reopens the newest one. That mechanism already answers *who was speaking* — the
fault evidence reads the holder out of it for every baseline row. A cell that treats "the baseline has
no durability" as the gap has misread this variant.

What the checkpoint store does **not** do is put the *domain* on the same footing as the position:

- **The checkpoint is the workflow's state, not the domain's.** Restoring one restores the
  conversation and the executor position. It does not know that a return for order 1234 was initiated
  as a fact separate from the transcript, so a crash between the tool result and the next checkpoint
  has nowhere to put that fact. In the Durable variant one commit writes the specialist's output
  document, the control record that says it exists and the checkpoint that lets the turn settle;
  `verify` requires the output and the record to share an entry id and a killed attempt to leave *no*
  output at all. In the baseline, the port's own README calls the control log "derived, not
  authoritative" — the same interval in which a log and a store can disagree does not exist here.
- **The checkpoint does not pin the model's answer.** A position does not record which specialist the
  model named for a turn. Here the routing decision is committed before it is acted on and read by
  every later attempt, so a resume that would answer differently does not (`evidence.sh` stage 3, and
  stage 3c for what happens when the read is skipped).

The fault evidence for the two rows where that matters:

| Fault | Baseline (port + checkpoint store) | Durable | Measured against |
| --- | --- | --- | --- |
| `kill-mid-delegation` | recovery failed — the resume aborts in the framework (*"Expected exactly one update for key 'SharedState'"*) and control stops at `return_agent` | resumed — a new process gives control to `return_agent`, whose first committed action is turn 0's output at `e45` | port + checkpoint store |
| `restart-after-checkpoint` | restart failed — same abort; control stops at `refund_agent` | resumed by `refund_agent`; the first committed action is turn 1's output at `e68`; `verify` held | port + checkpoint store |

Both baseline cells are *failures to recover*, not "no durability": the checkpoint was written and the
port tried to resume from it. The comparison is therefore "a checkpoint file whose resume cannot
finish" against "a store whose resume does".

### Part two — against the sample as published

Against the sample as published, Durable adds **nothing directly**. `handoff_simple.py` has no
checkpoint manager — `grep -ci checkpoint` returns 0 for both vendored Python files — so there is no
baseline durability mechanism to beat. What the band demonstrates about kill and resume is a property
this project added, first in the port (F1) and then in a different place here (F2). The sample is a
delegation baseline, not a durable-execution baseline; it never set out to be durable.

The claim that *is* earned against the sample is narrower and worth stating exactly: **the delegation
is the durable fact, rather than a log written beside one.** In the sample, "who holds the
conversation" is an executor position inside one running workflow, and the port's log observes it from
outside. Here the control record and the state it describes are written in one transaction. That is a
real difference in design — and it is not a win over the sample's design goals, because surviving a
process death was not one of them. A reader who takes part two as "we do what the sample did, but
better" has read it backwards.

## Where the Durable variant is worse

Stated here rather than left to the other document, because a comparison that only lists wins is not
evidence. All five faults are in `faults/README.md`; these are the rows and costs that count against
this variant:

- **`store-outage` splits the store.** The file is moved aside under a live handle; the process does
  not notice, reopens the original path and finishes there. Nothing committed is lost and nothing is
  torn, but the state is now in two files — 214 record(s) at the live path and the 36 the moved file held —
  and nothing merges them. An operator who restores the moved file *replaces* the post-outage run.
  The baseline's row in the same fault records no half-transition and a surviving index whose last
  entry still names `triage_agent`; "fails safe" is not "fails well", and neither row is a pass.
- **`competing-managers` is a tie.** Both variants refuse the second coordinator at open — the
  baseline in 0 ms via the framework's `index.jsonl` lock, the Durable variant in 1000 ms via
  `store.jsonl.lock`. The Durable refusal names the pid holding the lock; the framework's does not.
  That is a better error message, not a different outcome.
- **`duplicate-delivery` is not a win.** The row reads **not doubled**, and its basis is narrow: on
  the per-turn count, turn 3 committed one output version and zero reuse records for the re-delivered
  request. What stopped the second process was finding the turn's output already committed — not an
  identity for the request. The variant has no record that a delivery is a repeat. The third-delivery
  probe to the finished run (`resume: the run is already finished; there is nothing to resume.`) shows
  only that **a finished run is a no-op**; it says nothing about deduplication and is not part of the
  claim. The baseline's row is `not exercised`: its duplicate delivery was observed but its recovery
  cannot complete, so there is no completed run to count in. That is not a baseline win either — its
  recovery is broken, not safe.
- **The store's single-writer lock rules out concurrency by construction.** One holder at a time is
  the intended shape, but it is also a limit: two coordinators cannot share a store, and there is no
  queue or hand-off between them. The baseline's framework store has the same restriction.
- **No fork is demonstrated.** `lib/Durable.cs` can fork a conversation at an entry and the epic lists
  forking as a candidate advantage. It is not shown here, so it is not claimed.
- **No retry around the model calls.** A transient provider failure fails the run; the last checkpoint
  is the restart point. The recovered attempt is a new process, not a retry loop. The baseline's port
  has the same gap.
- **The specialist's turn is re-entered, not continued.** A crash inside a model turn leaves the
  attempt recorded and nothing it produced, and the resume asks again. The layer cannot continue a
  half-finished model turn. The routing *decision* is memoized because it is cheap and must not change;
  the model call is not, because it cannot be resumed at all.
- **The script is not the sample's script.** Two turns differ (the opening greeting dropped, the
  closing line folded into a request), both because a turn this coordinator cannot delegate is a turn
  it cannot serve. The baseline runs the sample's script with one inserted turn; this variant does not.
- **It costs more code.** 724 durability-specific lines against the port's 145, in the matrix row's
  counting rule (§*Lines of custom durability code*).

## The Durable variant

`durable/` is the sibling of `baseline/`: the same four agents and the same three tools with the same
result strings, with the delegation moved out of a framework executor's position and into a durable
task's checkpoint. The scripted user is close to, but not the same as, the baseline's — the opening
greeting is dropped and the closing line folded into a request, both because a turn this coordinator
cannot delegate is a turn it cannot serve. `durable/README.md` names both changes. Both variants run
from this folder.

```bash
./run.sh                                  # baseline; needs OPENAI_API_KEY (see Prerequisites)
(cd durable && ./run.sh)                  # Durable variant; needs nothing for the scripted stages
(cd durable && ./evidence.sh)             # every claim it makes, with its controls
(cd durable && LIVE=1 ./evidence.sh)      # the same, plus a live-model stage
```

The Durable variant has its own `durable/README.md` with the checkpoint design, the evidence, and the
two honest-comparison paragraphs. Three things are worth knowing before reading the code:

- **`durable/` references `lib/Durable.cs`** through a project reference
  (`../../../lib/Copilot.Durable.csproj`). No part of the store is copied into the scenario.
- **`durable/evidence/run-stdout.txt`** is one full run of `durable/evidence.sh`, pasted unedited —
  including the stage whose deliberate control fails `verify`. It is captured output, not a
  description of what the code should do.
- **The Durable variant is not a faster horse for the sample.** The vendored `handoff_simple.py` has
  no checkpoint manager; everything the variant demonstrates about kill and resume was added by this
  project, first in the port above and then here. `durable/README.md` says which claim is earned
  against which piece of work rather than against the reference.

`baseline/` is unmodified by this work. Its port's checkpointing and its control-transfer log are its
own additions, and the variant's README treats them as such.

## The five faults, transcribed

Run against **both** variants, each fault with a `--fault-not-injected` control. Every cell below is the
`outcome` field of `faults/results/<variant>/<fault>.json`; `faults/results/summary.txt` is rendered
from those files by `faults/lib/summarise.py`, which fails if a variant is missing a fault's result or
its control's. All 20 runs — 5 faults × 2 variants plus 10 controls — exited 0 (`summary.txt`
§*Harness status*); a non-zero exit is a harness error, never a scenario losing work. Harness detail,
the injection points and the controls are in `faults/README.md`.

| Fault | `baseline/` | `durable/` | Measured against | Result files |
| --- | --- | --- | --- | --- |
| `kill -9` mid-delegation | **recovery failed; control stops at `return_agent`** | **resumed by `return_agent`** | port + checkpoint store | [`baseline`](faults/results/baseline/kill-mid-delegation.json) · [`durable`](faults/results/durable/kill-mid-delegation.json) |
| restart after a committed checkpoint | **restart failed; control stops at `refund_agent`** | **resumed by `refund_agent`** | port + checkpoint store | [`baseline`](faults/results/baseline/restart-after-checkpoint.json) · [`durable`](faults/results/durable/restart-after-checkpoint.json) |
| duplicate delivery | **`not exercised`** | **not doubled** — per-turn count only, not a win | port + checkpoint store | [`baseline`](faults/results/baseline/duplicate-delivery.json) · [`durable`](faults/results/durable/duplicate-delivery.json) |
| two competing managers | **refused: the second manager** | **refused: the second manager** | port + checkpoint store | [`baseline`](faults/results/baseline/competing-managers.json) · [`durable`](faults/results/durable/competing-managers.json) |
| store unavailable mid-run | **no half-transition; the store's path stopped resolving** | **failed safely, with a split store; resumed at `return_agent`** | port + checkpoint store | [`baseline`](faults/results/baseline/store-outage.json) · [`durable`](faults/results/durable/store-outage.json) |

**No row in this table is measured against the sample as published**, because the sample has no
checkpoint to fault: with nothing written, a kill, a restart, a second manager and an outage all
simply lose the process. The sample's own row is the absence of a row, and part two above is where it
is stated.

What each cell says, in the result files' own words. The holder in every row is read from **state** —
the Durable variant's `checkpoint.holder` in its JSONL store, the baseline's newest framework
checkpoint — never from a log line:

- **`kill-mid-delegation`, baseline — recovery failed.** Killed with `SIGKILL` while `return_agent`
  held control and a handoff was queued at that agent's executor. The recovery did not complete:
  attempt 1 and attempt 2 both aborted with *"Expected exactly one update for key 'SharedState'"*.
  The first agent to act in the recovered process was `return_agent`, so control after recovery is
  `return_agent` — read from the checkpoint, not from the port's log.
- **`kill-mid-delegation`, durable — resumed.** *"A new process reopened the same store and gave
  control to return_agent — not back to the coordinator — and its first committed action was
  return_agent produced turn 0 at e45."* `verify` exited 0.
- **`restart-after-checkpoint`, baseline — restart failed.** Stopped with `SIGTERM` at turn 1 delegated
  to `refund_agent` with nothing produced. The restart gave control to `refund_agent`, and then
  aborted on the same framework error. The port's own resume line named `triage_agent`.
- **`restart-after-checkpoint`, durable — resumed.** *"The restarted process read that checkpoint,
  took control as resumed by refund_agent, and its first committed action was refund_agent produced
  turn 1 at e68 … verify held every claim, so nothing the checkpoint recorded was written twice."*
- **`duplicate-delivery`, baseline — `not exercised`.** *"The request the kill interrupted was
  delivered a second time … The delivery could not be completed … and the port refuses a fresh run
  over a directory that already holds one, so there is no completed run to compare counts in."* The
  row is recorded as `not exercised` rather than as a comparison. It is not a durability win for the
  baseline — its recovery is broken, not safe.
- **`duplicate-delivery`, durable — not doubled.** *"On the per-turn count, which is the finding, turn
  3 committed one output version and nothing was reused, so the request arrived twice and the
  specialist's tool ran once."* The observations are `outputVersionsForTurn3: 1` and
  `reuseRecordsForTurn3: 0`. *"What stopped it was that committed output and not an identity for the
  request: the variant has no record that a delivery is a repeat."* The third delivery to the finished
  run is one separate fact — *"the run is already finished; there is nothing to resume"* — and shows
  only that a finished run is a no-op. **Read as a green cell, this row is wrong.**
- **`competing-managers`, baseline — refused.** *"It was refused by the framework checkpoint store's
  lock at open, after 0ms and before it could commit anything … The refusal names no pid."*
- **`competing-managers`, durable — refused.** *"It was refused by the store's single-writer lock at
  open, after 1000ms … The refusal names the pid that holds the lock, so the outcome is a defined
  winner rather than first-past-the-post."* Same outcome, better error.
- **`store-outage`, baseline — no half-transition.** *"The moved index survived intact — 20 index
  entr(y|ies), 0 torn, 20 whose checkpoint document is present … the checkpoint its last entry names
  holds triage_agent. There is no half-transition in the surviving index."* The `entr(y|ies)` is the
  harness's literal text: `faults/store-outage.sh:172` prints that placeholder unrendered instead of
  "entries". Quoted as produced rather than tidied; the count is 20 entries.
- **`store-outage`, durable — failed safely, with a split store.** *"The live process exited on its
  own — it did not notice: it reopened the original path and finished the run there … The state is now
  in two files and nothing merged them … Control was not lost; it was forked, which is the failure
  this row records."* A limitation, not a pass.

### The judgement call in the two `baseline/` recovery rows

On both baseline rows the port's own resume line and the framework's queued handoff disagree about who
holds control: the log prints `resumed with triage_agent holding control`, while the checkpoint names
`return_agent` (`kill-mid-delegation`) or `refund_agent` (`restart-after-checkpoint`), and the first
agent to act after recovery is that specialist. **The evidence reports from state, not from the log**,
so the table's holder is the checkpoint's. The disagreement is real and is not smoothed over: the
port's README already says its control log is "derived, not authoritative", and F3 found the case where
that matters. A reader who trusts the printed line gets a different answer from a reader who reads the
store, and only one of them is the state.

### The before/after controls, and where they prove nothing

Each control is the same script with the injection removed, so a difference between the pair is
attributable to the fault. `lib/summarise.py` flags a pair whose outcome, holder *and* holder at the
injection point all match — such a pair is indistinguishable and proves nothing. Transcribed from
`results/summary.txt` §*Before/after controls*:

| Fault | Variant | Control | Fault | Pair |
| --- | --- | --- | --- | --- |
| `kill-mid-delegation` | baseline | completed in one process, `triage_agent` | recovery failed, `return_agent` | differs in outcome and holder |
| `kill-mid-delegation` | durable | completed in one process, `return_agent` | resumed, `return_agent` | same holder and injection point; differs in that one finishes and one resumes |
| `restart-after-checkpoint` | baseline | restart failed, `triage_agent` | restart failed, `refund_agent` | outcome same, **holder differs** |
| `restart-after-checkpoint` | durable | resumed, `refund_agent` | resumed, `refund_agent` | same outcome and holder; **differs in where it stopped** — the control stopped at phase `route` with `triage_agent` at the injection, the fault at `specialist` with `refund_agent` |
| `duplicate-delivery` | baseline | delivered once, `triage_agent` | not exercised, `—` | differs |
| `duplicate-delivery` | durable | delivered once, `return_agent` | not doubled, `return_agent` | same holder and injection point; the per-turn count is what the pair turns on |
| `competing-managers` | baseline | one manager completed, `triage_agent` | refused, `refund_agent` | differs in outcome and holder |
| `competing-managers` | durable | one manager completed, `return_agent` | refused, `return_agent` | same holder and injection point; the refusal is what the pair turns on |
| `store-outage` | baseline | completed, `triage_agent` | no half-transition, `triage_agent` | same outcome and holder; the injection point differs (`return_agent` at the injection in the control, `triage_agent` in the fault) |
| `store-outage` | durable | completed, `return_agent` | failed safely, `return_agent` | same holder and injection point; the split store is what the pair turns on |

No pair is flagged as indistinguishable: the one that comes closest — the durable
`restart-after-checkpoint` pair — differs in the phase it stopped in, and the evidence for each names
it. The pairs whose holder does not change are still evidence, because in each the fault leaves a store
state the control does not (a refused process, a moved file, an uncommitted attempt) and the result's
`evidence` field names it. What they do not do is prove that the holder changed because of the fault:
on those rows it did not change, and the row says so.

### Recovery cost, measured on both sides

| Variant | What an operator does after a mid-delegation kill |
| --- | --- |
| `baseline/` | Run `./run.sh --resume <checkpoint-dir>`. The port opens the newest checkpoint, prints the holder from its own log, and the framework **aborts** with *"Expected exactly one update for key 'SharedState'"*. The fault harness retried twice and both attempts aborted; the work is not recovered. |
| `durable/` | Run `./resume.sh` (or `run.sh --resume`) against the store. The process reads `checkpoint.holder` and gives control to that agent; in the recorded fault, one process sufficed and `verify` passed. No manual repair, no flag to set. |

The Durable variant's cost is smaller only because its resume finishes. The baseline's failure is not a
missing command — the command exists and runs — it is that the workflow cannot be restored into a
running graph that still has work outstanding.

## Lines of custom durability code

Measured with `wc -l` on 2026-11-05, in this directory:

```bash
wc -l baseline/BaselineHandoff.cs
wc -l durable/Contracts.cs durable/DelegationTask.cs durable/Documents.cs durable/Program.cs \
      durable/Router.cs durable/Script.cs durable/SpecialistRunner.cs
```

| Variant | Files counted | `wc -l` total | Code lines | Durability-specific | Split from |
| --- | --- | ---: | ---: | ---: | --- |
| `baseline/` | `baseline/BaselineHandoff.cs` (444) | **444** | **310** | **145** — the checkpoint store open, the resume mode, the log readers, `ControlLog`, `ControlRecord`, the checkpoint event and the handoff pairing | §*Per-file split* below; region list in the same section |
| `durable/` | 7 files: `Contracts.cs` (211), `DelegationTask.cs` (453), `Documents.cs` (87), `Program.cs` (491), `Router.cs` (215), `Script.cs` (51), `SpecialistRunner.cs` (239) | **1747** | **1129** | **724** — 64% of the variant's code lines | §*Per-file split* below |

Three numbers per variant, and the difference matters:

- **`wc -l`** is every line of the files listed, blank lines and comments included. This is the literal
  measurement; the file list and the command are above so it can be repeated. The two variants are not
  the same size as each other and were never meant to be: the baseline is one file-based app, the
  Durable variant is a project with seven source files.
- **Code lines** is non-blank, non-comment lines, which is the population the durability split is taken
  from. Both totals were computed with the same rule, and the region table below names every range so a
  reader can re-add them.
- **Durability-specific** is a per-region judgement, written down rather than estimated. A line is
  durability-specific when removing it would remove a guarantee the variant exists to show: a document
  or checkpoint definition, a guarded commit, a resume path, the memo read, the seam, the store open,
  the verifier. It is application logic when it would be the same in any version of the agent: the
  prompts, the tool definitions and their result strings, the scripted user, the routing keywords, the
  command line. A reader who disagrees with a call changes the split, not the `wc -l` total.

### Per-file split

`baseline/BaselineHandoff.cs` — 444 `wc -l`, 310 code lines, **145 durability / 165 application**:

| Region | Lines | Kind | Code lines |
| --- | --- | --- | ---: |
| package directives | `:19-21` | application | 3 |
| `using` directives | `:23-32` | application | 10 |
| endpoint and model configuration | `:34-39` | application | 6 |
| control-log path | `:43-44` | durability | 2 |
| checkpoint-directory validation | `:46-50` | durability | 5 |
| client construction | `:52-54` | application | 3 |
| `Agent` factory | `:58-65` | application | 8 |
| the sample's three tools | `:69-85` | application | 15 |
| the four agents | `:87-109` | application | 20 |
| `BuildWorkflow` (handoff mesh + termination) | `:120-126` | application | 7 |
| `JsonOptions` handed to `CheckpointManager` | `:130` | durability | 1 |
| scripted responses | `:137-143` | application | 7 |
| `agentIds` | `:145` | application | 1 |
| `LogJsonOptions` | `:148` | durability | 1 |
| `Mode()` | `:150` | durability | 1 |
| usage check | `:152-156` | application | 5 |
| `resumeSessionId` | `:160` | durability | 1 |
| checkpoint store open + `CheckpointManager` + environment | `:162-164` | durability | 3 |
| workflow construction | `:166` | application | 1 |
| control-log construction | `:167` | durability | 1 |
| startup banner | `:169-172` | application | 4 |
| resume branch (`GetLatestCheckpointAsync`, `ResumeStreamingAsync`) | `:174-197` | durability | 22 |
| run open | `:199` | application | 1 |
| session boundary record | `:200` | durability | 1 |
| session banner | `:201` | application | 1 |
| `await using` close | `:202` | application | 1 |
| user messages | `:205` | application | 1 |
| resume skip block | `:210-216` | durability | 7 |
| turn-loop head | `:218-227` | application | 9 |
| status and stop checks | `:229-238` | application | 9 |
| termination condition | `:243-251` | application | 9 |
| loop tail | `:253-256` | application | 3 |
| `SendTurnAsync` head | `:258-269` | application | 10 |
| executor / handoff / waiting / checkpoint events | `:270-298` | durability | 24 |
| output and error events | `:300-308` | application | 8 |
| event-loop close | `:309` | application | 1 |
| handoff pairing | `:313-326` | durability | 13 |
| `PrintConversation` | `:330-355` | application | 22 |
| log readers | `:358-379` | durability | 18 |
| `ControlLog` | `:382-435` | durability | 37 |
| `ControlRecord` | `:437-444` | durability | 8 |

`durable/` — 1747 `wc -l`, 1129 code lines, **724 durability / 405 application**:

| File | `wc -l` | Code lines | Durability | Application |
| --- | ---: | ---: | ---: | ---: |
| `Contracts.cs` | 211 | 97 | 72 | 25 |
| `Documents.cs` | 87 | 45 | 45 | 0 |
| `DelegationTask.cs` | 453 | 294 | 285 | 9 |
| `Router.cs` | 215 | 150 | 0 | 150 |
| `SpecialistRunner.cs` | 239 | 165 | 9 | 156 |
| `Script.cs` | 51 | 11 | 0 | 11 |
| `Program.cs` | 491 | 367 | 313 | 54 |
| **Total** | **1747** | **1129** | **724** | **405** |

The region table for the seven Durable files, in the same form as the baseline's:

| File | Region | Lines | Kind | Code lines |
| --- | --- | --- | --- | ---: |
| `Contracts.cs` | prelude | `:1-3` | durability | 1 |
| `Contracts.cs` | agent names, tool and instruction switches | `:4-37` | application | 25 |
| `Contracts.cs` | store/task/document names | `:38-61` | durability | 8 |
| `Contracts.cs` | phases | `:62-76` | durability | 6 |
| `Contracts.cs` | `DelegationInput` | `:77-110` | durability | 8 |
| `Contracts.cs` | `DelegationCheckpoint` | `:111-132` | durability | 9 |
| `Contracts.cs` | `RoutingDecision` | `:133-146` | durability | 8 |
| `Contracts.cs` | `SpecialistOutput` | `:147-169` | durability | 11 |
| `Contracts.cs` | `UserTurnRecord` | `:170-176` | durability | 5 |
| `Contracts.cs` | `WireRecord` | `:177-205` | durability | 10 |
| `Contracts.cs` | `DelegationResult` | `:206-211` | durability | 6 |
| `Documents.cs` | prelude | `:1-15` | durability | 2 |
| `Documents.cs` | the four document definitions | `:16-42` | durability | 11 |
| `Documents.cs` | `RunView` | `:43-87` | durability | 32 |
| `DelegationTask.cs` | prelude | `:1-37` | durability | 3 |
| `DelegationTask.cs` | fields and constructor | `:38-60` | durability | 20 |
| `DelegationTask.cs` | `Definition()` | `:61-77` | durability | 10 |
| `DelegationTask.cs` | `RouteAsync` (memo, routing commit, delegation commit) | `:78-201` | durability | 88 |
| `DelegationTask.cs` | `SpecialistAsync` (attempt commit, output + record commit) | `:202-307` | durability | 79 |
| `DelegationTask.cs` | `SettleAsync` | `:308-373` | durability | 43 |
| `DelegationTask.cs` | `ShouldKillAsync` seam | `:374-416` | durability | 21 |
| `DelegationTask.cs` | `KillPoint` seam | `:417-424` | durability | 7 |
| `DelegationTask.cs` | `CompleteAsync` | `:425-432` | durability | 7 |
| `DelegationTask.cs` | `WriteWire` | `:433-443` | durability | 7 |
| `DelegationTask.cs` | `ConsoleLog` | `:444-453` | application | 9 |
| `Router.cs` | whole file — the decision and its two sources | `:1-215` | application | 150 |
| `SpecialistRunner.cs` | prelude, fields, `Install`, `ProduceAsync`, `Prompt`, `ToolFor`, `ToolResults` | `:1-112`, `:123-239` | application | 156 |
| `SpecialistRunner.cs` | `PriorOutputs` (reads prior outputs from the store) | `:113-122` | durability | 9 |
| `Script.cs` | whole file — the user's turns | `:1-51` | application | 11 |
| `Program.cs` | prelude | `:1-16` | durability | 6 |
| `Program.cs` | `Main` (store path, verb dispatch) | `:17-45` | durability | 25 |
| `Program.cs` | `Usage` | `:46-67` | application | 18 |
| `Program.cs` | `Unknown` | `:68-75` | application | 5 |
| `Program.cs` | `RunAsync` (store open, task create, resume) | `:76-214` | durability | 107 |
| `Program.cs` | `KillPoints` | `:215-232` | durability | 15 |
| `Program.cs` | `Report` | `:233-254` | application | 18 |
| `Program.cs` | `TurnsFrom` | `:255-272` | application | 13 |
| `Program.cs` | `ReadWire` | `:273-293` | durability | 18 |
| `Program.cs` | `Show` | `:294-327` | durability | 21 |
| `Program.cs` | `Verify` | `:328-460` | durability | 94 |
| `Program.cs` | `WithRun` | `:461-484` | durability | 20 |
| `Program.cs` | `LogJson` | `:485-491` | durability | 7 |

Read the two splits with three things in mind:

- **`Router.cs` is 0 durability lines and that is a real call, not an oversight.** Routing — a model
  call or three keywords — would exist in a version of this agent with no durable layer. What makes it
  durable is the *commit* of the decision, and that lives in `DelegationTask.RouteAsync` and in
  `RunView`. The split follows the guarantee, not the subject matter.
- **`Contracts.cs`'s records are durability lines, its agent vocabulary is not.** A checkpoint type
  disappears if the state goes elsewhere; `Agents.Tool("refund_agent")` does not.
- **The baseline's 145 is a third of its file and the Durable variant's 724 is nearly two-thirds of
  its**, which is the cost of the comparison stated in one sentence: the baseline is a port that added
  a checkpoint store and a log, and the variant is a program whose control state *is* the store.
- **Not counted:** `run.sh`, `durable/run.sh`, `durable/resume.sh`, `durable/evidence.sh`,
  `durable/forge-missing-output.py`, `faults/**`. They present and check the variants; they are not
  part of the agent. The vendored Python sample is not counted either: `handoff_simple.py` (314 `wc -l`)
  and `handoff_autonomous.py` (167) are the reference, not the baseline's own code.

## Currency and fidelity

| | |
| --- | --- |
| Baseline run recorded | 2026-11-05 against `https://openrouter.ai/api/v1`, `OPENAI_MODEL=openai/gpt-4o-mini`, exit 0 (`evidence/run-stdout.txt`) |
| Durable variant evidence recorded | 2026-11-05 — one full run of `durable/evidence.sh`, exit 0, including the live-model stage 4 against `claude-sonnet-5` (`durable/evidence/run-stdout.txt`) |
| Fault evidence recorded | 2026-11-05 — `faults/run-all.sh`, 20 runs (5 faults × 2 variants plus 10 controls), all exit 0, produced at commit `272baac` (`faults/results/summary.txt`) |
| Fault machine | macOS arm64, .NET SDK 10.0.402. The baseline rows also used an OpenAI-compatible endpoint; the Durable rows used the SDK's bundled runtime with `BAND_F_ROUTING=scripted`, so **the durable fault rows made no model call for routing** |
| Durable layer pinned at | the `dev` tip the scenario was built against — `lib/Copilot.Durable.csproj` reads `0.5.0`, which is **not yet released**. Per the program epic's versioning rule, the scenario's feature PR lands pinned to the release that cuts it; until then these rows describe `dev`, not a published package |
| **What the fault evidence does not cover** | A kill *mid-write* (the store's own crash safety). Every kill lands between two commits, after the commit it names returned. A kill against a **live model** in the Durable variant — the fault rows use scripted routing and the live stage 4 is not killed. A model that answers differently on a second attempt: `BAND_F_FLIP` is a controlled stand-in for that, not evidence of it. Model quality on either side. Cost: no token, latency or dollar figure is recorded for either variant. |
| **Not exercised, and why** | `baseline/` `duplicate-delivery`: the second delivery happened, but the port's resume aborts and a fresh run over a used directory is refused, so there is no completed run to count in. Both variants' **offline** capability: no run lost the network and no cell measures it. |

## Licence

MIT. See `upstream/LICENSE` and `upstream/PROVENANCE.md`.
