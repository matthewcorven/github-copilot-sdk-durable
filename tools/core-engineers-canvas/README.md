# Core Engineers canvas

A project-owned, loopback-only dashboard rendered by the existing Browser canvas. It is a normal local web application, not a newly registered canvas extension. No third-party runtime or build dependencies are required beyond Node.js, git, python3, authenticated gh, and the repository's installed issue-work-queue helper for an online refresh. The optional reproducible browser test suite uses a pinned development-only Playwright dependency in this directory.

## Ownership and operating cadence

**Accountable operating owner: Commitments Facilitator.** It checks freshness, successful refresh/publication, missing evidence and the configuration-review agenda. **Technical maintainer: Core Engineer.** It owns the local process, restart/runbook, cache/clone, integrations, credentials availability and dependency/test maintenance. **MVP/configuration product owner: PO.** It approves the denominator, slices, priority/ETA assumptions and source inventory. **Technical configuration approver: Lead / Architect**, with Tester approving evidence/report contracts. Coordinator resolves ownership/admission conflicts. These are assigned duties, not proof that an unattended run has executed.

| Mechanism | Current cadence | What it actually does |
|---|---|---|
| Browser polling | every 30 seconds while page is open | Reads the existing local snapshot; it does not fetch GitHub or re-clone source |
| Explicit refresh | Refresh button or `server.mjs --refresh` | Refreshes exact committed-source clone, authoritative helper and GitHub caches |
| PO local job | daily 09:00, 13:00, 17:00 | Instructed through canonical charter to refresh before shift/status handoff; first run not yet verified |
| Facilitator local job | daily 18:00 | Instructed to refresh before daily reporting and verify publication; first run not yet verified |
| Event/commit integration | none installed | Member instructions require refresh after material changes; no automatic post-commit/webhook daemon exists |
| Freshness threshold | one hour | Flags stale source/cache; a flag does not automatically refresh or repair |

The server is a detached local Node process, **not an installed OS service**: no boot/login restart, forever uptime, crash recovery, app-independent scheduler, calendar provider or notification delivery guarantee is configured. Local scheduled work needs the computer/app to be available. The operator must detect and escalate missed runs, expired credentials, stale/uncertain locks, invalid evidence and changed source/configuration. Do not install persistence or expand cloud access merely to hide these limitations.

### Configuration inventory requiring an owner

- **PO + Architect:** approve/version `scope.json` (provisional versus approved status, slice/criterion definitions, source paths, ownership, priorities, evidence requirements and inclusion/exclusion of work). Scope changes need a visible denominator change record and fresh evidence digests.
- **Core Engineer + Coordinator:** canonical local repository/committed ref and clone policy; Node/git/python/gh availability; authenticated repository access and authoritative helper path; loopback process/port/restart and cache locations/permissions; freshness thresholds, process-overlap lock recovery, dependency versions and tested upgrade path. Never put tokens in scope or report files.
- **Tester + Architect:** normalized result/review schema, authentic raw reports, source/artifact hashes, independent reviewer records, local coverage and every-E2E screenshot completeness; retention and redaction rules. The dashboard is not a deployment gate.
- **Facilitator + Coordinator:** enabled job IDs/cadences, local timezone and machine/app availability, successful run/publication checks, last successful refresh/run/report, alert/escalation channel and fallback, meeting participants/agenda and follow-through. Live meetings require actual authorized calendar/voice services and invitees.
- **All owners:** route changed actual source/issue/evidence first, refresh before handoff, and disclose uncommitted source excluded from the clone. A recent cache must never masquerade as a recent test run.

### First ownership/configuration review

**Accountable scheduler/facilitator: Commitments Facilitator.** Proposed first review: **2026-11-11, 09:00–09:30 at UTC−05:00**, aligned with the next configured PO shift. Required roles: Facilitator, Core Engineer, PO and Lead / Architect; Tester joins for acceptance/evidence configuration, Coordinator for decisions. Actual invitees, calendar service and timezone still require confirmation. **This is an unsent proposal, not a booked calendar event or a claim of a live AI voice meeting.** The already-saved PO shift is separate from this review.

Agenda: (1) approve RACI and MVP denominator; (2) validate refresh/source/evidence and credential prerequisites; (3) choose availability/restart and stale/missed-run escalation policy; (4) confirm scheduler/timezone/report-publication checks; (5) assign each unresolved configuration item an owner, due checkpoint and closure evidence. Do not conflate an in-app agent work session with a calendar invitation or live call.

## Run and refresh

From the repository root:

```sh
node tools/core-engineers-canvas/server.mjs --refresh
node tools/core-engineers-canvas/server.mjs
```

The server prints its actual ephemeral loopback URL. Open that URL in the Browser canvas. `--refresh` creates/updates the local read-cache clone, runs the authoritative queue helper first, fetches GitHub issue metadata, and atomically publishes a snapshot. The visible Refresh button performs the same work; polling every 30 seconds reads the cache, not GitHub. A concurrent refresh in the same process joins the existing operation; separate processes use an exclusive cache-refresh lock. A lock left by an abruptly terminated process requires owner verification, not age-based stealing. This cache lock is not a squad work/admission claim.

```sh
node tools/core-engineers-canvas/server.mjs --offline
node --experimental-test-coverage '--test-coverage-exclude=**/*.test.mjs' --test tools/core-engineers-canvas/model.test.mjs
```

For reproducible Chromium browser checks (development dependencies only):

```sh
npm --prefix tools/core-engineers-canvas ci
npm --prefix tools/core-engineers-canvas run test:e2e
# Only if Playwright reports its browser binary missing:
# npm --prefix tools/core-engineers-canvas exec -- playwright install chromium
```

The single browser flow verifies real load/percentages/provenance, actual refresh, source viewing and a narrow canvas. Screenshots are captured for every run, including failures; additional stage screenshots, traces, JSON/HTML reports and raw browser JS coverage are retained under `artifacts/core-engineers-canvas/runs/<run-id>/`. Runs have unique directories; failures are not overwritten by retries. Node coverage reports measure model/server code, not whole-product coverage or every frontend branch. Browser test reports are new evidence for this dashboard, not evidence that all product MVP criteria passed.

Offline refresh updates the local source clone but explicitly marks issue/admission data stale. Online failure preserves dated issue caches with visible errors, never silently substitutes raw labels for authoritative readiness. No cloud model calls, deployments, changes to GitHub issues, or new background schedules occur during refresh.

## What is cloned and measured

- `.scratch/core-engineers-canvas/source/` is an ignored local **detached clone of the repository's current committed HEAD**, with no hardlinks and no checkout hooks. Refresh fetches the exact local SHA; it never checks out or resets the user's source tree. Clone modifications cause a failure instead of a destructive reset.
- `issues.json`, `queue.json`, and `snapshot.json` are ignored read caches with independently recorded timestamps. An issue listing at the 1000-item limit is incomplete and its program percentages are withheld.
- The UI shows the source SHA/branch, dirty working-tree flag, clone path, refresh time, issue fetch/queue times and a SHA256 of `scope.json`. Uncommitted code is **excluded** from source completion/evidence. The scope definition itself is working configuration with a separate digest; it is not misleadingly claimed to have come from the cloned commit.
- Cache freshness expires after one hour. A changed source HEAD or scope definition, missing/failed remote fetch or stale issue/queue timestamp marks the view stale. A recent clone does not mean tests were recently run.

### Three metrics that must not be conflated

1. **MVP evidence-linked acceptance:** accepted explicit criteria / all explicit criteria; each criterion has equal weight. The initial inventory is **provisional, 18 criteria across six slices**, derived from the user requirements and #222. PO must approve/version this denominator before it is treated as the official MVP plan. A 0% reading means no qualifying acceptance records were found, not that no code exists. Missing runtime acceptance remains visible even when source references exist.
2. **Source-reference presence:** existing referenced files / configured references. These are candidate foundation/implementation references, not assertions that those files implement the capability. This is neither feature completion nor code coverage.
3. **Reference-program issue closure:** closed cached band-labelled leaf issues / cached labelled leaf issues, with epics excluded and overall IDs deduplicated. Matrix/assessment issues are included explicitly. Unlabelled/missing work may be absent; closure is not independent acceptance, effort weighting or MVP readiness.

Do not use one metric as the denominator or proof for another. Do not manufacture an ETA, financial number, coverage percentage, live-call status or approval from issue state.

## Member update contract

The user-directed **standing policy for every locally managed canvas** is `.squad/canvas-policy.md`. Its mandatory clause is inlined in all existing member charters, future hiring/support templates and coordinator/default instructions; dispatch templates require the full current charter, full policy and owned-section/source/refresh pointers. This is prompt-content accountability, not a claim to control a host's system/developer/user message roles. Run `node --test tools/core-engineers-canvas/canvas-policy.test.mjs` after new hires or tooling upgrades; `npm run test:unit` includes those checks.

Every specialist and support identity has a `Core Engineers canvas` section in its charter; Coordinator's contract is in `.squad/team.md`. Exact per-member responsibilities live in `scope.json` and are displayed on the dashboard.

- Update actual source/issue/evidence or propose a scope correction in the owned system first. Never hand-edit cached percentages or overwrite another owner's records.
- After an owned material change or evidence publication, and before a shift/status/meeting handoff, refresh the cache with the command above. If the source change is not yet committed, disclose that it is excluded from the clone. Respect the user's commit/review policy: do not auto-commit just to increase progress.
- PO owns the scope/denominator, goal/priority/ETA assumptions and issue links. Lead owns system boundaries and technical dependencies. Engineers own implementation/parity references. Tester owns actual acceptance evidence, local line/branch coverage, screenshots for every E2E test and independent recorded reviews. Docs owns runbook/source links. Facilitator owns daily freshness/presentation checks and unresolved corrective actions. Ralph reports helper-derived queue facts. Scribe only merges accepted decisions. Rai and Fact Checker report scoped findings/claim corrections; they do not edit percentages.
- Missing access, artifacts, source links, dates or review records remain Unknown/open gaps with an accountable owner and next action. If a refresh fails, report the last successful SHA/time and the failure; never stamp old data as newly fetched.
- Scheduled PO and Facilitator jobs read this contract and refresh before publishing. A dashboard is not the deployment gate, an authoritative project-wide lock, live meeting infrastructure, or proof of notification receipt.

## Recording qualifying evidence

There is intentionally **no fabricated accepted-evidence file** in the repository. Tester may publish `docs/core-engineers-evidence.json` only from actual results and independent review:

```json
{
  "schema": "core-engineers-evidence/v1",
  "criteria": {
    "scope-plan": {
      "status": "accepted",
      "sourceDigest": "the criterion sourceDigest displayed by the snapshot",
      "authoredBy": "accountable author",
      "reviewedBy": "different accountable reviewer",
      "evidence": [
        {"kind":"test-report","path":"evidence/actual-result.json","sha256":"actual file SHA256"},
        {"kind":"review","path":"evidence/actual-review.json","sha256":"actual file SHA256"}
      ]
    }
  }
}
```

The normalized result JSON must identify `criterionId`, matching `sourceDigest`, `outcome: "passed"`, and integer `tests` counts: positive `discovered`, `executed === discovered`, `passed === executed`, `failed: 0`, `skipped: 0`. Review JSON identifies the same criterion/digest, `reviewer` matching `reviewedBy`, and `decision: "accepted"`. Author and reviewer must differ.

E2E criteria additionally require unique nonempty `e2eTestIds`, a hashed actual PNG artifact with `kind: "screenshot"` and matching `testId` for **each** E2E ID, measured `coverage.line`/`coverage.branch` (0–100) in the normalized report, and a hashed `kind: "coverage"` artifact. Link raw runner/coverage/trace artifacts alongside the normalized result. All artifacts must be present in the committed read-cache source and match their SHA256. Changed criterion source content invalidates its prior digest. Failed attempts and screenshots remain retained; they do not qualify for passing acceptance.

This validates source/artifact consistency and **recorded** acceptance, not the reviewer's identity cryptographically, the truth of a hand-authored test report, or human deployment authorization. Independent review must establish authenticity. Do not write a normalized passing result unless the underlying real local tests actually passed. Screenshots do not substitute for assertions, and this format is not permission to expose secrets or commit sensitive evidence.

## Loopback boundaries

The server binds only to `127.0.0.1`, uses no external assets, does not serve arbitrary filesystem paths, and only permits source paths already referenced in the snapshot. Path traversal, symlink escapes, `.git`, and environment-file access are rejected; source files are capped at 2 MB and binary evidence is not rendered as text. Refresh requires a same-origin custom header; no cross-origin write grants are exposed. The read-cache is not an API for arbitrary shell commands. Stop only the specific dashboard process when no longer needed.
