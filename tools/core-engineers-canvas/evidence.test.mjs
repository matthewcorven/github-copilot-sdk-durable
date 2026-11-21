import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { buildSnapshot } from './model.mjs';
import { acceptedCriterion, candidateFromManifest, mappingCoverage, mappingFor, resultProvenance, sha256, EVIDENCE_SCHEMA, MAP_SCHEMA } from './evidence.mjs';
import { buildEvidenceFixture, FIXTURE_SHA, FIXTURE_NOW } from './evidence.fixture.mjs';

const scope = JSON.parse(await fs.readFile(new URL('./scope.json', import.meta.url), 'utf8'));
const manifestOf = fixture => JSON.parse(JSON.stringify({
  schema: EVIDENCE_SCHEMA,
  candidate: { sha: FIXTURE_SHA, generatedAt: FIXTURE_NOW, mode: 'local' },
  criteria: fixture.recordFor ? { [fixture.recordFor]: fixture.record } : {}
}));
async function snapshotFor(fixture, overrides = {}) {
  return buildSnapshot({ root: fixture.root, scope, meta: { generatedAt: FIXTURE_NOW, sourceSha: FIXTURE_SHA }, issues: null, queue: null, evidenceRoot: fixture.root, probe: fixture.probe, now: Date.parse(FIXTURE_NOW), ...overrides });
}
async function recordForFixture(fixture) {
  const data = await fs.readFile(path.join(fixture.root, scope.criterionMap), 'utf8').catch(() => null);
  const map = data ? JSON.parse(data) : { schema: MAP_SCHEMA, criteria: {} };
  const manifest = manifestOf({ recordFor: fixture.recordFor ?? fixture.criterion.id, record: fixture.record });
  return { map, manifest, record: manifest.criteria[fixture.recordFor ?? fixture.criterion.id] };
}

test('the manifest requires a full SHA, a time and a real run mode', () => {
  assert.equal(candidateFromManifest(null).ok, false);
  for (const candidate of [{ generatedAt: FIXTURE_NOW, mode: 'local' }, { sha: 'abc', generatedAt: FIXTURE_NOW, mode: 'local' }, { sha: FIXTURE_SHA, mode: 'local' }, { sha: FIXTURE_SHA, generatedAt: 'not-a-time', mode: 'local' }, { sha: FIXTURE_SHA, generatedAt: FIXTURE_NOW, mode: 'live' }]) {
    const result = candidateFromManifest({ schema: EVIDENCE_SCHEMA, candidate, criteria: {} });
    assert.equal(result.ok, false);
    assert.match(result.reason, /candidate|generatedAt|mode/);
  }
  assert.equal(candidateFromManifest({ schema: 'other/v1', candidate: { sha: FIXTURE_SHA, generatedAt: FIXTURE_NOW, mode: 'local' }, criteria: {} }).ok, false);
});

test('mock mode never credits acceptance even when every artifact matches', async t => {
  const fixture = await buildEvidenceFixture(t, { recordFor: 'durable-recovery', mode: 'mock' });
  const data = await snapshotFor(fixture);
  assert.equal(data.mvp.accepted, 0);
  assert.match(data.mvp.slices.flatMap(s => s.criteria).find(c => c.id === 'durable-recovery').reason, /simulated/);
});

test('local mode with matching artifacts does credit the criterion', async t => {
  const fixture = await buildEvidenceFixture(t, { recordFor: 'durable-recovery', mapFor: 'durable-recovery' });
  const data = await snapshotFor(fixture);
  const criterion = data.mvp.slices.flatMap(s => s.criteria).find(c => c.id === 'durable-recovery');
  assert.equal(criterion.complete, true, criterion.reason);
  assert.deepEqual(criterion.coverage, { line: 88, branch: 71 });
  assert.equal(data.mvp.accepted, 1);
  assert.equal(data.mvp.evidence.state, 'current');
});

test('evidence older than the freshness limit is stale and not credited', async t => {
  const completedAt = '2026-01-01T00:00:00.000Z';
  const fixture = await buildEvidenceFixture(t, { recordFor: 'durable-recovery', mapFor: 'durable-recovery', completedAt });
  const data = await snapshotFor(fixture);
  const criterion = data.mvp.slices.flatMap(s => s.criteria).find(c => c.id === 'durable-recovery');
  assert.equal(criterion.complete, false);
  assert.match(criterion.reason, /freshness limit/);
});

test('a normalized result must name the pinned candidate and its mode', async t => {
  const fixture = await buildEvidenceFixture(t, { recordFor: 'durable-recovery', mapFor: 'durable-recovery' });
  const { manifest } = await recordForFixture(fixture);
  const base = { candidateSha: FIXTURE_SHA, completedAt: FIXTURE_NOW, mode: 'local' };
  const candidate = { sha: FIXTURE_SHA, generatedAt: FIXTURE_NOW, mode: 'local' };
  assert.equal(resultProvenance({ report: { ...base }, candidate, now: Date.parse(FIXTURE_NOW) }).ok, true);
  assert.match(resultProvenance({ report: { ...base, candidateSha: 'd'.repeat(40) }, candidate, now: Date.parse(FIXTURE_NOW) }).reason, /stale evidence/);
  assert.match(resultProvenance({ report: { ...base, completedAt: undefined }, candidate, now: Date.parse(FIXTURE_NOW) }).reason, /completedAt/);
  assert.match(resultProvenance({ report: { ...base, completedAt: '2030-01-01T00:00:00.000Z' }, candidate, now: Date.parse(FIXTURE_NOW) }).reason, /future/);
  assert.match(resultProvenance({ report: { ...base, mode: 'mock' }, candidate: { ...candidate, mode: 'mock' }, now: Date.parse(FIXTURE_NOW) }).reason, /simulated/);
  assert.match(resultProvenance({ report: { ...base, mode: 'ci' }, candidate, now: Date.parse(FIXTURE_NOW) }).reason, /disagrees/);
  assert.ok(manifest.schema === EVIDENCE_SCHEMA);
});

test('a tampered screenshot is rejected', async t => {
  const fixture = await buildEvidenceFixture(t, { recordFor: 'durable-recovery', mapFor: 'durable-recovery' });
  const { map, manifest, record } = await recordForFixture(fixture);
  const criterion = fixture.criterion;
  const source = fixture.source;
  const screenshot = record.evidence.find(item => item.kind === 'screenshot');
  await fs.writeFile(path.join(fixture.root, screenshot.path), Buffer.from('not a png, but long enough to pass a length check'));
  const acceptance = await acceptedCriterion({ criterion, source, record, manifest, map, probe: fixture.probe, now: Date.parse(FIXTURE_NOW) });
  assert.equal(acceptance.complete, false);
  assert.match(acceptance.reason, /does not match its recorded SHA256/);
});

test('a CLI E2E test needs a terminal capture, not only an application screenshot', async t => {
  const fixture = await buildEvidenceFixture(t, { recordFor: 'durable-recovery', mapFor: 'durable-recovery' });
  const { map, manifest, record } = await recordForFixture(fixture);
  map.criteria['durable-recovery'].tests[0].kind = 'cli';
  record.evidence.find(item => item.kind === 'screenshot').capture = 'application';
  const acceptance = await acceptedCriterion({ criterion: fixture.criterion, source: fixture.source, record, manifest, map, probe: fixture.probe, now: Date.parse(FIXTURE_NOW) });
  assert.equal(acceptance.complete, false);
  assert.match(acceptance.reason, /requires a captured terminal screenshot/);
});

test('a record whose map states a gap is not accepted even when artifacts match', async t => {
  const fixture = await buildEvidenceFixture(t, { recordFor: 'durable-recovery', mapFor: 'durable-recovery' });
  const { map, manifest, record } = await recordForFixture(fixture);
  map.criteria['durable-recovery'] = { sourceDigest: fixture.source.sourceDigest, title: fixture.criterion.title, requiresE2E: true, gap: 'No executable E2E harness yet.', tests: [] };
  const acceptance = await acceptedCriterion({ criterion: fixture.criterion, source: fixture.source, record, manifest, map, probe: fixture.probe, now: Date.parse(FIXTURE_NOW) });
  assert.equal(acceptance.complete, false);
  assert.match(acceptance.reason, /Explicit gap recorded: No executable E2E harness yet/);

  const unaccounted = await buildEvidenceFixture(t, { recordFor: 'durable-recovery' });
  const cleared = await recordForFixture(unaccounted);
  delete cleared.map.criteria['durable-recovery'].gap;
  const missing = await acceptedCriterion({ criterion: unaccounted.criterion, source: unaccounted.source, record: cleared.record, manifest: cleared.manifest, map: cleared.map, probe: unaccounted.probe, now: Date.parse(FIXTURE_NOW) });
  assert.equal(missing.complete, false);
  assert.match(missing.reason, /unaccounted/);
});

test('coverage numbers must match the raw coverage artifact', async t => {
  const fixture = await buildEvidenceFixture(t, { recordFor: 'durable-recovery', mapFor: 'durable-recovery', coverage: { line: 88, branch: 71 } });
  const { map, manifest, record } = await recordForFixture(fixture);
  const coverageArtifact = JSON.parse(await fs.readFile(path.join(fixture.root, 'evidence/coverage.json'), 'utf8'));
  coverageArtifact.branchPercent = 12;
  await fs.writeFile(path.join(fixture.root, 'evidence/coverage.json'), JSON.stringify(coverageArtifact));
  record.evidence.find(item => item.kind === 'coverage').sha256 = sha256(await fs.readFile(path.join(fixture.root, 'evidence/coverage.json')));
  const acceptance = await acceptedCriterion({ criterion: fixture.criterion, source: fixture.source, record, manifest, map, probe: fixture.probe, now: Date.parse(FIXTURE_NOW) });
  assert.equal(acceptance.complete, false);
  assert.match(acceptance.reason, /do not match the normalized result/);
});

test('a stale mapping cannot silently survive a criterion change', async t => {
  const fixture = await buildEvidenceFixture(t, { mapFor: 'durable-recovery' });
  const map = JSON.parse(await fs.readFile(path.join(fixture.root, scope.criterionMap), 'utf8'));
  await fs.writeFile(path.join(fixture.root, fixture.criterion.sourcePaths[0]), '// changed after mapping');
  const changed = await import('./model.mjs').then(module => module.criterionSource(fixture.root, fixture.criterion));
  const data = await buildSnapshot({ root: fixture.root, scope, meta: { generatedAt: FIXTURE_NOW, sourceSha: FIXTURE_SHA }, issues: null, queue: null, evidenceRoot: fixture.root, probe: fixture.probe, now: Date.parse(FIXTURE_NOW) });
  const mapping = mappingFor({ map, criterion: fixture.criterion, source: changed });
  assert.equal(mapping.ok, false);
  assert.match(mapping.reason, /stale/);
  const row = data.mvp.mapping.rows.find(item => item.id === 'durable-recovery');
  assert.equal(row.stale, true);
  assert.ok(data.mvp.mapping.stale >= 1);
});

test('mapping coverage counts gaps and unaccounted criteria separately from mapped ones', () => {
  const criterion = { id: 'x', title: 'x', requiresE2E: true, sourcePaths: ['a.cs'] };
  const testScope = { slices: [{ criteria: [criterion] }] };
  const sources = new Map([['x', { sourceDigest: 'digest', present: ['a.cs'] }]]);
  assert.equal(mappingCoverage({ scope: testScope, map: null, sources }).unaccounted, 1);
  assert.equal(mappingCoverage({ scope: testScope, map: { schema: MAP_SCHEMA, criteria: { x: { sourceDigest: 'digest', gap: 'not written' } } }, sources }).complete, true);
  assert.equal(mappingCoverage({ scope: testScope, map: { schema: MAP_SCHEMA, criteria: { x: { sourceDigest: 'digest', tests: [{ id: 't', kind: 'e2e', command: 'npm test' }] } } }, sources }).mapped, 1);
});

test('an evidence record for an out-of-scope criterion is reported, not credited', async t => {
  const fixture = await buildEvidenceFixture(t, { recordFor: null });
  const manifest = { schema: EVIDENCE_SCHEMA, candidate: { sha: FIXTURE_SHA, generatedAt: FIXTURE_NOW, mode: 'local' }, criteria: { unknown: { status: 'accepted' } } };
  await fs.writeFile(path.join(fixture.root, scope.evidenceManifest), JSON.stringify(manifest));
  const data = await snapshotFor(fixture);
  assert.equal(data.mvp.accepted, 0);
  assert.equal(data.mvp.evidence.contested, 1);
});

test('the committed criterion-to-test map accounts for every criterion and stays current', async () => {
  const root = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../..');
  const map = JSON.parse(await fs.readFile(path.join(root, scope.criterionMap), 'utf8'));
  const { criterionSource } = await import('./model.mjs');
  assert.equal(map.schema, MAP_SCHEMA);
  const ids = scope.slices.flatMap(slice => slice.criteria).map(criterion => criterion.id).sort();
  assert.deepEqual(Object.keys(map.criteria).sort(), ids, 'the map must name every criterion exactly once');
  assert.equal(map.candidateSha.length, 40);
  for (const slice of scope.slices) for (const criterion of slice.criteria) {
    const entry = map.criteria[criterion.id];
    assert.equal(entry.title, criterion.title);
    assert.equal(entry.requiresE2E, Boolean(criterion.requiresE2E));
    const source = await criterionSource(root, criterion);
    if (source.present.length !== criterion.sourcePaths.length) continue;
    assert.equal(entry.sourceDigest, source.sourceDigest, `${criterion.id} map entry is stale`);
    assert.ok(entry.tests.length > 0 || typeof entry.gap === 'string' && entry.gap.trim() !== '', `${criterion.id} needs a mapped test or an explicit gap`);
    for (const item of entry.tests) {
      assert.ok(['unit', 'integration', 'parity', 'e2e', 'cli'].includes(item.kind));
      assert.equal(typeof item.command, 'string');
    }
    if (criterion.requiresE2E && entry.gap) assert.equal(entry.tests.length, 0, 'a gap must not carry unverified tests');
  }
});
