# Squad Team

> github-copilot-sdk-durable

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| PO | Product Owner | .squad/agents/po/charter.md | 🎯 Active |
| Lead | Lead / Architect | .squad/agents/lead/charter.md | 🏗️ Active |
| Core Engineer | Core Engineer | .squad/agents/core-engineer/charter.md | 🔧 Active |
| Polyglot Engineer | Polyglot Engineer | .squad/agents/polyglot-engineer/charter.md | 🌐 Active |
| Tester | Tester | .squad/agents/tester/charter.md | 🧪 Active |
| Docs | Docs | .squad/agents/docs/charter.md | 📝 Active |
| Commitments Facilitator | Commitment coordination, presentation feedback and meeting readiness | .squad/agents/commitments-facilitator/charter.md | 👤 Active |
| Scribe | Scribe | .squad/agents/scribe/charter.md | 📋 Silent |
| Ralph | Work Monitor | .squad/agents/ralph/charter.md | 🔄 Monitor |
| Rai | RAI Reviewer | .squad/agents/Rai/charter.md | 🛡️ RAI |
| Fact Checker | Fact Checker | .squad/agents/fact-checker/charter.md | 🔍 Verifier |

## Branch Model

| Branch | Purpose | Work lands here? |
|--------|---------|------------------|
| `main` | Release branch — released/tagged code only | Only via promotion from `dev` |
| `dev` | Integration branch — all feature work lands here | Yes — every `squad/{issue}-{slug}` PR targets `dev` |

Feature branches are created from `origin/dev` and follow the `squad/{issue-number}-{kebab-case-slug}` convention. Release tags are cut after `dev` is promoted forward to `main`.

## Issue Source

**Repository:** matthewcorven/github-copilot-sdk-durable
**Connected:** 2026-10-23
**Platform:** GitHub
**Filters:**
- Labels: `squad`, `squad:{member}`
- Project: `github-copilot-sdk-durable Roadmap` (project number 5)

**Repository queue precedence (mandatory):**
- Run the `issue-work-queue` helper first for backlog, triage, readiness, ordering, blockers and board-status reports:

```bash
python3 ~/.copilot/installed-plugins/corven-agent-tools/github-issues/skills/issue-work-queue/scripts/issue_queue.py \
  --repo matthewcorven/github-copilot-sdk-durable --limit 100 --format text
# add --mode status for the board breakdown and drift diagnostic
```

- Issue-body `## Depends On` / `## Blocked By` sections govern readiness. Keep them synchronized; native links alone do not establish readiness for the helper.
- The project board is a projection and may drift, not an overriding source. Targeted issue/PR reads are permitted; PR scans use `gh pr list` / `gh pr view`.
- Use the `github-issues` skill when available for issue work; if the helper is unavailable or dependencies are ambiguous, report Unknown and do not infer readiness from board/native links.

## Standing accountability for every locally managed canvas

The user requires every involved AI agent to own the accuracy and freshness of its canvas sections as part of its standing role, not optional UI housekeeping. Canonical policy: `.squad/canvas-policy.md`. Every member charter contains the mandatory clause; Coordinator must inline the full current charter and policy at every dispatch, identify affected sections/source/refresh integration, and verify the freshness/provenance handoff before accepting affected work as complete. Scheduled roles and new hires follow the same requirement. Missing permissions/tooling/ownership means an explicit stale/blocked handoff with owner and next action, never fabricated completion. The runtime controls prompt message roles; this repository does not override platform safeguards.

## Core Engineers Canvas — all-member upkeep

Project dashboard: `tools/core-engineers-canvas/README.md`. Accountable operating owner: Commitments Facilitator; technical maintainer: Core Engineer; MVP scope/configuration product owner: PO; technical configuration approver: Lead / Architect, with Tester owning evidence contracts. First configuration-review proposal and scheduling prerequisites are recorded in that runbook; no calendar booking is implied. Explicit ownership/slice/duty configuration: `tools/core-engineers-canvas/scope.json`. Every member follows its charter's canvas duty. Coordinator reconciles cross-slice ownership, blockers and admission budget before dispatch and handoff. Refresh the detached committed-source clone and authoritative queue before user-facing status/meeting snapshots with `node tools/core-engineers-canvas/server.mjs --refresh`; disclose failures, stale data and uncommitted source excluded from that clone. Never hand-edit cached percentages. PO must approve/version the provisional MVP denominator; Tester/independent review supply genuine acceptance evidence. Source presence, reference-program issue closure, accepted criteria, code coverage and deployment authorization are distinct facts. No cache or dashboard grants execution/deployment permission.

## Operating Responsibilities and Limits

- Coordinator + Ralph remain the coordination core. PO owns product goals, the delivery path, priority, ETA/risk communication and collaboration within the user's mandate. Lead (persistent key `lead`) remains the Architect and technical authority; the user retains scope, spend and deployment authority.
- Productize this team exchange as a featured service use case: client-architect-configured member hiring/pausing, workflow customization, optional linked skills/MCPs/tools and deliberate system-prompt arrangement/hooks. PO owns scope breakdown in #222; repository roster changes alone do not implement that product capability.
- Default maximum: **two active implementation issues**, one accountable engineer per issue. Serialize shared store/wire-contract changes and scarce emulator use; readiness does not imply admission. No speculative downstream fan-out.
- Charter policy is not proof of runtime enforcement. Installed CLI v1.1.0 help exposes `--max-concurrent N` (default 1) and timeout; the supported bounded invocation is `squad watch --execute --max-concurrent 2`. **Do not run it now:** help proves flag availability, not authoritative-helper readiness, safe claims, global/shared-resource serialization or independent reviewer limits. Verify admission and those boundaries before any future authorized execution; instruction files cannot override watcher parallelism.
- Core Engineer owns Aspire, emulator and test-dependency orchestration; Tester owns actual local line/branch coverage, criteria-to-E2E mapping and evidence completeness. Existing language, documentation and review ownership is preserved.
- Commitments Facilitator owns readiness and commitment-register coordination, presentation critique and next-meeting feedback closure; Scribe remains a narrow durable decision merger, not a session logger. Built-ins Scribe, Ralph, Rai and Fact Checker remain non-registry support identities.
- Evidence acceptance requires exact committed SHA, configuration and artifact identities, actual local line/branch coverage and criteria-to-E2E mapping; capture screenshots of **every E2E pass and fail**, including the actual terminal for CLI cases. Missing, failed or skipped required evidence is not completion; mocks must never be described as live.
- Final independent technical/test review and explicit human evidence/deployment review are fail-closed gates. No deployment approval is inferred from green tests. Changes to the reviewed SHA/configuration/artifacts invalidate approval and require renewed review. This assigns operating responsibility; it does not claim enforcement infrastructure exists.
- Recurring commitment reviews remain requested at least twice weekly, with weekly measurable AI-member performance feedback and milestone/PI deeper review. PO is scheduled at 09:00/13:00/17:00 local daily; Commitments Facilitator is scheduled daily at 18:00 local to publish a high-level user snapshot in the app. These local activations require the machine/app to be available and are not confirmed live calls, guaranteed notifications or email CC. See `ceremonies.md` for scheduling state and overlap rules; calendar/voice/recipients and organization-finance sources remain unconfirmed.

## Project Context

- **Project:** github-copilot-sdk-durable
- **Created:** 2026-10-23
- **User:** Copilot App
- **Stack:** C# (.NET 10 file-based apps), GitHub Copilot SDK; xUnit tests; `scripts/validate.sh`
- **Built so far:** `lib/Durable.cs`, 31 C# examples, xUnit suite, docs/ (design, audits, SDK notes)
- **Initiatives:** (1) Examples in TypeScript, Python, Go, Rust, Java. (2) Unified extension library: a single builder extension binding checkpointing and fork-isolation hooks into the Copilot client pipeline.
