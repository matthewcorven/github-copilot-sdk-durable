# Issue #48 — determinism check: findings and decisions

Re-derived counts on `origin/dev` @ `2499e49` (the issue body's 34 examples / 21
`LIVE_EXAMPLES` are stale — they predate #54, #64 and #71):

- `ls examples/*.cs | wc -l` → **37** examples: `00`–`35` plus `37`. There is no `36`; that
  file belongs to #41.
- `scripts/validate.sh` `LIVE_EXAMPLES` → **23** entries → **14** examples need no sign-in.

## What the deterministic replay runtime can actually drive

`tools/ReplayRuntime/` answers the runtime JSON-RPC surface from a fixture. Measured, not
assumed — every example was run with `DURABLE_RUNTIME_PATH` pointed at the replay build:

| Group | Examples | Against the replay runtime |
|---|---|---|
| Conversation-creating | 35 of 37 | **Fail.** `session.create` answers a recorded session id, but every example creates its session without naming it, so the durable layer asks for the session the runtime just handed it and the fixture has no entry for it. The replay runtime refuses to invent a response and exits 1 with `session.create: fixture records no session '<id>'`. |
| Store-only, no session | `32-jsonl-store`, `37-ef-core-store` | **Pass**, exit 0, all assertions satisfied. These touch no Copilot session at all. |

So the honest deterministic mode runs the 2 examples that genuinely need no runtime, plus the
recorded-runtime suites that do exercise the runtime end to end (`tests/ReplayRuntime.Tests`
and the recorded tool-dispatch tests inside `tests/Durable.Tests`). The other 35 examples are
excluded with a printed reason, never silently.

Extending coverage means teaching the replay runtime to allocate a session for an unnamed
`session.create` — that is a change to `tools/ReplayRuntime/`, which #48 explicitly puts out of
scope. The refusal is recorded here so a later issue can lift it deliberately.
