# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Product goals, roadmap, scope breakdown, priority, ETA/risk and dependency coordination | PO | Developer experience, implementation runway/support, #222 and priority-then-age PO shifts |
| Architecture priorities within approved scope, triage, cross-language contract | Lead | Existing Lead / Architect; builder API shape, parity rules, PI feedback |
| `lib/Durable.cs`, extension library, checkpoint/fork hooks; Aspire/emulator/test-dependency orchestration | Core Engineer | Builder extension, client pipeline binding; serialize scarce emulators |
| TS/Python/Go/Rust/Java examples and bindings | Polyglot Engineer | Port example 02-forks to Go |
| xUnit, validate.sh, parity/durability, actual local coverage and E2E evidence completeness | Tester | Line/branch coverage, criteria mapping, every-E2E screenshots including actual CLI terminal |
| README, docs/, guides, audits | Docs | Multi-language README section |
| Commitment register, readiness, presentation critique, meeting preparation and feedback closure | Commitments Facilitator | Read-only permitted Scribe evidence discovery, owner/due time/status/evidence, weekly measurable improvement |

Fact Checker and Rai remain built-in support reviewers, not specialist routing destinations:
request claim/version/URL verification and pre-mortems from Fact Checker, and content-safety
or responsible-AI review from Rai when relevant. They do not acquire product implementation ownership.

Specialist routes must resolve to active persistent entries in the casting registry.
Scribe, Ralph, Rai and Fact Checker are non-registry support identities; their support routes do not make them specialist registry entries. Coordinator + Ralph remain the coordination core. Lead retains persistent key `lead` and first-column roster name Lead; Lead / Architect is its role, not another member. Scribe runs only for accepted durable decision merging, never session logging.

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, assign `squad:{member}` label | Lead |
| `squad:{name}` | Pick up issue and complete the work | Named member |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, the **Lead** triages it — analyzing content, assigning the right `squad:{member}` label, and commenting with triage notes.
2. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
3. Members can reassign by removing their label and adding another member's label.
4. The `squad` label is the "inbox" — untriaged issues waiting for Lead review.

### PO Shift Selection

PO resumes its previous unfinished priority when still eligible. Otherwise, after the helper establishes readiness, select among eligible PO-owned issues by explicit priority (`priority:p0`, `priority:p1`, `priority:p2`, then unprioritized), then oldest creation time, then issue number. Do not substitute this ordering for dependency checks or claim another owner's work. Record factual blockers/reprioritization before moving on. Use an atomic repository-wide shift claim; unavailable claim/state facilities mean read-only planning, not unsafe duplicate dispatch. Coordinator still owns implementation admission.

### Repository Queue Precedence

Use the `github-issues` skill when available. The repository rule takes precedence over generic Squad board/native-link guidance. Run the `issue-work-queue` helper **first** for backlog, triage, readiness, ordering, blockers and board-status reports:

```bash
python3 ~/.copilot/installed-plugins/corven-agent-tools/github-issues/skills/issue-work-queue/scripts/issue_queue.py \
  --repo matthewcorven/github-copilot-sdk-durable --limit 100 --format text
# add --mode status for the board breakdown and drift diagnostic
```

Issue-body `## Depends On` / `## Blocked By` govern readiness; keep them synchronized. The board is a projection that may drift. Native links alone do not establish helper readiness. Targeted issue/PR reads are permitted; PR scans remain `gh pr list` / `gh pr view`. Unavailable helper or unresolved dependency evidence means Unknown, not admission to start.

## Rules

1. **Bounded ownership:** choose one accountable specialist per outcome and one engineer per implementation issue. Default maximum **two active implementation issues**; queue additional implementation work. No speculative downstream work or all-actionable simultaneous fan-out.
2. **Shared resources:** serialize shared store/wire-contract changes and scarce emulators under Lead/Core Engineer coordination, even within the two-issue budget. A `squad:{member}` label routes ownership but does not bypass readiness/admission or user authorization.
3. **Watcher boundary:** instruction files cannot override watcher parallelism. Installed v1.1.0 help exposes `--max-concurrent N` (default 1) and timeout; the supported bounded invocation is `squad watch --execute --max-concurrent 2`. **Do not run it now.** Help proves flag availability, not authoritative-helper readiness, safe claims, global/shared-resource serialization or independent reviewer limits. Verify these boundaries before future authorized execution; policy is not proof of runtime enforcement.
4. **Decision-only Scribe:** run Scribe only when accepted durable decisions require merging. Facilitator reads permitted canonical output/references without rewriting decisions, histories or logs; exact reads are an explicitly disclosed fallback when an authorized semantic index is unavailable, not equivalent semantic search.
5. **Preserve handoffs:** quick facts may be answered directly; otherwise choose the primary-domain owner, with only bounded collaboration needed for the requested outcome. Existing specialists retain their domains. Facilitator coordinates presentation/meeting readiness, not product implementation or sole technical/test review.
6. **Evidence acceptance:** Tester owns actual local line/branch coverage, criteria-to-E2E mapping and completeness. Require exact committed SHA/configuration/artifact identities and screenshots of every E2E pass/fail, including actual terminal for CLI cases. Missing/failed/skipped required evidence is not completion; never call mocks live.
7. **Fail-closed release:** final independent review and explicit human evidence/deployment review are required. Changes invalidate approval; green tests never imply deployment. The user retains scope, spending and deployment authority. No enforcement infrastructure is claimed implemented here.
8. **Meeting readiness:** follow `ceremonies.md` for preparation-only cadence and evidence-based, bounded improvements. Unknown remains distinct from On-Track/At-Risk/Complete; organization-wide claims require an authorized source inventory. No calendar/voice/CC/automation authority follows from this routing table.
