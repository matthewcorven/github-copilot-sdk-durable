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

| Variant | run 2 comments | run 3 comments posted | run 3 comments adopted | PRs holding two comments | run 3 resumed |
| --- | --- | --- | --- | --- | --- |
| baseline | 13 | 13 | 0 | **13** | no |
| durable | 13 | 0 | 13 | **0** | yes |

Baseline, PRs holding two comments: `#101 #102 #103 #104 #105 #106 #107 #109 #111 #112 #113 #114 #115` —
every eligible PR, all thirteen.

### What the baseline does

Run 2 posts all thirteen comments and its Phase 8 state write does not happen:

```
$ ./faults/cooldown-desync.sh --variant baseline
harness: wrote faults/results/baseline/cooldown-desync.json

$ python3 -c 'import json;print(json.load(open("faults/results/baseline/cooldown-desync.json"))["detail"])'
Run 2 posted 13 comment(s) and its state write did not happen (… memory/pr-triage was left where run 1
put it), so run 3 — 61 minutes later, past the cooldown — read the state run 1 wrote (prior run:
2026-11-05T12:00:00Z triaged 13 PR(s)), decided the previous run was old enough to triage again, and
posted the same 13 comment(s) a second time: 13 PR(s) hold two comments …
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
| `kill-mid-turn` | `kill -9` of the real PID inside Phase 2, confirmed gone, then a new process | baseline: cold start, 13 comments posted again. Durable: resumed at `classify`, finished | nothing about a *clean* stop — that is the next row |
| `restart-after-checkpoint` | `SIGTERM` (baseline) / its own seam (durable) after Phase 5's `labels.json`, then a restart | baseline has no checkpoint at all, so the restart is a cold start. Durable resumed at `comment` and posted 13 — the work the stop left undone — with 0 re-posted | that the durable resume posts nothing. It posts what was *not* done; it adopts what was |
| `duplicate-delivery` | two live processes over one repository, first held at Phase 7, second run through | baseline: both posted 13, both exited 0, no conflict was ever detected. Durable: second refused at the store open (exit 3) | that the Durable refusal is per *repository* — it is per store file (see the variant's stage 5b control) |
| `concurrent-clients` | first held with `SIGSTOP` at a phase, second runs to completion, first released | baseline: same doubling and no conflict detection as above. Durable: refused while the first is merely paused between phases | that either variant is safe under a *crash* mid-write — that is `store-outage` |
| `store-outage` | real `mv` of the state aside while the process is live | baseline fails loudly (exit 1) with the effects kept and the state gone; durable survives with the state split across two files | a *tie*: neither variant keeps one coherent store. The durable variant fails **safe**, not **well** |
| `cooldown-desync` | baseline `--dry-run` (Phase 8 objects written, ref not moved) / durable `BAND_C_EXIT_AFTER_COMMENTS=13`, then two later runs | the 13-PR double comment above vs. 0 | the one-comment window the durable guard cannot close (measured in `durable/evidence.sh` 4c) |

## The before/after controls

Each control is the same script with the injection removed. The pair is what makes a result
attributable; the control's outcome is labelled `control: …` in every result file and in the summary,
so it is never read as the fault's.

| Fault | Variant | Control | Fault | What the control proves | What it does **not** prove |
| --- | --- | --- | --- | --- | --- |
| `kill-mid-turn` | baseline | completed | lost | the two-run shape alone does not lose anything; the loss belongs to the kill | that the baseline would survive a kill elsewhere — it has no state at any point |
| `kill-mid-turn` | durable | completed | resumed | the resume belongs to the kill, not to running twice | that any interruption resumes — only the one measured |
| `restart-after-checkpoint` | baseline | completed | lost | an uninterrupted run commits; the stop is what costs the restart | nothing about the baseline's ability to resume — it has none |
| `restart-after-checkpoint` | durable | resumed (at `classify`) | resumed (at `comment`) | the phase is read from the record, not assumed: an earlier seam re-enters earlier | that the resume is exact for every phase — two phases were measured |
| `duplicate-delivery` | both | control: single delivery | doubled / refused | one process posts 13 once; the second delivery is what adds the second set or the refusal | that a *sequential* rerun is covered — that is `cooldown-desync` |
| `concurrent-clients` | both | control: not doubled | doubled / refused | the concurrency is the difference; run one after the other and nothing doubles | that the baseline would resist a *different* overlap — the window was chosen, not sampled |
| `store-outage` | both | completed | failed loudly / split | the outage is the difference, not the length of the run | that a restored store merges — it does not, and that is in the detail |
| `cooldown-desync` | baseline | control: not doubled | doubled | the harness's three-run shape did not produce the fault row's numbers — run 3 hitting the run gate when run 2 *did* commit isolates the missing state write | that the baseline is otherwise safe: with the state committed the duplicate persists but is *inside* the gate, which is a different bug |
| `cooldown-desync` | durable | control: not doubled | not doubled | the ledger alone already prevents an unchanged rerun | which guard the fault row needed (resume or ledger). Both are reported in its evidence |

### Where the control's outcome does **not** flip

`cooldown-desync` / durable is `not doubled` in both halves, and that is the honest result: the variant
does not have the defect the control removes. The control's value there is the *other* direction — it
shows the ledger, not the resume, is what stops a *completed*-run rerun, while the fault run's evidence
(`resumed=1 posted=0 adopted=13`) shows the resume is what finished the interrupted one. A row that
cannot flip is explained, not dressed up.

`restart-after-checkpoint` / baseline similarly produced the same outcome from an earlier stop, which
is why its control is "not stopped at all" rather than "stopped earlier" — the baseline has no commit
point to be before or after, and the control says so in its `phase`.

## The injection point

A fault script never sleeps and hopes. Every script waits for a named artefact the program itself
produced and then acts:

| What the script waits for | Why that artefact |
| --- | --- |
| `phase 2/8` **and** at least one `  #NNN …` line under it | the classifier is running and has done work — `kill-mid-turn` |
| `wrote:      labels.json` in the log | Phase 5 has finished — `restart-after-checkpoint` |
| `outputs/<run>/labels.json` on disk | Phase 5 has finished, without depending on a stdout flush — `concurrent-clients`, `store-outage` |
| `outputs/<run>/triage-report.md` on disk | Phase 7 has finished and Phase 8 has not — `duplicate-delivery` |
| the store file exists, its `.lock` sidecar exists, and it holds a committed `band-c.run` | the store is open, locked and committed — `store-outage` (durable) |
| the seam's exit 42 | the variant's own committed checkpoint — `cooldown-desync`, `restart-after-checkpoint` (durable) |

The baseline's stdout is **block-buffered** when redirected (a ~4 KB chunk arrives together), so a log
line is not a reliable "still running" signal. That is why three of the faults wait on files the program
writes rather than on lines it prints, and it is a real property of the program, not a harness
workaround: the log line appears when a buffer flushes, the file appears when the write happens.

`SIGSTOP`/`SIGCONT` are used by `concurrent-clients` and `duplicate-delivery` (baseline arm) to *hold* a
process at a phase. They are real signals to a real PID; they change nothing the program does. The fault
itself is the second process.

## Never simulated in-process

| Fault | The operating-system act |
| --- | --- |
| `kill-mid-turn` | `kill -9 <pid>`, then a poll until `kill -0` fails. The PID is the program's own — the starter `exec`s, so the shell's `$!` *is* the process |
| `restart-after-checkpoint` | `kill -TERM <pid>`, then a poll (baseline); the Durable variant's seam is `Environment.Exit(42)` inside the program, which is the variant's own seam, not a harness fabrication |
| `duplicate-delivery` | two real processes; `kill -STOP`/`kill -CONT` on the first |
| `concurrent-clients` | `SIGSTOP` on the first process, confirmed by `ps -o state=` reading `T`, then released |
| `store-outage` | a real `mv` of the store path while the process holds it open |
| `cooldown-desync` | the baseline's existing `--dry-run` flag; the Durable variant's existing `BAND_C_EXIT_AFTER_COMMENTS` seam. Neither file is edited |

Nothing in `baseline/` or `durable/` is edited by this harness. The one thing the harness does to the
baseline's *invocation* is build it with `dotnet build -o` and launch the DLL: `dotnet publish` on a
file-based app refuses on this machine (it tries NativeAOT and needs a clang toolchain), and a DLL
launched out of process is the same program with a PID a fault can signal.

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
