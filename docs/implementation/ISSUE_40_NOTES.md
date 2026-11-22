# Issue #40 — public-path contract tests for the durable client facade

Notes captured before the session that scoped this issue ended, so the next
agent does not have to re-derive them.

## Scope vs. what already exists

Issue #40 lists eight in-scope contract areas. Six are already covered by facts
that exist on `dev`; two are genuine gaps.

| # | Area | Status on `dev` |
|---|------|-----------------|
| 1 | Identity / resume | Covered — `FacadeDurabilityTests.cs`, `TaskResumeTests.cs` |
| 2 | Checkpoint failure | **GAP** — no fact injects a failing provider |
| 3 | Duplicate event processing | Covered — `ReplayToolDispatchTests.cs` |
| 4 | Interrupted forks | Covered — `ForkTests.cs` |
| 5 | Hook composition | **GAP** — the wrapper is untested |
| 6 | Ownership | Covered — `DependencyInjectionTests.cs` (`AddDurableCopilotClient_LeavesTheStoreToOneRuntime`) |
| 7 | Historical choices (`AsOf` / `Current` / `Initial` at the fork boundary) | Covered — `ForkTests.cs` |
| 8 | Sandbox propagation | Covered — `SandboxPropagationTests.cs` |

## Gap 2 — checkpoint failure

Inject a provider whose `Commit()` throws, through the public seam only:

- `DurableStoreOptions.Provider` (`lib/Durable.cs:738`) is a
  `Func<DurableStoreOptions, IDurableStoreProvider>?` that takes precedence over
  `ProviderName` then `Format`. This is the supported way to supply a failing
  double without touching `lib/`.
- `IDurableStoreProvider` (`lib/Durable.cs:759`) exposes `Open`,
  `BeginTransaction`, `Rollback`, `Commit`, `Sequence`, `Conversations`,
  `Tasks`, `Documents`, `Conversation`, `Task`, `Document`, and friends. A
  delegating wrapper that forwards everything and throws from `Commit` is
  straightforward.
- `InMemoryStoreProvider` (`lib/Durable.cs:889`) is the natural delegate target.

Assert the full state after the failure, not just the exception: the
conversation record, the entry count, and that a retry behaves correctly.

## Gap 5 — hook composition

`lib/Durable.cs:5459-5483` wraps `OnUserPromptSubmitted`. The wrapper is the
only place the durable layer composes its own hook with a caller-supplied one,
and nothing asserts the composition order or that both run.

`DurableClientBuilder` (`lib/Durable.cs:6093`) is the public entry point:
`UseStore(DurableStore)`, `UseStore(DurableStoreOptions)`,
`UseClient(CopilotClient)`, `UseClient(CopilotClientOptions?)`, `UseRegistry`,
`UseHarnessOptions`, `Configure(Action<DurableClientOptions>)`, `Build()`.
`Build()` validates and constructs only — it performs no I/O, so a fact can
assert the composed hook without a live runtime.

`RuntimeConnectionTests.cs` already asserts hook *options* at lines 189 and 195;
extend that pattern rather than starting a new file.

## Constraints

- Each fact must be isolated with a unique store directory and assert full
  state, per the issue's acceptance criteria.
- Do not modify `lib/Durable.cs`. If a fact reveals a genuine product bug, stop
  and report it.
- Do not restyle existing facts.
- The new example and the builder/facade boundary docs are #41, not #40.
- Vocabulary rule: `scripts/validate.sh` bans `mock|fake|stub|simulat` across
  `examples/`, `lib/`, `docs/`, `README.md`, `tools/`, `scripts/`. Use
  "double", "stand-in", or "synthesised" instead.

## Baseline

`dotnet test tests/Durable.Tests` → 127 passed on `dev` at `7e19f03`.
