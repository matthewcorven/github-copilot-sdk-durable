# Issue #78 — the recorded runtime now drives 16 of 37 examples

Supersedes the measured table in `ISSUE_48_NOTES.md`. #48 disclosed the limit honestly and put
the fix out of scope; #78 lifts it.

## The two options, and the choice

The issue named two ways to widen the deterministic floor:

1. **Record one transcript per example.** Faithful, but 35 new fixtures to author and keep in
   sync.
2. **Let the recorded runtime allocate a session for an unnamed `session.create`.** Cheap, but
   weakens the guarantee unless a synthesised pass is visibly distinguished from a recorded one.

**Option 2 was chosen, with the mandatory marker.** The deciding evidence is the measurement
below: most of the 35 failing examples do not fail because the session is unnamed. They fail
because they assert a tool call, a model answer, or a host event that the fixture does not
contain. A per-example fixture would have to record those too, which means a live run per
example — the thing the deterministic mode exists to avoid. Option 1 would have bought 35
fixtures and lifted far fewer than 35 examples.

## What changed

- `tools/ReplayRuntime/ReplayHost.cs`: a `session.create` for an id the fixture does not record
  is served from the fixture's first recorded session instead of being refused. The same
  fallback applies to `session.sendMessages` and `session.resume` for an unrecorded id. Each
  fallback writes the phrase `synthesised session` to stderr and, when `REPLAY_MARKER_PATH`
  names a file, appends it there. The file sink exists because the SDK captures the runtime's
  stderr and discards it on success, so a successful run would otherwise lose the marker.
- `scripts/validate.sh`: `DETERMINISTIC_EXAMPLES` grew from 2 to 16, and every excluded example
  is printed by name with the reason it cannot run. A synthesised pass prints
  `PASS <example> (synthesised session — not a recorded transcript)`; a recorded one prints
  `(recorded transcript)`.
- `docs/DESIGN.md` §6.1: rewritten to describe the synthesised path, the marker, and the four
  groups of exclusions.

## Measured, on `origin/dev` @ `c3e401a`

Every example was run with `DURABLE_RUNTIME_PATH` pointed at the replay build and
`REPLAY_MARKER_PATH` at a scratch file.

| Group | Examples | Result |
|---|---|---|
| Recorded transcript | `32-jsonl-store`, `37-ef-core-store` | **Pass**, exit 0. They never open a Copilot session. |
| Synthesised session | `00`, `01`, `03`, `04`, `05`, `06`, `09`, `11`, `12`, `14`, `17`, `18`, `23`, `35` | **Pass**, exit 0, marked synthesised. |
| Unrecorded request | `02`, `07`, `08`, `22`, `24` | **Fail.** `sessions.fork`, a subagent request, a compaction request, or a `models.list` for a model the fixture does not record. The runtime refuses an unrecorded method rather than inventing a response. |
| Asserts a tool call | `10`, `16`, `21`, `25`, `26`, `27`, `28`, `29`, `33`, `34` | **Fail.** The fixture records the model's side only, so no tool call happens. |
| Asserts model output | `13`, `15`, `19`, `20` | **Fail.** The recording has one canned answer; these assert the answer, its deltas, or the transcript shape. |
| Asserts a host event | `30`, `31` | **Fail.** A runtime restart and a permission request the recording does not contain. |

**16 of 37 run; 21 are excluded with a printed reason.** Before #78 the count was 2 of 37.

## What a synthesised pass proves, and what it does not

It proves the example's own code paths: its store, its harness wiring, its event handling, its
argument parsing. It does not prove a recorded transcript, and it says so on its own PASS line.
The vocabulary rule still holds — a synthesised pass is described as synthesised, never as a
recorded one.

## Follow-ups this measurement surfaced

- `sessions.fork` is not in `ReplayHost.SupportedMethods`. Adding it would unlock `02-forks` and
  `08-context`, though both also assert fork transcript contents.
- `07-harness-conversations` needs a fixture that records a model with a supported reasoning
  effort.
- `30-reload-and-restart` takes 124 seconds and times out against the recording; it needs a
  recorded restart, not a synthesised session.
