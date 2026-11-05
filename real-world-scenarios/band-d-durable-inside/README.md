# Band D (D2) — Durable inside: a hosted agent whose working state is the library's

The same hosted deployment as D1, with one hosted agent whose **working state lives in the
Durable layer inside the container** while Foundry keeps the transcript. The band's claim is
narrow and it is demonstrated end to end: **a human approval that parks on an external event
survives a container restart and resumes at the approval, not before it.**

**What is proven, from one run that happened** (`evidence/run-20261105T170223Z/`):

| | |
| --- | --- |
| The approval is a durable task, not a loop | `status: WAITING`, `waitingOn: ["external:approval/PA-D2-0001"]`, `restarts: 0`; the turn that parked it took 6 s, and nothing was held open afterwards |
| The deadline is durable | `1793984575.504916` written before the stop, read back **byte-identical** afterwards, `requestedAt` too |
| The restart is the same session | `agent_session_id 8c508ac2…` on all four turns, **asserted in the script**; `status: idle` → `active`; `created_at` unchanged |
| It resumes **at** the approval | `resume_work` settled `pending` at task `t4`; the completed run re-entered `checkpoint.phase: "await"` and never `park`; the store's five task records all carry the same deadline |
| The decision is one commit | `record_approval` woke `t4`, which completed with the approval document answered and the work state `decided`, `decidedBy: "Dr. Chen"` |
| A fresh session has nothing | the control names **the same store path** and reports `sha256: "no-file"`, 0 documents, 0 tasks |

**What is documented only:** the 900 s idle window was not waited out (the stop is explicit),
`kill -9` mid-commit is D4's, and the count of concurrent approvals is one. §9 of
`evidence/restart-across-stop.md` lists the limits rather than leaving them implied.

**What it cost:** one gpt-5.4 deployment at 50 K TPM, one hosted agent billed per active
second, one Basic registry. The published run is four turns of **7 s, 6 s, 7 s and 8 s** of
session compute (each Response's `created_at`→`completed_at`), with a 5 s stop gap between
the second and third. The 24 h approval deadline was never waited out — the answer arrived
before it — which is the point of a durable deadline rather than a sleep.

---

## What is here

| Path | What it is |
| --- | --- |
| `agent/main.py` | The hosted agent: five tools over the working state, served by `ResponsesHostServer`. The transcript is not mirrored. |
| `agent/durable_state.py` | The two documents, the approval task, the workspace resolution and the container facts. The layer is imported, never copied. |
| `agent/d2_tool.py` | The same operations from a shell: `assess`, `resume`, `approval`, `state`, `probe`. What `durable/local-restart.sh` drives. |
| `agent/Dockerfile`, `agent/requirements.txt` | The image. `lib/py` is installed into it, so the container runs the repository's layer at the commit under test. |
| `infra/main.bicep` | D1's infrastructure, unchanged in shape: resource group, registry, Log Analytics + App Insights, Foundry account + project + both capability hosts + one gpt-5.4. |
| `infra/provision.sh` | `az deployment sub create`, subscription recorded in `.deployment.json`. Takes `RUN_STAMP` (see "The grave"). |
| `infra/deploy.sh` | Build → push → grant pull roles → register the version → wait for `active`. Assembles the build context from `agent/` **and `lib/py/`**. |
| `infra/invoke.sh` | The five-stage run, with `assert_same_session` and `assert_same_version` on every turn. `--control` runs the fresh-session control. |
| `infra/teardown.sh` | Deletes the group, waits, verifies, **and purges the Foundry account's soft-delete record**. Idempotent. |
| `BOUNDARY.md` | Which facts live in Foundry, which in Durable, who wins on a conflict, and the two rejected rules. |
| `durable/GAP.md` | The layer gap, four reproductions, the revision, and the compatibility notice. |
| `durable/PROVENANCE.md` | Layer commit, package versions, what "in step" means across the three ports. |
| `durable/local-restart.sh` | The same restart as four processes over one file — no subscription needed. |
| `evidence/` | Captured runs. Index below. |

## Running it

Prerequisites are D1's: an `az` session with **Contributor + User Access Administrator** on
the subscription, and a Python that `import durable, copilot` for the local path. `azd` is
not required.

```sh
cd real-world-scenarios/band-d-durable-inside
./infra/provision.sh                     # ~1 min
./infra/deploy.sh                        # ~2 min (ACR remote build)
./infra/invoke.sh                        # ~30 s; writes evidence/run-*/
OUT_DIR=evidence/control-session-$(date -u +%Y%m%dT%H%M%SZ) ./infra/invoke.sh --control
./infra/teardown.sh                      # mandatory, and the last thing to run
```

`./durable/local-restart.sh` needs none of it and reproduces the restart in about a second.

## The five stages, and why each guard exists

| Stage | What it does | The guard |
| --- | --- | --- |
| `ask` | Reads the state on a fresh container; records `agent_session_id` and `agent_reference.version` | `assert_same_version` from here on |
| `assess` | Commits the assessment, creates and parks the approval task | `assert_same_session`, `assert_same_version` |
| `stop` | `POST …/{session}:stop` | The session object is captured after it; the stop returns 202 and an empty body, which proves nothing on its own |
| `resume` | The same session, a prompt that names only the request id | both assertions |
| `decide` | Delivers the human answer and resumes the task | both assertions |

Every stage's tool outputs are saved verbatim (`*.tools.txt`) **because the assistant's prose
is not the evidence**. In the first, discarded run a turn's message said `"approval task:
null"` while its own `working_state` call in the same response returned `t4`. A model
summarising is a witness; the function-call output is the record.

## What was thrown away, and why it is in this README

Two complete attempts at this evidence were produced and then **deleted, not published**,
because each would have made a claim the platform would not have supported.

1. **A "resume" over a fresh session.** Stage 2 was posted without the session id, so the
   platform created a second session; its assessment went into a different store, and the
   "resumed" turn read an empty one. The captures look like a restart until you notice
   `agent_session_id` and `agent_reference.version` differ between turns. Fixed by posting
   stage 2 on the same session and by asserting the id on every turn.
2. **A run that straddled two agent versions.** A `deploy.sh` executed while the run was in
   flight, so `ask`/`assess` ran version 3 and `resume`/`decide` version 4. Both were the same
   image, but "the same agent resumed" would have been a weaker claim than it reads. Fixed by
   waiting for one deploy to settle, re-running, and asserting `agent_reference.version`.

Both are the failure mode this band exists to catch — a claimed restart that was really a
fresh session — and both were caught by a check in the script rather than by reading harder.

## The grave: a Foundry teardown is not a resource-group delete

Deleting the group leaves the Foundry account **soft-deleted** under its name, and
re-provisioning that name fails:

```
{"code": "FlagMustBeSetForRestore", "message": "An existing resource with ID
'/subscriptions/…/accounts/aif-qe7dhfdujkpiw' has been soft-deleted. …"}
```

Three things landed rather than being worked around by hand: `infra/provision.sh` takes
`RUN_STAMP` and `infra/main.bicep` mixes it into the name token, so a re-provision starts from
fresh names; `infra/teardown.sh` **purges** the account and verifies that no soft-delete record
remains; and when `.deployment.json` is gone it finds the graves by **resource group** instead
of skipping the purge. Without the last two, "teardown verified" would have been false in a way
`az resource list` cannot see — and the final run's teardown did run without the record, which
is how the gap was found. See `evidence/teardown.md` §"The soft-delete grave".

## The layer change, and the control

The scenario needed a task that parks on an external event. The layer did not express it, and
`durable/GAP.md` reproduces four separate defects. The change is a separate commit from the
scenario on purpose (its hash is recorded in `durable/PROVENANCE.md`, because editing the
scenario later moves this branch's tip):

```
lib/Durable.cs            TaskCommit.WaitingForEvent / IsExternalEvent / ExternalEventPrefix
                          DurableStore.RaiseEventAsync / ExternalEventMemoKey
                          runner: external names are not child tasks; a park settles as pending
                          both commit arms write WaitingOn; a completion is per run
lib/ts/durable.ts         the same, ported
lib/py/durable.py         the same, ported
tests/Durable.Tests/ExternalEventTests.cs   5 facts
lib/ts/durable.test.ts                     1 test
lib/py/test_sdk.py                         1 test
```

**The control that the tests are not vacuous.** With the layer change stashed and the new test
file in place, the suite does not build — the new members do not exist:

```
$ git stash && dotnet test tests/Durable.Tests --filter FullyQualifiedName~ExternalEventTests
error CS1061: 'DurableStore' does not contain a definition for 'RaiseEventAsync' …
error CS0117: 'TaskCommit' does not contain a definition for 'WaitingForEvent' …
error CS0117: 'DurableStore' does not contain a definition for 'ExternalEventMemoKey' …
```

And the two behavioural fixes have narrower controls inside the change: the `WaitingOn` fix is
pinned by `WaitingForEvent_WithNoChildTasks_IsNotFailedByTheRunner` (which fails against the
runner's old child-task path), and the completion-reuse fix by
`RaiseEvent_DeliveredTwice_KeepsTheFirstAnswerAndWakesNobodyTheSecondTime`.

Three suites on the branch: **228 .NET** (`dotnet test tests/Durable.Tests` — 223 before
this change plus the 5 new facts), **126 TypeScript** (3 added to `lib/ts/durable.test.ts`),
**74 Python**. The repository's own gate — `SKIP_SANDBOX=1 scripts/validate.sh
--deterministic` — passes end to end with the layer change in place, including the parity
suite, the TypeScript unit tests and the .NET deterministic examples:

```
Deterministic run passed.
EXIT=0
```

The TypeScript suite's two pre-existing failures (`review: conversation lifecycle retains SDK
and writer through persistence and attachment`, and a `fork` case) are unchanged by this
work: measured by stashing the change and re-running the suite on the unmodified branch.

## Cost, and teardown

| Resource | What it costs |
| --- | --- |
| gpt-5.4 GlobalStandard, 50 K TPM | per 1K tokens; capacity held at D1's 50 |
| `durable-inside-agent`, 1 CPU / 2 GiB | billed per second of active session; `idle_timeout_seconds: 900` is stated in the registration so the window is not an inferred default |
| Container registry (Basic) | cents per day plus ~1 GB of layers |
| Log Analytics + App Insights | per GB; every tool call's stdout lands here |

The runs are small next to D1's: this agent does no tool-using fan-out, so a turn is a couple
of model calls plus five cheap local tool calls. What would scale is the number of requests
per session, not the size of any one — the store is 5.9 KB after a whole workflow.

**Teardown is mandatory and is the last thing that ran.** `infra/teardown.sh` deletes the
group, waits, verifies with a subscription-wide check, **and purges the account's soft-delete
record**, then a second invocation proves the repeat is harmless. Captured in
`evidence/teardown.md`.

## Evidence index

| Path | What it is |
| --- | --- |
| `evidence/run-20261105T170223Z/` | The complete run: four requests and Responses, four session objects, the post-stop object, and the tool outputs verbatim for each stage |
| `evidence/control-session-20261105T165116Z/` | The fresh-session control: same store path, empty |
| `evidence/restart-across-stop.md` | The run read back, with the guards, the byte-identical deadline, the store's five task records, and the limits |
| `evidence/container-filesystem.md` | What the container's filesystem actually does: writable paths, `/files` absent, `$HOME` per-session, the lock sidecar, and three build-time findings |
| `evidence/local-restart/` | The same restart as four local processes, with the survival marker and both store digests |
| `evidence/teardown.md` | What existed, the delete, the repeat, the purge, the subscription-wide check, and the grave |

## What a reader should not conclude

- That Durable is needed for *session* persistence. The platform already gives 30 days, and
  the same `agent_session_id` resumed in D1 with no Durable code at all. What Durable adds
  here is the **working state and the approval's position**, which the platform never had.
- That the approval is a "human-in-the-loop feature of Foundry". It is not: `record_approval`
  is a *tool call*. The human is outside the agent, and the tool is how their decision is
  written down. The alternative — a person inside the conversation — is D1's `invoke.sh`
  decision step, which the transcript labels as `applied by invoke.sh`.
- That the store is a database. It is a JSONL file in one container, with one writer at a time.
  It is enough for one hosted agent's working state and it is explicitly not shared.
- That the model's message is the record. Where the message and the tool output differ, this
  scenario quotes the tool output and says so.