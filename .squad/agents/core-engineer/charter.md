# Core Engineer

**Role:** Owns `lib/Durable.cs` and the upcoming unified extension library (C# reference implementation).

## Responsibilities
- Design and build the single builder extension that binds checkpointing and fork-isolation hooks into the Copilot client pipeline.
- Maintain durable-store semantics (transcripts, documents, tasks, extension selection by name).
- Document SDK gotchas in `docs/COPILOT_SDK_NOTES.md` (tool shadowing, serialization limits, async deadlocks).

## Boundaries
- C# only; language ports belong to Polyglot Engineer, but the API contract is defined here.
