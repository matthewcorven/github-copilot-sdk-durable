# Band D (D1) — the Prior-Authorization hosted agent, running, and what the platform does

> **Band D has three scenarios, and this is the baseline.** They are separate directories because they
> prove different things about the same platform:
>
> | | Directory | What it is |
> | --- | --- | --- |
> | **D1** | *this one* | The accelerator deployed as Foundry hosted agents, run for real, and torn down. |
> | **D2** | [`band-d-durable-inside/`](../band-d-durable-inside/README.md) | Durable *inside* the platform: the agent's working state is the library's, while Foundry keeps the transcript. |
> | **D3** | [`band-d3-outside-orchestrator/`](../band-d3-outside-orchestrator/README.md) | Durable *outside*: a local orchestrator that treats Foundry agents as tools. |
>
> The band's rows and its platform caveat are in
> [`docs/REAL_WORLD_MATRIX.md`](../../docs/REAL_WORLD_MATRIX.md) § *Band D*.
>
> **The finding is composition, not replacement.** Foundry owns **what was said** — the transcript, the
> session, the platform's history. Durable owns **what is true** — the working state, the decision, its
> deadline, the process. [`BOUNDARY.md`](../band-d-durable-inside/BOUNDARY.md) states the split and which
> side wins when they disagree.

The baseline side of Band D: `microsoft/Prior-Authorization-Multi-Agent-Solution-Accelerator`
(MIT) deployed as **Foundry hosted agents** on a real subscription, invoked for real, and
observed. The Durable variants (D2 inside, D3 outside) compare against this.

**What is proven:** two hosted agents (coverage, synthesis) deployed, a complete
prior-authorization run through the platform — coverage → synthesis → decision
applied → `pend_for_review` — captured in `evidence/run-20261105T125254Z/`, and four
platform questions answered in `evidence/platform-behaviour.md`.

**One step in that chain is not the platform's.** The decision is the
accelerator backend's rule (`routers/decision.py`) reproduced by `invoke.sh`: it
writes `"action": "accept"`, so accepting the recommendation is a no-op that keeps
`final = rec`. It is a *decision applied*, not a person, not a review UI, and not
a backend call — the run's `40-final-result.json` says so in
`provenance.decision`. Removing the step would produce byte-identical output.

**What is not proven:** the local Docker Compose path does **not** complete a prior-auth
run in this environment (three captured failures, `evidence/local-compose-path.md`), and
the accelerator's four-agent fan-out is not deployed — clinical and compliance are
absent from every run and every transcript says so. The honest one-line version: this
issue proves *one* decision path through the platform, not the accelerator's whole
application.

## What is here

| Path | What it is |
| --- | --- |
| `upstream/` | The reference accelerator at a pinned commit: `LICENSE`, `agents/coverage/*`, `agents/synthesis/*`. Byte-identical, hashes in `upstream/PROVENANCE.md`. |
| `agent/` | The two files this deployment changes: `requirements.txt` (a dependency-range fix, without which no MCP tool works) and `Dockerfile` (same image, build-context layout). `agent/PATCHES.md` says why. |
| `infra/main.bicep` | Provisioning: resource group, registry, Log Analytics + App Insights, Foundry account + project + both capability hosts + one `gpt-5.4` deployment. Nothing else. |
| `infra/provision.sh` | `az deployment sub create` against `infra/main.bicep`, writes `.deployment.json`. |
| `infra/deploy.sh` | Build + push both agent images, grant the roles Foundry needs, register both agent versions, wait for `active`, write `deployment.env`. |
| `infra/invoke.sh` | One four-step run (coverage → synthesis → decision applied → final result) with every input/output saved. The decision step replays the accelerator backend's rules; it is not a human round-trip. |
| `infra/teardown.sh` | Deletes the resource group on the subscription `provision.sh` recorded, **purges the soft-deleted Cognitive Services account the delete leaves behind**, and verifies three things: every group is absent, no soft-deleted account remains, and no resource **anywhere in the subscription** carries this deployment's `uniqueString`. Treats a failed `az` call as a failure rather than as "nothing left". Idempotent. |
| `evidence/` | Captured transcripts. Index below. |

## Cost, and teardown

**Teardown is mandatory.** This deployment is small but nothing in it is free while it
exists:

| Resource | What it costs |
| --- | --- |
| `gpt-5.4` GlobalStandard deployment, 50 K TPM capacity | per 1K tokens in/out; the deployment itself reserves nothing while idle, but capacity above the default quota is billed per model unit — keep `CAPACITY_K` at 50 |
| Two hosted agents, 1 CPU / 2 GiB each | **billed per second of active session**, from session start to idle timeout. The 900 s default idle window means one ~40 s run keeps compute alive for up to ~15 minutes unless the session is stopped |
| Container registry (Basic) | a few cents per day plus image storage for the two images (~1 GB with their layers) |
| Log Analytics + Application Insights | per GB ingested; agent stdout and OTel traces land here |
| Foundry account / project | no standing charge; model inference is billed per token |

What the captured run actually consumed, from the two Response objects' `usage`
(`evidence/run-20261105T125254Z/`):

| Step | input tokens | of which cached | output tokens |
| --- | --- | --- | --- |
| coverage agent | 295 172 | 257 536 | 2 401 |
| synthesis agent | 11 193 | 0 | 1 142 |

The coverage number is large because a tool-using turn re-sends the conversation on
every model call: several MCP round-trips, each carrying the skill and all tool results so far.
At `gpt-5.4` GlobalStandard list prices that is **single-digit US cents for the
coverage turn and about a cent for synthesis** — cheap per run, but the input side is
what scales, so a longer clinical note or more criteria costs noticeably more. Add the
per-second hosted compute for the session's lifetime on top, which is why an
idle-forgotten agent costs more than a busy one.

Everything is deleted by:

```sh
./infra/teardown.sh
```

which is what this issue ran last; see `evidence/teardown.md`.

**Teardown verifies three different things, and the second and third were added by D3.**

1. **Every group is absent** — the delete finished.
2. **No soft-deleted account remains.** Deleting the group frees the resources, but the
   Cognitive Services account then sits in a *soft-deleted* list under a name that is still
   reserved, so re-running `provision.sh` with the same `NAME_PREFIX` fails preflight with
   `FlagMustBeSetForRestore`. The purge is polled (60 × 10 s) across every group the run
   touched, because a purge is asynchronous and a single re-read reports a *successful*
   teardown as incomplete. `PURGE=0` skips it for an operator restoring instead.
3. **No resource anywhere in the subscription carries this deployment's `uniqueString`.**
   The first two are scoped to the group being deleted, which passes for a resource
   created in a *different* group. The token is derived from the recorded account name,
   never guessed, and the query runs with `--subscription` pinned like every other call.

D3 reproduced each failure and its fix; see `../band-d3-outside-orchestrator/README.md`
and that scenario's `evidence/teardown.md`, which includes the third check **firing**
against a deliberately cross-group resource.

**Re-provisioning under the same name does not yet work**, and that is a platform
behaviour rather than a script bug: after a purge, a re-created account and project
answered `Project not found` on its data plane for as long as it was probed (~15 min),
while a deployment under a fresh `NAME_PREFIX` worked immediately. Until that is
understood, re-creating this deployment means a fresh prefix.

## Running it

Prerequisites, all checked by the scripts: an Azure CLI session (`az login`) with
**Contributor + User Access Administrator** on the subscription (the accelerator's own
documented requirement, `docs/DeploymentGuide.md` §1.1), and `python3`. `azd` is **not**
required — the minimal path deliberately does not use `azd up`, so it does not need a
`git clean` repository, an `azure.yaml`, or the `azure.ai.agents` extension.

```sh
cd real-world-scenarios/band-d-hosted-foundry-agent
./infra/provision.sh            # ~2 min
./infra/deploy.sh               # ~3 min (ACR remote build for both images)
./infra/invoke.sh               # ~1 min; writes evidence/run-*/
...
./infra/teardown.sh             # mandatory, and the last thing you run
```

`invoke.sh` takes an optional reviewer name. `NAME_PREFIX`, `LOCATION`, `MODEL`,
`CAPACITY_K` override the defaults; `AGENTS` defaults to `coverage synthesis`.

## What "minimal" means, and what was left out on purpose

The accelerator's `infra/main.bicep` provisions a whole application: Foundry, ACR,
Log Analytics, App Insights, a Container Apps environment, a FastAPI backend, a Next.js
frontend, and the role assignments for all of them. D1 needs one thing — a hosted agent
answering a Responses call — so this deployment keeps the Foundry account and project
(with the two capability hosts the preview requires), the registry that holds the image,
and the monitoring that makes a deployed run observable.

**Not deployed, and why:**

| Not deployed | Why | Consequence |
| --- | --- | --- |
| `clinical`, `compliance` agents | Each is a separate specialist; D1 proves one decision path, and the `compliance` image in the accelerator cannot even import `agent_framework_foundry` (it omits `agent-framework-openai`) | Every synthesis transcript records `agents_absent: [compliance, clinical]` and the recommendation errs to PEND rather than claiming a verdict it did not make |
| FastAPI backend | It owns the four-agent fan-out *and* the in-memory review store; it is the thing D3 replaces with a Durable orchestrator | The decision step is applied in `invoke.sh` from the backend's own rules — an `accept` of the recommendation, hence a no-op on the outcome — and `40-final-result.json` labels it `"decision": "applied by invoke.sh…"` |
| Next.js frontend | Nothing to review without the backend | The transcript is the artefact |
| Foundry project App Insights *connection* | Portal Traces-tab rendering only | On this subscription the `connections` resource fails validation (`Required metadata property ResourceId is missing`); the platform still injects `APPLICATIONINSIGHTS_CONNECTION_STRING`, and `azd ai agent monitor` is the documented console-log path when `azd` is available |
| `session_configuration` on the agent version | Leaving it out is what makes the observed idle behaviour the platform *default* | The 900 s default applies; `evidence/platform-behaviour.md` §2 says the eviction itself was not waited out |

## What the platform actually does — the short version

Full detail, with citations and the distinction between *documented* and *observed*, in
`evidence/platform-behaviour.md`.

| Question | Answer |
| --- | --- |
| Session persistence | 30 days, sliding on activity — observed as `expires_at − created_at` of exactly 30 days on the live session, sliding with activity |
| Idle timeout | 900 s default, 120 s–14 400 s per version — documented; the eviction was not waited out |
| Foreground response, client gone mid-run | Response resolved `status: "cancelled"`, `background: false` — observed after abandoning a 40 s run at 5 s |
| Does it replay? | No for foreground. Only *stored background* responses with `resilient_background` opted in are re-entered, and re-entry is **from the handler's beginning, not deterministic replay** — documented, not exercised here |

The one sentence the matrix should carry: **the platform keeps the session and the
transcript; recovering in-flight work is the builder's job.**

## Evidence index

| Path | What it is |
| --- | --- |
| `evidence/run-20261105T125254Z/` | The complete run: request, coverage Response, coverage result, synthesis request, synthesis Response, synthesis result, the decision applied, final result. Captured before review, so its `provenance.decision` string is the shorter earlier wording; the field means the same thing. |
| `evidence/platform-behaviour.md` | The four questions, each marked documented/observed, with citations and the two observations that pin them (session TTL, cancelled foreground response, session resume vs. fresh-session control). |
| `evidence/session-resume/` | Session before stop, after stop, after resume, and the fresh-session control response. |
| `evidence/local-compose-path.md` | The local path: what was run, three verbatim failures, and where it stops. |
| `evidence/inventory.md` | What the deployment contained, captured before teardown. |
| `evidence/teardown.md` | Teardown output, including the second (harmless) invocation and the `az resource list` verification. |

## Preview gates, honestly

The deployment is in **East US 2**, on the published region list for hosted agents, and
it succeeded on the first attempt: the capability hosts were accepted, both images were
pulled by the platform, and both versions reached `active` in under a minute. There was
no enrollment error to record: the deploying identity is `Owner` on the subscription and
the preview is open to it. **`azd` was unavailable** (`brew install azure/tap/azd` fails:
the tap repository 404s), so the `azd ai agent` route was not used; the REST path that
the docs also publish was used instead, and `azd extension install azure.ai.agents`
succeeds, so that path is open for anyone who wants it.

## What a reader should not conclude

- That the accelerator's Compose mode works. It builds and starts; it does not complete
  a run against the shipped dependency pins.
- That Durable is needed for *session* persistence. The platform already gives 30 days
  of it, and the resume observation in `evidence/platform-behaviour.md` §5 happened
  without any Durable code.
- That a `pend_for_review` from the captured run is a coverage problem. It is the
  synthesis gate correctly refusing to pass a request whose clinical and compliance
  inputs are absent — with only the coverage specialist deployed, PEND is the right
  answer, and the audit trail says exactly which gate failed and why.
