---
updated_at: 2026-10-23T22:22:00-04:00
focus_area: Issue #1 — #9/#10/#11/#12 Done; #13 next (legacy single-file JSON reader)
active_issues: [1, 13, 14, 15, 16, 17, 18, 19]
---

# What We're Focused On

**Progress this session (2026-10-23/24, continuing from ISSUE_1_HANDOFF.md):**

- **#9 + #10 — DONE.** PR #21 merged into `dev` as `644ae88`. #10 re-scoped and verified
  against all acceptance criteria; both issues closed; board Done.
- **#11 — DONE.** PR #22 merged as `e21abb9`. Two real bugs caught by independent scratch
  verification beyond the compile/test gates and fixed before merge: (1) NRE on every first
  commit with an entry + missing committed-baseline seeding after open/Replay; (2) torn-tail
  guard was dead code (`JsonSerializer.Deserialize` throws rather than returning null) so
  torn tails crashed `Open`. Six-point scratch verification fully green.
- **#12 — DONE.** Two PRs merged 2026-10-24:
  - PR #23 → `f4d7601` — `DurableStore.DefaultStorePath()`, explicit Path wins, directory
    created on first write not on open, InMemory never touches the filesystem.
  - PR #24 → `a1ba1cd` — the Lead's "loud ephemeral tier" decision: `CreateEphemeral()`,
    `IsPersistent` (backed by a new `IDurableStoreProvider.IsPersistent` member),
    `Create(new DurableStoreOptions())` is persistent at the per-user default path, and
    `Auto` on a non-existent/empty file writes **JSONL** (discriminator = the envelope's
    top-level `type` property, which a legacy snapshot never carries).
  - PR #24 was stacked on #23 and showed CONFLICTING; resolved by rebasing onto the merged
    `dev` (the `1173c75` commit was skipped as already-applied, and the squad-state commit
    was dropped as already upstream). Gates re-verified on the rebased tree before merging.
  - Independently verified on the rebased tree: `CreateEphemeral().IsPersistent` false;
    `new DurableStore(path)` true; default path's first line is a JSONL envelope; legacy
    files still reopen under `Auto` without being rewritten.
- **Architecture decision recorded** (Lead, merged into `.squad/decisions.md`): keep the
  in-memory backend as an explicitly-named **ephemeral tier**; make the persistent tier the
  default of the options/factory API; migrate the examples to explicit names. Decision #7
  stands as a capability conclusion but its rationale is superseded. Tier ladder going
  forward: ephemeral → local-file (JSONL) → shared-database (EF Core, #2).
- **Lesson (keep applying):** compile-only gates do NOT cover new providers; every spine item
  needs an end-to-end scratch roundtrip before "green" is believed.

**Spine (serial):** #9 ✅ #10 ✅ #11 ✅ #12 ✅ → **#13 (next)**.
**Consumers:** #14 (example + README + docs prose + example migration to explicit tiers),
#15 (xUnit facts, target 9/0 — the tier model adds ~3 more).

**Parallel line (separate initiative, not part of #1):** #16–#19, the sandbox epic.

## Branch model

`dev` is the integration branch and the repo default. Feature work branches from
`origin/dev` and PRs target `dev`. `main` is release-only. `origin` is SSH.
Direct pushes to `dev` are rule-bypassed only for squad-state commits (observed, not a pattern).

## Board

GitHub-copilot-sdk-durable Roadmap (project 5, owner matthewcorven).
#1 In Progress; #9/#10/#11/#12 Done; #13–#19 Todo.

## Next action

Route **#13** (legacy single-file JSON reader) to Core Engineer on `squad/13-{slug}` from
`origin/dev`. Requirements it must satisfy:
- Detect and read the legacy single-file JSON shape without data loss; convert the snapshot
  into the new backend's state **at open**; the legacy file is **read-only by default** — no
  implicit write-back (plan §5 decision #4).
- Keep the `StoreSnapshot` / `DocumentSnapshot` / `DocumentVersionSnapshot` public types in
  place (they stay until #13 completes).
- **Close the gap PR #24 left open:** a file that is *neither* JSONL nor a legacy
  `StoreSnapshot` currently opens as an empty legacy store (silent data loss) instead of
  raising an actionable error. #13 must raise a clear error naming the path and the reason.
- `Auto` sniffing must not destroy data: a legacy file is opened by the legacy provider and
  written back in the legacy format; forcing `Format = Jsonl` on a legacy path is the
  explicit "convert this store" action; forcing `Format = LegacyJson` on a JSONL file is
  rejected with a clear error rather than silently discarding the JSONL records.