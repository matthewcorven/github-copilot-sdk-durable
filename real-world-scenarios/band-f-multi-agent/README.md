# Band F baseline — handoff orchestration with a triage agent and three specialists

This directory reproduces the delegation shape of the reference implementation for Band F
(multi-agent orchestration): `microsoft/agent-framework`'s `handoff_simple.py`. It is the baseline
side of the comparison; the Durable variant is F2 (#172), and the fault evidence is F3 (#173).

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
