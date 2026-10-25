# History

## Project context (seeded 2026-10-23, requested by Copilot App)
- Durable GitHub Copilot SDK agents: lib/Durable.cs plus 31 file-based C# examples, xUnit tests, docs/ (DESIGN, audits).
- Upcoming: examples in TypeScript, Python, Go, Rust, Java; unified extension library with a single builder extension binding checkpointing and fork-isolation hooks into the Copilot client pipeline.

## 2026-10-23T23:36:11-04:00 — Docs: landed #14 (examples/31-jsonl-store + docs prose)
- PR #25 (draft, base dev) on `squad/14-jsonl-store-example`, commit fefd4f4. Files: examples/31-jsonl-store.cs (new), README.md (Advanced row 31), docs/DESIGN.md §3.1, docs/COMPARATIVE_ANALYSIS.md §1, docs/EXAMPLES_AUDIT.md (row 31 + 32-example count).
- Verified every prose claim against lib/Durable.cs at ebacc83: JSONL envelope {"v":1,"seq":N,"type","payload"}, torn-tail discard+truncate on open, Auto sniffs `type` string on first content line, default path = LocalApplicationData (Windows) / $XDG_STATE_HOME else ~/.local/state, in `durable/` subdir, FileName option, CreateEphemeral/IsPersistent tier split.
- Example run evidence: validate.sh 31-jsonl-store PASS; COMPILE_ONLY full run 32/32 PASS; dotnet test 4/0.
- Learning 1: `DurableStore.CommitAsync` body must return Task — house style is `return Task.FromResult(...)` inside the sync lambda (matches examples 00).
- Learning 2 (discrepancy to flag): issue #14 and the plan say `dotnet test` should read 8 passed / 0 failed after #14, but dev @ ebacc83 carries only 4 xUnit facts (#15 not merged). Gate interpreted as "unchanged, 0 failed"; actual 4/0. Coordinator should re-baseline the count after #15 lands.
- Learning 3: stale count references outside this issue's scope exist and are now inconsistent: README.md:248 ("run all 31 examples"), docs/DESIGN.md:10 ("31 examples, 00–30"), docs/DESIGN.md:165 ("§4 Example suite (31 examples)"). Reported, not edited (scope control).
- Learning 4: previous interrupted run left a scratch dir in $TMPDIR (durable-example-*, Oct 23 00:46); removed it during cleanup.
