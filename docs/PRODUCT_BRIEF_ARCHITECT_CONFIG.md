# Product brief: architect-configured members, lifecycle and workflow

**Status:** product planning — proposal, not delivered capability, not an approved forecast.
**Owner:** PO (`squad:po`), under parent issue #222.
**Technical design:** `docs/ARCHITECT_CONFIG_DESIGN.md` — introduced by PR #234, design only,
review pending. It is **not yet on `dev`**, so it is referenced by path rather than linked.
**Source baseline:** `dev` at `131649e`; every `file:line` below was read at that commit.

This brief covers the product half of #222: goal, users, boundaries, measurable outcomes, the
current-versus-missing inventory, ordering, ETA honesty and the gates that must stay shut until
evidence exists. It deliberately does **not** restate the technical design; it cites it.

## P1. Product goal and target users

**Goal.** Make a durable, reviewable *team* a first-class product surface rather than a pile of
hand-edited Markdown. A client architect declares its members once — identity, role, capabilities,
ownership, lifecycle, schedules, workflow stages — and the runtime hires, pauses, drains, replaces
and schedules them with auditable state, bounded admission and least-privilege tool access.

**Why now.** The capability the product would sell is already half-present as separate primitives
(`AgentDefinition`, `DurableSchedule`, `DurableTaskDefinition`, hooks, permissions), and the
missing part is the layer that composes them under a versioned declaration. Doing this after the
primitives drift apart costs more than doing it while they are one store and one commit gate.

**Target users, in order:**

| User | What they get | What they must still supply |
|---|---|---|
| Client architect (primary) | One versioned document per member; validated, migrated, compiled to existing types; visible lifecycle state and why a transition was refused. | The role/ownership decisions themselves, and the secrets the config deliberately cannot hold. |
| Operator / support engineer | Durable state after a crash, drain/replace semantics, refusal messages that name the fix, and a store that reads the same after restart. | Host upgrade decisions when a document is newer than the host. |
| Developer on the team (secondary) | `docs/` walkthrough of the worked reference use case and troubleshooting. | — |
| Platform owner (approval only) | A candidate-specific evidence bundle and a fail-closed deployment gate. | Explicit deployment authorization. |

**Non-users.** Anyone expecting a hosted control plane, multi-tenant cloud service, calendar
provider, voice meeting infrastructure or email delivery. Those are separate integrations with
their own permissions and recipients (see P3).

## P2. Measurable outcomes

Outcomes are stated against the **provisional** 18-criterion inventory in
`tools/core-engineers-canvas/scope.json` (six slices × three equal-weight criteria). That
denominator is **not approved**; this brief does not approve it (P6).

| # | Outcome | Provisional criterion | Evidence that would accept it |
|---|---|---|---|
| O1 | An architect configures a member and activates it from the declaration alone. | `lifecycle/configure-members` | Integration test: declaration → compiled `AgentDefinition` → committed `member:` state; refusal cases tested. |
| O2 | Pause/resume/replace never loses or duplicates in-flight ownership. | `lifecycle/pause-resume`, `lifecycle/handoff` | Drain test with work in flight; crash-mid-drain restart test; replacement handoff pointer read back after restart. |
| O3 | Workflow stages and concurrency are enforced by the runtime, not by prose. | `customization/workflow-config`, `operations/shift-order` | Contention test at the cap; unmet-dependency refusal; priority-then-age selection test. |
| O4 | A linked skill/MCP/tool cannot widen authority, and a missing link fails closed. | `customization/linked-tools` | Negative tests: ungranted tool denied; retrieved content cannot widen permissions; export contains no secrets. |
| O5 | Prompt layers and hooks compose deterministically with inspectable provenance. | `customization/prompt-hooks` | Byte-identical repeat composition; conflicting-layer precedence test; untrusted-content injection refused. |
| O6 | An interrupted shift resumes from durable state, and a missed scheduled run is observable. | `lifecycle/handoff`, `operations/notifications` | Restart test mid-shift; missed-run policy observable in the store; overlap behaviour documented and tested. |
| O7 | Deployment refuses without a candidate-specific human approval record. | `evidence/deployment-gate` | Test that the path fails closed; approval tied to SHA + configuration + target. |

A source file existing, a criterion being listed in a dashboard, or a child issue being closed is
**not** any of these outcomes. Only a mapped, executed, independently reviewed test against the
exact committed candidate counts.

## P3. In and out of scope

**In scope (the product this brief plans):**

- Versioned declarative architect configuration with validation, migration and fail-closed refusal
  of unknown versions — design in `ARCHITECT_CONFIG_DESIGN.md` §A3.
- Member lifecycle: hire, activate, pause, resume, drain, replace; durable transitions and
  duplicate-safe claims — design §A5, §A6.
- Optional linked skills/MCP/tools with explicit per-member grants, availability checks, secrets
  separation and safe failure — design §A7, child #226.
- Deliberate system-prompt layering and lifecycle hooks with precedence and provenance —
  child #227.
- Runtime-enforced workflow gates, bounded concurrency and priority-then-age admission —
  child #228.
- Local scheduling and status snapshots with honest offline/missed-run/overlap limits —
  child #229.
- Local evidence, configuration migration/rollback and cross-language parity —
  children #230, #231.
- Worked reference use case, onboarding, troubleshooting and support ownership — child #232.
- Release gate separating local completion, independent review and deployment approval —
  child #233.

**Out of scope (stated so nobody infers it):**

- Any guarantee that a prompt, charter or configuration *enforces* security. The host permission
  handler remains the only authority (`lib/Durable.cs:6191`); the design says this in §A7.1 and
  this brief keeps it.
- Live meetings, voice narration, email delivery, calendar invites and notification receipt. No
  authorized service, recipient list or delivery guarantee exists.
- Provisioning, cloud spend, paid model calls and deployment. None is authorized by this brief.
- Human employment decisions. "Hiring" here means creating a configured service member record.
- A hosted multi-tenant control plane, or member records spanning multiple stores (open question
  Q1, P7).
- Replacing the shared durable layer with a scenario-private fork.

## P4. Current versus missing — evidence

### P4.1 Capability that exists and is reusable (read at `dev` `131649e`)

| Capability | Where | Honest limit |
|---|---|---|
| Agent capability declaration (tools, extensions, permissions, sandbox, model, instructions) | `AgentDefinition` `lib/Durable.cs:3450`, `AgentChoices` `:3342`, `ResolvedAgent` `:3415` | A *capability* declaration. No role, no owner, no lifecycle, no version. |
| Total conversion definition → choices | `AgentDefinition.ToChoices()` `lib/Durable.cs:3504` | Already the single path; nothing is dropped. |
| Validation of names, extensions, tools, sandbox, permissions | `AgentDefinitionValidator` `src/Durable.Runtime/AgentDefinitionValidator.cs:24` | Validates a definition, not an architect layer above it. |
| Durable schedule record with missed-run policy and `Enabled` | `DurableSchedule` `lib/Durable.cs:3567`, `MissedRunPolicy` `:3526` | `Enabled=false` is how a schedule is removed. There is no member state to disable. |
| Schedule read/write and the driving loop | `ScheduleStore` `src/Durable.Runtime/Scheduler.cs:10`, `Scheduler` `:160` | Drives schedules; does not decide who may run. |
| Prompt assembly | `BuildSystemMessage` `lib/Durable.cs:6353`, `:6724` | Composes sections; no layer provenance or declared precedence. |
| Hooks | `SessionHooks` on `AgentChoices` `lib/Durable.cs:3411`, applied at `:6235`–`:6237` | Code-level configuration, explicitly `[JsonIgnore]`; not declarable or persisted. |
| MCP/sandbox switches | `SandboxMcpServers` `lib/Durable.cs:3108`, applied `:5926` | A host switch, not a per-member link with authorization. |
| Task lifecycle and abort | `TaskStatus` `lib/Durable.cs:169`, `DurableTaskRecord` `:188`, `DurableTaskDefinition` `:406` | Task lifecycle, not member lifecycle; stages are written in code. |
| Admission intent/settle pattern | `InputAdmission` `lib/Durable.cs:625`, `SettleAsync` `:4266` | A pattern to model transitions on, not a member claim. |
| Single-runner store guard | `ClaimTaskScheduler` `lib/Durable.cs:2490` | Claims a store-wide slot, not a member shift. |
| Permission authority | `PermissionPolicy` `lib/Durable.cs:2976`, host wiring `:6191` | The authority. Configuration cannot reach it. |

### P4.2 Capability the product does not have

Verified absent: no `schemaVersion` on any persisted type, and no `architect-config` or
`member-state` document kind anywhere in `lib/` or `src/`. Concretely missing:

- No member, role, ownership or routing type; no acyclicity check on "reports to".
- No member lifecycle state, transition record, epoch or duplicate-safe claim.
- No drain, replacement or handoff pointer.
- No declarative workflow stages compiled into `DurableTaskDefinition`.
- No prompt-layer provenance or declared precedence.
- No per-member MCP/skill link with authorization and secrets separation.
- No configuration migration chain or rollback.
- No runtime-enforced admission by priority-then-age.

### P4.3 Configuration already applied to this repository — and why it is not the product

`.squad/` is a real working team configuration, and it is **hand-edited Markdown/JSON**:

| Artifact | Holds today | Lacks |
|---|---|---|
| `.squad/team.md:15`–`:21` | Roster with status strings (`🏗️ Active`, `📋 Silent`, `🔄 Monitor`) | Any state machine, transition record or claim. Status is text a human edits. |
| `.squad/routing.md` | Work-type → owner routing | Schema, version, validation, cycle check. |
| `.squad/agents/{member}/charter.md` | Prose charter | Machine readability, so nothing can validate or compile it. |
| `.squad/canvas-policy.md` | Ownership/refresh/handoff policy | Runtime enforcement; it is a prompt convention. |
| `.squad/config.json` | `{"version": 1}` | A `schemaVersion` contract, migration or rollback. |
| `.squad/ralph-instructions.md:8` | Refers to `squad watch --execute` | That is Squad's own CLI, a separate tool. Its availability is not evidence of this product's bounded admission, claims or gates. |

The reference use case is therefore *credible as a workflow* and *not evidence of product
capability*. This repo's team proves the shape of the need; it does not demonstrate the runtime.

## P5. Delivery graph — verified status

Readiness is taken from the authoritative `issue-work-queue` helper, not from labels or titles.
Children already exist; this shift created none and changed no owner, priority or dependency.

| Issue | Slice | Owner route | Priority | Declared `## Depends On` | Helper readiness |
|---|---|---|---|---|---|
| #223 | S1 design (spike) | `squad:lead` | p1 | none | **available** (open PR #234) |
| #224 | S2 member lifecycle | `squad:core-engineer` | p1 | #223 | blocked |
| #225 | S3 shift handoff / claims | `squad:core-engineer` | p1 | #223 | blocked |
| #226 | S4 linked skills/MCP/tools | `squad:core-engineer` | p1 | #223 | blocked |
| #227 | S5 prompt layers / hooks | `squad:core-engineer` | p1 | #223 | blocked |
| #228 | S6 workflow gates / admission | `squad:core-engineer` | p1 | #223, #224 | blocked |
| #230 | S8 evidence harness | `squad:tester` | p1 | #223 | blocked |
| #229 | S7 scheduling / snapshots | `squad:core-engineer` | p2 | #223, #225 | blocked |
| #231 | S9 parity / migration / rollback | `squad:polyglot-engineer` | p2 | #224, #227 | blocked |
| #232 | S10 reference use case / support | `squad:docs` | p2 | #224, #226, #227, #228, #229 | blocked |
| #233 | S11 release gate | `squad:tester` | p2 | #230 | blocked |

**Ordering rule applied.** Priority first (`p1` before `p2`), then oldest `createdAt` within equal
priority, then issue number. p1 order: #223, #224, #225, #226, #227, #228, #230. p2 order: #229,
#231, #232, #233. Readiness overrides this refinement: only #223 is eligible, and #230 — though p1
— cannot start before it.

**Dependency gap found (planning, not a change).** #233 (release gate) depends only on #230
(evidence harness), and #230 depends only on #223. Nothing in that chain requires the
implementation children #224–#228 to have produced evidence, so as declared the release gate could
be built and tested before the feature it gates exists. That is a *dependency-declaration* gap, not
an implementation bug; it is recorded here for Coordinator/Lead to rule on. This shift did not
edit #233 because the correction is a scope decision, not a proven defect in the issue text.

## P6. Scope and denominator status

`tools/core-engineers-canvas/scope.json` remains `status: "provisional"` with 18 criteria across
six slices. **This brief does not approve that denominator.** Reasons, stated plainly:

1. The acceptance criterion "PO/Architect approve MVP scope" needs the Architect's review of the
   design's boundaries (§A2 reuse claims, §A4 persistence) before PO can freeze a denominator that
   depends on them.
2. Approving 18 equal-weight criteria would imply a forecast this shift cannot support: no
   capacity, environment or cost facts exist (P8).
3. Several criteria are only measurable once #230's evidence harness defines the mapping.

Until then the dashboard's 0% acceptance reading means "no qualifying evidence records found", not
"nothing works" and not "the plan is approved". No cached percentage or timestamp was touched by
this shift.

## P7. PO positions on the design's product questions

`ARCHITECT_CONFIG_DESIGN.md` §A9 lists twelve open questions. These are PO's product-scope
positions; they are **inputs to review, not a sign-off**. §A10's reviewer table stays pending until
a reviewer posts on PR #234 — this shift did not review or approve that PR.

| Q | PO position | Confidence |
|---|---|---|
| Q1 (member record scope) | v1 is **one store = one team**. Cross-store membership implies identity reconciliation and a claim protocol this scope does not fund; treat multi-store as a later decision with its own issue. | medium |
| Q3 (per-member vs per-team document) | Per-member, matching `agent:{name}`. It keeps owner ids flat and makes a single member's declaration reviewable as a diff. | medium |
| Q4 (who may activate) | Activation must name an operator identity in the configuration; "any host that can open the store" is not an acceptable product default for a hire. This is a product requirement, not an implementation detail. | medium-high |
| Q11 (indefinite drain) | No indefinite `draining`. A missing `drainTimeout` must be a validation error or take a documented default, because an unbounded `draining` state is indistinguishable from a stuck member to the operator. | medium-high |
| Q10 (`permissions: ["*"]`) | Refuse in architect configuration, even though `PermissionGrant` can express it. A wildcard makes containment reporting meaningless and defeats per-member least privilege, which is the point of the feature. | medium |

Q2, Q5, Q6, Q7, Q8, Q9, Q12 remain for Core Engineer / Lead / Polyglot Engineer / Rai.

## P8. ETA, assumptions and confidence

**ETA: unknown.** No range is published, because none of its inputs exists:

| Input | Status |
|---|---|
| Architect sizing of S1 (#223) | Pending — PR #234 is design only and unreviewed. |
| Core Engineer sizing of #224–#228 | Unknown. |
| Implementation admission | Coordinator has not admitted any implementation issue; parent #222 caps it at two implementation issues. |
| Capacity / people / hours | Unknown. No capacity data exists in this repository. |
| Environment and dependency prerequisites | Local-only assumption: .NET SDK, Node, python3, authenticated `gh`. Not verified per candidate. |
| Cost | No spend authorized; local-only assumption stands. |
| Schedule/notification guarantees | None. Local jobs need the machine and app to be present. |

**Confidence: low**, and it cannot rise until #223 is reviewed and at least one implementation
issue is sized. Any ETA published before then would be a fabricated date, which the charter
prohibits.

**Assumption to verify first:** that the design's reuse claim (§A2) holds — that the architect
layer compiles onto `AgentDefinition`, `DurableSchedule` and `DurableTaskDefinition` without
changing them. If Core Engineer finds a required change to a persisted type, #231 (migration and
store compatibility) moves from p2 to a blocking prerequisite and the ETA confidence does not rise
at all until a migration story exists.

## P9. Risks and unknowns

| Risk | Impact | Mitigation | Owner |
|---|---|---|---|
| Reuse claim is wrong; a persisted type must change | Store-format contract break across C#/TS/Python; #231 becomes a blocker. | Core Engineer review of §A2/§A4 before any implementation admission. | Core Engineer / Lead |
| Release gate declared on a chain that skips the feature | #233 could "pass" with no feature evidence. | Dependency ruling requested (P5). | Coordinator / Lead |
| Configuration mistaken for authority | A config or retrieved content silently widens access. | Negative tests in #226; §A7 invariant; permission handler untouched. | Core Engineer / Rai |
| Provisional denominator treated as approved | False progress reporting on the canvas. | P6: status stays provisional; no cached value edited. | PO |
| Local scheduling presented as guaranteed | Missed shifts reported as delivered service. | #229 must expose missed-run/offline/overlap; no delivery guarantee claimed. | PO / Core Engineer |
| Secrets leak into portable configuration | Credential exposure in a committed, rewindable document. | #226 export test; Q5 must be answered before implementation. | Core Engineer / user |
| Parity lag between C#/TS/Python | Cross-language divergence in a shared contract. | #231 depends on #224 and #227; Q8 must be answered. | Polyglot Engineer |
| Two-issue implementation cap exhausted on scaffolding | Feature not demonstrable within the approved budget. | Coordinator sequencing; PO keeps the cap visible. | Coordinator / PO |

## P10. Completion, review and deployment are three different gates

| Gate | What satisfies it | Current state |
|---|---|---|
| Local completion | Mapped tests pass locally against a committed candidate SHA; per-E2E screenshots retained; coverage reported. | Not met — no implementation admitted. |
| Independent review / evidence | Tester's criterion-to-test mapping and a recorded independent reviewer sign-off for the exact candidate. | Not met — #230 not started; #223 review pending. |
| Deployment authorization | Explicit human approval naming candidate SHA, configuration and target; tooling fails closed without it. | Not met and not requested. This brief authorizes nothing. |

Parent #222 stays open until every planning deliverable in its body is met, including the
Architect review, the denominator decision and the follow-through record. Child issues existing is
not completion; PR #234 being green is not review.

## P11. Follow-through and next actions

| Action | Owner | Depends on |
|---|---|---|
| Review `ARCHITECT_CONFIG_DESIGN.md` (product scope: §A1, §A5, §A8, §A9 Q1/Q4/Q11) and post the sign-off row on PR #234 | PO | — (next PO shift) |
| Review §A2 reuse claims, §A3 validation, §A4 persistence, §A6 concurrency | Core Engineer | — |
| Rule on the #233 → #230 dependency gap in P5 | Coordinator / Lead | — |
| Admit #223 to Lead and settle the design review gate | Coordinator | PO + Core Engineer review |
| Approve/version the 18-criterion denominator, or amend it | PO + Architect | Design review, #230 mapping |
| Answer Q5 (secrets location), Q7 (migration chain), Q12 (config change while active) | Core Engineer / Lead | — |
| Size #224–#228 and publish the first defensible ETA range | Core Engineer / Lead | Design review |

**Next PO shift priority:** resume #222 (still eligible, p1, oldest eligible PO item). The bounded
deliverable is the PO review record for PR #234 — not new scope.

## P12. Provenance

- Baseline: `dev` at `131649e`.
- Queue facts: `issue-work-queue` helper over `matthewcorven/github-copilot-sdk-durable`, all
  states; no board drift reported.
- Design facts: `docs/ARCHITECT_CONFIG_DESIGN.md` on `squad/223-architect-config-design` (PR #234,
  open, no reviews).
- Source facts: read at `131649e`; no source change was made by this brief.
- Scope facts: `tools/core-engineers-canvas/scope.json`, `status: "provisional"`, unmodified.
