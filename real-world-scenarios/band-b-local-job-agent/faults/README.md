# Band B — fault evidence

Five faults and one comparison, run against **both** Band B variants. Every number in the band README's
fault table comes from these scripts; nothing is typed.

```bash
./faults/run-all.sh                 # five faults, controls, and the run-equivalence test, both variants
./faults/run-all.sh durable         # one variant
./faults/run-all.sh --no-controls   # the five faults and the diff test, no controls

./faults/kill-mid-turn.sh            --variant baseline
./faults/store-outage.sh             --variant durable
./faults/run-equivalence.sh          --variant durable
```

A plain run writes to a directory of its own and prints the `cp` that would promote it. It does not touch
`faults/results/`, which is the evidence: a machine without the Copilot CLI, or without a signed-in
account, would otherwise replace measured rows with `not exercised` and nothing would notice. Promoting is
explicit:

```bash
FAULTS_RESULTS_DIR=$PWD/faults/results ./faults/run-all.sh
```

Even then, replacing a non-`not exercised` result with `not exercised` is refused unless
`FAULTS_ALLOW_DOWNGRADE=1` is set, so a capability that goes missing is a deliberate edit rather than a
silent one.

Each script takes `--variant baseline|durable`; five of the six also take `--fault-not-injected`, which
runs the same steps with the injection removed and records it as a **control**. A script exits `0` when it
ran, including when the run lost work — lost work is the observation, not a failure. A non-zero exit is a
harness error: a process that never came up, a gate that never reached the injection point, a kill that
could not be aimed at the process it was meant to hit.

## Two things about this band decide the shape of the harness

**It makes live model calls.** There is no recorded runtime for this job. Band A could point both variants
at a fixture, because its enquiry was a transcript; Band B's work *is* the model's tool calls inside the
target, so a fixture would have nothing to run. Every run here calls `gpt-5.1-codex-mini` through the
Copilot CLI. That is why the runs take minutes rather than seconds, and why the results say which model
produced them.

**`dotnet run file.cs` is not the application.** It starts a `dotnet` host which starts the application, so
a PID from `$!` names the host — `kill -9` would land on the wrapper and the job would keep running. Both
variants are therefore built into an apphost once per run (`dotnet build file.cs -c Release -o <dir>`) and
started as `<dir>/ralph-loop*`. The PID a script kills is the application's own, and the death it confirms
is that PID's.

## The injection point

Both variants reach a model through the SDK, and the SDK resolves its runtime from `COPILOT_CLI_PATH`. The
baseline has no command line for a runtime, so that variable is the only seam it has; the Durable variant's
`DURABLE_RUNTIME_PATH` says the same thing by another name. Both point at `lib/cli_gate.py`, which `exec`s
the CLI the SDK shipped with the build (`<apphost>/runtimes/*/native/copilot`):

```
app  ── JSON-RPC frames ──▶  gate  ── JSON-RPC frames ──▶  copilot
```

The gate relays every frame unchanged, and can do four things, each named by an environment variable:

| Variable | What it does | Used by |
| --- | --- | --- |
| `FAULTS_HOLD=<in\|out>:<event>` | hold the Nth frame in that direction until `<run>/release` exists | all five faults |
| `FAULTS_REPEAT=1` | hold every matching frame from the Nth on, clearing the markers each time | `restart-after-checkpoint` |
| `FAULTS_DUPLICATE=<in\|out>:<event>` | write one client→runtime frame twice | `duplicate-delivery` |
| `FAULTS_TEAR_AFTER=<in\|out>:<event>` | close the runtime's input after a frame | not used by any shipped fault; the store outage moves files instead |

It writes `<run>/inflight` when it holds, so a script waits for a *marker* rather than sleeping; and
`<run>/servers` records the application, gate and runtime PIDs, so a `kill -9` that reparents the CLI
leaves nothing behind.

One correction it makes, stated because a reader should be able to tell: the gate re-numbers the *second*
copy of a duplicated request with an id from a range it reserves. A duplicate id is a protocol violation and
would measure the parser rather than the application; the body, which is what the application acts on, is
unchanged.

## What each fault does

| Script | Drive to | Inject | Observe |
| --- | --- | --- | --- |
| `kill-mid-turn.sh` | the runtime's second `tool.execution_start`, so a turn is in flight and files are already being touched | `kill -9` on the application's PID, confirmed gone | a **new** process: does it resume, restart, or refuse — and what did the half-turn leave on disk |
| `restart-after-checkpoint.sh` | a frame between iterations (Durable: the next iteration's `session.create`; baseline: a returned tool call) | `SIGTERM`, reported as the signal `wait` saw, never called "clean" | a second process: does it continue from what was committed |
| `duplicate-delivery.sh` | one iteration | the same prompt request forwarded twice | tool calls **after** the re-delivery, and the plan the run left |
| `concurrent-clients.sh` | loop 1 blocked at a tool call | loop 2 started in the same directory while loop 1 is blocked | which refuses, which corrupts, and whether the two connection windows actually overlapped |
| `store-outage.sh` | a tool result in flight | `mv` the store aside while the process holds it open | what the live path holds, what the moved file still recovers, what the target gained |
| `run-equivalence.sh` | *not a fault*: reference, repeat, and kill-then-continue of one staged target | — | whether the interrupted run's product is the reference's |

## The target is staged, and why

`baseline/target/` has twelve checkable items and the baseline's **unbounded** prompt finishes all twelve
in one pass on this machine — measured three times, and again with 62 and with 210 items staged (one
pass, exit 0, `baseline/verify.sh` passing). So the baseline has no second iteration to be faulted
between, and `restart-after-checkpoint` needs a different seam for it. The prompt is not changed: adding a
pass bound to it would be improving the baseline.

What the harness does instead is grow the *job*: `lib/stage.py` adds `docs/section-NN.md` files carrying the
template's only defect (no H1) and a plan line each, in the template's own wording, so `baseline/verify.sh`
covers them unmodified. Fourteen sections by default, which is enough for the Durable variant to take eight
iterations.

## The extra evidence: a run that was interrupted and continued

`run-equivalence.sh` runs the same variant three times over three copies of one staged target:

1. **reference** — uninterrupted, one process, to its own end;
2. **repeat** — a second uninterrupted run, which is what says two clean runs are even the same thing;
3. **interrupted** — a process killed with `SIGKILL`, then a second process started against the same
   target.

**What is compared, exactly.** The product, not the directory: every regular file under the target except
`.durable/` (the store, which differs by construction — one process opened it versus two). For each file,
its SHA-256 and its path relative to the target, as one text; two runs are identical when that text is
equal. So "identical" means *the same file set with the same bytes in each file* — not the same names, and
not the same contents in a different arrangement. A file renamed between two runs fails the comparison.

**It is made able to fail, twice, on every run.** Before any comparison is used, the script proves the
comparator can say no: an appended byte must report a difference, an extra file must report a difference,
and the unmodified tree must report identity. If any of the three misbehaves, the run stops and records
`comparator failed its self-check` rather than a comparison it could not make. The **repeat** run is the
second guard: if two clean runs differ, the result is `not comparable: two clean runs differed` and nothing
is claimed about the interrupted one.

## Why some rows are `not exercised`

Two, and both are checked rather than asserted:

- **`baseline` / `store-outage`.** The baseline writes no agent state outside the job's own files, so there
  is no store to take away. The script runs it once and lists the target's files before recording the
  reason: if it ever finds a store, checkpoint or session index, it records a finding about the README
  instead of the absence.
- **`durable` / `restart-after-checkpoint`.** A restart "after a committed iteration" needs a moment
  between two iterations that a process *outside* the run can stop at, and there is none. The only such
  frame is the creation of the next iteration's conversation — and at that frame the store already says
  `turn-started/n`, because the checkpoint that opens a turn is committed before its conversation is
  created and the settle that closes it is inside the same iteration. The script reads that checkpoint at
  the frame and records it, with the number of iterations that had completed. A process stopped there
  leaves what `kill-mid-turn` leaves: a turn issued and never finished, which the next process refuses
  rather than repeats. The harness does not move the seam inside the variant to make the row green.

`kill-mid-turn` and `restart-after-checkpoint` therefore overlap on the Durable variant. That is the
variant's shape, not a harness shortcut: it has exactly one externally reachable stop point that is not a
clean exit, and the honest report is that both names land on it with different consequences for the second
process.

## The before/after controls

Each control is the same script with the injection removed, and each outcome is named for the distinct
thing it is, so a control is never read as the fault's:

| Fault | Fault's observation | Control's observation |
| --- | --- | --- |
| `kill-mid-turn` | a dead turn | the held tool call released instead: the run finishes |
| `restart-after-checkpoint` | what a second process does with the stop | the same run, never stopped: it finishes |
| `duplicate-delivery` | the tool calls after the re-delivery | one delivery, nothing re-delivered: the counts to read the duplicate against |
| `concurrent-clients` | two overlapping loops | the same two loops one after the other: proves running twice is not what damages a target |
| `store-outage` | the store moved away | nothing moved: the run finishes against the store that is still there |

## Results

`results/<variant>/<fault>.json` — one document per run: `variant`, `fault`, `outcome`, `phase`, `detail`,
`evidence`, `harness` (dotnet, model, the command, the owning script, and the commit) and `recordedAt`.
`results/summary.txt` and `results/run-all.log` are written by `lib/summarise.py` and `run-all.sh` from
those files.

`summarise.py` exits non-zero on four things, so a green run means the evidence is present *and*
attributable:

| It fails when | Because |
| --- | --- |
| a variant has no result for a fault | a missing file is indistinguishable from a fault nobody ran |
| a result's `harness.script` is not the script that owns that fault | stale or hand-edited files would otherwise render as evidence |
| a result's `harness.commit` is not the checked-out commit | the numbers would describe different code from the one being read |
| an outcome is not one of the defined words | a sentence invented at the point of writing is how a result ends up contradicting its own evidence |

Outcome vocabulary, used in the results and in the band README:

| Outcome | Meaning |
| --- | --- |
| `completed` | the run reached its own end |
| `resumed and finished` | a second process continued the work and completed it |
| `resumed but did not finish` | a second process continued the work and did not complete it |
| `resumed after the kill` | a second process continued a run killed in a turn |
| `refused to resume the in-flight iteration` | the store records a turn that was issued and never finished; the variant stopped rather than repeating it |
| `refused to resume the dead turn` | the same, for a kill inside a turn |
| `restarted from the plan` | the second process began again; nothing recorded its position |
| `lost its position` | no process continued the work and none resumed it |
| `doubled` | the duplicate delivery produced tool calls of its own |
| `not doubled` | the duplicate was delivered and produced no tool call |
| `refused the second client` | the second loop exited without reading the plan, sending a prompt or writing a file |
| `not corrupted` | both loops finished and the target matches the plan |
| `corrupted` | the target the two loops left is not one either would have produced alone |
| `failed safely` / `failed safely, without advancing` | nothing durable was lost, and what the application did next is stated |
| `failed unsafely` | the store cannot be read back in full |
| `continued past the outage` | the run neither refused nor stopped after the store went |
| `identical` / `differs` | the run-equivalence comparison |
| `not comparable: two clean runs differed` | the repeat run makes the comparison meaningless |
| `not resumed: the interruption landed in the in-flight turn` | the kill hit the one state the variant does not resume |
| `not exercised` | the variant cannot be faulted this way; the reason is in `detail` |
| `control: …` | the outcome of a control run, never to be read as the fault's |

## Re-runnable

Every run gets its own directory under `$FAULTS_SCRATCH` (default `.scratch/band-b-faults/`), its own
staged target, its own gate log, and its own copy of the apphost. A cleanup trap reaps the application, the
gate and the CLI — by PID, from the gate's `servers` file, because `kill -9` on the application reparents
the other two — and removes the directory unless `KEEP=1`. A run that fails leaves no process and no
target behind, and a second run starts from nothing.

## Layout

```
faults/
  run-all.sh                    sequence every fault and the diff test, then render the summary
  kill-mid-turn.sh              one script per fault; all take --variant
  restart-after-checkpoint.sh
  duplicate-delivery.sh
  concurrent-clients.sh
  store-outage.sh
  run-equivalence.sh            reference, repeat, kill-then-continue, and the comparison
  lib/common.sh                 build, stage, start, wait, kill, digest, record
  lib/cli_gate.py               the JSON-RPC gate in front of the CLI; hold, repeat, duplicate, tear
  lib/stage.py                  grows the target so a run has several iterations
  lib/overlap.py                reads the gate log's two connection windows
  lib/plan_shape.py             reports whether a PLAN.md two processes wrote is still a plan
  lib/summarise.py              renders results/summary.txt; fails on a missing or stale result
  results/                      checked-in results and the rendered summary
```

Prerequisites: the .NET 10 SDK at `/opt/homebrew/bin/dotnet` (or `DOTNET`), `python3`, and a signed-in
Copilot CLI with access to `gpt-5.1-codex-mini` (`FAULTS_MODEL` overrides it). The CLI the harness uses is
the one the SDK shipped with the build, not the one on `PATH`: with the harness pointed at the 1.0.105
install, `session.create` refused `gpt-5.1-codex-mini` as unavailable, and the bundled 1.0.103 ran it.
