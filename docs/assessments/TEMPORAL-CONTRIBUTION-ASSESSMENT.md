> **Assessment — Lead / Architect, 2026-11-21.** Read-only. Produced by the Lead role under this repository's standing canvas policy; no source, issue, PR, commit or spend was created. Companion: [`docs/TEMPORAL_MAF_COMPARISON.md`](../TEMPORAL_MAF_COMPARISON.md), [`docs/assessments/DURABLE-TAKING-FROM-TEMPORAL.md`](DURABLE-TAKING-FROM-TEMPORAL.md).
>
> **⚠ Superseded in part by [`TEMPORAL-SPIKES.md`](TEMPORAL-SPIKES.md).** Four spikes ran against a live
> Temporal server and changed three of this document's conclusions: the version-window item is honest
> (398/398 at 1.28.0, so it is a metadata patch), the `TMPRL1101` recommendation was **falsified** (the
> hazard never reaches that code path), and approval binding is already tested upstream. The ranking in
> the spike document governs.

# Temporal contributions for .NET agent workloads — where outside work actually lands

**Basis.** `temporalio/ai-integrations` @ `7607956` (2026-11-20, `git log -1`), unshallowed clone at `/tmp/temporal-assess` (736 commits); `temporalio/sdk-dotnet` @ `0f47b6b` (2026-11-19, `/tmp/sdk-dotnet-assess`); GitHub reads via `gh api`/`gh search`. Comparison document: `docs/TEMPORAL_MAF_COMPARISON.md` — referenced, not restated.

**The headline number, before anything else.** The MAF plugin is 6 days old, has **one** authored commit, **zero** open issues, **zero** open or merged PRs beyond its creation, and **72 total NuGet downloads** (`curl -s "https://azuresearch-usnc.nuget.org/query?q=packageid:Temporalio.Extensions.MicrosoftAgentFramework&prerelease=true"`). Any investment thesis has to survive that. Mine does, but not for the reason a feature list suggests.

---

## 1. Contribution surface map

`AGENTS.md:176-250` is the .NET contract; `AGENTS.md:78-87` is the plugin contract every model/tool plugin obeys. Review is `* @temporalio/ai-sdk` (`.github/CODEOWNERS:2`), so "who owns it" is the same team for every row — the useful split is *who has demonstrated they will merge it*, not nominal ownership.

| # | Level | Seam (file / subsystem) | Owner today | Acceptance invariant it must respect | Observable evidence it is needed |
|---|---|---|---|---|---|
| I1 | Integration | `Internal/AgentLoop.cs` — the re-implemented loop | DABH authored (`#523`); campoy touches `dotnet/` | Must stay behaviourally equal to `FunctionInvokingChatClient` (`AGENTS.md:71`); every await on the Workflow scheduler | `README.md:543-547` states the loop exists because `AIAgent.RunAsync` uses `ConfigureAwait(false)`; `Internal/AgentLoop.cs:71-76` is the outside-Workflow guard |
| I2 | Integration | `Internal/ChatOptionsTransport.cs` — option/serialization bridge | same | `AGENTS.md:82` (every value must survive the real converter); `AGENTS.md:86` (supported, refused, or documented) | Three hard refusals at `ChatOptionsTransport.cs:48`, `:53`, `:60`; tool-type allowlist at `:93-100` |
| I3 | Integration | `Activities/ModelActivities.cs` — the model Activity | same | Heartbeat on a timer (`AGENTS.md:83`); no result data already in the input (`AGENTS.md:85`) | Heartbeat pump `ModelActivities.cs:143-157`; name const `:19` |
| I4 | Integration | `Internal/AgentFrameworkApiGuard.cs` — version-window guard | same | Must fail at construction, not mid-run | `AgentFrameworkApiGuard.cs:18` pins tested `1.26.0`; `:23-31` lists six constructors |
| I5 | Integration | `Mcp/*` + `Internal/McpConnectionCache.cs` | same | Sessions stateless, one connection per plugin instance (`README.md:272`) | `McpConnectionCache.cs:225-260` sweep timer; idle default `MicrosoftAgentFrameworkPluginOptions.cs:53` |
| I6 | Integration | `Testing/*` — `FakeChatClient`, `ScriptedResponse`, `InMemoryMcpServer` | same | Tests must not need provider credentials (`dotnet/README.md:39-41`) | `README.md:565-583`; `ScriptedResponse` surface `ScriptedResponse.cs:30-88` |
| I7 | Integration | `tests/.../Histories/0.x/*.json` + `ReplayTests.cs` | same | Replay promised per major only (`AGENTS.md:76`); histories are append-only | `ReplayTests.cs:12` `ReplayMajor = 0`; 11 files, 4–25 KB each |
| I8 | Integration | Packaging / release (`.csproj`, `plugin.toml`) | same | No hand-edited version (`AGENTS.md:15`); tags are the source (`AGENTS.md:65`) | `csproj:6` `0.0.0`; NuGet has `0.0.1`; no `PackageValidationBaselineVersion` although `AGENTS.md:503` requires one after first publish |
| I9 | Integration | `dotnet/_template/`, `.editorconfig`, `Directory.Build.props` | shared, campoy-adjacent | Byte-identical to canonical (`check_conventions.py:573-575`) | `CONTRIBUTING.md:35-40`: "There is no scaffolder yet" for .NET |
| C1 | Core | `WorkflowInstance.cs` scheduler (`TaskScheduler` subclass) | sdk-dotnet team (cretz, jmaeagle99) | Workflow code must stay on `TaskScheduler.Current` | `WorkflowInstance.cs:37`, `:976-995`, `:1010-1032`; `src/Temporalio/README.md:667-673` |
| C2 | Core | Determinism diagnostics | same | A diagnostic, not a behaviour change | `src/Temporalio/README.md:766-799` (the gotcha list); `WorkflowTracingEventListener.cs:199` |
| C3 | Core | Workflow analyzer (Roslyn) | cretz opened it, unowned | Transitive call-graph colouring | **`sdk-dotnet#576`, open**, filed 2025-12-12 |
| C4 | Core | Payload size limits | jmaeagle99 shipped it | Already landed | `PayloadLimitsOptions.cs:11-33`; `TemporalWorkerOptions.cs:396-403`; shipped by PR 787 (2026-07-23, +363/−11, 9 files) |
| C5 | Core | Workflow Streams | sdk-dotnet team | Experimental wire format | `Temporalio.Extensions.WorkflowStreams/README.md:1-20`; shipped in `1.20.0` release notes |
| C6 | Core | Local Activities | exists | — | `Workflow.cs:859-886`; **plugin never uses them** (`grep -rn "LocalActivity" src/` → no hits) |
| C7 | Core | Patch/versioning ergonomics | cretz | Must not change recorded histories | `sdk-dotnet#398` (memoized `Patched`), `#386` (WaitCondition semantics), `#387` (workflow local), all open |

**Distribution check, because it decides where effort is worth spending:**

```
gh api 'repos/temporalio/ai-integrations/issues?state=open&per_page=100'   → 91 open
  python 25 · typescript 24 · java 8 · go 1 · dotnet 0
gh api 'repos/temporalio/ai-integrations/pulls?state=closed&per_page=100'  → 100 recent closed
  python 56 · typescript 16 · java 7 · dotnet 0
```

Two commits touch `dotnet/` in the whole history: `#523` (DABH, 2026-11-15) and `#565` (campoy, 2026-11-16). `gh search prs --repo temporalio/ai-integrations "dotnet"` returns exactly those two.

---

## 2. Dotnet compatibility gaps — ranked

"How often" is genuinely unknown for most rows: there are no user issues to count. I mark it rather than invent it.

| Rank | Gap | What breaks today, for whom | Cost | Reviewer | Upstream-first? |
|---|---|---|---|---|---|
| **1** | **`Microsoft.Agents.AI` 1.27/1.28 drift against the `MAAI001` guard** | Anyone who installs the plugin and lets NuGet resolve `[1.26.0,2.0.0)`. The guard only checks **constructor existence** (`AgentFrameworkApiGuard.cs:43-59`); a 1.27 change in *behaviour* of those context types passes the guard and corrupts the loop. NuGet already serves `1.28.0` (`curl …/microsoft.agents.ai/index.json`); the plugin's lock is `1.26.0`. Frequency: **certain for every new consumer**, silent when it happens | **M**: bump the floor, add nightly-against-latest assertions, extend the guard from "exists" to "shape and semantics" where testable. Touches `AgentFrameworkApiGuard.cs`, `packages.lock.json`, `FailureTests.cs`, `README.md:7`/`:556` | DABH or campoy | **Plugin**, with an upstream issue when a break is found |
| **2** | **Middleware unusable in a Workflow** (`AIAgentBuilder`, `DelegatingAIAgent`, `AsAIFunction()`, `OpenTelemetryAgent`) | Any team with an existing `AIAgent` pipeline. `README.md:547` says "not supported"; `TemporalAgent.cs:361-363` throws. Workaround is "rewrite your pipeline as a chat-client decorator" — which is real work for anyone with cross-cutting middleware. Frequency: **highest-value gap for adoption**, unmeasurable today | **L**: supporting `DelegatingAIAgent` means invoking it without `ConfigureAwait(false)` — i.e. a new extension point in MAF, not the plugin. A plugin-only slice is **S**: an `AIFunction`-based sub-agent helper (documented manual recipe today) | campoy (design), DABH | **Upstream-first**, or plugin-only for the `AsAIFunction` slice |
| **3** | **Background responses / `ContinuationToken` / `ChatClientAgentRunOptions.ChatClientFactory` refused** | Long-running reasoning models (o-series, `AllowBackgroundResponses`) cannot be used at all. Refusals at `AgentLoop.cs:377-392`, `ChatOptionsTransport.cs:48-63`. For whom: anyone using the Responses API with background mode. Frequency: unknown, plausibly the *reason* a team stops evaluating | **L**: a background response needs the Activity to return a token and the Workflow to poll — a second Activity shape and a new history contract. **M** for a documented "use a non-background model" limit plus a clear `README` matrix | campoy | **Plugin** (the SDK gives you retries; the plugin must not park an Activity) |
| **4** | **`ConversationId` rejected** | Anyone porting `ChatClientAgent` code that sets `ConversationId`. `ChatOptionsTransport.cs:53-57` refuses it because the Workflow holds history. This is a **correct refusal** (`AGENTS.md:71`: match the framework, but Temporal requires determinism) | **S**: docs + a migration note ("delete `ConversationId`; carry the session") — the refusal already exists and is tested (`FailureTests.cs:331`) | any code owner | **Plugin, docs-only** |
| **5** | **Custom `AIContent` subclass fails serialization** | Anyone with a provider that returns proprietary content (`README.md:559`). The `$type` discriminator only knows MEAI's declared types | **M**: a documented `AIContent` converter hook, or a `JsonDerivedType` registration point on `TemporalAgentOptions`. Touches `ChatOptionsTransport.cs`, `AgentLoop.cs:331-339`, `SerializationTests.cs` | campoy | **Plugin** |
| **6** | **DI / `IHostedService` story absent** | Any ASP.NET / Worker-Service host. `grep -rn "Hosting\|IServiceCollection" dotnet/microsoft-agent-framework/` → no hits in `src/`; the SDK has `Temporalio.Extensions.Hosting` and `TemporalWorkerServiceOptions` (`Hosting/TemporalWorkerServiceOptions.cs:10`) but no plugin sample uses it. Onboarding cost is real; adoption blocker for enterprise | **S**: one `dotnet new`-shaped sample + a `README` section, plus an `AddMicrosoftAgentFramework()` sugar that wraps the factory dictionary. No new Activity | DABH | **Plugin** |
| **7** | **2 MiB payload ceiling / transcript growth** | Every multi-turn conversation. `README.md:557` names it; `AGENTS.md:85` forbids echoing input in results, and the plugin obeys (the model Activity returns only the response). The unbounded part is the **input**: whole transcript per call | **M**: the SDK already ships the fix — `DataConverter.ExternalStorage` (`Converters/ExternalStorage.cs:11-45`, threshold 256 KiB, Public Preview in 1.22.0). The plugin needs a documented recipe and a test proving it works through `ModelCallInput` | jmaeagle99 (SDK side), DABH | **Plugin doc + test**, leveraging C4 |
| **8** | **Non-OpenAI provider ergonomics** | Azure OpenAI is documented (`README.md:121-123`); Anthropic/Gemini/Ollama via `Microsoft.Extensions.AI` are not, and `ModelErrorClassifier` reads status by reflection on `Status`/`StatusCode`/`HttpStatusCode` (`ModelErrorClassifier.cs:23`, `:92-110`) — providers without one "propagate unchanged and retry" (`README.md:145`), i.e. a permanent 400 retries forever | **S–M**: three more factory snippets in the README + a test per status-extraction shape. Touches `README.md`, `FailureTests.cs:127` | campoy | **Plugin** |
| **9** | **Multi-targeting beyond `net8.0`** | Nobody, and it should stay that way. `AGENTS.md:237-244` explains precisely why: MAF's `netstandard2.0` assemblies polyfill `IsExternalInit` privately, so `init` accessors have a different signature and code compiled against them throws `MissingMethodException` on .NET 8+. `csproj:3` is `net8.0` deliberately | **n/a — do not do** | — | — |
| **10** | **Packaging / prerelease ergonomics** | Minor friction: `--prerelease` needed (`README.md:13`), no `PackageValidationBaselineVersion` yet (`AGENTS.md:503`). Real but low-value | **S**: add the baseline property once `0.0.1` is stable | any code owner | **Plugin** |
| **11** | **Testing fakes' coverage** | `FakeChatClient` (`Testing/FakeChatClient.cs:12-145`) and `ScriptedResponse` (`:30-88`) cover text, tool calls, JSON, failure, delays. Not covered: multi-modal responses, `TextReasoningContent` streams, provider `AdditionalProperties` echo. 128 `[Fact]`/`[Theory]` across 76 test files already | **S**: 2–3 scripted-response factories | any code owner | **Plugin** |

**Ordering rationale.** #1 is first not because it is the most exciting but because it is the only gap that is *certain* to bite and *cheap* to close. #2 and #3 are the adoption ceiling. #4–#6 are the "someone evaluates this for a day and hits a wall" set. #7 is already solved in the SDK and only needs the plugin to say so.

---

## 3. Core-level gaps — ranked

The test for "genuinely core": the fix cannot live in a plugin without forking the engine's guarantees.

| Rank | Gap | Why it is core, not convenience | Who it unblocks | Smallest acceptable slice | Risk of proposing |
|---|---|---|---|---|---|
| **1** | **A deterministic-async contract for library code** — `ConfigureAwait(false)` is currently a *documented* hazard, enforced only for the plugin's own `src/` by a regex (`scripts/ci/check_conventions.py:563-567`) and a disabled analyzer (`dotnet/_shared/analyzers.globalconfig:20-24`) | The failure mode is not an exception, it is a **silent stall**: the Workflow Task completes with no result and the run hangs until terminated (`README.md:545`). The SDK's runtime check catches *thread transfer* (`WorkflowTracingEventListener.cs:199`) and the worker fails the task after a 2-second deadlock timeout (`WorkflowWorker.cs:25`, `:355-392`) — but 2 s is a heuristic, and a legitimately slow activation trips it. There is **no** API for "run this library's async loop on my scheduler" | Every framework integration, not just MAF: any library whose internals use `ConfigureAwait(false)` | One public primitive — e.g. `Workflow.RunOnSchedulerAsync(Func<Task<T>>)` plus a `TaskScheduler.Current`-pinning helper — **and** a first-class diagnostic that names the offending call site rather than `TMPRL1101 deadlocked after 00:00:02`. No wire change, no history change | **High.** cretz has deliberately kept the scheduler surface small; a "just wrap it" API invites people to run non-deterministic code. Must be scoped to *re-scheduling a continuation*, not *executing arbitrary work* |
| **2** | **Roslyn workflow analyzer** — `sdk-dotnet#576`, open since 2025-12-12, `enhancement` | The SDK's own README says "Due to .NET's lack of a sandbox, there is not a good way to prevent non-deterministic calls" (`src/Temporalio/README.md:760-764`). MAF is the first integration where an *external library's* code is the non-deterministic part, which is exactly the transitive case cretz describes | Everyone; MAF specifically, because it would flag `AIAgent.RunAsync` calls statically instead of at runtime | The narrow first rule: flag `.ConfigureAwait(false)` and `Task.Run` reachable from a `[WorkflowRun]` method. cretz's issue already sketches the call-graph colouring | **Low politically** (he asked for it), **high technically** (transitive colouring, reflection blind spots). Someone must own maintenance |
| **3** | **Patch/versioning ergonomics for third-party loops** — `sdk-dotnet#398` (memoized `Patched`), `#386` (`WaitConditionAsync` semantics), `#387` (workflow local) | The plugin has **zero** `Workflow.Patched` calls (`grep -rn "Workflow.Patched" src/` → none) and `AGENTS.md:75` says a behaviour change that can alter histories "must be gated with a patch". So the very first behavioural fix to `AgentLoop` forces the author to invent replay archaeology. That is a core ergonomics problem surfacing in a plugin | Any plugin with a recorded-history contract: all 20 of them | #386 alone (`WaitConditionAsync` waking one condition at a time) makes the loop's approval handshake expressible without the `awaitingReply` flag dance at `README.md:355-375` | **Medium.** #386 needs an SDK flag and changes wake-up behaviour; getting it wrong breaks every existing Workflow |
| **4** | **Local Activities as the cheap deterministic tool-call primitive** | Exists (`Workflow.cs:859-886`) but the plugin ignores it: every `ActivityAsTool` call is a full Activity (`Internal/ActivityFunction.cs:85-94`). For a 40-iteration agent loop with 2 tools per round, that is ~80 Activity events plus task-queue round trips that a Local Activity would collapse | Agent workloads specifically — high call count, low latency tolerance, deterministic inputs | A plugin-side `ActivityAsTool.CreateLocal(...)` overload. **No SDK change needed.** This is the cheapest large win in the whole list | **Low.** The risk is semantic: Local Activities do not get the same retry/heartbeat guarantees, and `AGENTS.md:83` requires heartbeat when a timeout is set. Must be documented as "cheap and local, not durable-across-worker-loss" |
| **5** | **Telemetry conventions for model/tool spans** | Provider spans come from `UseOpenTelemetry()` in the worker factory (`README.md:563`), i.e. inside the Activity, with real timings — correct. But there is **no** convention for what an agent span should look like, and the Python sibling had to build `ReplaySafeMeterProvider` because MAF's telemetry re-emits on replay (`python/microsoft_agent_framework/README.md:451`). .NET has no equivalent problem *yet* because it runs the loop itself | Anyone operating agents at scale; anyone comparing Temporal's UI to their APM | A documented span-attribute set (model, tokens, tool name, call id) in `Temporalio.Extensions.OpenTelemetry`, plus a plugin test that the model Activity span carries them | **Low**, but it is convention-setting, which is slower to agree than to implement |
| **6** | **Payload size accounting** — mostly **already done**, which is why it ranks low | `PayloadLimitsOptions` (warn at 512 KiB, `PayloadLimitsOptions.cs:15-26`), `DisablePayloadErrorLimit` (`TemporalWorkerOptions.cs:396-403`), and core-side error-limit enforcement (PR 787). `sdk-dotnet#638` is **closed/completed**. Remaining gap: the plugin never sets or documents these, and `temporalio/features#723` is still open | MAF users with long transcripts | A plugin README recipe + one test that an oversized `ModelCallInput` fails with `[TMPRL1103]` rather than a mystery | **Low.** Do not re-propose enforcement; it shipped |
| **7** | **Cross-SDK precedent the .NET SDK lacks** | Python's MAF plugin rebinds `uuid4`/`perf_counter`/`time`/`asyncio.to_thread` at the module level because "Agent Framework has no time, id or thread provider hook" (`python/.../_workflow_runtime.py:1-32`, `:129-136`). .NET cannot do this — no module-level rebinding — which is *why* the .NET plugin must own the loop. The .NET SDK's answer to "a library reads the clock" is still "don't" | .NET integrations generally | A `Workflow.UtcNow`-backed injectable clock contract, or nothing | **Medium.** A clock abstraction in a framework that does not have one is a design change, not a patch |

**The honest ordering caveat.** #1 is the most valuable and the least likely to land as I would write it. #4 is the least glamorous and the most likely to land this quarter.

---

## 4. Ranked contribution plan

Ordered by (evidence × merge likelihood × reviewer cost). "First PR" is what I would actually open.

1. **Re-validate the `Microsoft.Agents.AI` window against 1.28.0**
   - *Level:* integration · *Impact:* every consumer, immediately (the range admits 1.27/1.28 today) · *Effort:* **M** · *Review risk:* **low** — a dependency bump plus tests is the most-merged PR shape in this repo
   - *Prerequisite:* none
   - *First PR:* bump `csproj:23` to `[1.28.0,2.0.0)`; add the missing constructor shapes to `AgentFrameworkApiGuard.cs:23-31`; extend `PluginTests.cs:168` so the guard test asserts *behaviour*, not only presence; update `README.md:7` and `:556`. Title: `[dotnet/microsoft-agent-framework] require Microsoft.Agents.AI 1.28`

2. **`ActivityAsTool` over Local Activities**
   - *Level:* integration · *Impact:* the dominant cost in a real agent loop — 40 iterations × N tools of Activity round trips · *Effort:* **M** · *Review risk:* **medium** — touches the durability contract, so it needs a `README` limit and a `SideEffectTests`-style event-count assertion
   - *Prerequisite:* none (SDK already has `ExecuteLocalActivityAsync`, `Workflow.cs:859`)
   - *First PR:* `ActivityAsTool.CreateLocal(...)` in `ActivityAsTool.cs`; a `LocalActivityOptions` path in `Internal/ActivityFunction.cs:85-94`; `AgentRunTests` case asserting `LocalActivity` event counts; `README.md:160-216` section. Title: `[dotnet/microsoft-agent-framework] run Activity tools as local activities`

3. **Document the payload ceiling with the SDK's own escape hatch**
   - *Level:* integration · *Impact:* anyone past ~10 turns; converts a documented dead end (`README.md:557`) into a recipe · *Effort:* **S** · *Review risk:* **low** — docs + one test
   - *Prerequisite:* none — `ExternalStorage` shipped in 1.22.0
   - *First PR:* a "Long conversations" README section wiring `DataConverter.ExternalStorage` through the worker; a `SerializationTests`-style test that a 1 MiB transcript round-trips via a fake driver; update `README.md:557`. Title: `[dotnet/microsoft-agent-framework] document external storage for long transcripts`

4. **`ConfigureAwait(false)` call-site diagnostics in the SDK**
   - *Level:* core · *Impact:* every framework integration; turns a 2-second hang into a named line · *Effort:* **M** · *Review risk:* **medium-high** — you are proposing scheduler-adjacent surface to the SDK's primary author
   - *Prerequisite:* a `sdk-dotnet` issue first (see §6) — do not open the PR cold
   - *First PR:* in `sdk-dotnet`, enrich `TMPRL1101` (`WorkflowWorker.cs:390-391`) with the last-executed task's stack when the deadlock timeout fires; no new public API. Title: `feat: name the stalled task when a workflow activation times out`

5. **DI / hosting sample + `AddMicrosoftAgentFramework()`**
   - *Level:* integration · *Impact:* enterprise onboarding; the single biggest "why can't I just…" question · *Effort:* **S** · *Review risk:* **low** — additive, no Activity changes
   - *Prerequisite:* none
   - *First PR:* a `README` "Hosting" section using `Temporalio.Extensions.Hosting`, plus a small `MicrosoftAgentFrameworkPluginOptions` factory overload taking `IServiceProvider`. Title: `[dotnet/microsoft-agent-framework] show the generic-host wiring`

6. **Non-OpenAI provider matrix**
   - *Level:* integration · *Impact:* removes the "is this OpenAI-only?" objection; `ModelErrorClassifier` already handles any status-carrying exception · *Effort:* **S** · *Review risk:* **low**
   - *Prerequisite:* none
   - *First PR:* README snippets for Azure OpenAI (exists), Anthropic via `Microsoft.Extensions.AI`, Ollama; one `FailureTests` theory row per extraction shape. Title: `[dotnet/microsoft-agent-framework] document three more providers`

7. **A `dotnet` scaffolder, matching `scripts/new_python_plugin.py`**
   - *Level:* integration (repo tooling) · *Impact:* makes the next .NET plugin (LangChain.NET? Semantic Kernel? the durable-extension adapter?) cheap; `CONTRIBUTING.md:35-40` currently says "there is no scaffolder yet" · *Effort:* **M** · *Review risk:* **medium** — repo tooling needs `scripts/tests` coverage and `check_conventions.py` alignment
   - *Prerequisite:* #5 (so the template has a working hosting sample)
   - *First PR:* `scripts/new_dotnet_plugin.py` + a fixture-driven test. Title: `[repo] add a .NET plugin scaffolder`

8. **Roslyn workflow analyzer — narrow first rule**
   - *Level:* core · *Impact:* catches the MAF hazard class statically · *Effort:* **L** · *Review risk:* **medium** — the author asked for it, so appetite exists; maintenance is the question
   - *Prerequisite:* a design comment on `sdk-dotnet#576` before any code
   - *First PR:* nothing in the plugin; a prototype analyzer behind a NuGet preview. Title: `feat: flag ConfigureAwait(false) and Task.Run reachable from workflow code`

**What I would personally not open:** #8, today. It is the right long-term answer to a problem that currently has a working (if ugly) answer, and a half-finished analyzer that misses reflection is worse than the runtime check because it buys false confidence.

---

## 5. What NOT to propose

| Proposal | Why it is rejected |
|---|---|
| **Per-plugin CI workflow / job / secret** | `AGENTS.md:12` and `.github/workflows/ci.yml:1-13`: one entry workflow, one reusable per language, plugin as a parameter. `dotnet` is already wired at `ci.yml:136-146` |
| **Hand-editing the version to `0.0.2`** | `AGENTS.md:15`, `:65`: `0.0.0` is permanent in-repo; protected release tags are the published source; `.NET releases pass the tag version as -p:Version` |
| **A changelog file** | `AGENTS.md:66`: notes are derived from commit subjects by `scripts/release/release_tool.py` |
| **A `Directory.Build.props` / `global.json` / `nuget.config` at `dotnet/`** | `AGENTS.md:9` and `check_conventions.py:559-562`: MSBuild imports the nearest file above a project, so a language-root file would silently apply to every plugin |
| **Forking the agent loop into a scenario or sample** | `Internal/AgentLoop.cs` exists *because* the framework's loop is unusable in a Workflow. A second copy re-introduces the drift `AGENTS.md:71-73` forbids ("Run the framework's own code and hook in through its extension points. Don't copy its objects") |
| **Bypassing the plugin/Activity seam** — e.g. calling the provider from Workflow code, or caching a client in Workflow state | The seam *is* the durability story. `MicrosoftAgentFrameworkPluginOptions.cs:7-9`: credentials live in worker factories; Workflow passes names |
| **Duplicating Temporal's durability** — a plugin-side checkpoint store, a plugin-side retry ledger, a plugin-side timer | Event History is the only state store (`AGENTS.md:73`). Adding a second store breaks replay, and `README.md:479` states the transcript lives in Workflow state and nowhere else |
| **Re-proposing payload-size enforcement in the SDK** | It shipped: `PayloadLimitsOptions.cs`, `TemporalWorkerOptions.cs:396-403`, PR 787, `#638` closed-completed 2026-07-23. Asking again reads as not having read the changelog |
| **Making `ConversationId` "work" by setting it on the Activity** | `ChatOptionsTransport.cs:53-57` refuses it because the Workflow holds history; enabling it makes the model see every turn twice. This is the plugin being *correct* |
| **Multi-targeting `netstandard2.0`** | `AGENTS.md:237-244` documents the `IsExternalInit` polyfill collision and the resulting `MissingMethodException` on .NET 8+. `net8.0` is the correct single target |
| **Adding `[tool.uv.sources]`-style cross-plugin references / `ProjectReference` in the packed library** | `AGENTS.md:5` (registry coordinates only) and `AGENTS.md:184-188` (a `ProjectReference` becomes a nuspec dependency on a coordinate not on nuget.org) |
| **Raising `Microsoft.Agents.AI` to `2.0.0`** | The `< 2.0.0` ceiling is deliberate: `AgentFrameworkApiGuard.cs:20-21` and `README.md:556`. A major bump needs its own migration plan, not a version-range edit |

---

## 6. Honest unknowns and verification debt

| Unknown | Why it matters | Command that would resolve it | What would change if it differs |
|---|---|---|---|
| **Is `microsoft/agent-framework` public?** `gh api repos/microsoft/agent-framework` → **404**; the NuGet catalog for 1.28.0 lists `repository.url = https://github.com/microsoft/agent-framework` and `projectUrl = https://learn.microsoft.com/agent-framework/`. `gh search repos "agent-framework" --owner microsoft` returns `Agent-Framework-Samples`, `agent-framework-go`, and **`agent-framework-durable-extension`** (public, 54 open issues, 25 stars) but not the core repo | Determines whether gap #2 and #3 in §2 are *upstream-first* or *plugin-only*. If the core repo is private, "fix it upstream" is not an option an outside contributor can take, which promotes plugin-side workarounds | `gh api repos/microsoft/agent-framework --jq .private` with an account that has access; or read `docs/` in `microsoft/agent-framework-durable-extension` for the extension model | If **public**: #2 (middleware) and #3 (background responses) become upstream-first and the plugin work shrinks to documentation. If **private**: the plugin must either absorb the workarounds or the features stay unavailable |
| **Does the plugin actually work against 1.27/1.28?** The lock is 1.26.0; nightly runs `sync-latest` (`AGENTS.md:270-271`) but there is **no** dotnet nightly issue open, which is weak evidence of green | Decides whether §2 #1 is urgent or already covered | `cd dotnet/microsoft-agent-framework && make sync-latest && make test` in the CI checkout (the lane that exists, `AGENTS.md:277`) | If 1.28 is green, #1 drops from rank 1 to rank 4 and the plan reorders around #2 |
| **Is the 2-second deadlock timeout tripping for legitimate MAF activations?** `WorkflowWorker.cs:25` sets 2 s; `README.md:595` tells you to run the suite on both runtimes | A false-positive deadlock would be the single most damaging bug class for an agent loop | `gh search issues --repo temporalio/sdk-dotnet "TMPRL1101"`; then run the plugin suite with `TEMPORAL_DEBUG` and count `TMPRL1101` occurrences in worker logs | If it trips, §3 #1 jumps to the top and becomes an SDK bug, not a feature request |
| **Real adoption.** NuGet total downloads = **72**; the Python sibling is **239/month** on PyPI against `temporalio-openai-agents` at **165,063/month** (`curl -s https://pypistats.org/api/packages/<pkg>/recent`) | Decides whether feature investment or adoption work (samples, docs, a blog post) has the higher return | `curl -s "https://azuresearch-usnc.nuget.org/query?q=packageid:Temporalio.Extensions.MicrosoftAgentFramework&prerelease=true" \| tr ',' '\n' \| grep totalDownloads` — re-run monthly | At 72 downloads, **no** feature in §2 outranks a working end-to-end sample. My ranking assumes the goal is readiness for adoption, not response to demand |
| **Does `DataConverter.ExternalStorage` compose with a plugin that installs no converter?** `README.md:563` says the plugin installs no data converter, so a user-set one should win | Determines whether §4 #3 is a doc PR or needs a plugin change | A test: set `ExternalStorage` on the client's `DataConverter` and run a >1 MiB transcript through `ToolsWorkflow` | If it does not compose, #3 becomes an **M** with a plugin change, and `AGENTS.md:82` applies |
| **Whether issue 425 (per-plugin CODEOWNERS) would include `dotnet/`** — its table lists 15 plugins and omits `dotnet/microsoft-agent-framework` entirely | If the team adds per-plugin owners, the .NET plugin may end up with **no named owner** by omission, which is worse than the current `* @temporalio/ai-sdk` | `gh api repos/temporalio/ai-integrations/issues/425 --jq .body` (already read: table covers Python/TypeScript/Go/Java only) | Raises "who reviews this?" from "the team" to "whoever volunteers", and makes contributions #1–#3 harder to land |
| **How much of the Python MAF plugin's determinism work is transferable** — Python rebinds module-level `uuid4`/`perf_counter` (`_workflow_runtime.py:129-136`); .NET has no equivalent hook | The Python plugin solved a problem the .NET plugin sidesteps by owning the loop. If MAF adds a time/id provider hook, both simplify | `gh api repos/microsoft/agent-framework/contents` (blocked on the 404 above) | If a hook exists, §3 #7 becomes a concrete plugin change instead of a wish |

**Not measured, with the command:** review latency for `dotnet/` PRs (only one data point: `#523` opened 2026-11-13, merged 2026-11-15, 86 review comments); CI wall-clock for the .NET matrix (`plugin.toml:12` → `8.0`, `10.0`; run `make test` and time it); whether the `MAAI001` constructors still exist unchanged in 1.28 (requires restoring 1.28 and running `PluginTests.AgentFrameworkApiGuard_PassesForTheInstalledVersion_AndNamesTheTestedOneOtherwise`).

---

## 7. What I would tell the user, in one paragraph

The plugin is not short of *features*; it is short of *users*, and the two gaps that would change that are the ones an outside contributor can land fastest: make the `Microsoft.Agents.AI` window honest against 1.28, and stop paying a full Activity round trip per tool call. The single most valuable core contribution is not a feature at all — it is making `ConfigureAwait(false)` fail with a stack instead of a 2-second `TMPRL1101`, because that hazard is the reason this plugin had to re-implement an agent loop and the reason every future .NET integration will have to as well. Everything else in this list is real work that a maintainer would merge and that no user has asked for yet.

---

**Canvas handoff:** Core Engineers canvas — architecture/dependency section **UNCHANGED**; source identity `temporalio/ai-integrations@7607956` + `temporalio/sdk-dotnet@0f47b6b` (read-only `/tmp` clones), plus `docs/TEMPORAL_MAF_COMPARISON.md`; no repo file, cached metric, scope or queue state was modified by this assessment, so no refresh is required. If the canvas carries a "plugin adoption / maturity" figure, it should stay as-is (MAF is `pre-release`, `0.0.1`, 72 NuGet downloads) — owner PO for product framing, Lead for architecture; next action is the `microsoft/agent-framework` visibility check in §6, which I could not confirm (404).