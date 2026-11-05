# Band A — Local dev-tool agent

A Blazor Server application in which each customer enquiry starts a Copilot session that works the
enquiry, streams progress into the browser, and reports a result. The scenario is built **twice**:

| | `baseline/` | `durable/` |
| --- | --- | --- |
| What it is | the Build26 reference application, vendored unmodified and ported to a buildable project | the same application, with the enquiry state moved into the Durable store |
| Upstream | `microsoft/Build26-BRK206-…` @ `da8b33a`, MIT — see `upstream/PROVENANCE.md` | written here, over `lib/Durable.cs` |
| Where an enquiry lives | a `Dictionary<string, Agent>` field in a singleton | one document per conversation in a `DurableStore` (JSONL file) |
| Restart | every enquiry, phase and report is gone | unfinished enquiries resume from what committed |
| Run | `./run.sh` (port 5027) | `(cd durable && ./run.sh)` (port 5028) |

## Why this is realistic

It is not a scenario written to show a point. It is an application Microsoft published and demoed,
and its durability story is exactly what a real local dev tool does when it is written quickly: the
agent state is an in-process dictionary, nothing is written to disk, and the SDK session that would
have made recovery possible is created and then forgotten. The observed defects — the dictionary, a
hard-coded `await Task.Delay(15000)` eviction, no lock, no retained session id — are the upstream
ones, preserved. `baseline/` differs from `upstream/AgentOrchestrator/` in three non-behavioural
places (assembly name, the scoped-CSS bundle name, and not carrying the `.sln`); they are listed in
`upstream/PROVENANCE.md`.

The work it does is ordinary: read a customer enquiry, validate it, search property listings, write
a report, or reject spam. The listings are real seeded JSON and the search is EF Core/SQLite, so the
*application* half is a real application. Only the durability half is the comparison.

## The two variants side by side

| Concern | `baseline/` | `durable/` |
| --- | --- | --- |
| Agent state | `Dictionary<string, Agent>` (`baseline/AppState.cs:13`) | `EnquiryState` document per conversation (`durable/EnquiryState.cs`) |
| Where it is written | nowhere | `DurableStore` at `BAND_A_STORE`, default `band-a-store.jsonl` beside the assembly |
| Rejected-enquiry removal | `await Task.Delay(15000)` then `Agents.Remove` (`baseline/AppState.cs:37-40`) | committed `hideAt` on a terminal document; the record stays |
| SDK session | `CreateSessionAsync` result not retained (`baseline/Agent.cs:53`) | conversation rebound on resume (`durable/EnquiryStore.cs`) |
| Concurrency | unlocked dictionary mutated from every run task | one commit gate per store; guards evaluated inside the transaction |
| Restart | empty dashboard | `ResumeUnfinished()` before the server accepts a request (`durable/Program.cs:62`) |
| Pipeline, tools, prompt, page | the same | the same |

The Durable variant is deliberately the baseline's sibling: same phases, same tools, same system
prompt, same Razor page. What changes is where an enquiry lives between two mouse clicks and a
restart. Design detail and the checkpoint boundary are in `durable/README.md`; do not duplicate it
here.

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| .NET SDK | 10.0.x | `net10.0` target, and the .NET 10 Blazor toolchain. `dotnet --version` must print 10.0.x, or set `DOTNET` to that install |
| GitHub Copilot CLI | signed in | The SDK spawns the runtime; `copilot` must be on `PATH` and authenticated. No Azure subscription and no database server are needed — listings are seeded into SQLite on first start |
| Fault harness only | Node, `python3`, `curl`, Playwright + Chromium, and the built `tools/ReplayRuntime` | Only for `faults/`. The two `run.sh` commands need none of them |

Check the two required ones before starting:

```bash
dotnet --version    # 10.0.x
copilot --version   # any version, signed in
```

## One command per variant

```bash
cd real-world-scenarios/band-a-local-dev-tool

./run.sh                        # baseline, port 5027
(cd durable && ./run.sh)        # Durable variant, port 5028
```

Both publish into a fresh `$TMPDIR` scratch directory, print their URL, and stop with Ctrl-C. Nothing
is written inside the repository: the SQLite file, the Copilot client's base directory, and (for the
Durable variant) the store all resolve inside that scratch directory.

Then, in the browser, click the **+** button and pick a sample enquiry — for example *"Looking for a
3-bed family home near good schools in the suburbs, budget around $500k"*. The card moves through
Validating → Searching → Writing Report and the dashboard counts follow it.

To watch the difference the scenario is about, keep the Durable variant's store and restart it:

```bash
(cd durable && KEEP=1 ./run.sh)          # note the "Resume with:" line it prints
# Ctrl-C, then:
(cd durable && BAND_A_STORE=<that path> ./run.sh)
```

The dashboard the second process opens is the one the first left behind. Do the same with the
baseline (`KEEP=1 ./run.sh`, Ctrl-C, restart) and the dashboard is empty — that is the whole point,
and it takes under a minute to see.

`KEEP=1` keeps the scratch directory; otherwise it is removed on exit. `PORT=` overrides the port.

## Teardown

| What | How |
| --- | --- |
| Either application | Ctrl-C |
| The scratch directory, store, SQLite file, Copilot sign-in | on exit, unless `KEEP=1` was set. With `KEEP=1`, delete the path the run printed |
| A fault-harness run | the harness reaps itself; to check: `pgrep -f 'BandA'; pgrep -f ReplayRuntime` should print nothing. Leftover runs live under `.scratch/band-a-faults/` |

Nothing needs `docker`, a cloud resource, or a background service to be shut down.

## The fault results table

Five faults, run against both variants. Transcribed from `faults/results/<variant>/<fault>.json`;
`faults/results/summary.txt` is rendered from those files by `faults/lib/summarise.py`, which exits
non-zero if a variant is missing a result. Evidence and controls: `faults/README.md`.

| Fault | baseline | durable | Result files |
| --- | --- | --- | --- |
| `kill -9` mid-turn | **lost** | **resumed** | [`baseline`](faults/results/baseline/kill-mid-turn.json) · [`durable`](faults/results/durable/kill-mid-turn.json) |
| restart after a committed checkpoint | **lost** | **resumed** | [`baseline`](faults/results/baseline/restart-after-checkpoint.json) · [`durable`](faults/results/durable/restart-after-checkpoint.json) |
| duplicate delivery | **doubled** | **doubled** | [`baseline`](faults/results/baseline/duplicate-delivery.json) · [`durable`](faults/results/durable/duplicate-delivery.json) |
| two concurrent clients | **not corrupted** | **not corrupted** | [`baseline`](faults/results/baseline/concurrent-clients.json) · [`durable`](faults/results/durable/concurrent-clients.json) |
| store unavailable mid-operation | **not exercised** — the baseline writes no agent state to disk | **failed safely, with a split store** | [`baseline`](faults/results/baseline/store-outage.json) · [`durable`](faults/results/durable/store-outage.json) |

What each of those five cells actually says, in the result files' own words:

- **`kill-mid-turn`, baseline — lost.** *"A new process, same port, same working directory, shows 0
  cards: processing 0, completed 0, rejected 0. Nothing resumes: the agent lived in a
  `Dictionary<string, Agent>` that died with the process, and the SDK session id was never
  retained."*
- **`kill-mid-turn`, durable — resumed.** *"The kill left a committed document at phase validating
  with 2 entries… A new process resumed 1 task(s) at phase validating and finished with task
  terminal at phase done, 10 entries, restarts=1, report 46 characters. The turn that was cut off
  was re-sent, not replayed: it left no answer to reuse."* The 10 entries are 9 plus a replayed
  turn — see the limitation below.
- **`restart-after-checkpoint`, baseline — lost.** *"It shows 0 cards: processing 0, completed 0.
  Whatever phase had committed is gone with the process, so there is no resume to be exact
  about."*
- **`restart-after-checkpoint`, durable — resumed.** *"The restart resumed 1 task(s) from the
  committed phase and finished at done with 10 entries (task terminal). The committed checkpoint was
  ahead of the last completed turn, so the remaining work ran and the finished part did not."*
- **`duplicate-delivery` — doubled, both.** Baseline: *"2 agents on the page … and 2 model turns
  reached the runtime. The application has no identity for an enquiry."* Durable: *"2 enquiry
  documents, 2 model admissions and 2 search tool call(s) … the variant has no identity for an
  enquiry to deduplicate on."*
- **`concurrent-clients` — not corrupted, both.** Baseline: *"In this sample the dictionary race
  did not manifest — the defect is present in the code … but one concurrent pair is not enough to
  make it throw."* Durable: *"2 enquiry documents and 2 task records, all 2 terminal, 16 transcript
  entries and 0 entries crossing documents. The commit gate serialized the two writers."*
- **`store-outage`, baseline — not exercised.** *"The baseline keeps every agent in an in-process
  `Dictionary<string, Agent>` and writes no agent state to disk for the lifetime of the process.
  There is no store file to remove, lock, or corrupt."* Removing `properties.db` was deliberately
  not substituted: that would fault the search tool, not the durability of an enquiry.
- **`store-outage`, durable — failed safely, with a split store.** *"Nothing was lost, but the state
  is now in two files: an operator reading the live path sees a different enquiry history … The
  application fails safe in the sense that it reports no corruption and keeps writing; it does not
  fail loudly, and it does not tell anyone the old file was gone."*

Every script has a control (`--fault-not-injected`) that runs the same steps without the injection.
The control table in `faults/results/summary.txt` is what makes the outcomes attributable: with the
kill removed, both variants **complete**; with the duplicate removed, both produce **one** enquiry.
Reproduce all of it with `./faults/run-all.sh` (~3 minutes).

## What the baseline gets right

This is a comparison, not a takedown. Where the baseline is simpler or better, it is stated plainly:

- **It is smaller, and the smallness is a real property.** The baseline's whole agent state is
  `baseline/Agent.cs` plus the dictionary and the timer in `baseline/AppState.cs` — 91 + 57 lines
  total, `wc -l`, and none of them are durability code. There is no store to open, no document
  schema, no commit discipline, no resume path. It is easier to read in one sitting.
- **It has no operational surface at all.** No store file, no format to migrate, no exclusive lock
  to trip over, no `BAND_A_STORE` to point somewhere. Restarting it is always a clean start, and a
  developer never has to reason about what the last run left behind.
- **It is dependency-free in the durable sense.** Nothing in `baseline/` references `lib/Durable.cs`
  or any library of ours. If the durable layer were wrong, the baseline would still work.
- **It never fails to start because of yesterday's state.** A corrupt, half-written or
  differently-versioned store cannot stop the baseline from coming up, because it has none. The
  Durable variant's split store (above) is a class of problem the baseline cannot have.
- **Its congestion story is honest for a demo.** "Successful agents stay on screen forever, rejected
  ones linger for fifteen seconds" is a deliberate demo choice, documented as such in the upstream
  comment, and it does exactly what it says.
- **It is not slower for the turn.** Both variants send the same prompts and make the same tool
  calls per enquiry; the Durable variant adds a commit per phase transition. That cost is real and
  is not measured against a benefit here.

It gets those things by having nothing to lose — and then it does lose everything, which is the fault
table. Both statements are true at once, and a reader deciding whether to adopt the durable layer
should weigh them together.

## Where the Durable variant is worse

Stated here rather than left to the other document, because a comparison that only lists wins is not
evidence:

- **`duplicate-delivery` is a tie.** Neither variant deduplicates. If an enquiry needs an identity,
  neither application has one today.
- **`concurrent-clients` is a tie on observed behaviour.** The Durable variant's isolation is
  structural (a commit gate, per-conversation documents, zero entries crossing) and the baseline's
  is incidental, but one concurrent pair did not make the baseline throw, so the row records no win.
- **`store-outage` is a limitation, not a pass.** The application recreated the store path, kept
  writing, and said nothing. Two files, no error, no operator signal.
- **A killed turn is re-sent, not replayed.** On the recorded runtime the resume commits the turn
  again, and session replay inflates the transcript (10 entries against an uninterrupted run's 9).
  A `done` enquiry whose answer never committed gets a second answer, not the first one back. Both
  are in `durable/README.md` under *Known limitations*.
- **One store writer per process.** The JSONL provider takes an exclusive file lock, so two servers
  cannot serve the same store. The baseline has no such restriction because it has no store.
- **607 durability-specific lines against 1310 counted.** That is 46% of the variant existing for a
  property the baseline does not claim to have. It is the cost, measured.

## Lines of custom durability code

Measured with `wc -l` on 2026-11-05, in this directory:

```bash
wc -l durable/EnquiryState.cs durable/EnquiryCheckpoint.cs durable/EnquiryDurable.cs \
      durable/EnquiryWrites.cs durable/EnquiryStore.cs durable/EnquiryTask.cs \
      durable/ExitSeam.cs durable/Program.cs durable/AppState.cs durable/EnquiryTools.cs \
      durable/EnquiryPhases.cs durable/EnquiryContracts.cs durable/Components/Pages/Home.razor
wc -l baseline/Agent.cs baseline/AppState.cs
```

| Variant | Files counted | `wc -l` total | Durability-specific | Baseline for the same |
| --- | --- | ---: | ---: | --- |
| `baseline/` | `baseline/Agent.cs` (91) + `baseline/AppState.cs` (57) | **148** | **0** — no line exists to make state durable | — |
| `durable/` | 13 files, as above | **2497** | **607** of **1310** code lines | 148 |

Two numbers, and the difference matters:

- **`wc -l` = 2497** is every line of the 13 files, blank lines and comments included. This is the
  literal measurement the issue asks for; there is no classifier script in this repo, so the file
  list is stated above and the command is the one shown.
- **607 / 1310** is the durability-specific split, and it comes from the per-file table in
  `durable/README.md` §*Custom durability code, counted* (source file:
  [`durable/README.md`](durable/README.md)), which classifies **non-blank, non-comment** lines line
  by line. A reader who disagrees with a call changes the split, not the `wc -l` total.

Three files in `durable/` are deliberately not in either number: `Headless.cs` (522 `wc -l`; the
evidence driver, not the application), `PropertyDatabase.cs` (159) and `Phase.cs` (35) — the last two
are the baseline's, reused verbatim, and are zero durability lines by construction. Adding every file
in `durable/` (the 13 above plus those three) gives **3213** `wc -l`; the matrix's "lines of custom
durability code" is the 607 figure, because that is the number that would go away if the durable
layer did.

## Layout

```
real-world-scenarios/band-a-local-dev-tool/
├── README.md                  # this file
├── run.sh                     # baseline: build, start, print the URL
├── upstream/                  # byte-identical Build26 copy, MIT, with PROVENANCE.md
├── baseline/                  # the vendored application as a project here
│   ├── Agent.cs               # one enquiry, in memory
│   └── AppState.cs            # Dictionary<string, Agent>, 15s eviction, no lock
├── durable/                   # the same application over lib/Durable.cs
│   ├── README.md              # checkpoint design, evidence, line-count table
│   ├── run.sh                 # build, start, print the URL
│   ├── evidence.sh            # reproduces evidence/
│   └── evidence/              # captured restart / terminal-state / concurrency output
└── faults/                    # five faults x two variants, with controls
    ├── README.md              # the harness, the injection point, the vocabulary
    ├── run-all.sh             # everything, ~3 minutes
    └── results/               # one JSON per run, plus summary.txt rendered from them
```

## What was executed

On 2026-11-04, macOS arm64, .NET SDK 10.0.402, GitHub Copilot CLI 1.0.105 signed in:

- The **baseline** published, started, served `GET /` with the Blazor InteractiveServer websocket
  connected. One enquiry submitted through the UI ran against the live runtime (card moved
  Validating → Searching, tool line *Search matching properties*, dashboard settled at
  `Processing 0 / Completed 1 / Rejected 0`). A second, spam enquiry was rejected and its card
  disappeared after the upstream 15-second delay, with no console error.
- The **Durable variant** built and published cleanly, and `durable/evidence.sh` ran its stages
  against the recorded runtime with all assertions passing; captured output is
  `durable/evidence/durable-restart-and-concurrency.md`. Stage 4 was additionally run against the
  live signed-in CLI, with both documents, transcripts and tasks distinct.

On 2026-11-05, same machine and SDK:

- `durable/evidence.sh` was re-run in full; the checked-in capture in
  `durable/evidence/durable-restart-and-concurrency.md` is that run.
- `./faults/run-all.sh` — 5 faults × 2 variants plus 10 controls, **exit 0 for all 20 runs**.
  Results in `faults/results/`, harness log in `faults/results/run-all.log`.

Not executed: the browser UI under a headless browser outside the fault harness (the harness does
drive it with Playwright for the baseline rows), and either variant against a CLI version other than
1.0.105. `durable/README.md` records that the checked-in stage 4 capture is the recorded-runtime
one, not the live one.

## Licence

MIT. See `upstream/LICENSE` and `upstream/PROVENANCE.md`.

## The Durable variant

`durable/` is the sibling of `baseline/`: the same page, the same pipeline, the same tools and the
same system prompt, with the enquiry state moved out of a dictionary and into the Durable store.
Both variants run from this folder, side by side.

```bash
./run.sh                                  # baseline, port 5027
(cd durable && ./run.sh)                  # Durable variant, port 5028
(cd durable && KEEP=1 ./run.sh)           # keep the store, so a restart can resume
```

The Durable variant has its own `durable/README.md` with the checkpoint design, the evidence, and
the line count. Two things are worth knowing before reading the code:

- **`durable/` references `lib/Durable.cs`** through a project reference
  (`../../lib/Copilot.Durable.csproj`). No part of the store is copied into the scenario.
- **`durable/evidence/durable-restart-and-concurrency.md`** is captured output, produced by
  `durable/evidence.sh`, not a description of what the code should do.

`baseline/` is unmodified by this work. Its defects are the comparison, so nothing in it is fixed.

## Fault evidence

`faults/` runs five faults against **both** variants and records what happened. The table below is
rendered from `faults/results/` by `faults/lib/summarise.py`; the full per-run detail, the evidence each
result cites, and the before/after controls are in `faults/results/summary.txt` and `faults/README.md`.

| Fault | baseline | durable |
| --- | --- | --- |
| `kill-mid-turn` | **lost** | **resumed** |
| `restart-after-checkpoint` | **lost** | **resumed** |
| `duplicate-delivery` | **doubled** | **doubled** |
| `concurrent-clients` | **not corrupted** | **not corrupted** |
| `store-outage` | **not exercised** (no store holds agent state) | **failed safely, with a split store** |

```bash
./faults/run-all.sh          # all five faults, both variants, controls included
./faults/run-all.sh durable  # one variant
```

Three of these rows deserve to be read carefully rather than as wins:

- **"resumed" does not mean "replayed nothing".** The Durable variant resumes from the committed
  checkpoint, but the turn that was cut off left no answer, so the new process sends it again and the
  intents that turn had already committed are committed a second time. The fault results show it
  (`validating/2` → `done/10` with one intent twice, against 9 entries and one copy for the control), and
  `durable/evidence/durable-restart-and-concurrency.md` documents the same effect. What the checkpoint
  guarantees is that work *before* it is not redone and nothing durable is lost.
- **`duplicate-delivery` is a tie at "doubled".** Neither variant deduplicates a repeated delivery: the
  baseline holds two agents, the Durable variant holds two conversations, two admissions and two
  searches. The Durable variant is not better here and the table does not claim it is.
- **`concurrent-clients` is a tie on behaviour.** Both completed both enquiries correctly in this sample.
  The baseline's unlocked `Dictionary<string, Agent>` is still a defect, but one concurrent pair did not
  make it throw; the Durable variant's commit gate is what makes the isolation structural rather than
  incidental. That distinction is in the code, not in this row.
- **`store-outage` records a split store.** Moving the store file aside did not make the Durable variant
  fail: it recreated the path and kept writing, while the moved file still held the committed prefix
  intact. Nothing was lost and nothing was corrupted, but the state ended up in two files and the
  application did not say so. That is the honest result, and it is a limitation rather than a clean pass.

The baseline's `store-outage` is **not exercised**: it writes no agent state to disk, so there is no store
to remove. `properties.db` holds listings, and an outage of it would test the search tool. That absence is
the finding.

Both variants were driven through the same recorded runtime behind the same JSON-RPC gate, so a fault is
injected at the same instant in both and the comparison is of the applications. The baseline is not
modified for this: the gate is reached through the SDK's `COPILOT_CLI_PATH`, which the baseline's own
client already honours.
