# Band F baseline — handoff orchestration with a triage agent and three specialists

This directory reproduces the delegation shape of the reference implementation for Band F
(multi-agent orchestration): `microsoft/agent-framework`'s `handoff_simple.py`. It is the baseline
side of the comparison; the Durable variant is F2 (#172), and the fault evidence is F3 (#173).

**The baseline delegates for real.** One agent decides that another should take over, and the
conversation moves to that agent. The evidence file under `evidence/` is our reproduction of that
against a live endpoint — the transcript and the control-transfer log — not a quotation of the
sample's README. Any comparison the band publishes has to treat "control transferred" as something
the baseline already does.

## What is here

| Path | What it is |
| --- | --- |
| `upstream/` | The reference sample, its autonomous sibling, and the repository's `LICENSE`, byte-identical to a pinned commit. Hashes in `upstream/PROVENANCE.md`. |
| `baseline/BaselineHandoff.cs` | The C# port. Single file-based app, two modes: `run` and `resume`. |
| `run.sh` | Runs the workflow with the sample's scripted user, prints the control-transfer trace, writes the log. `--resume <dir>` continues from a checkpoint directory instead. |
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
middle of the captured run moves `triage_agent → return_agent → refund_agent → return_agent` — four
agents in one user turn, each of them a real framework executor. That is not two agents chatting: the
framework's own handoff tool call is what moves the turn.

Second, its shape is the one the rest of the band needs to fault. The conversation is a single user
turn at a time with a single agent holding control at any moment, which is what makes "who held
control when the process died?" a well-posed question. `handoff_autonomous.py` adds an autonomous
loop per agent (`.with_autonomous_mode()`), which is a useful variant but a *different* question: it
is about one agent iterating, not about the control transfer itself. It is vendored as a sibling
reference and is not ported.

The one thing the simple sample lacks for our purpose is a *second* user turn after the specialist
answers. It ships three scripted responses, and its `sample output` block ends after the third, so
the conversation can finish while a specialist still holds control. The port adds a fourth scripted
message so the captured run also shows control returning to the coordinator and then moving to a
different specialist. The responses the sample does ship are used verbatim and in order.

## What the port preserves

| Baseline behaviour | In the port |
| --- | --- |
| One triage/coordinator agent plus three specialists (refund, order, return) | Same four agents, same names (`triage_agent`, `refund_agent`, `order_agent`, `return_agent`) and the sample's instructions |
| One tool per specialist, with the sample's exact result strings | `process_refund`, `check_order_status`, `process_return` via `AIFunctionFactory`, same messages |
| Tools require no approval (`approval_mode="never_require"`) | Plain `AIFunction` — the .NET default |
| Mesh handoff topology: participants are wired to each other, triage is the start agent | `new HandoffWorkflowBuilder(triage).AddParticipants([refund, order, returns])`, which produces the same all-to-all wiring |
| Handoff is the framework's own mechanism: a `handoff_to_<target>` function call from the current agent | Same mechanism; the port reads the framework's `AgentResponseUpdateEvent` for that call rather than hand-rolling routing |
| Scripted user responses instead of interactive input | The same three responses, in order, plus a fourth turn (see above) |
| Per-service-call history persistence | `ChatClientAgent` sessions, which is what the .NET handoff executor checkpoints |
| `FileCheckpointStorage` — a file-backed checkpoint store, cleared at startup | `FileSystemJsonCheckpointStore` — the framework's own file-backed store, one JSON file per checkpoint plus `index.jsonl`. The directory is a scratch directory created by `run.sh`, which is a stronger version of the same guarantee |
| A run that stops when an agent asks for user input | Same: the run yields when the workflow goes idle with the specialist's question |

## What the port adds: the control-transfer log

The issue asks for the delegation to be observable, so that a later fault run can assert "resumed at
agent B, not agent A". Two additions, neither of which changes the orchestration:

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

The `turn_end` record is the assertion the fault band needs: it pairs *who held control* with *the
checkpoint id written at that moment*. F3 can kill the process, resume from that checkpoint, and
compare the `resumed` record against it.

`resume` mode is what makes that testable now. It opens the newest checkpoint in the directory,
skips the user turns already recorded as complete, and prints which agent held control. A resume of
a finished run therefore does no model work and says so:

```
[control] resumed with refund_agent holding control; skipping 4 completed turn(s)
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
  executor checkpoints, which is the equivalent behaviour on this side, but it is not a flag the port
  sets — the client owns it.

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

`run.sh` prints the checkpoint directory as its last line, so a caller can chain the two. `KEEP=1`
keeps the directory and its log even on failure; a successful run of a directory `run.sh` created
removes it unless `KEEP=1`. A directory the script did not create (an existing empty one, or a
caller-supplied `CHECKPOINT_DIR`) is never deleted. `./run.sh --help` prints the same rules from the
script header.

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | The workflow completed. The checkpoint directory is the last line of stdout. |
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

Four agents held control in one user turn. The final turn shows the coordinator routing to a
different specialist:

```
- User: One more thing: please also process a refund for order 1234.
[control] triage_agent holds control
[control] handoff requested by triage_agent (handoff_to_1)
[control] refund_agent holds control
[control] handoff triage_agent -> refund_agent
```

`evidence/control-transfer.jsonl` is the same run's log. Its `turn_end` records pair the holder with
the checkpoint written at that point:

| Turn | Agent holding control at turn end | Checkpoint |
| --- | --- | --- |
| 0 | `triage_agent` | `bc5ac915c281408eadc83ca34acedd2f` |
| 1 | `return_agent` | `7135bf18e9d54690a77724aed43621af` |
| 2 | `triage_agent` | `4d9e98cc85344ef290fd4ab0eb1728dd` |
| 3 | `refund_agent` | `156a4dcecfce4e0287dc65f51189b4f2` |

`./run.sh --resume <that directory>` then exited 0 and printed:

```
=== Resuming from checkpoint 156a4dcecfce4e0287dc65f51189b4f2 (session 2b7771f5f5394204940adf7f29691a5b) ===
[control] session 2b7771f5f5394204940adf7f29691a5b
[control] resumed with refund_agent holding control; skipping 4 completed turn(s)
```

That is a restore of the workflow's own state — the resumed process rebuilt the four agents, opened
the checkpoint, and read back that `refund_agent` was the last holder. It made no model call,
because every scripted turn was already recorded as complete.

Model behaviour is not deterministic: an earlier run of the same code sent `return_agent` to
`refund_agent` and back again, which is the `handoff_to_2` / `handoff_to_3` pair visible in the
trace. The captured evidence is one run, not a fixed script.

## What the baseline does well

- Handoff is a framework feature, not application code: declaring the participants and the start
  agent is the whole orchestration. The handoff tools, the routing switch, the per-agent session and
  the mesh edges are generated.
- It checkpoints the workflow as it runs, with a file-backed store, so a process can resume from a
  serialized checkpoint. The resumed process rebuilds the graph and continues; the pre-checkpoint
  model calls are not repeated.
- It keeps each agent's session, so a specialist that regains control continues its own reasoning
  rather than restarting the conversation.
- The topology is the sample's: any participant may hand off to any other, so control can move
  specialist-to-specialist without going back through the coordinator (the captured run does exactly
  that).

## What it does not do

These are observations from building and running the port, not criticisms of the sample:

- **The checkpoint is the workflow's state, not the domain's.** A checkpoint restores who was
  speaking and what the conversation was; it does not know that a return was initiated for order
  1234 as a fact separate from the transcript. F2 is where that distinction gets tested.
- **Nothing is transactional.** The specialist's tool runs inside the model call; if the process
  dies after the tool result but before the next checkpoint, the tool's effect is not recorded
  anywhere. The port's own log is appended outside the checkpoint for the same reason, and it is
  evidence, not a commit record.
- **The store is single-process by construction.** `FileSystemJsonCheckpointStore` opens
  `index.jsonl` with `FileShare.None` and throws if a second process opens it. That is acceptable for
  a sequential handoff and rules out two coordinators sharing one checkpoint directory.
- **The handoff log is derived, not authoritative.** It is written by the port, so it is only as
  complete as the events the framework emits. `turn_end` names the newest checkpoint id observed
  during that turn; it does not prove which checkpoint the *next* turn started from.
- **There is no retry, timeout or backoff around the model calls.** A transient provider error fails
  the process and leaves the last checkpoint as the restart point.
