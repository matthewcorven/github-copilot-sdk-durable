---
updated_at: 2026-10-23T21:58:00-04:00
focus_area: Issue #1 — #9/#10/#11 Done; #12 in flight (PR #23 default path + PR #24 loud ephemeral tier)
active_issues: [1, 12, 13, 14, 15, 16, 17, 18, 19]
---

# What We're Focused On

**Progress this session (2026-10-23/24, continuing from ISSUE_1_HANDOFF.md):**

- **#9 + #10 — DONE.** PR #21 merged into `dev` as `644ae88`. Gates green. #10 re-scoped
  and verified against all acceptance criteria; both issues closed; board Done.
- **#11 — DONE.** PR #22 merged as `e21abb9`. Two real bugs caught by independent scratch
  verification beyond the compile/test gates and fixed before merge: (1) NRE on every first
  commit with an entry + missing committed-baseline seeding after open/Replay; (2) torn-tail
  guard was dead code (`JsonSerializer.Deserialize` throws rather than returning null) so
  torn tails crashed `Open`. Six-point scratch verification fully green.
- **#12 — IN FLIGHT, two PRs:**
  - PR #23 (`squad/12-default-path`, `1173c75`) — `DurableStore.DefaultStorePath()`,
    explicit Path wins, dir created on first write not on open, InMemory never touches disk.
  - PR #24 (`squad/12-ephemeral-tier-api`, `3269bb3`, based on #23) — the Lead's
    "loud ephemeral tier" decision: `DurableStore.CreateEphemeral()`, `DurableStore.IsPersistent`
    (backed by a new `IDurableStoreProvider.IsPersistent` member), `Create(new DurableStoreOptions())`
    is now persistent at the per-user default path, and `Auto` on a non-existent/empty file
    writes **JSONL** (discriminator = the envelope's top-level `type` property, which a legacy
    snapshot never carries). Independently verified: default path's first line is a JSONL
    envelope; legacy files still reopen under `Auto` without being rewritten; ephemeral writes
    nothing to disk.
  - Both PRs are DRAFT and open. #24 is based on #23 because #23 is not merged yet — **merge
    #23 first, then #24** (or rebase #24 onto dev).
- **Architecture decision recorded** (Lead, merged into `.squad/decisions.md`): keep the
  in-memory backend as an explicitly-named **ephemeral tier**; make the persistent tier the
  default of the options/factory API; migrate the examples to explicit names. Decision #7
  (in-memory stays the default) **stands as a capability conclusion but its rationale is
  superseded** — the API has moved on, so the old constructor's default is a legacy artifact.
  The tier ladder going forward: ephemeral → local-file (JSONL) → shared-database (EF Core, #2).
  Explicitly ruled out: flipping `new DurableStore()` to disk (28 examples would share one
  home-directory file and the suite becomes order-dependent), removing in-memory (it is the
  base class of the provider hierarchy and the parity reference for #2/#4/#5), a runtime
  warning (28 examples would spam stderr), renaming `DurableStoreFormat.InMemory`.
- **Lesson (keep applying):** compile-only gates do NOT cover new providers; every spine item
  needs an end-to-end scratch roundtrip before "green" is believed. Two real bugs in #22 and
  one design gap in #24 were found only that way.

**Spine (serial):** #9 ✅ #10 ✅ #11 ✅ → #12 (PR #23 + #24) → #13.
**Consumers:** #14 (example + README + docs prose + example migration to explicit tiers),
#15 (xUnit facts, target 9/0 — the Lead's tier model adds ~3 more).

**Parallel line (separate initiative, not part of #1):** #16–#19, the sandbox epic.

## Branch model

`dev` is the integration branch and the repo default. Feature work branches from
`origin/dev` and PRs target `dev`. `main` is release-only. `origin` is SSH.
Direct pushes to `dev` are rule-bypassed only for squad-state commits (observed, not a pattern).

## Board

GitHub-copilot-sdk-durable Roadmap (project 5, owner matthewcorven).
#1 In Progress; #9/#10/#11 Done; #12 In Progress; #13–#19 Todo.

## Next action

Merge PR #23, then PR #24 (rebase onto dev first). Then route #13 (legacy single-file JSON
reader) to Core Engineer — it must keep StoreSnapshot types in place, read legacy format on
open into memory, no implicit write-back, and raise an actionable error for a file that is
neither format (the gap #24 left open).