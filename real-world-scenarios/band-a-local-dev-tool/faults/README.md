# Band A — fault evidence

Five faults, run against **both** Band A variants, each writing one machine-readable result. This is
the harness the band README's fault matrix is measured with; nothing in that table is typed by hand.

```bash
./faults/run-all.sh                 # every fault, both variants, controls included (~5 minutes)
./faults/run-all.sh durable         # one variant
./faults/run-all.sh --no-controls   # the five faults only

./faults/kill-mid-turn.sh --variant baseline
./faults/store-outage.sh  --variant durable
```

A plain run writes to a directory of its own (`.scratch/band-a-faults/results-<timestamp>/`) and prints
the `cp` that would promote it. It does not touch `faults/results/`, which is the evidence: a machine
without Playwright would otherwise replace five measured baseline rows with `not exercised` and nothing
would notice. Promoting is explicit:

```bash
FAULTS_RESULTS_DIR=$PWD/faults/results ./faults/run-all.sh   # write the evidence
```

Even then, replacing a non-`not exercised` result with `not exercised` is refused unless
`FAULTS_ALLOW_DOWNGRADE=1` is set, so a capability that goes missing is a deliberate edit rather than a
silent one.

Each script takes `--variant baseline|durable`; four of the five also take `--fault-not-injected`, which
runs the same steps with the injection removed and records it as a **control**. Every script exits `0`
when it ran, including when the scenario lost work — a lost enquiry is the observation, not a failure. A
non-zero exit is a harness error: a process that never came up, a browser that could not be driven, a
fixture that never reached the injection point.

## What each fault does

| Script | Drive to | Inject | Observe |
| --- | --- | --- | --- |
| `kill-mid-turn.sh` | the gate holds a tool result, so the turn is in flight and the first tool call is committed | `kill -9` on the application's PID, confirmed gone | a **new** process on the same port and directory: the page (baseline) or the resumed store (Durable) |
| `restart-after-checkpoint.sh` | a committed phase, waiting on the runtime | `SIGTERM` (reported as the signal `wait` saw, never called "clean") | restart the same application: does it resume from the committed checkpoint, and how much does it repeat? |
| `duplicate-delivery.sh` | the page's sample entry | double click: two deliveries of one enquiry, no pause between | how many enquiries, admissions, searches and model turns resulted |
| `concurrent-clients.sh` | the first enquiry held inside a tool call | submit the second while the first is blocked; then release | whether either enquiry's state is corrupted or lost |
| `store-outage.sh` | a model turn in flight | `mv` the store file aside while the process holds it open | what the live path holds, what the moved file still recovers, what a fresh open says |

## The injection point is deterministic

A fault script never sleeps and hopes. Both variants talk to the recorded runtime
(`tools/ReplayRuntime`) through a JSON-RPC gate (`lib/runtime_gate.py`). The gate relays frames
unchanged, answers the one request the baseline's SDK client makes that the recorded runtime does not
implement, and — when `FAULTS_PAUSE_ON` names a method — writes `<run>/inflight` and holds that frame
until `<run>/release` exists. The script waits for the marker, then acts: it knows the application is
blocked inside that request, which is the moment the fault is about.

The gate is also how the **baseline** is driven at all. It has no command line and no runtime seam, so
the SDK's `COPILOT_CLI_PATH` points at the gate; the Durable variant points `DURABLE_RUNTIME_PATH` at
the same gate, so both variants are faulted by the same mechanism against the same transcripts. The gate
is a small behavioural accommodation — it answers `session.options.update` with an empty result, because
the recorded runtime does not implement it — so a reader should not take it as a byte-for-byte relay.
The accommodation is identical for both variants, which is what keeps the comparison sound, and
`baseline/` and `upstream/` are untouched.

## What "resumed" does and does not mean

The Durable variant resumes from the committed checkpoint. It does **not** make the interrupted turn
happen once: nothing durable records that a cut-off turn produced an answer, so a new process sends it
again, and the intents it had already committed are committed a second time. The shipped result shows
it — `validating/2 entries` before the stop, `done/10 entries` after, with `intent:Validate home enquiry`
twice, against 9 entries and one copy for the control. `durable/evidence/durable-restart-and-concurrency.md`
documents the same effect ("the earlier turn is sent and committed again … 10 does not mean 'nothing
repeated'"); the fault scripts now count the repeated entries (`repeated entries after the resume`) and
report them instead of calling the transcript exact.

What the checkpoint buys is narrower and still real: everything before the checkpoint is not redone, and
nothing that was durable is lost.

## A gap the fault exposed in the Durable variant

The duplicate-delivery fault was first written to drive the variant's `--enquiry` flag twice with the same
text. It does not work, and the reason is a real assertion in the variant rather than a harness defect:

```console
$ BandADurable --enquiry "same text" --enquiry "same text"
Unhandled exception. System.InvalidOperationException: cross-talk: two documents carry the same enquiry text: same text | same text
exit=134
```

`Headless.VerifyIsolation` refuses two documents that carry the same enquiry text. That is correct for the
isolation check it performs — two documents with identical text cannot be told apart — but it means the
head-less driver cannot express "the same delivery arrived twice" at all, and it means a duplicate
delivery that reaches the store ends the process rather than being enumerated. The fault therefore drives
the page (as a customer would) and records whichever the variant does: two conversations, or a crash.
`baseline/` has no equivalent assertion, so the baseline doubles silently.

## Why some rows are `not exercised`

`baseline` / `store-outage` is recorded as **not exercised**, with its reason in the result file: the
baseline writes no agent state to disk at all, so there is no store to remove. Moving `properties.db`
aside would test the search tool, not the durability of an enquiry, and reporting it under this fault
name would be worse than the absence. That is the finding the issue anticipates — the baseline cannot be
faulted this way.

## The before/after controls

Each control is the same script with the injection removed. The pair is what makes a result
attributable:

- `kill-mid-turn` control releases the held turn instead of killing: both variants then **complete**.
  So "lost" and "resumed" belong to the kill, not to the run.
- `restart-after-checkpoint` control stops earlier in the same run — before the phase commits — and
  restarts. The Durable variant lands at `queued` instead of `validating`, which is what shows the
  phase the fault stopped after was read from the checkpoint rather than assumed.
- `duplicate-delivery` control delivers the enquiry once: 1 enquiry / 1 search / 1 turn, against 2 of
  each for the duplicate. The doubling is attributable to the second delivery.
- `concurrent-clients` control submits the same two enquiries one after the other: same content, no
  overlap, so the concurrent run is the only one that could show a race.
- `store-outage` control moves nothing: the run finishes, so the outage run's split store is the
  outage's doing.

## Results

`results/<variant>/<fault>.json` — one document per run: `variant`, `fault`, `outcome`, `phase`,
`detail`, `evidence`, `harness` (dotnet, runtime, the command, the owning script, and the commit) and
`recordedAt`. `results/summary.txt` and `results/run-all.log` are written by `lib/summarise.py` and
`run-all.sh` from those files.

`summarise.py` exits non-zero on any of three things, so a green run means the evidence is present *and*
attributable:

| It fails when | Because |
| --- | --- |
| a variant has no result for a fault | a missing file is indistinguishable from a fault nobody ran |
| a result's `harness.script` is not the script that owns that fault | stale or hand-edited files would otherwise render as evidence |
| a result's `harness.commit` is not the checked-out commit | the numbers would describe different code from the one being read |

Proven, not promised — from the shipped tree:

```console
$ python3 faults/lib/summarise.py "$W/g1" /dev/null "$(git rev-parse HEAD)" | tail -3
## Problems

- durable / store-outage: no result file
exit=2

$ python3 faults/lib/summarise.py "$W/g2" /dev/null "$(git rev-parse HEAD)" | tail -5
## Problems

- durable / kill-mid-turn: recorded at 000000000000 but the checkout is 4ab32e3371a4; the numbers describe different code
- baseline / duplicate-delivery: harness.script is 'restart-after-checkpoint.sh' but the fault 'duplicate-delivery' is owned by 'duplicate-delivery.sh'
exit=2

$ python3 faults/lib/summarise.py faults/results /dev/null "$(git rev-parse HEAD)"; echo "exit=$?"
exit=0
```

Outcome vocabulary, used in the results and in the band README:

| Outcome | Meaning |
| --- | --- |
| `resumed` | a new process continued the work from what was committed |
| `lost` | the work did not survive; nothing continued it |
| `doubled` | the same side effect happened twice |
| `not corrupted` | both enquiries completed and neither acquired the other's state |
| `corrupted` | state crossed between enquiries, or a file became unreadable |
| `failed safely` / `failed safely, with a split store` | nothing durable was lost, and what the application did next is stated |
| `not exercised` | the variant cannot be faulted this way; the reason is in `detail` |
| `control: …` | the outcome of a control run, never to be read as the fault's |

## Re-runnable

Every run gets its own directory under `$FAULTS_SCRATCH` (default `.scratch/band-a-faults/`), its own
store, its own port (`lib/common.sh` assigns one per variant per fault) and its own clean build output.
A cleanup trap reaps the application, the gate and the runtime, by PID and by the run directory's path,
and removes the directory unless `KEEP=1`. A run that fails leaves no process and no store behind, and a
second run starts from nothing.

## Layout

```
faults/
  run-all.sh                    sequence every fault, then render the summary
  kill-mid-turn.sh              one script per fault; all take --variant
  restart-after-checkpoint.sh
  duplicate-delivery.sh
  concurrent-clients.sh
  store-outage.sh
  lib/common.sh                 publish, start, stop, drive, observe, record
  lib/runtime_gate.py           the JSON-RPC gate; the pause is the injection point
  lib/driver.js                 the browser driver the baseline needs
  lib/merge_spec.py             resolves Playwright and a Chromium build
  lib/discover_tool.py          reports whether Playwright exists, and where
  lib/summarise.py              renders results/summary.txt; fails on a missing result
  results/                      checked-in results and the rendered summary
```

Prerequisites: the .NET 10 SDK at `/opt/homebrew/bin/dotnet` (or `DOTNET`), Node, `python3`, `curl`,
the recorded runtime built (`dotnet build tools/ReplayRuntime/ReplayRuntime.csproj`), and Playwright
with a Chromium build for the baseline rows (`BAND_A_PLAYWRIGHT` / `BAND_A_CHROMIUM` override the
discovery). Without Playwright the baseline scripts record `not exercised` and the Durable rows still
run — the harness says which capability it lacked rather than skipping silently.
