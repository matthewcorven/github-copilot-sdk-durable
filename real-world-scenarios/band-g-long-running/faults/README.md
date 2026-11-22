# Band G — fault evidence

Six faults, run against **all three** Band G implementations, plus the comparison the band exists for. Every
number in the band README's tables and in this directory's results comes from a run of these scripts;
nothing is typed.

| Column | Driven by | Where it runs |
| --- | --- | --- |
| `baseline` — the file-checkpoint port (#176) | `run` / `resume`, and its OpenAI-compatible endpoint | in this repository |
| `durable` — this band's variant (#177) | its own verbs, and `DURABLE_RUNTIME_PATH` | in this repository |
| `extension` — the agent-framework durable extension | `extension/src`, against a Durable Task Scheduler | needs docker; see below |

```bash
./faults/run-all.sh                  # all three columns, every fault and control (~20 minutes)
./faults/run-all.sh durable          # one variant
./faults/run-all.sh --no-controls    # the six faults only

./faults/kill-during-wait.sh --variant baseline
./faults/store-outage.sh  --variant durable
./faults/extension.sh --fault kill-during-wait
```

**A partial run never writes `results/`.** `run-all.sh durable` or `--no-controls` is a diagnostic, so it
writes to `$SCRATCH_ROOT/partial-results/` and says where; only a complete run produces the committed tree.
A partial run that overwrote the checked-in summary would leave the band's evidence describing a matrix
nobody ran.

Each script takes `--variant baseline|durable`; five of the six also take `--fault-not-injected`, which runs
the same steps with the injection removed and records it as a **control**. A script exits `0` when it ran,
including when the run lost work — a lost run is the observation, not a failure. A non-zero exit is a
harness error: a process that never came up, a fixture that never reached the injection point, a kill that
could not be aimed at the process it was meant to hit.

## What each fault does

| Script | Drive to | Inject | Observe |
| --- | --- | --- | --- |
| `kill-during-wait.sh` | the run is waiting for the human, and a process is in the wait | `kill -9` on that process, confirmed gone | a new process: is the wait still there, and can it still be answered? |
| `restart-after-checkpoint.sh` | the pre-gate work is committed, and the next step is in flight | the process is stopped there (`SIGTERM` for the baseline, its own `--fault` for the Durable variant) | a second process: is the resume exact, and how many model calls did it need? |
| `duplicate-approval.sh` | the gate is live, and one approval is on file | the same approval, delivered a second time by a different reviewer | the decision on file, the refusal's reason, and what the run does with it |
| `late-approval.sh` | the gate has passed its deadline (Durable) or the run has finished (baseline) | an approval after the fact | whether the terminal state moves |
| `abort-mid-wait.sh` | the run is waiting at its gate | the variant's abort, as its own process | the child's status when the parent's handler ran; whether a later approval is refused |
| `store-outage.sh` | a decision is on file and about to be applied | the store is made unwritable by the OS (`chflags uchg`) while the writer runs | whether a half-committed decision is left, read back with the flag removed |

## The injection point is real, and it is deterministic

A fault script does not sleep and hope. Both variants give the harness an exact place to act:

- **The baseline** reads its model from an OpenAI-compatible endpoint (`OPENAI_BASE_URL`), so the harness
  runs one it owns (`lib/baseline_endpoint.py`). That endpoint serves the recorded transcript
  (`fixtures/baseline-transcript.jsonl`) and writes one line per request it serves, which is how a call
  count is per process. It can also hold the Nth request: a held request is a real model call in flight,
  which is where a kill or a stop lands.
- **The Durable variant** names its runtime with `DURABLE_RUNTIME_PATH`, so the harness points that at a
  relay in front of the recorded runtime (`lib/runtime_gate.py`). The relay forwards every frame unchanged
  and can hold the named method, which is the same injection point expressed in the variant's own seam. A
  variant with no relay armed is started with the recorded runtime directly.

Both variants are built into `.scratch/band-g-faults/bin/` and **started as their own process**, never
through `dotnet run`. `dotnet run file.cs` puts a `dotnet` host in front of the application, so the PID the
shell starts is the host: `kill -9` would land on the wrapper and the application would keep running. That
is not a detail — it is the difference between injecting a fault and illustrating one.

## The middle column, and why it is not "not run" any more

G2 and the first version of this harness both recorded the agent-framework durable extension as **not run**.
That was false for a bad reason: the stated ground was that no prerelease `Microsoft.Agents.AI.DurableTask`
package existed on NuGet. It has 41 prerelease versions (latest `1.16.0-preview.261029.1`, MIT, `net10.0`);
the probe used a malformed feed URL. A claim that a thing cannot be done needs the same evidence as a claim
that it was done.

`extension.sh` runs the same six faults against it:

```bash
docker run -d -p 8080:8080 mcr.microsoft.com/dts/dts-emulator:latest    # the Durable Task Scheduler
./faults/extension.sh --fault kill-during-wait
```

What that column can and cannot measure, said in the results rather than only here:

| | |
| --- | --- |
| Measurable | whether a run survives its process, what a duplicate answer does, what a late answer does on a terminated run, that a stopped scheduler costs availability and not state |
| **Not** measurable | **model calls.** The vendored sample's executors call no model, so the band's call-count comparison has no number here and none is borrowed from another column |
| **Not** observable | **a child cascade.** The ports live inside one orchestration, so "children cancelled" cannot be claimed |

Its answer to a port is an **event** (`RaiseEventAsync`), and its terminate did not surface as the framework's
own terminal status on this emulator — `abort-mid-wait` records `abort acknowledged, run unchanged` and five
3-second samples showing the status did not move. Those are findings about the substrate, recorded as
measurements, not smoothed into the vocabulary of the other two columns.

## Why some rows are `not exercised`

`baseline` / `abort-mid-wait` is **not exercised**, with its reason in the result file and re-checked on
every run: the baseline's port advertises exactly two modes, `run` and `resume`, so there is no abort verb
and no abort tree over the workflow's pending work. Injecting an abort would mean adding one to the
baseline, which this band does not do. The script verifies that claim before recording it, so a baseline
that grew an abort verb would fail the check rather than leave a stale paragraph in a checked-in result.
`abort-mid-wait` also has no control, and its result says why: the control for an abort is a run that was
never aborted, which the band's two-process evidence (#176, #177) already measures.

## The before/after controls

Each control is the same script with the injection removed, and each outcome is named for the distinct
thing it is, so a control is never read as the fault's:

| Fault | Fault's observation | Control's observation |
| --- | --- | --- |
| `kill-during-wait` | the wait survived the death of the only process in it | the same flow, no kill, completes |
| `restart-after-checkpoint` | the resume cost no model calls for committed work | the same flow, uninterrupted |
| `duplicate-approval` | the second delivery was refused by the decision document's rule | one delivery, nothing to refuse |
| `late-approval` | the terminal state held | the same approval, applied on time |
| `store-outage` | the store refused the write and the run did not advance | nothing made immutable, so the run completes |

## Model-call counting

This is the measurable part of the comparison, and it is taken two ways because the two variants record
their calls differently:

- **baseline** — the harness's endpoint writes one JSONL line per request. A number is the count of
  requests served *to the process that ran against that endpoint instance*, so process 2's count is
  process 2's, not a running total.
- **durable** — the variant's own ledger, read back from the store (`band-g.model-ledger`), which commits
  with the memo in the step's own transaction.

The counts are in every result's `measurements`, and the summary renders them. What they show is in the
band's README; the short version is that the baseline's second process repeats the work the first process
had done because its checkpoint is the workflow's state and a turn in flight is not in it, while the
Durable variant's second process adds nothing for a step the store already holds.

## Re-runnable

Every run gets its own directory under `$FAULTS_SCRATCH` (default `.scratch/band-g-faults/`), its own port
(`lib/common.sh` assigns one per variant per fault), its own store, and its own endpoint. The cleanup trap
reaps every process by PID — including a killed application's children, which is how the Durable variant's
recorded runtime is not left behind — removes any `chflags uchg` it applied, and removes the run's
directory unless `KEEP=1`. A run that fails leaves no process and no store behind, and a second run starts
from nothing. The build output lives in the same scratch root and is rebuilt per harness run.

`results/` and `fixtures/` are checked in; `.scratch/` is not (the repository's root `.gitignore` covers
`.scratch/` anywhere), so a run leaves no untracked file behind.

Every result is rewritten on every run, because `recordedAt` is the time it ran and `evidence` names the
PIDs that were killed. **The outcomes are the reproducible part, not the bytes:** a second full run
reproduces every outcome and every count, and differs only in timestamps, PIDs, and the checkpoint and
session ids the framework allocates. That is what makes `checked-in results are what the scripts emit`
checkable — re-run, then compare the outcome and measurement fields rather than the whole file. A
reviewer doing that will see `git diff` on all 22 files and an empty diff on everything a table cites.

## Results

`results/<variant>/<fault>.json` — one document per run: `variant`, `fault`, `outcome`, `phase`, `detail`,
`evidence`, `measurements`, `harness`, `recordedAt`. `harness.commit` is the commit that produced the
result, so a reader can find the code that emitted the number. `results/summary.txt` and
`results/summary.md` are rendered from those files by `lib/summarise.py`, which fails when a fault has no
result for a variant — a missing file is indistinguishable from a fault nobody ran. `results/run-status.txt`
records each script's exit status, which is what "every fault ran" is a claim about.

Outcome vocabulary:

| Outcome | Meaning |
| --- | --- |
| `lost` | the work did not survive; nothing continued it |
| `wait survived` / `survived` | the wait, or the run, was still there afterwards |
| `resumed, no calls repeated` | a new process continued the work without re-calling the model |
| `resumed, completed work re-done` | a new process continued the work and repeated what the dead one had done |
| `second delivery refused` | the duplicate was refused, and by the document's rule rather than the terminal guard |
| `doubled` / `applied again` | the same effect happened a second time |
| `terminal state held` | a decision after the fact could not move a finished state |
| `cancelled` | the parent's abort handler saw its child already terminal |
| `failed safely` / `failed safely, gate still answerable` | the store refused the write, the run did not advance, and what remains is stated |
| `not exercised` | the variant cannot be faulted this way; the reason is in `detail` |
| `… (the control)` | a control's outcome, named so it cannot be read as the fault's |

## Layout

```
faults/
  run-all.sh                     every fault, both variants, then the summary
  kill-during-wait.sh            one script per fault; all take --variant, five take --fault-not-injected
  restart-after-checkpoint.sh
  duplicate-approval.sh
  late-approval.sh
  abort-mid-wait.sh
  store-outage.sh
  lib/common.sh                  build, start, stop, observe, record, cleanup
  lib/baseline_endpoint.py       the recorded endpoint: the baseline's only seam, and its call counter
  lib/record_transcript.py       records that endpoint's transcript once, from a real model
  lib/runtime_gate.py            the relay in front of the recorded runtime, for the Durable variant's seam
  lib/store_read.py              reads either variant's store from disk; absent is exit 3, not an empty string
  lib/summarise.py               renders results/summary.{txt,md}; fails on a missing result
  fixtures/baseline-transcript.jsonl
                                 the recorded answers, keyed by request shape. See the note below.
  results/                       checked-in results and the rendered summary
```

The transcript is a recording of one two-process run of the baseline against a locally served model
(qwen3:1.7b through Ollama's OpenAI-compatible endpoint), taken with `lib/record_transcript.py`. It holds
four request/response pairs: the manager's pre-survey, its plan, the researcher's turn, and the writer's
turn. The answers are the model's own, unedited, because the Magentic manager parses the model's output:
a paraphrase loses the structure the plan step depends on. What the recording buys is a run that needs no
key, no network, and no sampler — so the fault comparison is against a fault and not against sampling
noise. **It is a recording, not a model**, and nothing here is a claim about model quality.

## Prerequisites

The .NET 10 SDK at `/opt/homebrew/bin/dotnet` (or `DOTNET`), `python3`, `curl`, and the recorded runtime
built (`dotnet build tools/ReplayRuntime/ReplayRuntime.csproj`). `store-outage` additionally needs
`chflags` (macOS); on a machine without it that fault records `not exercised` with the reason instead of
reporting an unaffected run.
