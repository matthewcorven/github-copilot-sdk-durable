# Copilot instructions

## Standing canvas accountability — all local tooling

Every involved AI agent is accountable for keeping its owned sections of every canvas created or maintained by authorized local tooling accurate and current. Read `.squad/canvas-policy.md` and the current role charter from canonical TEAM_ROOT before work. Identify affected sections and authoritative sources; after material changes and before handoff/completion, update authorized sources, refresh/rebuild and verify provenance/freshness. Report blocked/stale sections with a factual reason, accountable owner and next action rather than claiming current/complete. Never fabricate metrics, broaden permissions or bypass review. Preserve support-role boundaries, especially Scribe's decision-only role.

When dispatching a member, inline the full current charter and `.squad/canvas-policy.md` into the member's prompt context; a path-only reminder is not sufficient. Check this for new hires and after tooling upgrades. The runtime determines system/developer/user message roles; do not replace its system prompt or weaken safeguards to force an injection role.

## Issue backlog scans

For backlog, triage, queue-status, and "what's next" work, the **`issue-work-queue` helper is authoritative**.
Run it before reporting readiness, ordering, blockers, or board status; its output takes precedence over raw
`gh issue list`.

```bash
python3 ~/.copilot/installed-plugins/corven-agent-tools/github-issues/skills/issue-work-queue/scripts/issue_queue.py \
  --repo matthewcorven/github-copilot-sdk-durable --limit 100 --format text
# add --mode status for the board breakdown and drift diagnostic
```

- Issue bodies' `## Depends On` sections are the source of truth for readiness. Keep them in sync when filing
  or editing issues — a native `blocked by` link alone is invisible to the helper.
- The board is a view and may drift; the helper reports drift with `--project`. The repo declares its board in
  `.github/issue-queue.json`.
- This governs issue readiness, ordering, and board status only. PR scans stay with `gh pr list` / `gh pr view`.

## TEMPORARY: durable-primitive revision is in scope

<!-- TEMP-SCOPE:real-world-scenarios — remove this whole section when the program closes -->

**Status:** active until the real-world scenario program is complete. Delete this section, and
this file if it is otherwise empty, in the closing PR.

Normally the example suite and its scenarios consume `lib/Durable.cs` and the language ports as a
fixed dependency: a gap gets filed as an issue, not patched in passing. **That constraint is
suspended for this program.**

### What changes

When a real-world scenario cannot be expressed, or expresses badly, because of a limitation in the
durable layer, the scenario work MAY revise the layer itself. In scope:

- `lib/Durable.cs` and `lib/Durable.DependencyInjection.cs`
- `lib/ts/durable.ts` and `lib/py/durable.py`
- the store providers under `lib/Durable.EfCore*` and the runtime host under `src/Durable.Runtime`
- `lib/SandboxSupport.cs` and the store-format options

### What is still required

1. **Prove the gap first.** A scenario must fail for a reason, reproduce it, and record that
   failure in the issue or PR. "It would be cleaner this way" is not a gap.
2. **Keep the ports in step.** A semantic change to C# that exists in TypeScript and Python must
   land in all three, or the PR must state which ports are deliberately behind and why. The
   cross-language store wire format is a contract: a change to it needs a migration note and
   evidence that an existing store still reads.
3. **Do not fork the library.** A scenario gets its behaviour from the shared layer. If a scenario
   needs something the layer cannot do, the layer changes — a private copy inside one scenario is
   the failure mode this exception exists to prevent.
4. **Behaviour changes get tests.** Any changed or new public behaviour needs a test that fails
   without it. Existing parity and deterministic suites must stay green; a change that breaks them
   is a regression, not a licence.
5. **Record the reasoning.** A revision is a durable decision: write the gap, the alternatives
   rejected, and the compatibility impact so the next reader knows why the shape changed.

### What this does not permit

- No unrelated refactors, reformatting, or modernization while touching the layer.
- No breaking change to a published surface without a version bump and a changelog entry.
- No weakening the lock, transaction, or crash-safety guarantees to make a scenario simpler. Those
  guarantees are the thing being demonstrated.

### Versioning and release during the program

Iteration and landing use different reference points on purpose:

| Phase | Reference | Why |
| --- | --- | --- |
| **Starting point** | the pinned release (`v0.4.0`) | a baseline that reproduces for anyone who clones |
| **Tight iteration** | `dev` | a scenario surfaces the gap, and the fix has to be reachable immediately |
| **Landing a scenario** | a newly cut release | what the scenario was proven against must be published |

A scenario's feature-level PR does **not** land while its layer changes are unpublished. The
sequence is: revision lands on `dev` → a release PR cuts the tag and package version → the
scenario PR lands pinned to that tag. The release is a prerequisite, not a follow-up, so no
scenario is ever pinned to a version that does not exist.

While a scenario is under active development it may reference `dev` directly. That is a
development convenience only; the PR that merges must pin the release.

### Reporting

Scenario issues state both directions: what the scenario needed and what it would cost in the
layer. A revision that lands also updates the matrix cell that motivated it.
