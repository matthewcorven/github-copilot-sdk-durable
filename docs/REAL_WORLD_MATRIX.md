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

**Band A only, so far.** The other six bands' rows land with their own A4-equivalents — #152 (B),
#157 (C), #163 (D), #169 (E), #174 (F), #179 (G) — because each band owns its README and its own
rows, per the program epic. Nothing in this file is extrapolated from one band to another. A band
whose directory exists but whose rows are not here has not been measured to this standard yet; the
absence is deliberate.

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


## Currency

| | |
| --- | --- |
| Band A measurements recorded | 2026-11-04 (variant evidence) and 2026-11-05 (fault evidence) |
| Machine | macOS arm64, .NET SDK 10.0.402, GitHub Copilot CLI 1.0.105 |
| Durable layer pinned at | the `dev` tip the scenario was built against — `lib/Copilot.Durable.csproj` reads `0.5.0`, which is **not yet released** (no `v0.5.0` tag, no CHANGELOG section). Per the program epic's versioning rule, the scenario's feature PR lands pinned to the release that cuts it; until then these rows describe `dev`, not a published package |
| Rows added since | none — Band A is the first band to reach this document |

A band's rows are re-measured, not carried forward, when its scenario changes. If a later change to
`lib/Durable.cs` invalidates a cell, the fix is a new run of `faults/run-all.sh` and a re-transcription,
not an edit to the prose here.
