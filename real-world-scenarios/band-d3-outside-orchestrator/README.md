# Band D (D3) — Durable outside: an orchestrator that treats Foundry agents as tools

The other Durable position in Band D. D2 asks whether Durable can live *inside* the
platform; this asks the more common question: **what if the Durable store is the thing
that survives, and the agents are replaceable?**

The unit of work is one durable task. Its phases are the run:

```
coverage  →  call the coverage agent        (memoised, one service invocation)
gate      →  wait for a human decision      (memoised, no service invocation)
letter    →  call the decision-letter agent (memoised, one service invocation)
```

**What is proven, each from an execution in `evidence/`:**

| # | Requirement | Evidence |
| --- | --- | --- |
| 1 | Two agent calls per run, one a human gate | `evidence/run/30-resume-at-gate.log` — coverage and letter are real calls; the gate is a file a person writes |
| 2 | `kill -9` between calls, restart, no repeated call | `evidence/run/20-kill-and-resume.log` — PID gone, then **+0** from the platform's own counter |
| 3 | A changed definition is a new step | `evidence/memo-key/00-transcript.txt` — five scenarios, five passed assertions |
| 4 | Reuse D1's deployment | `deploy-agents.sh` reads D1's `.deployment.json`; it provisions nothing |
| 5 | Nothing left behind | `evidence/teardown.md` |

## The memo key, which is the whole judgement call

A step's key is:

```
step/{step}/{inputDigest}/{agentIdentity}
agentIdentity = {name}@{version}:{sha256(canonical definition)[..16]}
```

The name and the input are the obvious half. **The identity is the half a naive key
leaves out, and leaving it out is a silent correctness bug**: edit an agent's
instructions and a key of name+input still matches, so the orchestrator reuses a result
the *old* agent produced and reports a run the current agent never performed. That is
`MEMO_KEY_MODE=naive`, kept in the source so the evidence can show it failing:

```
D  CONTROL — a store written by v1, v2 live, NAIVE key   → 0 calls  (WRONG)
   D's store reports the coverage step as produced by d3-coverage-assessor@17:48d2a3c…
   while the platform was running d3-coverage-assessor@18:548932c6…
E  a copy of D's store, identity key                     → 1 call   (the key, corrected)
```

The identity is read **at call time, from the platform**, not frozen into the task input
when the run is created. Freezing it would make requirement 3 vacuous: a run created
before an edit would keep using the old identity and never notice the change. An agent is
a dependency that can move under a running workflow, so it is resolved when the step runs.

The version is also **pinned in the request** (`&agent_version=N`). That closes the gap
between "the definition I hashed" and "the definition that answered": a version registered
between the read and the call would otherwise run while the memo record named the older
identity. `evidence/run/80-last-responses.json` shows the platform agreeing — coverage
responses carry `agent_reference.version = "18"`, the letter's `"2"`.

## Requirement 2: the kill, and what the counter is

The counter is **not** the orchestrator's log. Each agent's platform keeps a Response
record per accepted request, listed at
`GET /agents/{name}/endpoint/protocols/openai/responses`. That list survives the client
process and the orchestrator cannot write to it. `count-calls.sh` reads it — **paginated**,
because the default page is 20 and a count that stops growing at 20 reads as "no new
calls" forever. That trap is recorded here because it was hit while building this: the
first counts were silently capped.

```
$ ps -p 22896 -o pid=,stat=,command=
22896 S    …/runfile/orchestrator-…/bin/debug/orchestrator
$ kill -9 22896                                    # exit 0
$ ps -p 22896 >/dev/null && echo alive || echo gone  → gone
$ pgrep -f 'runfile/orchestrator' | wc -l             → 0
```

The restart resumed the same task at `gate`, read the coverage memo, and made **no call**:

```
task:       resuming durable task t1 (restarts before this process: 0)
phase:      gate
memos:      1 on record
  step/coverage/1188409be0bb45d0/d3-coverage-assessor@18:d8f2c9f08a26c347

$ ./count-calls.sh --since evidence/run/21-counts-after-kill.json
  d3-coverage-assessor           45 →   45  (+0)
  d3-decision-letter              1 →    1  (+0)
```

Whole run, across two processes and one `kill -9`: **+2** — one per agent, no more.
The `ps` line above is honest about macOS: immediately after the kill, `ps` can still list
the dead PID with state `Z`/`?E` — a zombie, not a live process. `pgrep` returns nothing
and `ps -p` exits non-zero, which is the check that matters. A "restart" over a process
that was never killed is the failure mode requirement 2 names; this is what rules it out.

Re-running the *finished* run is the second control: `evidence/run/71-rerun-delta.txt`
shows `+0` for both agents.

## Requirement 3: both halves, in one transcript

`./memo-key-demo.sh` varies exactly one thing — the store — and holds the request, the
orchestrator and the current agent definition fixed:

| | Scenario | Calls | Reading |
| --- | --- | --- | --- |
| A | empty store, definition v1 | **+1** | a miss: nothing to reuse |
| B | A's store, definition unchanged | **+0** | **a hit**: the memo is reused |
| A2 | empty store, NAIVE key, v1 | +1 | the control's input |
| — | edit the instructions, re-deploy | | the platform reports a new version and digest |
| C | fresh store, definition v2 | **+1** | **a miss**: a changed definition is a new step |
| D | A2's store, v2 live, NAIVE key | **+0** | **wrong**: it reused v1's result as if v2 produced it |
| E | a copy of D's store, identity key | **+1** | the same bytes, the correct key: v1's memo is invisible |

B and E leave the phase *unadvanced* (`STOP_AFTER_COVERAGE=stuck`), so the process after
them re-enters `coverage`. That is what makes "no call" mean "the memo was consulted"
rather than "there was nothing left to do", and it is the same store shape a killed
process leaves. Each scenario's own log and store report is under `evidence/memo-key/`.

## Requirements 4 and 5: reuse, and leaving nothing

`deploy-agents.sh` registers two prompt agents into the project D1's
`.deployment.json` names. It creates no resource: no registry, no account, no model
deployment. `provision.sh`, `deploy.sh` and `teardown.sh` are D1's, run unchanged except
for the teardown fix below.

**D3's only cloud objects are two agent registrations inside D1's project, and they go
with D1's resource group.** `evidence/teardown.md` records the teardown and the
`az resource list` verification.

## What this found in D1's scripts, and fixed

Reusing D1's `infra/` was not a no-op. Two defects, both reproduced, and the first one
blocked requirement 4 outright:

1. **`teardown.sh` left the Foundry account soft-deleted.** Deleting the resource group
   frees the resources but the Cognitive Services account lands in a soft-deleted list
   under a **still-reserved name** for its retention window. The next
   `provision.sh` fails preflight:

   ```
   FlagMustBeSetForRestore: An existing resource with ID '…/aif-<token>' has been
   soft-deleted. To restore the resource, you must specify 'restore' to be 'true'…
   If you don't want to restore existing resource, please purge it first.
   ```

   D1's teardown verified the *group* was gone and called that clean; the account record
   outlived it. `teardown.sh` now purges the deleted account, waits for the purge, and
   reports "no soft-deleted cognitive account, name is free for re-provisioning" — so
   "nothing left behind" covers the name, not just the resources. `PURGE=0` skips it for
   an operator restoring instead.

2. **`deploy.sh` re-registering with an unchanged image is not the only way to get a new
   version.** Not a defect in D1 — noted because it matters here: every `deploy-agents.sh`
   run creates a new version, which is the documented behaviour and is what the memo-key
   demonstration uses.

One thing the fix does **not** solve, recorded rather than papered over: re-provisioning
under the *same* `NAME_PREFIX` after a purge recreated the account and project but the
project's data plane returned `Project not found` for as long as it was probed (~15
minutes, `GET /agents`, `POST /agents/{name}/versions`, and a fresh project name on the
same account), while a deployment under a **new** prefix worked immediately. So reuse
across teardown cycles currently means *re-running provision with a fresh `NAME_PREFIX`*,
and D3's run used `NAME_PREFIX=bandd-d3`. That is a platform behaviour D1 could not have
seen — it never re-provisioned — and it is worth knowing before anyone tries to make
these scripts re-creatable under one name.

## A live trap in the layer, avoided on purpose

`TaskRuntime.MemoAsync` takes the store's commit gate, and so does `CommitAsync`. Calling
`MemoAsync` **inside** a `CommitAsync` callback waits for a semaphore the caller already
holds and hangs forever — no exception, no timeout. Every memo read and write here sits
between commits, never within one.

The order of the two writes is also load-bearing: after an agent returns, the **memo is
written first**, then the checkpoint that moves the phase on. A process that dies in the
gap re-enters the same phase, finds the memo, and does not call the agent. Band B commits
the checkpoint first; for a step whose result the checkpoint *carries* that is right, but
it makes "the phase has not moved" and "the memo is written" independent, and a resumed
phase then has nothing to consult. The window this does not close is the one before the
memo: an agent call that returns and then loses its process before the memo write is
re-issued, and nothing in the store can tell that from a call that never started. That is
the honest limit.

`lib/Durable.cs` needed no change for this variant.

## Running it

Prerequisites are D1's: an `az` session with Contributor + User Access Administrator.

```sh
cd real-world-scenarios/band-d3-outside-orchestrator

# Reuse D1's deployment (provision + deploy are D1's scripts).
NAME_PREFIX=bandd-d3 ../band-d-hosted-foundry-agent/infra/provision.sh
NAME_PREFIX=bandd-d3 ../band-d-hosted-foundry-agent/infra/deploy.sh
./deploy-agents.sh                      # D3's two prompt agents, into that project

./count-calls.sh                        # the platform's counter, before anything
KEEP=1 ./run.sh                         # run to the human gate; prints the PID
./gate.sh status                        # what the reviewer sees
./gate.sh pend_for_review "A. Reviewer" "note"   # a person answers
#   or: kill -9 <pid> then `KEEP=1 ./run.sh --resume`

./memo-key-demo.sh                      # requirement 3, self-contained, ~8 min
NAME_PREFIX=bandd-d3 ../band-d-hosted-foundry-agent/infra/teardown.sh   # mandatory, last
```

| File | What it is |
| --- | --- |
| `orchestrator.cs` | The run: three phases, the memo key, and the Foundry calls |
| `agent/*.yaml` | The two agent definitions. Editing one changes its identity |
| `deploy-agents.sh` | Registers them in D1's project; provisions nothing |
| `run.sh` / `gate.sh` | One process of the run; the human side of the gate |
| `count-calls.sh` | The platform's invocation counter, paginated |
| `store-report.py` | Reads a store's task, memos and result as JSON |
| `memo-key-demo.sh` | Requirement 3, with the naive-key control |

Env: `RUN_DIR`, `REVIEWER`, `GATE_TIMEOUT_SECONDS`, `MEMO_KEY_MODE`,
`RUN_REQUEST_FILE`, `STOP_AFTER_COVERAGE`, `STOP_AFTER_MEMO`, `DOTNET`.

## What a reader should not conclude

- **That the memo makes an agent call exactly-once.** It makes a *completed* call
  non-repeating across a restart. A call that returns and loses its process before the
  memo write is re-issued. Requiring exactly-once of a non-idempotent remote service is
  not something a client-side store can give.
- **That the platform's response list is a billing meter.** It is a count of accepted
  requests. It is used here because it is the service's own record and the orchestrator
  cannot write to it, not because it corresponds to money.
- **That D3 replaces the accelerator's backend.** It replaces the *fan-out and the review
  store* with a durable task. It deploys two specialist agents, not the accelerator's
  four; a `pend_for_review` here is the coverage specialist correctly refusing to confirm
  a specialty it was never given, exactly as in D1.
- **That the `naive` key is a strawman.** It is the obvious key — step name and input —
  and it is what a reader would write first. It is in the file so the failure is visible
  rather than described.
