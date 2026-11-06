# Real-world matrix — Durable against first-party baselines

The comparison the [real-world scenario program](https://github.com/matthewcorven/github-copilot-sdk-durable/issues/142)
exists to produce. Every scenario in `real-world-scenarios/` is built twice — once the way the
published reference does it, once over the durable layer — and each cell below is a **measurement
with a link to the file it came from**. A cell with no measurement says `not exercised` and gives
the reason. No cell is blank, and no cell is a guess.

Dimensions are fixed by the program epic (#142) so bands are comparable:

| Dimension | What is recorded |
| --- | --- |
| Where state lives | in-memory / SDK session store / app store / platform service |
| Survives process death | yes / no / partially — with the mechanism named |
| Mid-turn failure behaviour | lost / replayed / resumed from checkpoint |
| Duplicate delivery | idempotent / duplicated side effects / untested |
| Concurrent clients | safe / last-write-wins / untested |
| Store outage | behaviour and whether work is lost |
| Offline capability | none / read-only / full turns |
| Recovery cost | operator steps to resume after a crash |
| Operational burden | services to run, credentials to rotate, drift surfaces |
| Lines of custom durability code | measured, not estimated |

## Scope of this document

**Bands A, B and C, so far.** The other four bands' rows land with their own A4-equivalents — #163 (D),
#169 (E), #174 (F), #179 (G) — because each band owns its README and its own rows, per the program epic.
Nothing in this file is extrapolated from one band to another. A band whose directory exists but whose
rows are not here has not been measured to this standard yet; the absence is deliberate.

**Three kinds of evidence are in this file, and they are not interchangeable.** Bands A and B both ran
real agent turns against a signed-in Copilot runtime — A's inside a web app, B's as a CLI job whose work
*is* the model's tool calls. **Band C's is a local reproduction of an Actions workflow and never ran the
hosted Action** — no runner, no live GitHub API, no model call. Band C's cells carry that caveat inline;
Band B's carry a different one, that its counts are one model's on one machine and that its jobs are live.
The three bands' cells are not an aggregate.

## Band A — local dev-tool agent

A Blazor Server application where each customer enquiry spawns a fresh Copilot session, streams
progress to the UI and reports a result. Baseline: `microsoft/Build26-BRK206-…` at `da8b33a`, MIT,
vendored unmodified (`real-world-scenarios/band-a-local-dev-tool/upstream/PROVENANCE.md`).

Evidence: [`band-a-local-dev-tool/README.md`](../real-world-scenarios/band-a-local-dev-tool/README.md),
[`faults/README.md`](../real-world-scenarios/band-a-local-dev-tool/faults/README.md),
[`faults/results/summary.txt`](../real-world-scenarios/band-a-local-dev-tool/faults/results/summary.txt),
[`durable/evidence/durable-restart-and-concurrency.md`](../real-world-scenarios/band-a-local-dev-tool/durable/evidence/durable-restart-and-concurrency.md).

| Dimension | `baseline/` | `durable/` |
| --- | --- | --- |
| **Where state lives** | **In-memory.** `Dictionary<string, Agent>` in a singleton, one process, nothing written. `properties.db` holds listings only. `baseline/AppState.cs:13` | **App store.** One `EnquiryState` document per conversation in a `DurableStore` (JSONL file at `BAND_A_STORE`). `durable/EnquiryState.cs`, `durable/Program.cs:39` |
| **Survives process death** | **No.** *"A new process, same port, same working directory, shows 0 cards: processing 0, completed 0, rejected 0."* No mechanism exists — [`faults/results/baseline/kill-mid-turn.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/baseline/kill-mid-turn.json) | **Yes, from the last committed phase transition.** `ResumeUnfinished()` runs before the server accepts a request (`durable/Program.cs:62`); *"resumed 1 task(s) at phase validating and finished with task terminal at phase done … restarts=1"* — [`faults/results/durable/kill-mid-turn.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/durable/kill-mid-turn.json) |
| **Mid-turn failure behaviour** | **Lost.** Same file: the agent *"lived in a `Dictionary<string, Agent>` that died with the process, and the SDK session id was never retained."* | **Resumed from checkpoint, with the interrupted turn re-sent.** *"The turn that was cut off was re-sent, not replayed: it left no answer to reuse."* Transcript 10 entries against an uninterrupted run's 9 — replay, disclosed in `durable/README.md` *Known limitations* — [`faults/results/durable/kill-mid-turn.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/durable/kill-mid-turn.json) |
| **Duplicate delivery** | **Duplicated side effects.** *"Two deliveries of the same enquiry produced 2 agents on the page … and 2 model turns reached the runtime."* — [`faults/results/baseline/duplicate-delivery.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/baseline/duplicate-delivery.json) | **Duplicated side effects — a tie.** *"2 enquiry documents, 2 model admissions and 2 search tool call(s) … the variant has no identity for an enquiry to deduplicate on."* — [`faults/results/durable/duplicate-delivery.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/durable/duplicate-delivery.json) |
| **Concurrent clients** | **Not corrupted in the measured run — the defect is in the code.** *"In this sample the dictionary race did not manifest — the defect is present in the code (unlocked `Dictionary<string, Agent>` mutated from every run task), but one concurrent pair is not enough to make it throw."* — [`faults/results/baseline/concurrent-clients.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/baseline/concurrent-clients.json) | **Not corrupted — a tie on observed behaviour.** *"2 enquiry documents and 2 task records, all 2 terminal, 16 transcript entries and 0 entries crossing documents. The commit gate serialized the two writers."* Structural here, incidental in the baseline: [`faults/results/durable/concurrent-clients.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/durable/concurrent-clients.json) |
| **Store outage** | **`not exercised` — there is no store to break.** *"The baseline … writes no agent state to disk for the lifetime of the process. There is no store file to remove, lock, or corrupt."* Moving `properties.db` aside would fault the search tool, and was deliberately not substituted — [`faults/results/baseline/store-outage.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/baseline/store-outage.json) | **Failed safely, with a split store.** Nothing lost or torn, but the path was silently recreated and writing continued: *"the live path … holds phase done with 9 entries, in 51 records, while the moved file still holds the committed prefix intact (28 records, 0 torn) … it does not fail loudly, and it does not tell anyone the old file was gone."* A limitation, not a pass — [`faults/results/durable/store-outage.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/durable/store-outage.json) |
| **Offline capability** | **`not exercised` — no offline claim in either direction.** Neither variant is documented or measured for network loss, and no run drove one; both need the Copilot runtime reachable to finish an enquiry. Not tested, so not claimed. | **`not exercised` — same reason**, and note that a resumed *process* is not an offline *turn*: the fault evidence resumes against the runtime. `not exercised` here, not "partial". |
| **Recovery cost** | **Unbounded: the work is gone.** Restart and the operator re-submits by hand — 0 cards after a new process on the same port — [`faults/results/baseline/kill-mid-turn.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/baseline/kill-mid-turn.json) and [`faults/results/baseline/restart-after-checkpoint.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/baseline/restart-after-checkpoint.json) | **Restart the process; no manual step.** Restart alone resumed — *"The restart resumed 1 task(s) from the committed phase and finished at done"* — [`faults/results/durable/restart-after-checkpoint.json`](../real-world-scenarios/band-a-local-dev-tool/faults/results/durable/restart-after-checkpoint.json). Caveats: the card hide is a render event, not a timer, and a split store needs an operator to notice it — `durable/README.md` *Known limitations* |
| **Operational burden** | **None.** No service, no credential beyond the Copilot CLI's own sign-in, no file to age or migrate. The flip side is that it has no drift surface because it has no state — `baseline/AppState.cs` | **One local file and one exclusive lock.** `BAND_A_STORE` (default `band-a-store.jsonl`) must be backed up, versioned and role-managed; *"the JSONL provider takes an exclusive file lock, so two processes cannot serve the same store"* — `durable/README.md` *Known limitations*. No Azure resource, no rotated secret, plus the Copilot sign-in both variants need |
| **Lines of custom durability code** | **0.** `baseline/Agent.cs` (91) + `baseline/AppState.cs` (57) = **148** `wc -l`, of which none makes state durable. Command and file list: `band-a-local-dev-tool/README.md` §*Lines of custom durability code* | **607 durability-specific lines** out of **1310** code lines; **2497** `wc -l` across the 13 counted files. Per-file classification: `durable/README.md` §*Custom durability code, counted*; `wc -l` command and file list: band README, same section |

### Fault outcomes, transcribed

Rendered from `faults/results/*/*.json` by `faults/lib/summarise.py` into
[`summary.txt`](../real-world-scenarios/band-a-local-dev-tool/faults/results/summary.txt). All 20
runs — 5 faults × 2 variants plus 10 controls — exited 0
([`run-all.log`](../real-world-scenarios/band-a-local-dev-tool/faults/results/run-all.log)).

| Fault | `baseline` | `durable` |
| --- | --- | --- |
| `kill -9` mid-turn | **lost** | **resumed** |
| restart after a committed checkpoint | **lost** | **resumed** |
| duplicate delivery | **doubled** | **doubled** |
| two concurrent clients | **not corrupted** | **not corrupted** |
| store unavailable | **not exercised** | **failed safely, with a split store** |

### Where the bands' rows diverge from the easy story

- **Two of five faults are ties** (`duplicate-delivery`, `concurrent-clients`) and one is
  `not exercised` because the baseline cannot be faulted that way. The durable layer wins two of
  five rows outright, and both of those are about process death.
- **`concurrent-clients` is a tie on behaviour only.** The Durable variant's isolation is structural
  (a commit gate and per-conversation documents); the baseline's unlocked dictionary is a latent
  defect a longer or wider run could still surface. That difference belongs to the code, not to this
  row, and the row is not upgraded for it.
- **`store-outage` is the Durable variant's weakest row**, and it is recorded as such: recovering
  needs an operator who notices the file changed identity.

## Band B — local job agent (the Ralph Loop)

An unattended agent job on a developer's machine: a plan file lists work, the loop sends a prompt to a
**fresh** session per iteration, lets the agent edit files in a scratch target, re-reads the plan, and stops
when the plan reports complete. Baseline: `github/awesome-copilot`'s Ralph Loop recipe at `7e40881`, MIT,
vendored byte-identical (`real-world-scenarios/band-b-local-job-agent/upstream/PROVENANCE.md`), ported to C#
against the plain SDK. Durable variant: the same loop, the same prompt, the same `baseline/verify.sh`, with
the loop position in a Durable task instead of a local variable.

Evidence: [`band-b-local-job-agent/README.md`](../real-world-scenarios/band-b-local-job-agent/README.md),
[`faults/README.md`](../real-world-scenarios/band-b-local-job-agent/faults/README.md),
[`faults/results/summary.txt`](../real-world-scenarios/band-b-local-job-agent/faults/results/summary.txt),
[`durable-kill-and-resume.md`](../real-world-scenarios/band-b-local-job-agent/evidence/durable-kill-and-resume.md).

> **This band's runtime was real, and its jobs were live model turns.** Every run here called
> `gpt-5.1-codex-mini` through the Copilot CLI on a signed-in account; the work *is* the model's tool calls
> inside the target, so there is no fixture to substitute and Band A's fixture approach does not transfer.
> What that buys is that the interruption is a real process death over real files. What it costs is
> reproducibility: a different model groups the plan's items differently per pass, so every tool-call count
> and every "N iterations" in these cells is that model's, on that machine. The fault rows are each pinned by
> a **control** — the same steps with the injection removed — and a row is quoted only where the control's
> outcome differs from the fault's.

| Dimension | `baseline/` | `durable/` |
| --- | --- | --- |
| **Where state lives** | **A local variable and a markdown file.** `var iteration = 0;` in `RalphLoopAsync` — nothing else remembers it, and no event is journaled — `baseline/ralph-loop.cs:68`. The only artifact between iterations is `PLAN.md`, which the agent edits. | **An app store: one JSONL file inside the target.** One durable task (`band-b.ralph-loop`) whose checkpoint is the loop position; `<target>/.durable/store.jsonl` by default, `DURABLE_STORE` overrides — `durable/ralph-loop-durable.cs:89`. The plan file stays an input, not a record. |
| **Survives process death** | **No.** *"The baseline's position is a local variable, so nothing continued the killed turn: the second process re-read the plan and began at its own iteration 1."* Not partial — there is no mechanism — [`faults/results/baseline/kill-mid-turn.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/kill-mid-turn.json) | **Yes — from the last committed iteration, and only from there.** One iteration is one checkpoint (`turn-started` → `turn-complete` → `iteration-complete`), so a stop *after* an iteration commits resumes at the next one: the store makes the second process print *"resuming durable task t8 … resuming at iteration 4"* and *"last stage: iteration-complete"*, with no cap on its command line — [`evidence/durable-kill-and-resume.md`](../real-world-scenarios/band-b-local-job-agent/evidence/durable-kill-and-resume.md) §1, and the same banner in [`faults/results/durable/kill-mid-turn.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/durable/kill-mid-turn.json)'s second process, which reads it and then refuses. **The fault row for this dimension is `not exercised`, not this banner** — see below |
| **Mid-turn failure behaviour** | **Lost.** Killed with `SIGKILL` at the runtime's second `tool.execution_start`: *"Whatever the half-turn had already written stays on disk and is not recorded anywhere — that is what 'lost its position' means here, not 'lost its files'."* — [`faults/results/baseline/kill-mid-turn.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/kill-mid-turn.json) | **Neither replayed nor resumed — refused, and that is the variant's stated limit, not a win.** The store records `turn-started` with no outcome, and the second process reports what it holds and **fails the run** rather than re-issuing a turn that may already have written files: *"the turn was issued (plan digest 07426b11a9af6214) and its outcome was never recorded"*, exit 1 — [`faults/results/durable/kill-mid-turn.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/durable/kill-mid-turn.json). The layer queues each tool record on the conversation's serial chain and drains it only at the turn's completion barrier, so a turn that does not return leaves *no* tool record — not a partial list. A dead turn is observable as *issued* and never replayable |
| **Duplicate delivery** | **Duplicated side effects — a tie.** One `session.sendAndWait` forwarded twice; **4 of the run's 4** `tool.execution_start` frames crossed the gate after the re-delivery: *"a second set of tool calls over the same files, not an error the loop reports."* — [`faults/results/baseline/duplicate-delivery.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/duplicate-delivery.json) | **Duplicated side effects — the same tie.** One `session.send` forwarded twice; **8 of the run's 8** tool starts came after the re-delivery, and the loop reported nothing. *"This is a measured count of frames, not an assumption about the variant: the same words would be chosen for either variant."* Neither variant names a delivery, so neither can deduplicate one — [`faults/results/durable/duplicate-delivery.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/durable/duplicate-delivery.json) |
| **Concurrent clients** | **Not corrupted, and nothing serialised them.** Two loops over one folder, loop 1 blocked at its gate when loop 2 started: *"Neither refused the other, both finished (exit 0 and 0), the plan is still a plan (intact), and the verifier passes … Nothing serialised them and nothing broke: that is the finding, and it is not the same claim as 'serialised'."* — [`faults/results/baseline/concurrent-clients.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/concurrent-clients.json) | **Refused the second client.** Loop 2 exited **4** before reading the plan, sending a prompt or writing a file: *"The second loop did not read the plan, send a prompt or touch a file: the refusal comes from the layer's exclusive lock on the store file."* The lock is `lib/Durable.cs`'s `StoreFileLock` (`FileShare.None` on a `.lock` sidecar), and the variant now reports the refusal as itself — [`faults/results/durable/concurrent-clients.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/durable/concurrent-clients.json) |
| **Store outage** | **`not exercised` — there is no store to break.** *"The baseline keeps no state outside the job's own files, so there is no store to take away … its target holds no file but PLAN.md, PROMPT.md, release-notes.md and docs/*.md."* Checked on every run, not assumed; removing one of those would fault the file the job is rewriting, which is a different fault — [`faults/results/baseline/store-outage.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/store-outage.json) | **Continued past the outage — not "failed safely".** The store was `mv`'d while the process held it open; exit 0, plan complete, verifier passed: *"The writes made after the move went into the file the renamed — a .NET FileStream follows the inode, not the name … the run did not turn that into a failure. It is reported as that, not folded into 'failed safely'."* The live path ended at `iteration-complete/8`, the moved file at `turn-started/0` — a split store — [`faults/results/durable/store-outage.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/durable/store-outage.json) |
| **Offline capability** | **`not exercised` — no offline claim in either direction.** Both variants need the Copilot runtime reachable to finish a turn; no run drove a network loss, so nothing is claimed. | **`not exercised`, same reason.** A resumed *process* is not an offline *turn*: every resume in this band re-enters against the runtime for its next turn. Not tested, so not claimed — and not upgraded to "partial" because the store is local |
| **Recovery cost** | **Re-run the job; nothing continues.** The operator restarts and the second process re-reads the plan and starts at its own iteration 1 — for the `SIGKILL` row *"the second process re-read the plan and began at its own iteration 1"*; for the stopped pass *"the iteration the first process committed is not known to it"* — [`faults/results/baseline/kill-mid-turn.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/kill-mid-turn.json), [`faults/results/baseline/restart-after-checkpoint.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/restart-after-checkpoint.json) | **One command, no manual step — after an iteration boundary only.** `KEEP=1 durable/resume.sh` resumes at iteration 3 and finishes at 8 with no operator input — §1 of [`durable-kill-and-resume.md`](../real-world-scenarios/band-b-local-job-agent/evidence/durable-kill-and-resume.md). **The caveat is load-bearing:** after a death *inside* a turn the restart is not a resume, it is a refusal (exit 1) and the operator must decide, because nothing durable records what the dead turn did — [`faults/results/durable/kill-mid-turn.json`](../real-world-scenarios/band-b-local-job-agent/faults/results/durable/kill-mid-turn.json) |
| **Operational burden** | **None.** One file-based .NET app, one shell runner, no service, no store, no credential beyond the Copilot CLI's own sign-in. The flip side is that it has no drift surface because it has no state — `baseline/ralph-loop.cs`, `run.sh` | **One store file inside the job's own working directory, plus two sidecars while it is held.** `<target>/.durable/store.jsonl` and, for the store's lifetime, `<store>.lock` and `<store>.lock.owner` (`StoreFileLock`, `lib/Durable.cs:1230`). The scratch target is the whole footprint, so it travels with the job — and so does its drift surface: the store is a record the target's own agent can write inside. No Azure resource, no rotated secret, plus the Copilot sign-in both variants need |
| **Lines of custom durability code** | **0.** `baseline/ralph-loop.cs` is **163** `wc -l` / **104** code lines, of which none makes state durable — the progress model is one `int` and the markdown. `wc -l` command and file list: band README §*Lines of custom durability code* | **294 durability-specific lines** out of **674** code lines (**44%**); **989** `wc -l`. Per-region classification whose 22 rows sum to 674/294/380: [`evidence/durable-kill-and-resume.md`](../real-world-scenarios/band-b-local-job-agent/evidence/durable-kill-and-resume.md) §6, recomputed by `python3 durable/evidence/table.py` (which aborts rather than render if the regions stop covering the file). The honest reading is "294 durability lines added to a 104-line baseline"; the other 276 of the 570-line difference is application code the baseline keeps in its prompt or in `verify.sh` |

### Fault outcomes, transcribed

Five faults, both variants, **nine controls** (the baseline has no `store-outage` control to run) and the two run-equivalence tests — 21 runs, all exited 0
([`run-all.log`](../real-world-scenarios/band-b-local-job-agent/faults/results/run-all.log)), recorded
2026-11-06T03:33:43Z–03:52:17Z at `harness.commit 32770e70b581`. Rendered from the result files by
`faults/lib/summarise.py` into [`summary.txt`](../real-world-scenarios/band-b-local-job-agent/faults/results/summary.txt);
`summarise.py` exits non-zero on a missing result, a result from the wrong script, a result from a different
commit, or an outcome not in the defined vocabulary.

| Fault | `baseline` | `durable` | Result files |
| --- | --- | --- | --- |
| `kill -9` mid-turn | **lost its position** | **refused to resume the dead turn** | [`baseline`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/kill-mid-turn.json) · [`durable`](../real-world-scenarios/band-b-local-job-agent/faults/results/durable/kill-mid-turn.json) |
| restart after a committed iteration | **restarted from the plan** | **not exercised** | [`baseline`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/restart-after-checkpoint.json) · [`durable`](../real-world-scenarios/band-b-local-job-agent/faults/results/durable/restart-after-checkpoint.json) |
| duplicate delivery | **doubled** | **doubled** | [`baseline`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/duplicate-delivery.json) · [`durable`](../real-world-scenarios/band-b-local-job-agent/faults/results/durable/duplicate-delivery.json) |
| two concurrent clients | **not corrupted** | **refused the second client** | [`baseline`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/concurrent-clients.json) · [`durable`](../real-world-scenarios/band-b-local-job-agent/faults/results/durable/concurrent-clients.json) |
| store unavailable mid-run | **not exercised** | **continued past the outage** | [`baseline`](../real-world-scenarios/band-b-local-job-agent/faults/results/baseline/store-outage.json) · [`durable`](../real-world-scenarios/band-b-local-job-agent/faults/results/durable/store-outage.json) |

Five rows: **two `not exercised`, one tie, and two where the wording differs**. Read in this order:

- **`kill-mid-turn` is not a win, and the durable cell says so.** The word is `refused to resume the dead
  turn`, not `resumed`. The variant stops rather than repeat writes it cannot see, which is defensible and
  is still a run that did not finish: exit 1, plan still open, verifier fail. It is the variant's stated
  limit, measured from outside the process for the first time.
- **`restart-after-checkpoint` is `not exercised` on the durable side, and the reason is a finding, not a
  gap.** A restart "after a committed iteration" needs a moment between two iterations that a process
  *outside* the run can stop at, and there is none: the only such frame is the creation of the next
  iteration's conversation, and at that frame the store already says `turn-started/n` — because the
  checkpoint that opens a turn is committed *before* its conversation is created and the settle that closes
  it is inside the same iteration — so a stop there leaves exactly what `kill-mid-turn` leaves. *"A restart
  from a settled boundary exists only inside the process, and a fault injected from outside cannot land on
  it."* The harness does not move the seam inside the variant to make the row green. **`kill-mid-turn` and
  `restart-after-checkpoint` therefore overlap on this variant** — one externally reachable stop, two names.
- **`duplicate-delivery` is a tie on both.** Both variants did a second set of tool calls and neither
  reported an error. The Durable variant's `run.sh` exit 3 on that run is its **iteration cap**, not a
  refusal, and the row is not upgraded for it.
- **`store-outage` / `baseline` is the second `not exercised`**, and it is checked: the target holds no
  file but the job's own, so there is nothing to take away. Removing one of those would fault the file the
  job is rewriting, which is a different fault and is not reported under this name.
- **Then the two rows whose wording differs**, and they differ for one property each: `concurrent-clients`
  is exclusion (a lock on the store file the second process cannot open), and `store-outage` / `durable` is
  *survival without detection* — it continued past the outage, which is not the same as failing safely.
  Neither of the interruption rows finished on the durable side.
- **One variant change is visible and should not be read as a scenario behaviour.** `concurrent-clients`
  proved the *layer* refuses a second store writer while the *variant* died of an unhandled exception; the
  variant now catches that refusal, prints it as a refusal, and exits **4** (`durable/run.sh:140`, `durable/resume.sh:84`).
  `lib/Durable.cs` was not changed — the half that was missing was the variant's.

### The headline: a run that was interrupted and continued

The one claim that holds on the files. Per variant, three runs over three copies of one staged target —
reference, repeat, interrupted — compared as **the same file set, with the same SHA-256 per file, at the
same relative path**, every regular file under the target except `.durable/` (the store, which differs by
construction) and the harness's build directory. **17 files** in each case.

| Variant | Interruption | Repeat vs reference | Interrupted vs reference |
| --- | --- | --- | --- |
| `durable` | `STOP_AFTER_ITERATION=2`, then `resume.sh` — resumed at iteration 3, finished at 8 | **identical**, 17 files, sha256 per file and path | **identical**, 17 files, sha256 per file and path |
| `baseline` | `SIGKILL` at `out tool.execution_start 2`, then the same command again | differs byte-for-byte in **14 of 17** files; identical after dropping blank lines | differs byte-for-byte in **14 of 17** files; identical after dropping blank lines |

Both verdicts are taken from the **normalised** comparison, because on the baseline two *clean* runs are not
byte-identical — 14 of 17 files differ in spacing. The comparator is proved able to fail on every run: one
appended byte reports `differs` (`./PLAN.md`, 17 vs 17), one extra file reports `differs` (`./extra.txt`,
17 vs 18), an unmodified tree reports `identical`, and an appended line still differs after normalising
(`1 of 17 file(s)`). If the repeat run disagrees the result is `not comparable: two clean runs differed` and
nothing is claimed about the interruption.

**What the baseline's `identical` does and does not mean.** Its "continuation" is a second process
**starting over** after a `SIGKILL` — no position restored, no work skipped, every iteration after the kill
re-done. So its agreement says *the job can be finished by starting over and the end state is the same*; it
says nothing about recovery, and a product comparison cannot distinguish *resumed* from *redone*. It also
rests on the normaliser. **What the Durable variant's `identical` does not say:** it is over the files; the
store's account of the interruption differs from the uninterrupted run's and is not compared. And its
interruption is the variant's own committed-iteration stop, not a fault injected from outside.

### Where this band's numbers stop

- **No cell says a dead turn's work is recoverable.** It does not, and the `kill-mid-turn` durable cell is
  where that is measured. The memo boundary is the *turn*, not the tool call inside it, and the layer is
  asymmetric about it: a turn that returns is fully recorded, a turn that does not leaves only `turn-started`.
- **No cell says the baseline is unsafe.** Two concurrent loops did not corrupt the target, and a duplicate
  delivery and a kill both produced a *finished* job. The baseline's weakness is that recovery is rework;
  its strength is that it has nothing to corrupt and nothing to migrate.
- **Every count is one model's.** `gpt-5.1-codex-mini` on macOS arm64, .NET SDK 10.0.402. Tool-call counts,
  iteration counts and the "8 iterations" in the variant's runs are properties of that model's grouping of
  the plan, not constants of the variants.
- **The kill seam has a measured limit.** `tool.execution_start` is emitted *before* the tool runs, and a
  probe that held the 2nd and 4th start on a cold target found 0 changed files. The recorded runs did land
  after writes; the result reports the files the half-turn left, new and modified, so a run whose kill landed
  before any write says so rather than implying a half-written target (`faults/README.md`).

## Band C — CI repo agent

A reproduction of `github/gh-aw`'s `.github/workflows/pr-triage-agent.md`: a scheduled agent job that
triages open `app/github-copilot` pull requests in eight phases and keeps its only memory between runs
in a git branch (`memory/pr-triage`). The workflow is vendored byte-identical at `380ae7b`, MIT
(`real-world-scenarios/band-c-ci-repo-agent/upstream/PROVENANCE.md`). Baseline: the phase order, the
one-hour cooldown, the payload and the end-of-run write reproduced as written. Durable variant: the
same eight phases and the same triage code (`evidence/compare.py --code` checks 386 significant lines
identical), with the state in a `DurableStore`.

Evidence: [`band-c-ci-repo-agent/README.md`](../real-world-scenarios/band-c-ci-repo-agent/README.md),
[`FIDELITY.md`](../real-world-scenarios/band-c-ci-repo-agent/FIDELITY.md),
[`faults/README.md`](../real-world-scenarios/band-c-ci-repo-agent/faults/README.md),
[`faults/results/summary.txt`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/summary.txt),
[`durable/README.md`](../real-world-scenarios/band-c-ci-repo-agent/durable/README.md),
[`durable/evidence/durable-triage-and-idempotency.md`](../real-world-scenarios/band-c-ci-repo-agent/durable/evidence/durable-triage-and-idempotency.md).

> **Every cell below is marked `(local repro)`: this section was measured on the *reproduction*, not
> the hosted Action.** There was no Actions runner, no `gh aw` invocation, no live `pull_request_read`,
> `add-comment`, `add-labels` or `create-issue` call, and no model call — the PR set is a fixed 17-PR
> fixture and classification, risk and score are deterministic functions of it. What that buys is
> reproducibility and byte-identical output between the two variants; what it costs is that this band
> cannot speak to runner lifecycle, API rejection or rate limits, triage *quality*, or model cost and
> budget. `FIDELITY.md` is the boundary document: *"A comparison that is honest about one and not the
> other is measuring the substitute."* One consequence is load-bearing — the workflow has **no resume
> path** (the runner is gone and the state does not record an interruption), so the Durable variant
> here *adds* a state the platform does not have rather than repairing a baseline defect. That is what
> the `resumed` cells mean.

| Dimension | `baseline/` | `durable/` |
| --- | --- | --- |
| **Where state lives** | **A git branch, one whole file.** `pr-triage-latest.json` on `refs/heads/memory/pr-triage` in a bare repository, written with `hash-object`/`mktree`/`commit-tree`/`update-ref`. *(local repro)* — the branch stands in for the Action's `repo-memory` tool; the branch is local, not on GitHub. `baseline/PrTriageBaseline.cs:931` | **An app store: three document kinds.** `band-c.run` per repository, `band-c.pr` per pull request, `band-c.comments` once, in a JSONL `DurableStore`. *(local repro)* — a local file where the Action has a branch; the layer is the real one. `durable/TriageStore.cs`, `durable/Contracts.cs` |
| **Survives process death** | **No.** A new process is a cold start and redoes every phase; the only thing that survives is whatever Phase 8 wrote last time. *"Nothing was resumed, because a whole-file snapshot written at the end of Phase 8 is the baseline's only state and this run never reached it."* — [`faults/results/baseline/restart-after-checkpoint.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/restart-after-checkpoint.json). *(local repro)*: "process death" is a real `kill -9`/`SIGTERM` here; in the Action it is a discarded runner, which was not exercised | **Yes, from the phase the record names.** *"The restart resumed that task at 'comment' rather than starting over … the restart is exact: the committed phases did not run again."* — [`faults/results/durable/restart-after-checkpoint.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/restart-after-checkpoint.json). *(local repro)*: an addition to the workflow's design, not a behaviour the Action is known to have |
| **Mid-turn failure behaviour** | **Lost.** Killed with 13 comment files on disk and no state written: *"it re-classified every PR and posted all thirteen comments again, of which 13 were byte-for-byte what was already there."* — [`faults/results/baseline/kill-mid-turn.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/kill-mid-turn.json). *(local repro)*: the failure is a `kill -9`, not a runner cancellation or a 30-minute `timeout-minutes` | **Resumed from checkpoint, and the interrupted phase continues.** *"The new process resumed at 'comment' — the phase the kill cut short — and finished with the run at 'save'. It wrote 4 comment(s) of which 0 were already on disk."* — [`faults/results/durable/kill-mid-turn.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/kill-mid-turn.json). *(local repro)*: no model turn to re-send — this band's work is deterministic, so it avoids the replay Band A pays for |
| **Duplicate delivery** | **Doubled, silently.** *"Two live processes … each posted all thirteen comments … the compare-and-swap never saw a conflict and neither run reports an error. The duplicate is entirely silent."* — [`faults/results/baseline/duplicate-delivery.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/duplicate-delivery.json). *(local repro)*: a duplicate here is a second file write, which a real `add-comment` would translate into a second comment — untested | **Refused, not doubled.** The second process is refused at the store open (exit 3) before it reads the fixture — [`faults/results/durable/duplicate-delivery.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/duplicate-delivery.json). **Caveat carried, not hidden:** the refusal is *per store file, not per repository*; two paths both proceed (`durable/evidence.sh` stage 5b), and it is a refusal, not a queue. *(local repro)*: no GitHub-level delivery semantics involved |
| **Concurrent clients** | **Doubled, no conflict detected.** First writer held after Phase 5, second run to completion, both exit 0, one state replaces the other. — [`faults/results/baseline/concurrent-clients.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/concurrent-clients.json). *(local repro)*: one chosen overlap window; nothing argues the baseline fails at every overlap | **Refused, not doubled.** The lock is held for the store's lifetime, so the second is refused while the first is merely paused. — [`faults/results/durable/concurrent-clients.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/concurrent-clients.json). Same per-store-file caveat; the Action's schedule would not normally produce two runs at once, so this is a hazard of re-runs, not a measured Action behaviour. *(local repro)* |
| **Store outage** | **Failed loudly, effects kept, state lost.** Repository moved aside while Phase 8 was about to commit: exit 1, 13 comments and the report on disk, no state anywhere. — [`faults/results/baseline/store-outage.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/store-outage.json). *(local repro)*: `mv` of a local path, not a GitHub outage or a revoked token | **Failed safely, with a split store — a tie, not a win.** Exit 0, path recreated, *"the state is now in two files … it does not fail loudly, and it does not tell anyone the file it was writing had gone."* Nothing committed was lost. — [`faults/results/durable/store-outage.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/store-outage.json). *(local repro)*: neither variant reparses or merges a restored store |
| **Offline capability** | **`not exercised` — the substitution removes the network entirely.** Nothing in this band calls an API or a model, so no run lost one and there is nothing to measure. *(local repro)* means "offline" is the normal state of this reproduction, which is exactly why it says nothing about the Action's offline behaviour | **`not exercised`, same reason.** A resumed *process* is not an offline *turn*: the durable variant resumes deterministic phases with no network in the first place. Not tested, so not claimed — and not upgraded to "full" because the substitute needs nothing |
| **Recovery cost** | **Re-run, and the re-run duplicates.** The operator restarts; the cold start re-comments every eligible PR with nothing detecting the duplicate — *"13 of run 3's 13 written comment(s) were byte-for-byte what was already on disk"* — [`faults/results/baseline/cooldown-desync.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/cooldown-desync.json). *(local repro)*: an operator, not a scheduler retry | **Restart; no manual step.** The store record names the phase and the run continues — 13 adopted, 0 posted. — [`faults/results/durable/cooldown-desync.json`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/cooldown-desync.json). Caveats: the guard is a memo plus a ledger keyed `(pr, digest)`, and a stop between a comment and its ledger entry costs one duplicate (`durable/evidence.sh` stage 4c). *(local repro)* |
| **Operational burden** | **A branch in one bare repository, nothing to install.** `git show memory/pr-triage:pr-triage-latest.json` is the whole state, readable by hand. Its drift surface is a force-push or a deleted branch, which is a silent reset to cold start — `FIDELITY.md` §"What *git branch as a KV store* costs". *(local repro)*: a local branch, not `repo-memory` against GitHub | **One store file per repository, plus `.lock` and `.lock.owner` sidecars.** `durable/run.sh` uses one default path, `.scratch/band-c-durable/store.jsonl`, whose documents are keyed by repository, so one path per repository is a *convention in a script* — and one `run.sh` records them as a single repository — not a guarantee in the layer. One clean run leaves 73 versions (8 `run` + 52 `pr` + 13 `comments`) against the baseline's one blob. — [`durable/README.md`](../real-world-scenarios/band-c-ci-repo-agent/durable/README.md) §*Concurrency*, §*Where the baseline wins*. *(local repro)*: no runner minutes, no artifact retention, no `max-ai-credits` to budget |
| **Lines of custom durability code** | **106.** `baseline/PrTriageBaseline.cs` is **1243** `wc -l`; its whole `Repo` class (`:931-1062`) is 106 non-blank, non-comment lines, and it is the only durability code — the read, the write and the compare-and-swap. `wc -l` command and file list: band README §*Lines of custom durability code*. *(local repro)*: written to match the workflow's shape, so it is the shape's cost and not the Action's source | **592 durability-specific lines** out of **1443** code lines (41%); **2216** `wc -l` across the 8 counted files. Per-file classification: [`durable/README.md`](../real-world-scenarios/band-c-ci-repo-agent/durable/README.md) §*Custom durability code, counted*; `python3 durable/evidence/count.py`. Note `Triage.cs`'s 499 code lines are the baseline's triage moved, so the honest reading is "592 durability lines added to a 1243-line baseline, 106 of which it already had". *(local repro)* |

### Fault outcomes, transcribed

Six faults, both variants, twelve controls. Rendered from `faults/results/*/*.json` by
`faults/lib/summarise.py` into
[`summary.txt`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/summary.txt). All 24 runs
exited 0, results recorded 2026-11-05T18:04:06Z–18:05:02Z. Nothing in this table is `not exercised`:
both variants have a store on disk, both can be killed, and both can be started twice.

| Fault | `baseline` | `durable` | Result files |
| --- | --- | --- | --- |
| `kill -9` mid-turn | **lost, and re-commented** | **resumed** | [`baseline`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/kill-mid-turn.json) · [`durable`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/kill-mid-turn.json) |
| restart after a committed checkpoint | **lost** | **resumed** | [`baseline`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/restart-after-checkpoint.json) · [`durable`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/restart-after-checkpoint.json) |
| duplicate delivery | **doubled** | **refused, not doubled** | [`baseline`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/duplicate-delivery.json) · [`durable`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/duplicate-delivery.json) |
| two concurrent clients | **doubled, no conflict detected** | **refused, not doubled** | [`baseline`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/concurrent-clients.json) · [`durable`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/concurrent-clients.json) |
| store unavailable mid-run | **failed loudly, effects kept, state lost** | **failed safely, with a split store** | [`baseline`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/store-outage.json) · [`durable`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/store-outage.json) |
| cooldown desync | **doubled — 13 PRs hold two comments** | **not doubled — 0 posted, 13 adopted** | [`baseline`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/baseline/cooldown-desync.json) · [`durable`](../real-world-scenarios/band-c-ci-repo-agent/faults/results/durable/cooldown-desync.json) |

Six rows, and they are **not six wins**. Read in this order:

- **`store-outage` is a tie, and the Durable variant's worst row.** The baseline fails loudly and loses
  the state *after* the effects; the durable variant survives but splits its store in two, silently.
  "Fails safe" is not "fails well", and the result file says so in those words.
- **`restart-after-checkpoint` is not the same fault on both sides.** The baseline's "control" for it is
  an *earlier* stop, and it loses everything too — the control does not flip, because the baseline has
  no commit point. What that shows is that the phase the stop lands in changes only how much work is
  lost. The durable row's control does flip (it resumed at `classify`), which is what makes the phase
  record — not the restart — the thing being measured.
- **`cooldown-desync`'s baseline control does not flip either.** With run 2's state committed the
  baseline *still* re-posts all thirteen, because it has no identity for a comment; what the fault row
  adds is that run 3 triaged from run 1's stale view. The durable side's control does not flip because
  there is no defect to remove, so the flip was built *inside* the fault as a guard-disabled arm (below).
  `faults/README.md` §"Where the control's outcome does **not** flip" records all of this.
- **Then the wins:** the four rows where behaviour differs all come from one property — a commit per
  phase transition plus a store the second writer cannot open. Three of the four are about
  interruption; the fourth (`concurrent-clients`) is about exclusion.

### The headline: `cooldown-desync` in numbers

Transcribed from the `observations` objects in the two result files, not from prose.

| Variant | run 2 comments | run 3 posted | run 3 adopted | run 3 duplicated (byte-level) | run 3 resumed | guard-disabled arm: posted / duplicated |
| --- | ---: | ---: | ---: | ---: | --- | --- |
| `baseline` | 13 | 13 | 0 | **13** | no | not run (no guard to disable) |
| `durable` | 13 | **0** | **13** | **0** | yes | **13 / 13** |

Baseline PRs holding two comments: `#101 #102 #103 #104 #105 #106 #107 #109 #111 #112 #113 #114 #115`
— every eligible PR. Durable: none. *(local repro)*: the duplicate is measured byte-for-byte against a
snapshot of the comment files, so it is a file-level duplicate; a real `add-comment` API call is not
involved. The Durable arm's stop is *inside* the comment phase (that variant has no end-of-run write to
fail), so the two injections stop at the same business window but not the same program counter.

### Where this band's numbers stop

- **No cell here says anything about triage quality.** The classifier, the scorer and the actions are
  deterministic — *"A model could rank the same PRs differently. Any claim that depends on the quality
  of the triage decisions is out of scope for this band"* (`FIDELITY.md`).
- **No cell says anything about the Action's cost or budget.** No `timeout-minutes`, no
  `max-ai-credits`, no `max-tool-denials`.
- **Two design additions are visible and should not be read as fixes:** the Durable variant's resume
  (`resumed` rows) and its per-PR digest. The Action has neither. The baseline's run gate — the one
  thing added to the workflow's state shape — exists only because a command can be run twice in a row
  (`FIDELITY.md` §"The run gate").
- **The one-comment window is measured, not closed.** A stop between the comment file and its ledger
  entry costs exactly one duplicate, however often it is repeated (`durable/evidence.sh` stage 4c).

## Currency

| | |
| --- | --- |
| Band A measurements recorded | 2026-11-04 (variant evidence) and 2026-11-05 (fault evidence) |
| Band B measurements recorded | 2026-11-05 (variant evidence, `durable/evidence/run-all.sh`) and 2026-11-06 (fault evidence, `faults/run-all.sh`, 21 runs, 03:33:43Z–03:52:17Z at `32770e70b581`) |
| Band C measurements recorded | 2026-11-05 — variant evidence (`durable/evidence.sh`, two identical runs) and fault evidence (`faults/run-all.sh`, 24 runs, 18:04:06Z–18:05:02Z) |
| Machine | macOS arm64, .NET SDK 10.0.402; GitHub Copilot CLI 1.0.105 for Bands A and B (Band B's fault harness used the CLI bundled with the SDK build, 1.0.103, because the 1.0.105 install refused `gpt-5.1-codex-mini` as unavailable); Band C needs no CLI — it makes no model call |
| Durable layer pinned at | the `dev` tip the scenario was built against — `lib/Copilot.Durable.csproj` reads `0.5.0`, which is **not yet released** (no `v0.5.0` tag, no CHANGELOG section). Per the program epic's versioning rule, the scenario's feature PR lands pinned to the release that cuts it; until then these rows describe `dev`, not a published package |
| Rows added since | Band A (#147), Band C (#157), then Band B (#152) on 2026-11-06. **Band B needed no layer change**: `DurableStore.Create`'s exclusive `StoreFileLock` already refused a second writer, and the `concurrent-clients` fault showed the *variant* was the half that was missing — it caught the refusal and reports exit 4 now (`durable/ralph-loop-durable.cs:114-128`). Band C (#157), 2026-11-05, needed `Transaction.Memo` in all three ports (filed as #201) and a silent rollback fix (`UndoLog.Restore` resetting the baseline map); both are on `dev` with tests that fail without them (`durable/README.md` §*What `lib/Durable.cs` could not express*) |
| **Fidelity differs by band** | Bands A and B ran real agent turns against a signed-in CLI; Band B's jobs are live model work over real files, so its counts are one model's and not reproducible byte for byte in their *numbers* (its *products* are, per the run-equivalence rows). **Band C's is a local reproduction of an Actions workflow and never ran the hosted Action** — see the caveat on its section. A reader comparing sections is comparing kinds of evidence, not configurations of one experiment |

A band's rows are re-measured, not carried forward, when its scenario changes. If a later change to
`lib/Durable.cs` invalidates a cell, the fix is a new run of `faults/run-all.sh` and a re-transcription,
not an edit to the prose here.
