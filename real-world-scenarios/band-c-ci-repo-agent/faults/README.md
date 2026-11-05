# Band C — fault evidence

Six faults, run against **both** Band C variants, each writing one machine-readable result. The
classic five, plus the one this band exists for: **`cooldown-desync`**. Nothing in the table below is
typed by hand — every row is read from `results/<variant>/<fault>.json`, which the scripts below
wrote.

```bash
./faults/run-all.sh                 # twelve faults+controls, both variants (~50 s on this machine)
./faults/run-all.sh durable         # one variant
./faults/run-all.sh --no-controls   # the six faults only

./faults/kill-mid-turn.sh --variant baseline
./faults/cooldown-desync.sh --variant durable --fault-not-injected
```

Each script takes `--variant baseline|durable` and `--fault-not-injected`. Every script exits `0` when
it ran, **including when the scenario lost work** — a lost run is the observation, not a failure. A
non-zero exit is a harness error: a publish that failed, a process that never reached the injection
point, a seam that never fired. `run-all.sh` records each exit status and exits non-zero if any of them
did.

## The matrix

| Fault | baseline | durable |
| --- | --- | --- |
| `kill-mid-turn` | **lost** | **resumed** |
| `restart-after-checkpoint` | **lost** | **resumed** |
| `duplicate-delivery` | **doubled** | **refused, not doubled** |
| `concurrent-clients` | **doubled, no conflict detected** | **refused, not doubled** |
| `store-outage` | **failed loudly, effects kept, state lost** | **failed safely, with a split store** |
| `cooldown-desync` | **doubled** | **not doubled** |

Nothing in this table is recorded `not exercised`. Both variants have a store on disk (the baseline's
is a git branch in a bare repository), both can be killed, and both can be started twice, so the faults
reach both arms. Where a row is a *tie* it is written as one — `store-outage` is not a win for either
side: the baseline fails loudly and loses the state after the effects, the Durable variant survives but
splits its store in two, silently. See §"What each row does and does not show".

## The headline: `cooldown-desync`

| Variant | run 2 comments | run 3 posted | run 3 adopted | run 3 duplicated (byte-level) | run 3 resumed | guard-disabled arm: posted / duplicated |
| --- | --- | --- | --- | --- | --- | --- |
| baseline | 13 | 13 | 0 | **13** | no | not run (no guard to disable) |
| durable | 13 | 0 | 13 | **0** | yes | **13 / 13** |

Baseline, PRs holding two comments: `#101 #102 #103 #104 #105 #106 #107 #109 #111 #112 #113 #114 #115` —
every eligible PR, all thirteen.

"Duplicated" is measured, not inferred: for each comment a run says it wrote, the harness compares the
bytes with a snapshot taken before the run. A count of files cannot see a duplicate (a file is written
once per PR and adopted files are rewritten identically), and neither can "did it post anything". The
readers' failure mode matters too — a reader that errors must not be quoted as a zero, so every measure
in `lib/common.sh` fails the run rather than returning an empty list.

### What the baseline does

Run 2 posts all thirteen comments and its Phase 8 state write does not happen:

```
$ ./faults/cooldown-desync.sh --variant baseline
harness: wrote faults/results/baseline/cooldown-desync.json

$ python3 -c 'import json;print(json.load(open("faults/results/baseline/cooldown-desync.json"))["detail"])'
Run 2 posted 13 comment(s) and its state write did not happen (no memory/pr-triage ref was moved, so the
branch was left where run 1 put it), so run 3 — 61 minutes after run 2, at 2026-11-05T14:31:00Z — read
the state run 1 wrote (prior run: 2026-11-05T12:00:00Z triaged 13 PR(s)), decided the previous run was
old enough to triage again, and posted the same comments a second time: 13 of run 3's 13 written
comment(s) were byte-for-byte what was already on disk …
```

Run 3's own log, from the same run:

```
  state:      loaded memory/pr-triage:pr-triage-latest.json (7346 bytes, commit …)
  prior run:  2026-11-05T12:00:00Z triaged 13 PR(s)      ← run 1's record, not run 2's
  …
  wrote:      comments/pr-101.md                          ← posted for the second time
  …
triaged 13 PR(s): #101 auto_merge, … #115 batch_review
```

The mechanism, stated plainly: the baseline's only memory is a whole-file snapshot written as the last
step of Phase 8. A run that dies between its comments and that write leaves the comments and **the
previous run's view**. The next run reads that view, finds it past the one-hour cooldown, and triages
again. It has no record of a comment — not a digest, not a timestamp, not an id — so there is nothing
to compare against and nothing that could notice.

### What the Durable variant does

Run 2 posts all thirteen comments and stops before the comment phase commits:

```
run2 exit=42 … task=id=t2 status=1 phase=comment attempts=1 commented=0 memos=13 … ledger=13
run3 … resumed=1 at phase=comment posted=0 adopted=13
run4 exit=0 posted=0 adopted=13
```

The run record is what did not commit — and it did not need to, because each comment was memoized as it
was made (`memos=13` on the task record) and the comment ledger outlived the process that wrote it
(`ledger=13`). Run 3 finds an unfinished task at `comment`, resumes there, and adopts all thirteen
instead of re-posting. Run 4 — a new generation of a finished task, which is what a later scheduled run
is — still posts nothing, so the guard outlived the *task* and not just the process.

### This fault's limitation, stated

The Durable variant's injection stops *inside the comment phase* (`BAND_C_EXIT_AFTER_COMMENTS=13`), not
after all eight phases: it must, because that variant has no state write at the end to fail — every
decision is committed as it is made. The window the two variants are stopped in is therefore the same
window in the sense that matters (comments posted, run not committed), but not byte-for-byte the same
program counter. The variant's own `durable/evidence.sh` stage 4c measures the residue: a stop *between*
one comment and its ledger record costs exactly one duplicate, because that window cannot be closed by
any memo. That is recorded there and is not re-measured here.

## What each row does and does not show

| Fault | Injection | What it shows | What it does **not** show |
| --- | --- | --- | --- |
| `kill-mid-turn` | `kill -9` of the real PID with 7 comment files on disk, confirmed gone, then a new process | baseline: cold start, all 13 comments posted, 13 of them byte-identical duplicates. Durable: resumed at `comment`, wrote the remaining 4 with 0 duplicates | nothing about a *clean* stop — that is the next row. The fault is deliberately *not* "mid-classification": the baseline's buffered stdout cannot bound a kill to Phase 2 (see §"The injection point") |
| `restart-after-checkpoint` | `SIGTERM` (baseline) / its own seam (durable) after Phase 5's `labels.json`, then a restart | baseline has no checkpoint at all, so the restart is a cold start. Durable resumed at `comment` and posted 13 — the work the stop left undone — with 0 re-posted | that the durable resume posts nothing. It posts what was *not* done; it adopts what was |
| `duplicate-delivery` | two live processes over one repository, first held at Phase 7, second run through | baseline: both posted 13, both exited 0, no conflict was ever detected. Durable: second refused at the store open (exit 3) | that the Durable refusal is per *repository* — it is per store file (see the variant's stage 5b control) |
| `concurrent-clients` | first held with `SIGSTOP` at a phase, second runs to completion, first released | baseline: same doubling and no conflict detection as above. Durable: refused while the first is merely paused between phases | that either variant is safe under a *crash* mid-write — that is `store-outage` |
| `store-outage` | real `mv` of the state aside while the process is live | baseline fails loudly (exit 1) with the effects kept and the state gone; durable survives with the state split across two files | a *tie*: neither variant keeps one coherent store. The durable variant fails **safe**, not **well** |
| `cooldown-desync` | baseline `--dry-run` (Phase 8 objects written, ref not moved) / durable `BAND_C_EXIT_AFTER_COMMENTS=13`, then two later runs, plus a guard-disabled arm | the 13-PR double comment above vs. 0, with the guard-disabled arm posting 13/13 duplicates when the guard is removed | that a partial adoption is impossible: if run 3 re-posted even one already-present comment it would be filed `doubled`, with the measured list. The one-comment window `durable/evidence.sh` 4c describes is exactly that case |

## The before/after controls

Each control is the same script with the injection removed. The pair is what makes a result
attributable; the control's outcome is labelled `control: …` in every result file and in the summary,
so it is never read as the fault's.

| Fault | Variant | Control | Fault | What the control proves | What it does **not** prove |
| --- | --- | --- | --- | --- | --- |
| `kill-mid-turn` | baseline | completed | lost, and re-commented | the two-run shape alone does not duplicate anything (0 duplicates); the loss belongs to the kill | that the baseline would survive a kill elsewhere — it has no state at any point |
| `kill-mid-turn` | durable | completed | resumed | the resume belongs to the kill, not to running twice | that any interruption resumes — only the one measured |
| `restart-after-checkpoint` | baseline | control: lost | lost | an *earlier* stop in the same run loses the work in exactly the same way, so the fault row's cold start is the stop's doing and not the run's | nothing about the baseline's ability to resume — it has none, from either stop. It does **not** isolate *which* phase the stop lands in: this control and the fault row both stop, and both lose everything |
| `restart-after-checkpoint` | durable | resumed (at `classify`) | resumed (at `comment`) | the phase is read from the record, not assumed: an earlier seam re-enters earlier | that the resume is exact for every phase — two phases were measured |
| `duplicate-delivery` | both | control: single delivery | doubled / refused | one process posts 13 once; the second delivery is what adds the second set or the refusal | that a *sequential* rerun is covered — that is `cooldown-desync` |
| `concurrent-clients` | both | control: not doubled | doubled / refused | the concurrency is the difference; run one after the other and nothing doubles | that the baseline would resist a *different* overlap — the window was chosen, not sampled |
| `store-outage` | both | completed | failed loudly / split | the outage is the difference, not the length of the run | that a restored store merges — it does not, and that is in the detail |
| `cooldown-desync` | baseline | control: doubled | doubled | the harness's three-run shape is not what produced the fault row: **with the state committed it doubles too (13/13)**, so the duplicate belongs to the baseline's missing comment identity, and the fault row adds only "the triage was redone from a stale view" | that committing state helps. It does not, and this row says so: this control does not flip |
| `cooldown-desync` | durable | control: not doubled | not doubled | the ledger alone already prevents an unchanged rerun | which guard the fault row needed. The fault's own *guard-disabled* arm answers that: `BAND_C_NO_IDEMPOTENCY_GUARD=1` on the same three runs posts 13 with 13 duplicates |

### Where the control's outcome does **not** flip

Three rows do not flip, and each is explained rather than dressed up. The rule the harness follows: a
control that cannot flip is *reported as not flipping*, and where a second control can be built that
*does* flip, it is built.

**`cooldown-desync` / baseline — `doubled` → `doubled`.** Committing run 2's state does not stop run 3
re-posting: it re-posts all thirteen, byte for byte, because the baseline has no identity for a comment
at all. So this control does not isolate the missing state write — it shows the duplicate is *not*
caused by it. What the fault row adds is the stale view: run 3 re-triages run 1's snapshot rather than
run 2's. Both rows are duplicates and both say so.

**`cooldown-desync` / durable — `not doubled` → `not doubled`.** The variant does not have the defect
the control removes. The flip was built *inside* the fault instead: the **guard-disabled arm** replays
the same three runs from the same post-run-2 store with `BAND_C_NO_IDEMPOTENCY_GUARD=1` and posts 13
with **13 duplicates**, against the fault row's 0. That is the evidence that the zero is the guard
working, and it is in the same result file (`observations.guard_disabled_*`) and in the headline table.

**`restart-after-checkpoint` / baseline — `lost` → `lost`.** The control is an *earlier* stop in the
same run (SIGSTOP then SIGTERM before its first phase completed), not "no stop": an uninterrupted run
would be a different question and would flip trivially. Both arms lose everything, which is the
finding — the baseline has no commit point, so the phase the stop lands in changes only how much work
is lost, not whether any survives. It does **not** show that a stop after Phase 5 is worse than a stop
after Phase 1; it shows neither is recoverable. (A stop *inside* Phase 8 is a different case again: the
run exits 1 at `commit-tree` with no cold start and no re-post — that is `store-outage`, and it is why
"any phase" is not claimed here.)

## The injection point

A fault script never sleeps and hopes. Every script waits for a named artefact the program itself
produced and then acts:

| What the script waits for | Why that artefact |
| --- | --- |
| **7** `comments/pr-NNN.md` files on disk | the Phase 6 effect has happened 7 times — `kill-mid-turn` |
| `wrote:      labels.json` in the log | Phase 5 has finished — `restart-after-checkpoint` |
| `outputs/<run>/labels.json` on disk | Phase 5 has finished, without depending on a stdout flush — `concurrent-clients`, `store-outage` |
| `outputs/<run>/triage-report.md` on disk | Phase 7 has finished and Phase 8 has not — `duplicate-delivery` |
| the store file exists, its `.lock` sidecar exists, and it holds a committed `band-c.run` | the store is open, locked and committed — `store-outage` (durable) |
| the seam's exit 42 | the variant's own committed checkpoint — `cooldown-desync`, `restart-after-checkpoint` (durable) |

The baseline's stdout is **block-buffered** when redirected (a ~4 KB chunk arrives together), so a log
line is not a reliable "still running" signal. That is why three of the faults wait on files the program
writes rather than on lines it prints, and it is a real property of the program, not a harness
workaround: the log line appears when a buffer flushes, the file appears when the write happens.

This was a defect in the first version of this harness and is recorded rather than quietly fixed.
`kill-mid-turn` originally triggered on `phase 2/8` plus "at least one classification line", which was
supposed to bound the kill to Phase 2. A probe of 12 runs killed on that trigger showed what it actually
bounded: **4 of 12 landed with `phase 3/8` already printed** and 13 classification lines in the log,
because the trigger fired on a flushed chunk rather than a phase boundary — and phases 3, 4 and 5 print
in the same `  #NNN …` shape, so the "classification lines" count was really four phases' worth of
lines. The injection was moved to a comment count, which is an artefact the harness sets: 12 of 12
probe runs were caught at exactly 7 comment files, for both variants.

`kill-mid-turn` therefore kills with comments already on disk, not "during classification" as the issue
first described. The question it answers is the one that matters for the band — does the resumed run
re-comment? — and it answers it exactly; the classification-only phrasing did not.

`SIGSTOP`/`SIGCONT` are used by `concurrent-clients` and `duplicate-delivery` (baseline arm) to *hold* a
process at a phase. They are real signals to a real PID; they change nothing the program does. The fault
itself is the second process.

## Never simulated in-process

| Fault | The operating-system act |
| --- | --- |
| `kill-mid-turn` | `kill -9 <pid>` once 7 comment files exist, then a poll until `kill -0` fails. The PID is the program's own — the starter `exec`s, so the shell's `$!` *is* the process |
| `restart-after-checkpoint` | `kill -TERM <pid>`, then a poll (baseline); the Durable variant's seam is `Environment.Exit(42)` inside the program, which is the variant's own seam, not a harness fabrication |
| `duplicate-delivery` | two real processes; `kill -STOP`/`kill -CONT` on the first |
| `concurrent-clients` | `SIGSTOP` on the first process, confirmed by `ps -o state=` reading `T`, then released |
| `store-outage` | a real `mv` of the store path while the process holds it open |
| `cooldown-desync` | the baseline's existing `--dry-run` flag; the Durable variant's existing `BAND_C_EXIT_AFTER_COMMENTS` seam, plus `BAND_C_NO_IDEMPOTENCY_GUARD` for the arm that measures what the guard is worth. Neither file is edited |

Nothing in `baseline/` or `durable/` is edited by this harness. The one thing the harness does to the
baseline's *invocation* is build it with `dotnet build -o` and launch the DLL: `dotnet publish` on a
file-based app refuses on this machine (it tries NativeAOT and needs a clang toolchain), and a DLL
launched out of process is the same program with a PID a fault can signal.

## A failed run leaves no result

Every script removes the result file it is about to write **before** anything else happens
(`begin_result` in `lib/common.sh`). A harness error exits before `record_result`, so the artefact after
a failed run is a *missing* file, never yesterday's document wearing this run's name. `run-all.sh`
stamps the suite's start (`started <iso8601>`) as the status file's first line and `summarise.py`
refuses any result whose `recordedAt` predates it — so even a hand-run single script that somehow left
an older file behind cannot be rendered as this suite's row.

## Re-runnable

Every run gets its own directory under `$FAULTS_SCRATCH` (default `.scratch/band-c-faults/`), its own
store (a bare repository or a JSONL file), its own build output, and its own log. A cleanup trap reaps
every PID the script started — **by PID and by run-directory path** — and removes the directory unless
`KEEP=1`. A second `run-all.sh` starts from nothing; a completed suite leaves the scratch root empty
apart from `run-all.status`, which the next run truncates.

The three things a Band C script needs beyond Band A's: `git` (the baseline's store is a repository),
`curl` (not used by any fault today, checked by `require_tools`), and the .NET SDK at
`/opt/homebrew/bin/dotnet` (10.0.402 on this machine; `DOTNET` overrides). There is no recorded runtime
and no model: a run is one process, and the fixture is a file.

## Results

`results/<variant>/<fault>.json` — one document per run: `variant`, `fault`, `outcome`, `phase`,
`detail`, `evidence`, `harness{dotnet, runtime, command}`, `recordedAt`. `cooldown-desync` additionally
carries `observations` (the counts quoted above); that key is additive, and the other keys are the ones
Band A's results have. `results/summary.txt` is rendered from the JSON by `lib/summarise.py`, which
fails if a variant is missing a fault's result **or its control's**.

Outcome vocabulary:

| Outcome | Meaning |
| --- | --- |
| `resumed` | a new process continued the work from what was committed |
| `lost` | the work did not survive; nothing continued it |
| `doubled`, `doubled, no conflict detected` | the same side effect happened twice, and (second form) no process reported a conflict |
| `refused, not doubled` | the second writer was refused before it did anything |
| `not doubled` | the interrupted run's comments were adopted rather than posted again |
| `failed loudly, effects kept, state lost` | the failure was reported and the effects outlived the record of them |
| `failed safely, with a split store` | nothing committed was lost, and the state now lives in two files |
| `completed` / `control: …` | a control run — never to be read as the fault's |

## Layout

```
faults/
  run-all.sh                    sequence every fault, then render the summary
  kill-mid-turn.sh              one script per fault; all take --variant and --fault-not-injected
  restart-after-checkpoint.sh
  duplicate-delivery.sh
  concurrent-clients.sh
  store-outage.sh
  cooldown-desync.sh
  lib/common.sh                 build, start, hold, kill, wait, observe, record, reap
  lib/store.py                  reads either variant's committed state from the bytes
  lib/fresh_fixture.py          one-field copy of the fixture, captured at a run's own clock
  lib/summarise.py              renders results/summary.txt; fails on a missing result or control
  results/                      checked-in results and the rendered summary
```

## Not proven here

- **The baseline never writes state before Phase 8** is *shown*, not *proven for all inputs*: five of
  the six faults stop it at a different point and every restart is a cold start. The `baseline/` code
  was read to confirm there is no other write path.
- **The Durable refusal is per store file, not per repository.** Two store paths for one repository
  both proceed; the variant's own `durable/evidence.sh` stage 5b is that control, and this harness
  does not repeat it.
- **Only one overlap window is sampled per concurrency fault.** The windows were chosen (Phase 7 for
  `duplicate-delivery`, after Phase 5 for `concurrent-clients`) and nothing here argues the baseline
  fails at *every* overlap; it is enough that it fails at the chosen ones without reporting anything.
- **The one-comment window a Durable memo cannot close** is measured in `durable/evidence.sh` stage 4c,
  not here. This harness records it as a limitation of the `cooldown-desync` injection rather than
  re-deriving it.
