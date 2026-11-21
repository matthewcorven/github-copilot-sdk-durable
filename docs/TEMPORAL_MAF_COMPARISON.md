# Three durable-agent stacks compared: Temporal × Microsoft Agent Framework, this repository, and the Copilot SDK alone

A three-way comparison of what each stack **makes durable**, **what it leaves to you**, and **what
it costs**. Every claim about another project is cited to the file it came from; claims about this
repository cite `docs/` or `lib/`.

| | Stack | Version studied |
|---|---|---|
| **Temporal MAF** | [`temporalio/ai-integrations` → `dotnet/microsoft-agent-framework`](https://github.com/temporalio/ai-integrations/tree/main/dotnet/microsoft-agent-framework), `Temporalio.Extensions.MicrosoftAgentFramework` | commit [`7607956`](https://github.com/temporalio/ai-integrations/commit/7607956c80b945891e3aa96eff9fde3dcdb9f326), 2026-11-20, release stage **pre-release** |
| **Durable** | `matthewcorven/github-copilot-sdk-durable`, `lib/Durable.cs` + `lib/ts` + `lib/py` | commit [`4420dfa`](https://github.com/matthewcorven/github-copilot-sdk-durable/commit/4420dfab0b889e0038e4e6c678cc08baee89148f), `v0.4.0` |
| **Copilot SDK alone** | `github/copilot-sdk` (.NET / TS / Python) driving the Copilot CLI runtime | as studied in [`DURABILITY_ANALYSIS.md`](DURABILITY_ANALYSIS.md) §1 and [`DESIGN.md`](DESIGN.md) §2.2 |

**These are not three answers to one question.** Temporal MAF is a *durable-execution substrate
adaptor*: it moves an existing agent loop into a workflow engine. Durable is a *store and state
model*: it gives the Copilot runtime an application plane it does not ship. The Copilot SDK alone
is the substrate the second one extends. A fair comparison therefore has to be made per dimension,
not per project.

---

## 1. What each stack actually is

### 1.1 Temporal MAF — the agent loop becomes a workflow

You write a Temporal `[Workflow]`; inside it you construct `TemporalAgent("gpt-4o-mini", …)` and
call `RunAsync`. Every model call becomes one `MicrosoftAgentFramework.InvokeModel` Activity; MCP
listing and calls become `MicrosoftAgentFramework.McpListTools` / `.McpCallTool`; tools you opt in
become Activities through `ActivityAsTool`. Credentials and provider clients live on the **worker**,
in `MicrosoftAgentFrameworkPluginOptions.ChatClients`; Workflow code passes only a model name.

Durability is **Temporal's**, not the plugin's: Event History is the record, replay is the recovery
mechanism, continue-as-new bounds history, and Activities carry retry policy, timeouts and
heartbeats. The plugin's own contribution is the *seam* — making an agent loop that calls
`await`-heavy `IChatClient` code safe to run inside a deterministic Workflow.

It is small: **4,761 lines of source across 32 production files, 4,783 lines of tests**, MIT,
`net8.0`, depending on `Temporalio` `1.20.0`, `Microsoft.Agents.AI` `[1.26.0, 2.0.0)` and
`ModelContextProtocol.Core` `2.2.0`. (`wc -l` over `src/**/*.cs` and `tests/**/*.cs`; the
production count includes the shipped `Testing` fakes.)

One design consequence deserves highlighting because it is the source of several matrix rows:
**the plugin owns the agent loop**. `AIAgent.RunAsync` awaits with `ConfigureAwait(false)`, which
moves the continuation off the Workflow scheduler and leaves the Workflow Task without a result, so
`TemporalAgent` re-implements the loop over Agent Framework's extension points and hides the base
methods with `new` overloads (README §"Why the plugin owns the loop", `TemporalAgent.cs`). Agent
middleware (`AIAgentBuilder`, `DelegatingAIAgent`, `AsAIFunction()`, `OpenTelemetryAgent`) is
therefore unsupported inside a Workflow.

### 1.2 Durable — application state the runtime does not keep

This layer leaves the agent loop where it is — the Copilot SDK/CLI owns generation — and adds the
**application plane**: a store (`DurableStore`, JSONL by default; EF Core providers for SQLite,
SQL Server, PostgreSQL and MySQL), documents with history and fork policies, checkpointed task
state machines with memos and durable timers, atomic multi-entity commits, a name-keyed extension
registry, durable permission grants, watches, and a host/runtime (`DurableHarness`,
`src/Durable.Runtime` with `serve --http` / `serve --rpc` / `schedule`).

It is large by comparison: **7,682 lines** in `lib/Durable.cs`, plus **3,695** in
`lib/ts/durable.ts` and **5,322** in `lib/py/durable.py`, 38 example apps, 205 `[Fact]`/`[Theory]` declarations in
`tests/Durable.Tests`, and a 73-test EF Core matrix.

Durability is **the store's**, and the store is deliberately small: one append-only JSONL file,
one exclusive `{path}.lock`, one writer, torn-tail truncation on open, and a documented
`InvalidDataException` for mid-file corruption (`DESIGN.md` §3.1).

### 1.3 Copilot SDK alone — a durable transcript

The SDK gives an append-only session event log
(`~/.copilot/session-state/{sessionId}/events.jsonl`), `ResumeSessionAsync`, `ForkAsync(sessionId,
toEventId, name)`, compaction (`InfiniteSessions`), tools, hooks, permissions, subagents, and the
`Tasks`/`Fleet` RPC families. What it does not give is any notion of *your* state
(`DURABILITY_ANALYSIS.md` §2), and — measured on hosted Foundry in `REAL_WORLD_MATRIX.md` §Band D
— the platform "keeps the session and the transcript; it does not keep in-flight work."

---

## 2. Matrix A — durability and execution model

The core comparison. This is where the three stacks diverge most sharply.

| Dimension | **Temporal MAF** | **Durable** | **Copilot SDK alone** |
|---|---|---|---|
| **Durability substrate** | Temporal Event History; replay is the recovery mechanism | Append-only JSONL (or EF Core database) owned by the app; replay reconstructs store state | Runtime-owned `events.jsonl` per session id |
| **Unit of recovery** | The **Workflow execution** (deterministic replay from history) | The **committed record**: a task checkpoint, a document version, a transcript entry | The **session**: `ResumeSessionAsync` rebuilds transcript context |
| **Crash mid-model-call** | Activity retried or Workflow replayed; the model call is not lost | The turn is **re-sent**, not replayed — the interrupted turn left no answer to reuse (`REAL_WORLD_MATRIX.md` §Band A) | `cancelled` on hosted Foundry; no retry, no completion (Band D) |
| **Crash mid-tool-call** | Tool Activity retried; result recorded in history | Tool side effects are not journalled; the layer's answer is checkpoint re-entry, not per-tool replay | Lost with the turn |
| **Determinism requirement on *your* code** | **Hard.** Workflow code must be deterministic: no clock, no random, no I/O, no `Task.Run`; in-Workflow `AIFunction`s re-run on replay | **None on the agent loop.** Determinism is not required of handlers; the store is the authority | None (no replay of user code) |
| **Idempotency primitive** | Temporal's own: Activity retry + determinism + `SideEffect`/child-workflow semantics | `runtime.MemoAsync(key)` stored in the task record, surviving crashes (`DESIGN.md` §3.2) | None |
| **Timed waits / deadlines** | `Workflow.DelayAsync` / `WaitConditionAsync` — native, free while parked, survives worker restart | `runtime.SleepUntilAsync(deadline, nextCheckpoint)`; deadline lives in the checkpoint | None |
| **Approval / human-in-the-loop wait** | Native and explicit: `ApprovalRequiredAIFunction` → `ToolApprovalRequestContent`, Query to list, Update to decide. **No Activity is open while waiting** (README §Approvals) | Parking task on an external event: `TaskCommit.WaitingForEvent(cp, "external:<name>")`, no invocation and no timer held; `RaiseEventAsync` wakes it (`DESIGN.md` §3.2) | None |
| **Where the transcript lives** | Workflow state (session state bag), rebuilt from Event History on replay; nothing stored elsewhere (README §Sessions) | SDK session binding: the layer *names* `SessionConfig.SessionId` and records the binding in the store (`DESIGN.md` §3.6) | Runtime-owned session state |
| **Transcript growth** | Whole conversation is the input of **every** model Activity → 2 MiB payload cap; continue-as-new bounds history, not payload (README §Limits) | Transcript is SDK-side; the layer pages it (`EntriesAsync`, `ContextAsync`) and supports compaction (example `24`) | `InfiniteSessions` compaction |
| **Application documents** | **None.** Agent Framework has no document concept; you put data in Workflow state or an external store | First-class: typed, versioned, `Latest` / `Rewindable`, forked per `asOf` / `current` / `initial` | None |
| **Fork semantics** | Not an agent-level concept. `ForkAsync` on the Copilot SDK clones a transcript, not app state | Fork inherits transcript through one entry and copies documents per policy — the two planes fork together (`DESIGN.md` §3, `lib/Durable.cs`) | `ForkAsync(sessionId, toEventId, name)` — transcript only |
| **Atomicity across entities** | Temporal gives Workflow-level atomicity over its own state; it does not know about your external writes | `DurableStore.CommitAsync` writes transcript entries, documents and tasks in one commit, with an in-memory undo log for rollback (`DESIGN.md` §3.1) | None; events, tool results and app writes are decoupled |
| **Concurrent writers** | The Workflow is single-threaded by construction; scale-out is the server's job | **One writer per store file**, enforced by an exclusive lock; a second process is refused at open (`DESIGN.md` §3.1) | Runtime-mediated; no app-level guarantee |
| **Store backend choice** | Temporal persistence (server/cloud) | JSONL, in-memory, and EF Core over SQLite / SQL Server / PostgreSQL / MySQL, selected by `Format`, `ProviderName` or `Provider` | Runtime-chosen |
| **Retry policy for provider errors** | **Authored and tested**: HTTP status classification (408/409/429/5xx retryable; other 4xx not), `x-should-retry` override, `retry-after-ms` / `Retry-After` honoured and capped at 60 s, per-agent `ActivityOptions` (README §Retries; `Internal/ModelErrorClassifier.cs`; 22 facts in `FailureTests.cs`) | **Not authored.** No retry/backoff policy in the layer; recovery is re-entry from a committed checkpoint | Runtime's own behaviour; not a policy you can see or tune from the SDK |
| **Versioning / replay compatibility** | Real and tested: recorded histories replay per major (`ReplayTests.cs`, `ReplayMajor = 0`), plus an API guard that fails loudly if `Microsoft.Agents.AI` changes the experimental constructors (`Internal/AgentFrameworkApiGuard.cs`) | **Not implemented.** No recorded-history replay test and no versioned workflow semantics; the guarantees are store replay and output determinism (`DESIGN.md` §6.1) | The runtime's own; opaque to the app |
| **Offline capability** | Requires a Temporal Service (dev server or cloud) — a run cannot continue without it | Works fully offline against a local store file; only model calls need the runtime | Needs the Copilot runtime reachable to finish a turn |
| **Operational prerequisites** | Temporal Service + worker(s) + provider credentials on the worker | One store file (or one database) and one writer; no server | Copilot CLI sign-in |

**Sources.** Temporal MAF: README §Installation, §Retries and timeouts, §Why the plugin owns the
loop, §Approvals, §Sessions and continue-as-new, §Limits, §Context and history providers;
`TemporalAgentOptions.cs`; `Internal/ModelErrorClassifier.cs`; `tests/.../ReplayTests.cs`.
Durable: `DESIGN.md` §3.1, §3.2, §3.6, §6.1; `docs/REAL_WORLD_MATRIX.md` §Band A, §Band D, §Band G.
Copilot SDK alone: `DURABILITY_ANALYSIS.md` §1–2.

---

## 3. Matrix B — agent surface parity

What each stack gives an agent *to work with*, independent of durability.

| Capability | **Temporal MAF** | **Durable** | **Copilot SDK alone** |
|---|---|---|---|
| **Agent runtime** | Microsoft Agent Framework (`Microsoft.Agents.AI` 1.26+), any `IChatClient` | GitHub Copilot SDK → Copilot CLI runtime over JSON-RPC | Copilot CLI runtime |
| **LLM providers** | Any `IChatClient` — OpenAI, Azure OpenAI, and anything `Microsoft.Extensions.AI` adapts. Multiple models per Workflow via named factories | Whatever the Copilot runtime serves (`models.list`), with reasoning-effort validation | Copilot models |
| **Where credentials live** | Worker-side factories; Workflow passes a model name. Credential-looking `AdditionalProperties` keys **fail the run before any Activity is scheduled** | Host process (CLI auth); the layer stores permissions, not keys | CLI sign-in |
| **Model selection** | Per agent, by factory name; two agents in one Workflow can use two models | Stored per conversation in `AgentChoices`; `EffectiveModel` reports what the runtime actually answered (`DESIGN.md` §3.4) | `SessionConfig.Model`; `auto` re-routes per request |
| **Reasoning effort** | Whatever the provider client accepts | Validated against `ModelInfo.SupportedReasoningEfforts` before sending — the runtime rejects the session otherwise (`DESIGN.md` §6) | Must be validated by the caller |
| **Tools — plain in-process** | `AIFunctionFactory.Create` — runs **inside the Workflow**, so must be deterministic and re-runs on replay (README §Tools) | Plain `AIFunction` handlers, no replay constraint | `CopilotTool.DefineTool` |
| **Tools — durable/remoted** | `ActivityAsTool.Create(...)` → one retried, timed-out Activity per call | The layer's own task/checkpoint machinery; a tool that needs durability commits through the store | None |
| **Tools — MCP** | First-class: `TemporalMcpServer.ListToolsAsync` (one Activity) + `TemporalMcpTool` (one Activity per call), shared `McpClient` per plugin instance, `McpConnectionIdleTimeout`, `ToolNamePrefix`, cache toggle | MCP available through the SDK; confinement expressible in the sandbox policy (`docs/SANDBOX_MATRIX.md`) | MCP via session config |
| **Hosted tools** | `HostedWebSearchTool`, `HostedCodeInterpreterTool`, `HostedFileSearchTool`, `HostedMcpServerTool` carried by kind across the Activity boundary; **headers only from the worker** | Provider-side tools as the SDK offers them | Provider-side tools as the runtime offers them |
| **Tool overrides / shadowing** | Tools must be a known `AITool` type or the run fails; duplicate names → first match wins | `ToolWrapper` decorates whichever tool won after "later wins" resolution; built-in-name collision documented and handled (`docs/COPILOT_SDK_NOTES.md`) | Built-in tools silently shadow same-named custom tools unless `OverridesBuiltInTool = true` |
| **Tool concurrency** | `AllowConcurrentInvocation` → `Workflow.WhenAllAsync`; default sequential, matching `ChatClientAgent` | Task/child-task concurrency in the layer | Runtime/RPC dependent |
| **Approvals** | `ApprovalRequiredAIFunction`; request id `ficc_…`; response bound to the surfaced request so a tampered `FunctionCallContent` cannot redirect the call; unanswered requests and forged ids **fail the run** (README §Approvals) | Durable permission grants: `kind:scope`, fail-closed, persisted with the conversation, inherited by a fork (`DESIGN.md` §3.1) | `SessionConfig.OnPermissionRequest`; the default **approves everything** |
| **Permission persistence** | N/A — Temporal handles human decisions; there is no agent permission model | Yes: grants are part of the durable record and must be reproducible | None — grants live in the process |
| **Sandbox** | Not a plugin concern | First-class per-conversation policy: filesystem, network egress, credential masking/injection, dev tools, MCP/LSP confinement, bypass, trust (`docs/SANDBOX_MATRIX.md`, examples 33–35) | `SandboxConfig`, enforced by the CLI |
| **Subagents** | Build one yourself: wrap `subAgent.RunAsync(...)` in an `AIFunction` | Foreground and **persistent background** subagents that survive a restart (examples 21, 22) | `CustomAgents` / `DefaultAgent`, `subagent.*` events |
| **Streaming** | `RunStreamingAsync`; the Activity consumes the provider stream and returns the whole response, then the Workflow yields updates; optional Workflow Streams topic with 100 ms / 256-update batching | `session.On<T>` event stream; `serve --rpc` JSON-lines transport; late-join snapshot-then-deltas (example 20) | Event subscription |
| **Structured output** | `RunAsync<T>` — schema sent as response format, parsed on return, non-object `T` wrapped in `{"data": …}` | Prompt/library level | Caller-managed |
| **Context / memory providers** | `ChatHistoryProvider` + `AIContextProvider` run **in the Workflow**, must be synchronous and deterministic; I/O belongs in an Activity before the run (README §Context and history providers) | Prompt sections rendered per turn through `OnUserPromptSubmitted`; documents are the memory (`DESIGN.md` §3.3) | Hooks |
| **Hot reload of extension code** | Not a concept — Workflow code changes must respect deterministic replay | Extension registry keyed by **name**; stored choices are strings, so new code binds to the old name (examples 09, 30) | Not a concept |
| **Plan mode** | Build it in Workflow code | Example `26` | Runtime feature |
| **Watches / change feed** | Use Workflow Queries, or Workflow Streams for live updates | `WatchHandle<T>`, bounded 100-frame, single-consumer, reset-on-overflow (`DESIGN.md` §3.11) | `session.On<T>` (runtime events, not store writes) |

**Sources.** Temporal MAF: README §Chat clients and credentials, §Tools, §MCP, §Hosted tools,
§Approvals, §Streaming, §Structured output, §Context and history providers, §Limits;
`TemporalAgentOptions.cs`, `ActivityAsTool.cs`, `MicrosoftAgentFrameworkPluginOptions.cs`.
Durable: `DESIGN.md` §3.1–§3.4, §3.11, §4, §6; `docs/SANDBOX_MATRIX.md`;
`docs/COPILOT_SDK_NOTES.md` §Tool authoring gotchas, §Permissions.
Copilot SDK alone: `DESIGN.md` §2.2; `docs/COPILOT_SDK_NOTES.md`.

---

## 4. Matrix C — operations, verification, extensibility, release

| Dimension | **Temporal MAF** | **Durable** | **Copilot SDK alone** |
|---|---|---|---|
| **Release stage / API stability** | **Pre-release**, coordinate on NuGet, `maturity = pre-release`, `Version 0.0.0` in-repo (tags are the release source). Replay is promised **within a major only** | `v0.4.0`, semver tag + changelog + package version held to one number; ports kept in step by a parity contract (`examples/parity.json`) | GA SDK |
| **Dependency risk it manages for you** | Guards `Microsoft.Agents.AI` experimental constructors at construction time and names the tested version (1.26.0); nightly runs against newest release | Pins the SDK in three language ports and documents SDK gotchas as they are found | — |
| **Test strategy** | Real Temporal dev server (`DevServerFixture`), scripted `FakeChatClient`, `InMemoryMcpServer`; **120+ `[Fact]`/`[Theory]` cases**; exact Activity-count assertions with `MaxCachedWorkflows = 0`; 11 recorded histories replayed | 205 declarations in `Durable.Tests` + EF Core matrix (61 pass / 12 skip on a bare machine); `validate.sh`; `--deterministic` mode against a **recorded runtime** with effective-model and output-determinism checks; parity suite with a mutation meta-test | — |
| **Does it test the failure it claims?** | Yes — retry classification, heartbeat timeouts, cancellation, approval tampering, forged ids, replay | Partly — fault bands assert kill/restart/duplicate/store-outage per scenario (`REAL_WORLD_MATRIX.md`), and report the losses: duplicate delivery doubled in Band A; store outage silently recreated the file | — |
| **Multi-language story** | .NET only for MAF. The same repository ships Python and TS plugins for **other** frameworks (LangGraph, OpenAI Agents, Vercel AI), not for Agent Framework | C# canonical + TypeScript + Python ports over one JSONL wire format, with a parity contract and cross-language store compatibility | .NET / TS / Python SDKs |
| **Multi-agent orchestration** | `Workflow.WhenAllAsync`, child Workflows, Signals/Updates; m-agent patterns are your Workflow code | Parent/child **tasks** with `failFast`, cascading abort, abort handlers, task graph (`DESIGN.md` §3.2, example 23) | `Fleet`/`Tasks` RPC |
| **Multi-stage deployment** | Temporal Service + worker; scale via task queues | Single process with a file, or `serve --http` / `serve --rpc` / `schedule`; Dev Tunnels for host portability (`DESIGN.md` §3.13–3.15) | CLI process |
| **Observability** | Temporal UI, Event History as the debugging artifact, `Temporalio.Extensions.OpenTelemetry`; provider spans from `UseOpenTelemetry()` in the factory | Store file, task records with `restarts`, examples that print observable state; no tracing story documented | Runtime events |
| **Secret handling** | Explicit and enforced: headers and credential keys may not enter Workflow code, because Activity input is recorded in history | Permissions persisted; sandbox credential masking/injection; no documented discipline for secrets in prompts | CLI-managed |
| **Cost model** | Every model call is an Activity; a parked approval costs nothing; Temporal's default retry policy has **no attempt limit** unless you set one | Failed approach: restart the process; a resumed turn may be re-sent (one extra model admission, Band A) | Per-call |

**Sources.** Temporal MAF: `plugin.toml`, `README.md` §Installation, §Testing, §Develop;
`tests/.../ReplayTests.cs`, `SideEffectTests.cs`, `FailureTests.cs`, `ApprovalTests.cs`,
`PluginTests.cs`; `Internal/AgentFrameworkApiGuard.cs`. Durable: `README.md`, `docs/DESIGN.md`
§3.1, §3.12–3.15.5, §6; `docs/RELEASING.md`; `docs/REAL_WORLD_MATRIX.md`.

---

## 5. Where Temporal MAF is genuinely better

1. **Failure semantics are the engine's, not the app's.** Retry policy, timeouts, heartbeats,
   cancellation, parked waits and replay are Temporal primitives with years of operational
   evidence. This repository's own Band G recorded that the vendor's durable extension "covers the
   operational story better than this variant does"; the same applies here, with a stronger engine.
2. **The human-in-the-loop contract is tighter.** Request ids are generated, responses are bound to
   the surfaced call, a tampered `FunctionCallContent` cannot redirect it, an unanswered request
   **fails** the run rather than silently dropping the decision, and a parked approval costs
   nothing and survives a worker restart. Durable's equivalent (park on `external:` event) is
   capable but leaves the decision's *identity* to the app.
3. **Provider-error classification is authored, tested and shared across plugins.** HTTP-status
   classification, `x-should-retry`, `Retry-After` in three formats with a 60 s cap, and 22 facts
   covering it. The Durable layer has no retry policy at all.
4. **Determinism is enforced rather than assumed.** Calling through the `AIAgent` base fails fast;
   context providers that await an Activity fail with a named error; `AgentId` is stable across
   replay. These are guard-rails this repository does not attempt.
5. **Replay compatibility is a tested contract** — 11 recorded histories per major, replayed in CI.
6. **Model-agnostic.** Any `IChatClient`, multiple providers, credentials on the worker with an
   explicit rule that credential-shaped keys may not cross the Activity boundary.

## 6. Where Durable is genuinely better

1. **Application state is first-class.** Documents with history policies and fork policies,
   versioned reads at a past entry, and forks that branch *transcript and documents together*. No
   amount of Temporal configuration gives you `SnapshotAsOfAsync`; you would build it.
2. **No determinism tax on your code.** Workflow programming in Temporal is a real constraint:
   plain `AIFunction`s re-run on replay, providers must complete synchronously, middleware is
   unsupported, and a `Task.Run` fails the Workflow Task. Here, handlers are ordinary async code.
3. **The store is yours and portable.** One JSONL file or one EF Core database, four relational
   providers, no server, no scheduler, works offline, and the same semantics in C#, TypeScript and
   Python with a parity contract. Temporal MAF has none of that — and needs a Temporal Service.
4. **Code identity is decoupled from state.** Extensions are resolved by name from stored choices,
   so a restart binds old choices to new code. In Temporal, changing Workflow code is a
   replay-compatibility problem to be managed carefully.
5. **Permission and sandbox policy are durable.** Fail-closed by default, per-conversation grants
   persisted and inherited by forks, with an OS-level sandbox policy beneath it. The SDK's default
   is "approve everything"; neither Temporal MAF nor the SDK alone fixes that.
6. **The failure evidence is from the target runtime.** Every claim in `REAL_WORLD_MATRIX.md` was
   produced against Copilot, Foundry, MAF+DTS or an Actions-shaped CI, and the losses are published
   alongside the wins.

## 7. What the Copilot SDK alone actually covers

Worth stating plainly, because the temptation is to under-credit it:

- **It is durable for the transcript.** Append-only events, resume, fork-at-event, compaction.
- **It has more of the agent surface than either layer adds:** built-in tools, MCP, hooks,
  permissions, subagents, background tasks, fleet, sandbox.
- **What it has no answer for** (`DURABILITY_ANALYSIS.md` §2, Band D measurement):
  application documents, checkpointed task state, idempotency memos, atomic multi-entity commits,
  durable deadlines, persisted permissions, and any recovery of *in-flight* work. On hosted Foundry
  the platform "keeps the session and the transcript; it does not keep in-flight work," a
  disconnected foreground response is `cancelled`, and a fresh session answers `NO PRIOR CONTEXT`.

So: **SDK alone is sufficient when the agent is one turn and the state is the conversation.
Neither of the other two is needed.** The moment the work spans turns, approvals, retries or
process death, something must own the state machine — and the three stacks differ only in *who*.

## 8. Things neither stack does, stated honestly

- **Neither gives you per-tool side-effect replay.** Temporal records a completed Activity's
  result and does not re-run it, but a tool that wrote to the outside world before the Activity
  completed is on its own — as it is here, where a stop between a comment and its ledger entry
  costs one duplicate (Band C evidence).
- **Neither makes duplicate *delivery* idempotent for free.** Band A measured 2 enquiries → 2
  documents and 2 model admissions in the durable variant too: *"the variant has no identity for an
  enquiry to deduplicate on."* Temporal's answer is a Workflow id you choose; this layer's answer
  is a memo key you choose.
- **Neither rescues a bad transcript strategy.** Temporal caps Activity input at 2 MiB and grows
  history per call; this layer leaves transcript growth to the SDK and the app.
- **Neither is a completed product.** Temporal MAF is pre-release with a stated dependency window
  (`Microsoft.Agents.AI` 1.26–2.0) and a replay promise scoped to one major. Durable is `v0.4.0`
  with a measured split-store limitation and no replay-compatibility guarantee.

## 9. Decision guide

| If your situation is… | Reach for | Why |
|---|---|---|
| One turn, one conversation, no external side effects | **Copilot SDK alone** | Everything else is overhead |
| Long-running, multi-step agent work where the *workflow engine* should own retries, timeouts and replay | **Temporal MAF** | Its failure semantics are already proven at scale; you accept a Temporal Service and deterministic Workflow code |
| Human approvals as first-class, tamper-resistant decisions on parked runs | **Temporal MAF** | Request-id binding and fail-on-unanswered are stronger than what this layer offers |
| Domain state that must survive, be versioned, be forked, and be queried at a past point | **Durable** | Documents + fork policies + `AsOf` reads are the point |
| The same durable semantics across C#, TypeScript and Python, with one wire format | **Durable** | Only stack with a cross-language parity contract |
| No server, no scheduler, offline-capable, one file | **Durable** | Temporal requires a service; this needs a store path |
| Vendor-neutral LLM choice with credentials kept off the code path | **Temporal MAF** | Worker-side factories; any `IChatClient` |
| Hosted Foundry agents where the platform already owns the transcript | **Foundry + Durable inside, or neither** | Band D measured composition, not replacement; the layer buys less when the platform already keeps the session |

The honest summary: **Temporal MAF and this repository solve different problems, and neither
subsumes the other.** If you had to pick one axis, Temporal MAF is stronger where *execution*
must be durable; this layer is stronger where *application state* must be durable and portable.

---

## 10. Follow-on assessments

Two role assessments build on this document. Both are read-only, both are pinned to the same
upstream commit, and both record what they could not confirm:

- [`assessments/TEMPORAL-CONTRIBUTION-ASSESSMENT.md`](assessments/TEMPORAL-CONTRIBUTION-ASSESSMENT.md)
  (Lead / Architect) — where outside contribution would actually land in Temporal, at the
  MAF **integration** level and the `sdk-dotnet` **core** level. Headline: the plugin has one
  authored commit, zero open issues and 72 NuGet downloads, so the ranked plan leads with
  correctness and onboarding work, not features.
- [`assessments/DURABLE-TAKING-FROM-TEMPORAL.md`](assessments/DURABLE-TAKING-FROM-TEMPORAL.md)
  (PO) — which Temporal properties this library could adopt without becoming a Temporal
  service. Verdict: **adopt three** (typed misuse detection, an authored provider-error
  classifier with a *required* attempt limit, and a store-compatibility corpus), three narrowly,
  and **reject** multi-writer stores, provider neutrality, continue-as-new and heartbeats.
- [`assessments/TEMPORAL-SPIKES.md`](assessments/TEMPORAL-SPIKES.md) — **four engineering spikes run
  against a live Temporal dev server**, testing the contribution claims rather than ranking them.
  Three of four changed shape: local-activity tools measured at **3.5×** and need an opt-in
  two-plugin delivery; the 1.28.0 window is **honest** (398/398) and needs only a metadata patch;
  external storage **works but needs a coordinated `Temporalio` 1.22.0 bump**; and the
  `ConfigureAwait(false)` hazard was **falsified** — it produces an empty *successful* completion,
  never `TMPRL1101`. It also retires approval binding (already tested upstream) and promotes one
  real bug: unclassifiable provider errors retry forever.
