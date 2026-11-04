# Band A — Durable variant

The same local dev-tool agent as `../baseline/`, with the enquiry state moved into the Durable
store. The page, the pipeline, the tools and the system prompt are the baseline's; what changes is
where an enquiry lives between two mouse clicks and a restart.

```bash
./run.sh                       # build, run, print the URL (port 5028)
KEEP=1 ./run.sh                # keep the store and the Copilot sign-in
BAND_A_STORE=/path/store.jsonl ./run.sh   # resume a store from an earlier run
./evidence.sh                  # reproduce the evidence in evidence/
```

| Path | What it is |
| --- | --- |
| `EnquiryState.cs` | The enquiry document: phase, intent, report, transcript, restart count, hide time. |
| `EnquiryCheckpoint.cs` | The task checkpoint: the task phase key, the enquiry phase it mirrors, the pass count. |
| `EnquiryDurable.cs` | The document definition, the task factory, and the read helper. |
| `EnquiryTask.cs` | The state machine: one pass over the enquiry's committed phase per iteration. |
| `EnquiryWrites.cs` | Every guarded write: the terminal rule, the forward-only rule, the compare-and-swap. |
| `EnquiryStore.cs` | The durable host: opens the store, resumes unfinished enquiries, maps sessions. |
| `ExitSeam.cs` | `BAND_A_EXIT_AFTER_PHASE`: the deterministic kill point. |
| `AppState.cs` | The read-only projection the renderer uses. There is no dictionary. |
| `Headless.cs` | The command-line driver the evidence runs use. |
| `fixtures/` | Recorded transcripts for the deterministic evidence stages. |
| `evidence/` | Captured output, with the commands that produced it. |

## The baseline, and what is different

| Baseline | Here |
| --- | --- |
| `Dictionary<string, Agent>` in a singleton | one enquiry document per conversation in a `DurableStore` |
| an in-process `Agent` holds phase, intent, report | the document holds them, and every change is a commit |
| `await Task.Delay(15000)` then `Agents.Remove(...)` | a committed `hideAt` on a terminal document; the record stays |
| lost on restart | resumed by `ResumeUnfinished` + `ResumeConversationAsync` |
| unprotected dictionary, mutated from every run task | one commit gate per store, and guards evaluated inside it |

The baseline is not modified. Its defects are the comparison.

## The checkpoint boundary

**A checkpoint is one committed phase transition.** Each transition writes three things in one
transaction: the enquiry document's new phase, the transcript entry that records it, and — when
the task is the writer — the task checkpoint. A process that dies at any point therefore restarts
from either the phase before the transition or the phase after it, never from part of one.

Where the boundary sits was the design decision, and it is not the obvious one. The baseline's
unit of work is a whole enquiry: one Copilot session, one run loop, one dictionary entry. Here the
unit of work is a **phase pass** — one prompt, one model turn, one transition — for three reasons:

1. **The model moves the phase itself.** The system prompt tells it to call `set_current_phase`
   while it works, and those tool calls land in the document immediately. If the checkpoint were
   the whole enquiry, a restart would have to reconstruct which of those reports had been made. As
   it is, the document *is* the progress record and the checkpoint only has to remember where to
   resume reading it.
2. **A rejection must be able to land mid-turn.** A model that decides an enquiry is spam rejects
   it during the validation pass. If rejection could only be committed at a phase boundary, the
   pass would have to complete first, and the enquiry would keep running while it was already
   decided. Committing the phase from the tool call is what makes requirement 3 possible.
3. **A phase pass is the smallest step that has an observable side effect.** Nothing smaller
   (a tool call) is worth a checkpoint on its own: the tool's effect is a row in the transcript and
   the model's next message, both of which the phase's commit covers.

Two boundaries are deliberately **not** separate commits:

- **Intent reports** are their own commit, but not their own checkpoint: `report_intent` writes the
  document without touching the task. Restarting does not replay an intent; it is display state.
- **The report attach** is a separate commit, not a second checkpoint. The model reports `done`
  through its phase tool and only then answers; the answer is written onto the already-terminal
  document. It cannot change the phase, so it cannot be a transition.

## Requirements, and where each is demonstrated

All five are exercised by `./evidence.sh`; the captured output is in
`evidence/durable-restart-and-concurrency.md`.

1. **The enquiry flow keeps its public shape.** `Home.razor` renders the same pipeline, the same
   cards and the same detail panel. `/run.sh` serves it on 5028. Creating an enquiry goes through
   `AppState.RunAgentAsync`, exactly as the page did before.
2. **Each phase transition is a committed checkpoint.** `EnquiryTask.Run` commits the pass boundary
   and the next phase; `BAND_A_EXIT_AFTER_PHASE=searching` kills the process on the commit path
   (stage 1, exit code 42) and a new process resumes that store at `searching` and finishes it
   (stage 2).
3. **A rejection is terminal and a late wakeup cannot overwrite it.** Stage 3 races four phase
   moves and a report attach against a `rejected_garbage` document: all five are refused, the
   phase does not move and the transcript does not grow. Stage 3b restarts the same store and the
   rejection is still there — the baseline's timer would have deleted it.
4. **Concurrent enquiries do not corrupt each other.** Stage 4 submits two enquiries for two
   customers before awaiting either, then asserts that their entry ids are disjoint, that every
   entry in a document belongs to that document's conversation, and that no two documents share an
   enquiry or a task. Stage 5 goes further and fires eight writers at one document: exactly one
   write lands and the transcript gains one entry, because the guard is inside the commit gate.
5. **`lib/Durable.cs` through the project reference.** `BandADurable.csproj` references
   `../../lib/Copilot.Durable.csproj`. Nothing from the store is copied into this folder.

## The deterministic kill point

`BAND_A_EXIT_AFTER_PHASE=<phase>` exits the process with code 42 the moment that phase is
committed, before anything else runs. The check lives on the commit path (`ExitSeam.AfterCommit`),
so the store is guaranteed to hold the phase the process died after — there is no timer to race
and no window to miss.

The exit is a real `Environment.Exit`, not a thrown exception: no `finally` block, no disposal, no
flush beyond what the store already did. That reproduces what a killed process actually leaves
behind, which is the thing the evidence has to show.

## How the two concurrent enquiries map onto the commit gate

There is one `DurableStore` per process and one gate in it: `DurableStore.CommitAsync` holds a
semaphore for the whole transaction, so two enquiries commit one at a time. That gives two
properties, and the second is the one that matters:

- **Serialization.** Two transactions cannot interleave their read-then-write, so a guard evaluated
  inside a transaction sees a state no other writer can change until the transaction ends.
- **Isolation.** Each enquiry has its own document and its own transcript entries. Serialization is
  the only coordination they need, because they never touch the same record.

The baseline had neither. Its dictionary was mutated from every run task with no lock at all, and
`Agents[agentId] = agent` racing `Agents.Remove(agentId, ...)` is exactly the interleaving a
`Dictionary` does not survive. Stage 5 is that race, held under the gate.

## Custom durability code, counted

Counting rule: **non-blank, non-comment lines** in `durable/**.cs` and `durable/Components/Pages/Home.razor`.
A line is *durability-specific* when removing it would remove a guarantee this variant exists to
show: a document or checkpoint definition, a guarded commit, a resume path, the commit wiring that
ties a task to its document, or the deterministic kill point. A line is *application logic* when it
would be the same in any version of this app: the pipeline vocabulary, the prompts and tool
descriptions, the search query, the Razor markup, and the read-only projection the page renders.

| File | Durability | Application |
| --- | ---: | ---: |
| `EnquiryState.cs` | 33 | 0 |
| `EnquiryCheckpoint.cs` | 7 | 0 |
| `EnquiryDurable.cs` | 22 | 0 |
| `EnquiryWrites.cs` | 150 | 24 |
| `EnquiryStore.cs` | 120 | 33 |
| `EnquiryTask.cs` | 85 | 15 |
| `ExitSeam.cs` | 18 | 0 |
| `Program.cs` | 14 | 36 |
| `AppState.cs` | 10 | 64 |
| `EnquiryTools.cs` | 25 | 137 |
| `EnquiryPhases.cs` | 0 | 42 |
| `EnquiryContracts.cs` | 0 | 12 |
| `Components/Pages/Home.razor` | 0 | 329 |
| **Total** | **484** | **692** |

**484 durability-specific lines against 692 application lines** — 41% of the variant is the
durability the matrix row counts. For comparison, the baseline's whole agent state is
`Agent.cs` (about 60 code lines of which none are durability) plus `AppState.cs` (about 40, of which
the dictionary lookup and the timer are the durability story, and are wrong).

## What is reused, and what is not

Reused verbatim from the baseline: `Phase.cs` (the pipeline graph and its geometry),
`PropertyDatabase.cs` (the listings and the search query) and the Razor shell
(`App.razor`, `Routes.razor`, the layout, `app.css`). The seed data under `Data/Properties/` is a
copy of the baseline's, because a listings database is not agent state.

Rewritten: `AppState` (a projection instead of a dictionary), the run loop (a durable task instead
of a fire-and-forget `Task`), the tools (they commit instead of assigning a field) and the system
prompt's placement (an appended section rather than a `Customize` override).

## Running without a browser

`Headless.cs` drives the same `AppState` the page injects, so the evidence is the application's
own behaviour rather than a second implementation:

```bash
dotnet <publish>/BandADurable.dll --enquiry "…"            # one enquiry, print its transcript
dotnet <publish>/BandADurable.dll --enquiry a --enquiry b  # two, submitted together
dotnet <publish>/BandADurable.dll --resume                 # finish what the last process left
dotnet <publish>/BandADurable.dll --enquiry "…" --contend 8 # eight writers, one document
dotnet <publish>/BandADurable.dll --enquiry "…" --late-wakeup # terminal-state guard
BAND_A_EXIT_AFTER_PHASE=searching …                        # kill the process at a chosen phase
BAND_A_FIXTURE=<fixture> DURABLE_RUNTIME_PATH=<replay>     # run against a recorded runtime
```

## What was executed, and what was not

Executed on 2026-11-04, macOS arm64, .NET SDK 10.0.402:

- `dotnet build` and `dotnet publish` of `BandADurable.csproj`, clean apart from the baseline's own
  NU1903 advisory and the EF Core model-validation warning.
- `./evidence.sh` — stages 1, 2, 3, 3b and 5 against the recorded runtime. All assertions passed;
  the captured output is the evidence file.
- Stage 4 against the live signed-in Copilot CLI (1.0.105), two enquiries submitted together. Both
  documents, transcripts and tasks were distinct; the isolation assertions passed.
- The browser UI was not driven by a headless browser. `./run.sh` was started and served
  `GET /`, but no click was automated; the page renders the same stores the headless driver
  exercises.

Not executed:

- Stage 4 against the recorded runtime. Its `ResolveSession` serves every pinned session from the
  first recording, so two pinned sessions get the same turns; that is a limitation of the recorded
  runtime, and it is why the concurrency stage has a `LIVE=1` mode rather than pretending.
- Stages 1-3 and 5 against the live CLI. They are deterministic by construction, which is the point.

## Known limitations of this variant

- **One store writer per process.** The JSONL provider takes an exclusive file lock, so two
  processes cannot serve the same store. That is correct for this scenario — one server, restart to
  recover — and it is recorded rather than worked around.
- **A turn that was sent but not recorded fails the enquiry.** If a process dies after the model
  turn starts and before the pass boundary commits, the resumed task cannot tell whether the turn
  had an effect, so it fails the enquiry instead of silently re-sending. That is the conservative
  choice, not a completed one; a real system would reconcile against the native session.
- **The card hides, the record does not.** A rejected enquiry's card disappears fifteen seconds
  after the rejection because `hideAt` has passed. The document stays. That is deliberate — it is
  the fix for the baseline's delete — but it means the store grows with rejected enquiries.

## What `lib/Durable.cs` could not express

Three gaps surfaced while building this variant. None of them blocked a requirement, so none is a
release blocker; they are recorded because the next consumer will hit them.

1. **A task phase must be named by a string property called `phase` in the checkpoint.**
   `DurableRuntime.RunTaskCoreAsync` reads `checkpoint["phase"]` and requires a JSON *string*. A
   checkpoint whose phase is an enum is not rejected: `GetString()` on a JSON number throws
   `InvalidOperationException`, which `RunTaskCoreAsync` turns into a committed `failed` outcome
   rather than a clear configuration error. The store's serializer options
   (`JsonSerializerDefaults.Web`) emit an enum as a number, so the type that reads most naturally
   is the one that fails. *Consequence*: `EnquiryCheckpoint.Phase` is a `string` with a documented
   constant, and the enquiry's real phase lives in a second property.

2. **`TaskRuntime` exposes no store and no conversation.** A phase handler receives
   `TaskRuntime`, which has the task record, the definition, a cancellation token, and the commit
   methods — but nothing that can read a document or reach the conversation the task belongs to.
   The only way to write a document from inside a phase is to close over the store, and the only
   way to send a prompt is to close over a delegate. *Consequence*: `EnquiryDurable.Task` is a
   factory rather than a `static readonly` definition, and `EnquiryStore` has to build the task
   after the store exists. This is workable and arguably correct — the session is a per-process
   resource — but the durable layer documents tasks as "the checkpoint is the whole state", and a
   handler that needs the store is outside that story.

3. **`DurableConversation.SubmitAsync` swallows a failed tool call's outcome into the transcript
   only.** A tool that throws comes back to the model as a tool result, and the checkpoint
   `ConversationCheckpoint` records `Success = false` with the error text. Nothing in the
   checkpoint stream distinguishes "the model chose not to search" from "the search tool was
   called with bad arguments and failed". *Consequence*: this variant reads its own tool writes
   instead, and the recorded-runtime evidence had to be regenerated once the search tool's
   required parameters were found by reading a failed tool checkpoint in the JSONL by hand.

Point 1 is the one worth filing: a non-string `phase` silently falling through to `"run"` is a
sharp edge. Filed as a note in the pull request rather than a new issue, because the library
correctly refuses an unclear configuration once the phase key is found, and the fix would be a
validation error message rather than new behaviour.
