# Band F — Durable variant

The same four agents as `../baseline/`: a `triage_agent` that routes, and three specialists that each
own one tool. What changes is where the delegation lives. In the baseline, "who holds the
conversation" is a framework executor's position inside a running workflow, and the port's JSONL log
records it beside a workflow checkpoint. Here it is a field of a durable task's checkpoint, and the
specialist's work is a document committed in the same transaction that says it exists.

```bash
./run.sh                                  # a fresh run in a fresh scratch directory (KEEP=1 to keep it)
./resume.sh                               # reopen it and give control back to the agent the checkpoint names
./run.sh --kill-in-turn 3                 # stop inside a specialist's turn, with the attempt committed
./run.sh --kill-after-delegating 0        # stop once a delegation commits and the specialist holds control
./run.sh --kill-after-routing 2           # stop once a routing decision commits and nothing has moved
./evidence.sh                             # every claim below, with its control; LIVE=1 adds the model stage
```

| Path | What it is |
| --- | --- |
| `Contracts.cs` | The four agent names, the phases, and the task's input, checkpoint, and record types. |
| `Documents.cs` | The four durable documents and `RunView`, the durable read model. There is no coordinator variable. |
| `DelegationTask.cs` | The state machine: `route` → `specialist` → `settle`, one commit per transition. |
| `Router.cs` | The coordinator's decision, by tool call (`model`) or by keyword (`scripted`), recorded with its source. |
| `SpecialistRunner.cs` | One agent turn per specialist, handed its own prior output from the store. |
| `Script.cs` | The user's turns. Three are the baseline's; the fourth is this variant's. |
| `Program.cs` | `run`, `resume`, `wire`, `show`, `verify`. |
| `run.sh`, `resume.sh` | Process 1, and every process after it. Both refuse to touch a directory they did not create. |
| `evidence.sh` | Reproduces every block in `evidence/run-stdout.txt`, including the controls. |
| `evidence/run-stdout.txt` | One full run of `evidence.sh`, pasted unedited. |

## The design, in one commit at a time

**A delegation is a task phase transition.** `DelegationCheckpoint.Holder` names the agent that holds
the conversation, and it is written by the same commit that moves control. The phase is read by
`lib/Durable.cs` out of the checkpoint (`phase`), not held in a field, so the checkpoint is the only
record of where the run is. There is no loop variable in this program that says who is speaking.

Two commits, deliberately separate:

| Commit | Writes | Checkpoint after |
| --- | --- | --- |
| routing | the `band-f.routing` document and the `band-f.user-turn` document, sharing one entry id. **No control record**: nothing moved | unchanged — the coordinator still holds the turn |
| delegation | one `delegated`/`delegated-from-memo` control record naming the routing entry it was made from | `holder` = the specialist, `phase` = `specialist` |
| production | the `band-f.specialist-output` document and one `produced` control record, sharing one entry id | `phase` = `settle`, holder unchanged |
| settle | one `settled` control record | `holder` = `triage_agent`, `turn` = next, or the run completes |

Splitting routing from delegation is what makes design point 4 a real claim rather than a tautology.
A single commit that wrote the decision and moved control could not be resumed in the state "decided
but not delegated", so a memo miss would be unobservable; the evidence's stage 3c depends on that
state existing.

## The five design points, and how each is shown

### 1. A delegation is a task phase transition

```
[control] routing decision for turn 0 committed at e9: return_agent (scripted)
[control] delegation committed at e13: triage_agent -> return_agent holds turn 0 (routing decision e9)
```

`wire` prints the record the store holds, including the entry it was committed under:

```json
{"turn":0,"event":"delegated","agent":"return_agent","request":"start the return: …","routingEntry":"e9","entry":"e13","seq":15}
```

### 2. A kill mid-delegation resumes at the specialist, not the coordinator

Stage 1 kills the process with `SIGKILL` after the delegation commits, and resumes:

```
[control] resume: turn 0 phase 'specialist' — return_agent holds control (delegated at e13)
[control] the checkpoint names return_agent, so control goes to return_agent — not back to triage_agent
```

**Control for this claim (stage 1c).** The same program, resumed from a store whose checkpoint names
the coordinator because the kill landed before the delegation:

```
[control] resume: turn 1 phase 'route' — triage_agent holds control (delegated at (none))
```

That is what "resumed at the wrong agent" prints. Two resumes, two checkpoints, two different
holders — the difference is in the store, not in the program.

### 3. The specialist's output is a document, not transcript text

Stage 2 kills inside the specialist's turn — the attempt is committed, the model has not been asked —
and resumes. The resumed agent is handed its own earlier output as a value:

```
[control] return_agent holds control (delegated at e112, attempt 2)
[control] this is a resumed attempt: return_agent was named by the checkpoint, not by a fresh routing decision
[specialist] return_agent was handed 1 prior output(s) from the store: turn 0 (process_return)
```

Stage 2c then reads the output with the process gone and nothing running:

```
turn 3
  user:     Thanks for resolving this. One last thing: can you confirm the return for order 1234 is still open?
  routing:  return_agent for: start the return: Thanks for resolving this. …
            written 1 time(s), source scripted
  output:   Return initiated successfully for order 1234. You’ll receive return instructions via email.
            return_agent, 2 model turn(s), pid 75733, tool process_return -> Return initiated successfully for order 1234. You will receive return instructions via email.
```

The record carries the tool's own result beside the agent's prose
(`tool process_return -> Return initiated successfully for order 1234. …`), so the claim can be
checked against the tool rather than against the model's summary of it.

**Control for this claim (stage 2d).** `verify`'s whole value is that it fails on a store the claim
rules out, so the evidence forges one: a completed run with turn 3's `band-f.specialist-output`
version and its `produced` record removed. `show` says `output: (none)`, and `verify` exits 1 with
*two* failures rather than skipping the turn:

```
FAIL  turn 3: no output for a committed delegation — the store holds no band-f.specialist-output value for this turn
FAIL  turn 3: no output for a committed delegation — the store holds no produced record for this turn
verify: 2 claim(s) did not hold
```

**The limitation, stated where the claim is made.** "A resumed agent produces" means the specialist
is handed its own prior output and asked to finish its task. It does **not** mean a half-finished
model turn is resumed: this layer cannot continue one, the SDK is never asked to, and the resume
re-enters the specialist's phase as a fresh turn. What survives is the delegation, the routing
decision and whatever the specialist had already committed — not the in-flight call. Stage 2 is the
demonstration, and stage 2d is the check on the other half of the claim: a delegation with no output
must fail `verify`, not be skipped by it.

### 4. The routing decision is memoized

The decision is a document, read before any model call. Stage 3 kills after turn 2's decision commits
and resumes with `BAND_F_FLIP=1` set, which would make the scenario answer with a *different*
specialist — and the resume never gets that far:

```
[control] routing: turn 2 was already routed to order_agent (scripted) — reusing the committed decision, not asking again
```

`BAND_F_FLIP` is set here to label the answer that was **not** asked for. It is unreachable on this
resume because the memo returns before `DecideAsync` is called, which is the short-circuit working.
The flip only becomes observable in stage 3c, where the read is skipped.

**Control for this claim (stage 3c).** The same killed store, resumed with the memo read skipped
(`BAND_F_FORCE_REROUTE=1`), diverges — and `verify` fails:

```
FAIL  turn 2: the routing decision was written once — written 2 time(s)
verify: 1 claim(s) did not hold
```

`BAND_F_FORCE_REROUTE` exists for this control and nothing else. It is read in one place, it is named
in the README, and no evidence block uses it outside stage 3c.

### 5. `lib/Durable.cs`, no private store

`BandFDurable.csproj` references `lib/Copilot.Durable.csproj`. There is no second persistence
mechanism in this directory: no file of its own, no dictionary that outlives a process. `wire`,
`show` and `verify` open the store through `DurableStore.Create` and read it with the same serializer
that wrote it.

## The two honest-comparison paragraphs

**What Durable adds over the port's own file-backed checkpoint store.** The baseline's port persists
`WorkflowCheckpoint` objects with `FileSystemJsonCheckpointStore` and logs control transitions beside
them. That already answers "who was speaking" — so the comparison is not "the baseline has no
durability". What it does not do is put the *domain* on the same footing as the position. In this
variant, one commit writes the specialist's output document, the control record that says it was
produced, and the checkpoint that allows the turn to settle; the evidence proves it by requiring the
output and the record to share an entry id (`verify`: "the output shares its entry with the record
that says it exists"), and by requiring a killed attempt to leave *no* output at all (stage 2: the
store holds the attempt and nothing produced). The baseline's checkpoint restores the conversation;
it does not know that a return for order 1234 was initiated as a fact separate from the transcript,
so a crash between the tool result and the next checkpoint has nowhere to put that fact. The second
addition is the memo: the baseline re-runs its workflow from the checkpoint, which is a position, and
a position does not pin the model's answer for a turn. Here a routing decision is committed before it
is acted on and read by every later attempt, which is why a resume that would answer differently
does not.

**What Durable adds over the sample as published.** Nothing directly: `handoff_simple.py` has no
checkpoint manager at all (`grep -ci checkpoint` returns 0 for both vendored Python files, as the
baseline README records). The reference is a delegation baseline, not a durable-execution baseline,
and this variant is not a faster horse for it. What the comparison can honestly say is that the
sample's control transfer is an executor position inside one process, and that everything the band
demonstrates about kill and resume is a property this project added — to the port first, in F1, and
here in a different place. The claim this variant *does* earn against the sample is narrower and
worth stating exactly: the delegation is the durable fact, rather than a log written beside one. In
the baseline the log is explicitly "derived, not authoritative" (its own README says so, under "What
it does not do"); here the control record and the state it describes are written in one transaction,
so there is no interval in which the log and the store disagree.

## What each stage proves, and what it does not

`evidence.sh` runs stages in the order they depend on each other; `evidence/run-stdout.txt` is one
full run of it, from a clean scratch directory to the live-model stage, pasted unedited.

| Stage | Proves | Does not prove |
| --- | --- | --- |
| 0 | A fresh scripted run serves all four turns; `verify` holds. | Anything about a model. |
| 1 | `SIGKILL` after a delegation commits, then resume: the checkpoint's holder is the agent that runs. | That the kill landed in the middle of a *write*; it lands after a commit, which is the case the band is about. |
| 1c | The control: a checkpoint naming the coordinator resumes at the coordinator. | That the program picks holders at random — it does not; the difference is the store. |
| 2 | `SIGKILL` inside a specialist's turn: the store holds the attempt and no output; the resume re-enters the specialist, hands it its earlier output, and produces once. | That the interrupted model call was *resumed*: it was not. The attempt was re-entered, and the SDK was never asked to continue a half-finished turn. |
| 2c | The output survives with no process running. | That a second process could not have produced the same answer; only that it did not need to. |
| 2d | The control for point 3's other half: a store with a turn's output and its `produced` record removed must *fail* `verify`. | That the forgery is a state the layer could produce by crashing; it is a state the variant claims it cannot produce, and the forgery is how the claim is tested. |
| 3 | A resume that *would* answer differently reuses the committed decision. | That a real model would answer differently: `BAND_F_FLIP=1` is a controlled stand-in for model non-determinism, not evidence of it. On this stage the flip is also unreachable — the memo short-circuits before `DecideAsync` is called at all — so what the stage shows is the short-circuit, and the flip only labels the answer that was not asked for. |
| 3c | Bypassing the memo makes the decision diverge, and `verify` catches it. | That the divergence would be silent otherwise; it is not silent, which is the point. |
| 4 | A live model makes all four routing decisions through the tool, and `verify` holds. | Nothing about model determinism, and nothing about a kill against a live model: stage 4 is not killed. |

### The kill is a real kill, and it is not a crash-mid-write test

Each killed stage starts `run.sh` in the background, reads the pid **the app** prints after the
commit it is stopping at returns, and sends `SIGKILL` to that pid. Nothing runs in the process
afterwards: no `finally`, no flush, no disposal. `evidence.sh` then checks that the pid is gone and
continues only if it is.

The pid matters, and getting it wrong is a mistake this script made first. `run.sh` is a wrapper
that starts the app and waits for it, so killing the wrapper leaves the app running and reparented to
`pid 1`, where nothing the script holds can reach it and where it keeps the store lock. Each stage
therefore registers both pids: the wrapper as a last resort, and the app — read from the app's own
line — as the one that is killed and the one the exit trap can reach.

That distinguishes two claims the band must not merge:

- **the store's crash safety** — what happens when a write is torn. Not demonstrated here.
- **the run's resume behaviour** — what happens when a process stops between two commits. That is
  what these stages show.

The seam is one-shot. A seam's `--kill-*` option is recorded in the run's input, and the run also
records that the seam has *served* (`seam/<seam>/<turn>` memo) before the process announces itself.
Without that, stage 3c — which deliberately resumes the same killed store — would stop at the same
seam forever and could never show the divergence it exists to show. The memo is written before the
announcement, so a kill between the two still leaves the seam marked served.

## What the scripted path is, and exactly what it does not prove

`BAND_F_ROUTING=scripted` makes the routing decision from three keywords in the user's message, with
no model call at all. Both paths return the same `RoutingDecision` and are committed by the same
code, so design points 1, 2 and 4 are demonstrated identically on both; only the quality of the
decision differs, and every record says which path produced it (`source: scripted` or `source:
model`).

What it does **not** prove: that a model, asked twice, would answer differently and that the memo
would catch it. `BAND_F_FLIP=1` makes the *scenario* answer differently; it is a controlled stand-in
for model non-determinism, not evidence of it, and on stage 3 it is not even reached — the memo
returns before `DecideAsync` runs, so the flip labels an answer the process never asked for. What
stage 3 shows is the short-circuit; what stage 3c shows is what the short-circuit prevents. Stage 4
is the live-model evidence — every decision was made by `claude-sonnet-5` through the delegation
tool — and stage 4 is not killed.

The routing mode is part of the run's input, written once. A run that decided with a model cannot
start deciding with keywords three turns later because a resume did not inherit an environment
variable; the store would hold a mixture and only the recorded `source` would still say so.

## `verify`: the claims as checks, not as prose

`verify` reads only committed state and prints one line per claim:

```
PASS  turn 0: the routing decision was written once — written 1 time(s)
PASS  turn 0: return_agent produced the turn it was routed — routed return_agent, produced by return_agent
PASS  turn 0: the delegation names the routing decision it was made from — delegation names e9, store holds e9
PASS  turn 0: control moved once from the committed decision — 1 delegation record(s): delegated
PASS  turn 0: the output shares its entry with the record that says it exists — produced e35, output e35
PASS  the reported holders name every delegation in the store — reported [return_agent, refund_agent, order_agent, return_agent], store [return_agent, refund_agent, order_agent, return_agent]
PASS  the run served all 4 turn(s) in its script — store holds 4 turn(s)
verify: every claim held
```

Every check that names a turn is a check that can *fail* on that turn. A missing output is a failure
in both claims that inspect one, not a turn that is skipped: a verifier that skips the absent case
passes exactly the store this variant exists to rule out, which is what stage 2d demonstrates.

It exits 1 if any claim fails, and stage 3c is the run where one does. This is the difference between
"the program printed that the delegation committed" and "the delegation is in the store".

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| .NET SDK | 10.0.402 | Must resolve to `/opt/homebrew/bin/dotnet`. |
| A signed-in Copilot CLI | 1.0.105 or later | Only for `BAND_F_ROUTING=model` and `evidence.sh LIVE=1`. The scripted stages need no model and no network. |
| Network access to NuGet | — | `GitHub.Copilot.SDK` and `Microsoft.Extensions.DependencyInjection.Abstractions` are restored on first build. |

The scenario talks to the SDK's bundled runtime through `DurableHarness`, so it inherits the harness's
authentication. `BAND_F_MODEL` selects the model (default: the harness's `gpt-6-luna`); the captured
live evidence used `claude-sonnet-5`.

## The script, and what is not the sample's

| # | Message | Source |
| --- | --- | --- |
| — | ~~`Hello, I need assistance with my recent purchase.`~~ | the sample's **opening message, not used** — see below |
| 0 | `My order 1234 arrived damaged and the packaging was destroyed. I'd like to return it.` | the sample, response 1 |
| 1 | `Please also process a refund for order 1234.` | the sample, response 2 |
| 2 | `One more thing: what is the status of order 1234?` | **added by the F1 port** |
| 3 | `Thanks for resolving this. One last thing: can you confirm the return for order 1234 is still open?` | the sample's response 3, with **this variant's addition folded in** |

Two turns differ from the sample's script, and both differ for the same reason.

**The opening greeting is dropped.** `Hello, I need assistance with my recent purchase.` names no
order and no problem, so no specialist owns it: routing it would mean picking an agent at random, and
the guard refuses that rather than inventing a holder. The script starts where the requests start.

**The closing line is folded into a request.** `Thanks for resolving this.` stood alone when this
script was written that way first, and the live run failed on it — correctly, with *"the coordinator
answered without calling delegate_to_specialist"*. In the sample the triage agent is an ordinary chat
agent and can simply reply; here routing **is** the coordinator's job, so a turn with nothing to
route is outside it. Widening the coordinator's authority to keep a courtesy line was the alternative,
and it would have cost the fail-closed property the routing guard exists for.

Turn 3 also does work design point 3 needs: `return_agent` answers turn 0, so turn 3 is a turn that
returns to a specialist that has already produced something and can be handed its own prior output.

So "the same scripted user" is true of turns 0, 1 and 3's opening line, not of the script as a whole.
The earlier claim that this was "the baseline's scripted user, verbatim" was wrong at turn 0 and is
corrected here.

The run stops on its own turn count, not on a phrase. The sample's `"welcome"` predicate only governs
autonomous continuation in the .NET port anyway (see the baseline README's "What it does not do").

## What this variant does not do

- **No fork.** `lib/Durable.cs` can fork a conversation at an entry, and the issue lists forking as a
  candidate advantage. It is not demonstrated here, so it is not claimed.
- **No concurrent specialists.** The run is sequential: one holder at a time, by construction. The
  store's single-writer lock (`store.jsonl.lock`) makes two processes on one store an error, and that
  is the intended shape, not a limitation being worked around.
- **No retry around the model calls.** A transient provider failure fails the run and leaves the last
  checkpoint as the restart point. The recovered attempt is a new process, not a retry loop.
- **The specialist's turn is re-entered, not continued.** A crash inside a model turn leaves the
  attempt recorded and nothing it produced; the resume asks again. That is why the memo is on the
  routing *decision* (which is cheap and must not change) and not on the model call (which cannot be
  resumed by this layer at all).
