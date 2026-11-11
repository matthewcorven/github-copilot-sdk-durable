import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseTeam } from './model.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const read = relative => fs.readFile(path.join(root, relative), 'utf8');
const policy = await read('.squad/canvas-policy.md');
const clause = policy.split('## Mandatory standing charter clause\n\n')[1]?.split('\n\n## ')[0].trim();
assert.ok(clause?.startsWith('You are accountable for keeping your owned sections of every locally managed canvas'));
const team = await read('.squad/team.md');
const members = parseTeam(team, {}).filter(member => member.charterPath);

test('canonical standing rule covers all local canvases with safe blocked handoffs', () => {
  assert.match(policy, /every canvas created or maintained by authorized local tooling/);
  assert.match(clause, /before handoff\/completion/);
  assert.match(clause, /factual reason, named owner and next action/);
  assert.match(clause, /Do not invent metrics, broaden access or bypass approval/);
  assert.match(policy, /runtime chooses system\/developer\/user roles/);
  assert.match(policy, /Scribe remains a narrow accepted-decision merger/);
});

for (const member of members) {
  test(`${member.name} charter carries the standing rule inline, not just a link`, async () => {
    const charter = await read(member.charterPath);
    assert.ok(charter.includes(clause), `${member.name} omitted/changed the mandatory clause`);
    assert.equal(charter.split('## Standing canvas accountability').length - 1, 1);
    assert.match(charter, /\.squad\/canvas-policy\.md/);
    assert.match(charter, /## Core Engineers canvas/);
  });
}

for (const template of ['charter.md', 'ralph-charter.md', 'scribe-charter.md', 'rai-charter.md', 'fact-checker-charter.md']) {
  test(`future member template ${template} carries the same rule`, async () => {
    const content = await read(`.squad/templates/${template}`);
    assert.ok(content.includes(clause));
    assert.match(content, /\.squad\/canvas-policy\.md/);
  });
}

test('coordinator and repository instructions require policy inclusion before dispatch', async () => {
  const coordinator = await read('.github/agents/squad.agent.md');
  const repository = await read('.github/copilot-instructions.md');
  assert.ok(coordinator.includes(clause));
  assert.match(repository, /inline the full current charter and `\.squad\/canvas-policy\.md`/);
  assert.match(team, /Every member charter contains the mandatory clause/);
  assert.match(await read('.squad/routing.md'), /Standing Canvas Dispatch Gate/);
});

test('normal and Scribe dispatch templates explicitly inline current policy and owned sections', async () => {
  const coordinator = await read('.github/agents/squad.agent.md');
  const normal = coordinator.split('**Full Spawn Template**')[1]?.split('**Scribe Spawn Template**')[0];
  const scribe = coordinator.split('**Scribe Spawn Template**')[1]?.split('### ❌')[0];
  for (const content of [normal,scribe]) {
    assert.ok(content);
    assert.match(content, /YOUR CURRENT CHARTER:/);
    assert.match(content, /STANDING CANVAS POLICY:/);
    assert.match(content, /inline actual \.squad\/canvas-policy\.md contents from canonical TEAM_ROOT/);
    assert.match(content, /OWNED\/IMPACTED CANVAS SECTIONS:/);
  }
  const reference = await read('.squad/templates/spawn-reference.md');
  assert.match(reference, /STANDING CANVAS POLICY:/);
  assert.match(reference, /paste actual current contents of \.squad\/canvas-policy\.md/);
  assert.match(reference, /OWNED\/IMPACTED CANVAS SECTIONS:/);
});

test('Scribe stays decision-only and its canvas duty is not new logging authority', async () => {
  for (const file of ['.squad/agents/scribe/charter.md','.squad/templates/scribe-charter.md']) {
    const content = await read(file);
    assert.match(content, /Scribe's decision-only role/);
    assert.match(content, /does not create specialist histories, orchestration logs, session logs/);
  }
});
