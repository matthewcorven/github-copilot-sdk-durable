---
updated_at: 2026-11-03T21:55:00Z
updated_by: coordinator (program planning)
focus_area: real-world scenario program (#142) filed — 40 issues, 7 bands, 70 dependency edges
active_issues: [5, 6, 19, 134, 135, 136, 141, 142, 143, 148, 153, 158, 164, 170, 175]
---


## Real-world scenario program filed (2026-11-03)

#142 is the program epic: seven band epics, 29 work issues, matrix (#180) and TL;DR (#181). Every work issue
carries `model:high` or `model:low` with a rationale; 70 native `blocked by` edges verified.

**Startable now:** A1 #144, B1 #149, C1 #154, D1 #159, E1 #165, F1 #171, G1 #176.
**Riskiest:** Band E (#164) — five `high` issues, MAUI deviation, Kotlin AAR with no .NET binding, and a
reconciliation model with no first-party precedent.
**Expect ties:** Band G (#175) and Band F (#170) baselines already checkpoint/resume. Their READMEs must say so.
**Do not pre-file releases.** Band issues file their own release issue when a gap is proven.

# Now

## Where the team is

`dev` is at `bbb781b` ("fix(examples): name both the language and the example id in completeness
errors (#140)"). **Zero open PRs.** Round 16 landed #137 as `bbb781b`; `build` went green in 14m45s.

**The deterministic gate is green on `dev`** — `./scripts/validate.sh --deterministic` returns
`EXIT=0` with zero `^FAIL` lines. If it is not green for you, read the environment section below
before suspecting a diff.

Open issues: three epics — **#5, #6, #19** — plus epic #19's three remaining sub-issues
**#134, #135, #136** and the capture-reference bug **#141**, all `Todo` on the board.
**#132, #133, and #137 are closed and `Done`.**

Epic #4 (polyglot ports) is **CLOSED** with all five acceptance criteria verified empirically.
Epic #8 (runtime host) is **CLOSED** with all eight criteria verified or deliberately scoped.

## What landed this round

| PR | Issue | Outcome |
|----|-------|---------|
| #123 | — | MERGED `d34d84d` — SDK-bound TS/PY durable language support |
| #126 | #105 | MERGED `352c076` — full 36×2 parity contract, runners, global manifest |
| #119 | #102 | CLOSED as superseded — merge tree byte-identical to `dev` |
| #122 | #103 | CLOSED as superseded — merge tree byte-identical to `dev` |
| #125 | #104 | MERGED `a953d32` — the `advanced-read-tools.test.mjs` guard, +56 lines |
| #130 | #8 | MERGED `7a5e373` — resolved store backend reported on host startup |
| #131 | #129 | MERGED `feafd0a` — widened deterministic gate (parity + language unit suites) |
| #138 | #132 | MERGED `f383c63` — `--test-force-exit` in `lib/ts`'s `test` script |
| #139 | #133 | MERGED `ac4c2e3` — `PortHarness` resolves its own Python interpreter |
| #140 | #137 | MERGED `bbb781b` — completeness errors name both language and example id |

Issues closed: #98, #102, #103, #104, #105, **#129**, **#132**, **#133**, and **epics #4 and #8**.
All six sub-issues of epic #4 and all six of epic #8 are CLOSED. #132 and #133 were filed in round 13,
fixed in round 14, and merged and closed in round 15.

## Epic #4 — closed, five criteria verified

1. **36 examples per language** — counted: TS 36, PY 36, C# 38. TS/PY cover 00–35 exactly.
2. **Test commands pass with the four parity facts** — TS rollback tests at
   `lib/ts/durable.test.ts:596,2008,2238,2519`; PY `test_abort_cascade_commits_default_abort_and_preserves_pending_records`
   at `lib/py/test_sdk.py:3656`. PY unit suite empirically `Ran 72 tests` / `OK`.
3. **Documented per-language run command that exits non-zero on failure** — `README.md:122-123`,
   `:135-138`. Empirically green in both languages: `TypeScript: 15 selected, 0 failures, 4 actual
   C# output comparisons` and the identical Python line.
4. **≥4 examples compared against checked-in expected output** — `examples/expected/` holds 5 files
   (`00-conversation.txt`, `01-documents.txt`, `03-owned-conversations.txt`, `04-watches.txt`,
   `provenance.json`), referenced from `examples/parity.json` `expectedOutput` (lines 15, 74, 192,
   251). **Genuinely enforced**, not merely present: `run_all.mjs:157-170` and `run_all.py:117-129`
   compare normalised stdout and fail on mismatch or on `compared !== required`; `run_all.mjs:95-100`
   gates on `provenance.json` `expectedSha256`; `run_all.mjs:69-70` gates the manifest inventory;
   `tests/parity/runner.test.mjs:22` asserts the expected-ID set.
5. **DESIGN section + README coordinates** — `docs/DESIGN.md:877` `### 3.15.1 Language ports and the
   parity contract`; `README.md:98-99` names `@github/copilot-sdk` and `github-copilot-sdk`.

## Epic #8 — CLOSED, all eight criteria verified

Verified: (1) effective-model run command, (2) `serve --http`/`--rpc` integration tests on an
ephemeral port, (3) `schedule --every 5m` survives restart, (4) missed-run policy, (5) `--dev-tunnel`,
(6) Linux+Windows CI smoke, (7) DESIGN §3.9 + README CLI docs.

**Criterion 8 is a three-clause sentence. All three clauses are now satisfied or deliberately scoped:**

> The host's agent-definition schema accepts a sandbox policy, **a host-started agent reports the
> resolved backend and platform on startup**, and the host's documentation states the interaction
> between "approve all permissions" and "no sandbox".

- Sandbox policy in schema: ✅ `AgentDefinitionValidator.cs:111` → `ValidateSandboxPolicy` at 115.
- Approve-all vs no-sandbox docs: ✅ `README.md:492`, `docs/DESIGN.md:231`, `:369`.
- **Resolved backend on startup: ✅ implemented in PR #130.** `store: jsonl at <path>` or
  `store: in-memory (ephemeral)`, printed after `ClaimOwnership` in all four entry paths and before
  the containment report.
- **Platform on startup: deliberately excluded** — see below.

**The "platform" half is FORBIDDEN by an existing invariant.**
`tests/Durable.Tests/RuntimeHostPortabilityTests.cs:80` `CorePath_HasNoPlatformSpecificApis()`
asserts no `src/Durable.Runtime/*.cs` except `HostShutdown.cs` matches
`\b(OperatingSystem|OSPlatform|RuntimeInformation|PosixSignal|PosixSignalRegistration|DllImport|LibraryImport|Registry|WindowsIdentity)\b|Environment\s*\.\s*(OSVersion|SpecialFolder)|[A-Za-z]:\\`.
That test *is* the enforcement of criterion 6. A literal platform startup line would break
criterion 6. **Do not add platform detection; do not weaken that test.**

**The "resolved backend" half needed a real fix, not just an accessor.** `DurableStore._format` was
assigned `options.Format` **verbatim**, so an `--format auto` store carried the *question* (`Auto`)
rather than the *answer* — any accessor would have been a lie. `ResolveFormat(provider, options)` now
maps the selected provider to the format it actually writes through (`JsonlStoreProvider`→`jsonl`,
`LegacyJsonStoreProvider`→`legacy-json`, `InMemoryStoreProvider`→`in-memory`, caller-supplied
factory→requested format unchanged). The accessor is `internal DurableStoreFormat Format => _format;`
beside the existing `Provider` accessor at `:2336`; `lib/Copilot.Durable.csproj:42` grants
`InternalsVisibleTo("Durable.Runtime")`, so no public API grew.

**Gotcha for any future format-sniffing test:** a `LegacyJson` store writes nothing until its first
commit, so a freshly created legacy path is still empty and `Auto` correctly sniffs it as `Jsonl`.
The test commits a conversation before reopening with `Auto`.

The host's real answer to "what bounds this agent's authority" is the containment report:
`containment: {Sandboxed|PermissionGated|None} for agent '{name}': {reason}`
(`Program.cs:1268-1272`, called from all four store-opening paths at 134, 177, 496, 873).

## The finding that mattered

**#126 subsumed #102/#103/#104.** It ports all 36 examples (00–35) in both languages and owns the
global `examples/parity.json` manifest (`"scope": [0, 35]`), which is exactly what #105 was scoped
to do. After #126 merged, `git merge-tree --write-tree` for #119 and #122 returned a tree
**byte-identical to `origin/dev`** (`7f0fc017ba188b4eff8104e63f4ae750e9f0eb44`) — merging them
would have changed nothing. That tree-hash comparison is the proof; it is why they were closed
rather than merged.

**#126 shipped a real regression.** It changed `resolve(base, path)` → `join(base, path)` in the
`read_file` handlers of `examples/ts/26-plan-mode.ts` and `examples/ts/27-reviewer.ts`. `join`
prepends the working directory to absolute paths, so absolute-path reads broke. #126 had no test
asserting `join`; #125's `advanced-read-tools.test.mjs` is the guard that caught it. Fixed in
`98af351` before merge. The Python ports were already correct (`Path(directory) / params.path`
discards the left operand when the right is absolute).

**The rebase trap.** Every polyglot branch was cut from `915e13a` and carried the shared SDK history
that `d34d84d` squash-merged. `git rebase origin/dev` replays all of it and conflicts on every
`lib/` file. `-X ours` is worse — it keeps non-conflicting branch hunks, producing 4544 spurious
insertions. Correct procedure: `git reset --hard origin/dev` + selective `git cherry-pick` of only
the payload commits, verified by tree-hash equality per top-level directory.

## Corrections to prior handoffs

- **Supplement 002's "stale abort anchor" claim is false.** All 8 mutation anchors in
  `tests/parity/test_mutations.py` resolve to exactly one occurrence. `abortTaskAsync` is defined at
  `lib/ts/durable.ts:2277`; no `abortCascadeAsync` exists. Verified twice (coordinator + Tester);
  `test_mutations.py` passes 2/2. Do not dispatch a fix for this.
- **Tester's "2 failing TS tests" was an environment artifact.** Running
  `DURABLE_RUNTIME_PATH=... npx tsx --test --test-force-exit durable.test.ts` on `dev` gives
  **119/119 pass**. The guard and the test were both introduced by `d34d84d` and no PR touches them.
- **`scripts/validate.sh --deterministic` does not run the parity suite**, `test_mutations.py`, or
  either language unit suite. "Five green gates" and "parity suite red" can coexist. Widening it is
  a real gap that belongs in its own issue.

## Environment notes

- **`lib/py/.venv` is required and was silently missing.** `python examples/run_all.py --deterministic`
  fails all 15 selected examples with `ModuleNotFoundError: No module named 'copilot'` from
  `lib/py/durable.py:38`. The venv had been deleted during an earlier disk-pressure cleanup.
  Recreate with `python -m venv .venv` in `lib/py`, then
  `pip install "github-copilot-sdk==1.0.28"`. **`pytest` is NOT installed there** — run the unit
  suite as `python lib/py/test_sdk.py` (unittest).
- **`examples/ts` needs its own `npm ci`.** `npm run examples:ts` fails with
  `ERR_MODULE_NOT_FOUND: Cannot find package '@github/copilot-sdk' imported from examples/ts/tools.ts`
  unless `examples/ts/node_modules` exists. `lib/ts/node_modules` alone is not enough. This produced
  3 spurious failures (05, 06, 09) that vanished after `npm --prefix examples/ts ci`.
- `tsx --test` never exits without `--test-force-exit`.
- `node --test tests/parity/` fails with MODULE_NOT_FOUND; use `npm run test:parity:ts`. A single
  file works with `node --experimental-transform-types --test <file>`.
- `PortHarness_PhysicalLineEndingsPreserveGeneratedClients` needs `lib/py/.venv/bin` on `PATH`.
- `lib/ts` `npm test` needs `DURABLE_RUNTIME_PATH` set.
- No solution file at the repo root — `dotnet build` alone fails with MSB1003. Name a project.
- Disk pressure is real: `/System/Volumes/Data` hit 100% twice, causing `index.lock` and `MSB3923`
  failures. `uv cache clean` freed 11.6 GiB; `~/.nuget/packages` (13G) is the next lever.
  **Cleanup deleted `lib/py/.venv` once — check it before blaming the code.**
- `gh pr view --json mergeable` returns `UNKNOWN` for ~20–25s after a push.
- Draft PRs cannot be merged — `gh pr ready <n>` first.
- GitHub project automation moves a board item to `Done` when its issue closes; manual
  `item-edit` is usually unnecessary. Item IDs are `PVTI_lAHOAajMkc4BobdZzhE1Z…` — note the `h`;
  better to read `.id` from `gh project item-list 5 --owner matthewcorven --format json`.
- **`dotnet` must resolve to `/opt/homebrew/bin/dotnet` (SDK 10.0.402).** A .NET 11 RC at
  `/usr/local/share/dotnet` can win the PATH; when it does, every net10.0 test host aborts and the
  gate reports **22 failed checks** including **17 C# examples at `exit 150`**. The Homebrew SDK
  was still installed, just unlinked. **Fix: `brew link --overwrite dotnet`.** Read the log's
  actual error text first, then `which -a dotnet` / `dotnet --list-sdks`.
- **A green `build` on a bug-fix PR is not evidence the bug is fixed.** CI runs
  `npm test -- --test-force-exit` (so a `package.json` change is never exercised) and CI's PATH
  happens to contain a working `python3` (so the #133 fix was never exercised). Both PRs would
  have gone green without their fixes. **State a local before/after control in the PR body.**

## Next action

Work epic #19's three remaining sub-issues, which are filed, board-added, and linked (`#19` is
blocked by each of them). **#134 is in flight** — Tester is on it. **#135** and **#136** are
larger. **#141** is an independent, reproducible bug and a good Tester task.

| Sub-issue | Criterion | Gap |
|---|---|---|
| **#134** | 5 (second half) | `SKIP_SANDBOX=1` appears nowhere but `docs/implementation/ISSUE_1_HANDOFF.md:217` |
| **#135** | 2 + 6 | no `policy`-mode expected-output fixtures for 33/34/35; `expectedOutput` is `null` |
| **#136** | 8 + 9 | no advisory host-enforcement CI job, no `continue-on-error`, no `docs/SANDBOX_CI.md` |
| **#137** | 7 | **DONE** `bbb781b` — the gate did not name both; it does now |

**Five of #19's nine criteria were already satisfied** before decomposition — do not re-implement
them. `lib/SandboxSupport.cs` already has `SandboxRunMode` (28), `Resolve` (73), `AssertPolicy`
(111), `AssertDenied` (174), `AssertNoPermissionDenial` (207), `Expand` (230), `PrintList` (236);
`33 34 35` are already in `LIVE_EXAMPLES` (`scripts/validate.sh:31`); `policy_resolved` is already
emitted and asserted; and the "no simulated sandbox" wording criterion already holds.

#5 depends on #3 and #4; #6 depends on #5 and #4.

## Do not

- Do not touch #3 (closed, 2/8 criteria pass, needs human re-scope).
- Do not cut a release — `lib/Copilot.Durable.csproj` is at 0.5.0 and needs user sign-off.
- Do not re-open #119/#122; their content is fully in `dev`.
- Do not re-open #4 or #8; both are closed with per-criterion evidence comments.
- Do not add platform detection to `src/Durable.Runtime/` or weaken
  `RuntimeHostPortabilityTests.CorePath_HasNoPlatformSpecificApis`.
