# Ceremonies

> Team meetings that happen before or after work. Each squad configures their own.

## Scheduled Product and Commitment Shifts

- **PO:** three local daily activations at 09:00, 13:00 and 17:00 (`0 9,13,17 * * *`). Finish the previous shift's priority when still eligible; otherwise choose the next eligible PO-owned issue by priority, then oldest creation time. The authoritative helper still determines readiness. Keep a durable handoff and factual ETA/risk/dependency updates.
- **Commitments Facilitator:** one local daily activation at 18:00 (`0 18 * * *`), producing a high-level snapshot visible to the user in the app and an attached detailed report. Daily reporting supports, but does not book, the requested twice-weekly calls or Sprint Demos.
- **Isolation and overlap:** isolated automation worktrees; canonical team configuration/state resolved from the configured team root. PO must obtain an atomic repository-wide shift claim before mutations; unknown or live ownership is not silently stolen. No unbounded watcher, speculative fan-out or bypass of the two-implementation-issue budget. If claim support is unavailable, report the blocker and remain read-only.
- **Availability:** schedules use the app's local time and require the machine/app to be running. Missed activations, inaccessible sources or publication failures are gaps to report, not fabricated attendance or guaranteed notification delivery. A successful in-app publication does not prove the user has read it.
- **Authorization:** this is the user's requested recurring product-planning/communication work, not approval for spending, deployment, new notification channels or scheduling additional jobs. Live calls and voice/email integrations remain separate prerequisites.
- **Verified saved state (2026-11-11):** local enabled autopilot/worktree job `a4b5da34-fce7-486c-8d67-41a105acde21` (PO priority shifts), cron `0 9,13,17 * * *`; local enabled autopilot/worktree job `8f8bd7c5-bc7b-45c4-a274-4fc2f84e6105` (Daily commitments snapshot), cron `0 18 * * *`. The daily job replaces the prior disabled twice-weekly reporting draft; the request for actual twice-weekly calls is unchanged. No immediate run was requested or claimed. Read back live configuration before relying on these IDs; charter descriptions alone are not runtime enforcement.

## Design Review

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | before |
| **Condition** | multi-agent task involving 2+ agents modifying shared systems |
| **Facilitator** | lead |
| **Participants** | all-relevant |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Agenda:**
1. Review the task and requirements
2. Agree on interfaces and contracts between components
3. Identify risks and edge cases
4. Assign action items

---

## Retrospective

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | after |
| **Condition** | build failure, test failure, or reviewer rejection |
| **Facilitator** | lead |
| **Participants** | all-involved |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Agenda:**
1. What happened? (facts only)
2. Root cause analysis
3. What should change?
4. Action items for next iteration


---

## Commitment Reviews — preparation policy, not scheduled calls

| Field | Value |
|-------|-------|
| **Trigger** | manual, when authorized; no active calendar/timer trigger installed |
| **When** | live reviews requested at least twice weekly; daily 18:00 local report feeds readiness, not a booked call |
| **Facilitator** | commitments-facilitator |
| **Participants** | PO, Coordinator, Ralph, Lead / Architect and relevant accountable owners; actual call recipients unconfirmed |
| **Time budget** | focused, bounded to approved agenda |
| **Enabled** | preparation policy only; recurring calls not confirmed |

**Preparation and close checkpoints (not unsupported active triggers):**
1. **T-1 business day:** once the meeting time, timezone and business-day calendar are confirmed, check readiness and source inventory. Prepare active-quarter notes, highlights/lowlights, milestone commitments and evidence gaps. Otherwise report timing Unknown; do not invent calendar dates.
2. **T-2 hours:** review remaining gaps and rehearse evidence-backed presentations. Critique clarity, source traceability and explicit decisions/asks. Every correction needs owner, due time/timezone, status and evidence; unresolved values stay Unknown.
3. **Meeting close:** coordinate action recording in the approved meeting/commitment artifact, not Scribe histories/logs. Record decisions only through the accepted durable-decision process. Read back unresolved asks; silence is not consent.
4. **Next meeting:** review prior feedback/action closure against evidence, keep overdue items visible and distinguish Unknown, On-Track, At-Risk and Complete. On-Track is a forecast; Complete needs accepted evidence, not attendance or a green test alone.

**Sources and authority:**
- Facilitator monitors canonical Scribe decisions read-only and samples explicitly permitted relevant inputs/decision references. Scribe remains a narrow durable decision merger, not a session logger. Never rewrite its decisions/history/logs or fabricate missing records.
- Occasional bounded semantic search requires an available authorized index of permitted inputs. Disclose unavailability; scoped exact reads are an explicit fallback, not equivalent semantic search. Do not access other agents private histories, secrets, `.env`, personal finance, raw unapproved conversations or external providers.
- Organization-wide completeness requires an authorized organization-source inventory: repository coverage is not organization coverage. Validate CapEx/OpEx source, currency and period; never invent values or accounting classifications. Route uncertainty to the authorized human owner.
- Calendar/voice service, actual recipients and organization-finance sources remain unconfirmed; no calls, invitations or email CC are booked by this policy. The user separately authorized local PO shifts three times daily and facilitator reporting once daily. Automation `8f8bd7c5-bc7b-45c4-a274-4fc2f84e6105` is the facilitator report job; its verified state is recorded below. Inside scheduled runs, do not enable/change schedules or add communication channels.

## Sprint Demos and Live PI Planning — readiness requirements

| Field | Value |
|-------|-------|
| **Trigger** | manual, subject to actual service/recipient authorization |
| **When** | Sprint Demo / PI planning as confirmed by the user; no live call scheduled here |
| **Facilitator** | commitments-facilitator |
| **Participants** | relevant owners and Lead / Architect; user CC required on Sprint Demos once recipients/service are authorized |
| **Enabled** | preparation only; calendar/voice/recipient gaps unresolved |

**Agenda:**
1. Prepare live PI planning with voice-capable presentations and Architect feedback. Track unavailable voice/calendar service as a gap, not a completed presentation.
2. Review active-quarter commitments, highlights/lowlights, evidence and risks within the user mandate. User retains scope, spend and deployment authority.
3. Rehearse and critique presentations; record each corrective-action owner, due time, status and evidence. Verify feedback closure at the next meeting.
4. Preserve independent technical/test review and explicit human evidence/deployment review. Facilitator is not the sole technical/test reviewer and has no product implementation/deployment authority.

## Weekly AI-Member Performance and Milestone/PI Review

| Field | Value |
|-------|-------|
| **Trigger** | manual, when authorized; no recurring runtime trigger installed |
| **When** | weekly measurable feedback; deeper review at milestone / PI boundaries |
| **Facilitator** | commitments-facilitator |
| **Participants** | Coordinator, Lead / Architect and relevant accountable owners |
| **Enabled** | preparation policy only |

**Agenda:**
1. Compare actual commitment timeliness, accepted-evidence completeness, rework and feedback closure against explicit baselines/targets. Include counts, denominators, observation period and source evidence; absent data stays Unknown.
2. Identify evidence-based, bounded improvements. Track each with owner, due time/timezone, status, success measure and evidence; review closure next meeting. Preserve specialist handoffs; do not reassign all work or expand scope speculatively.
3. At milestone/PI boundaries, review deeper architecture/quality trends and remaining risks with Architect feedback. No hiring/spend authority and no unapproved accounting classifications.
4. Keep the deployment gate unchanged: exact committed SHA/configuration/artifact identities, actual local line/branch coverage, criteria-to-E2E mapping and every-E2E pass/fail screenshots (actual CLI terminal), final independent review and explicit human evidence/deployment review. Missing/failed/skipped required evidence is not completion; mocks are not live. Changes invalidate approval; green tests never imply deployment. These are responsibilities, not implemented enforcement infrastructure.

**Prior enforcement text:** the former automatic weekly `.squad/log/` check, `Test-RetroOverdue` invocation and mandatory issue creation are not current triggers or authorization. Do not manufacture session logs or GitHub issues to satisfy meeting readiness. This static change installs no calendar, voice, cloud or runtime gate.
