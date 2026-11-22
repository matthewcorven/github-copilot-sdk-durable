# Standing canvas accountability

This is a user-directed standing team policy for **every canvas created or maintained by authorized local tooling**, not just Core Engineers. It supplements role charters; it grants no extra tool, data, deployment or spending permissions.

## Mandatory standing charter clause

You are accountable for keeping your owned sections of every locally managed canvas accurate and current. Before work, identify impacted sections, authoritative sources and the refresh path. After material changes and before handoff/completion, update authorized sources, refresh/rebuild and verify provenance/freshness. If blocked, report the stale section, factual reason, named owner and next action; never claim it current or complete. Do not invent metrics, broaden access or bypass approval. Preserve role boundaries, including Scribe's decision-only role. Canvas upkeep is part of your standing responsibility, not optional UI housekeeping.

## Ownership and trigger contract

1. **Discover:** At task/shift start, read the relevant canvas configuration and identify the sections your domain owns or affects, the authoritative sources, source/acceptance identity, refresh mechanism, freshness policy and operating owner. An undiscovered owner or source is an explicit gap for Coordinator, not an excuse to ignore the canvas.
2. **Update truth first:** A material code, test/evidence, decision, requirement, priority, ETA, risk, dependency or ownership change must update the permitted source of truth. Never change a cached percentage or timestamp to look current. Keep source presence, test execution, accepted work, forecasts and deployment approval distinct.
3. **Refresh and verify:** Before handing off or calling affected work complete, invoke the authorized refresh/rebuild integration and verify it succeeded, names the intended source/configuration and shows current provenance. Browser polling or a running server is not proof of source refresh. Uncommitted source excluded from a clone must be disclosed.
4. **Delegated operation does not remove accountability:** If a designated operator must refresh a shared canvas, send a precise source/section change handoff and obtain a verified refresh result. Without that result, report the section as pending/stale, with owner and next action; do not silently assume the operator did it.
5. **Bounded exceptions:** If this task cannot affect any canvas section, say why when relevant; do not manufacture an update. A read-only/support member updates only its permitted source or notifies the accountable owner. Scribe remains a narrow accepted-decision merger; it does not become a canvas editor or session logger.
6. **Failure handoff:** Missing access, stale cache, unavailable tools, failed refresh, uncertain lock, unverified evidence or unknown ownership must be disclosed with last successful source/time, factual reason, owner and next action. Keep the stale indicator; do not broaden permissions, restart unrelated processes or invent completion to hide the failure.

## Prompt inclusion — existing and future members

- Repository-wide Copilot instructions carry the standing rule for agents using this repository.
- The Squad coordinator instructions carry the rule directly and require every member dispatch to contain both the full current member charter and this policy, resolved from canonical `TEAM_ROOT`. A link alone or "read it later" is insufficient for dispatch; lack of readable policy is a pre-spawn configuration blocker.
- Each existing charter contains the mandatory standing clause, including support members. Hiring/scaffolding templates contain it for future members, and the coordinator must check it before activating a new member even if a CLI-generated charter omitted it.
- Task agents, persistent children and scheduled roles use the same standing rule. Scheduled jobs must read the current canonical charter/policy before work, not rely on a stale disposable checkout or a prior conversation.
- Return a compact canvas handoff: affected canvas/section, source/configuration identity, refresh outcome and time, or an explicit unchanged/blocked result with owner/next action. Coordinator must not accept "done" for affected sections without that handoff.
- These are prompt-construction requirements, not a claim that repository Markdown can dictate a platform's system-role injection. The runtime chooses system/developer/user roles and retains higher-priority safety rules. Do not replace the runtime's system prompt or weaken safeguards to make this policy appear at a particular role.

## Current local canvas registration

| Canvas | Scope and sources | Accountable operator | Technical maintainer | Domain owners / refresh |
|---|---|---|---|---|
| Core Engineers | `tools/core-engineers-canvas/scope.json`, committed-source clone, authoritative issue queue and qualifying evidence | Commitments Facilitator | Core Engineer | PO: MVP denominator/goals; Lead: architecture; engineers: implementation/parity; Tester: acceptance evidence; Docs: runbooks; support roles: governed findings. Refresh: `node tools/core-engineers-canvas/server.mjs --refresh`; verify snapshot SHA/time/errors. |

Register additional locally managed canvases here or in their explicit project configuration with named section/domain owners, source, refresh integration and freshness policy before relying on their status. Core Engineers details and per-member duties are in its runbook/scope configuration; this policy is deliberately broader than that one dashboard.

## Maintenance

Coordinator owns this standing policy and dispatch compliance under user direction. Commitments Facilitator audits freshness/ownership gaps; Core Engineer maintains refresh integrations; PO/Architect approve scope and technical configuration; each member remains accountable for its owned sections. Upgrades may replace bundled templates: after a Squad/tooling upgrade or a new hire, rerun `node --test tools/core-engineers-canvas/canvas-policy.test.mjs` and reconcile policy inclusion before dispatch.
