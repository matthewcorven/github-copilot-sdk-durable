import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { criterionSource } from './model.mjs';
import { sha256, MAP_SCHEMA, EVIDENCE_SCHEMA, DEFAULT_EVIDENCE_MAX_AGE_SECONDS } from './evidence.mjs';

const scope = JSON.parse(await fs.readFile(new URL('./scope.json', import.meta.url), 'utf8'));
export const FIXTURE_SHA = 'c'.repeat(40);
export const FIXTURE_NOW = '2026-11-21T00:21:34.320Z';

// A syntactically valid PNG header plus filler; the harness only checks the signature and length.
export const PNG = Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), Buffer.alloc(32, 7)]);

async function put(root, file, value) {
  const target = path.join(root, file);
  await fs.mkdir(path.dirname(target), { recursive: true });
  await fs.writeFile(target, Buffer.isBuffer(value) ? value : typeof value === 'string' ? value : JSON.stringify(value, null, 2));
}

export async function buildEvidenceFixture(t, { recordFor = null, mapFor = null, writeManifest = true, writeMap = true, mode = 'local', completedAt = FIXTURE_NOW, coverage = null } = {}) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'core-engineers-evidence-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  for (const slice of scope.slices) for (const criterion of slice.criteria) {
    for (const file of criterion.sourcePaths) await put(root, file, `// fixture for ${criterion.id}`);
  }
  const criteria = {};
  for (const slice of scope.slices) for (const criterion of slice.criteria) {
    const source = await criterionSource(root, criterion);
    criteria[criterion.id] = { sourceDigest: source.sourceDigest, title: criterion.title, requiresE2E: Boolean(criterion.requiresE2E), gap: `Unmapped: fixture gap for ${criterion.id}.`, tests: [] };
  }
  const criterion = scope.slices.flatMap(slice => slice.criteria).find(item => item.id === (recordFor ?? mapFor ?? 'durable-recovery'));
  const source = await criterionSource(root, criterion);
  if (mapFor) {
    criteria[mapFor] = {
      sourceDigest: source.sourceDigest,
      title: criterion.title,
      requiresE2E: Boolean(criterion.requiresE2E),
      tests: [{ id: 'browser-e2e-one', kind: 'e2e', command: 'node --test browser.spec.mjs' }]
    };
  }
  const record = recordFor ? { status: 'accepted', sourceDigest: source.sourceDigest, authoredBy: 'author', reviewedBy: 'independent reviewer', evidence: [] } : null;
  const mapCriteria = structuredClone(criteria);
  if (recordFor) {
    const e2e = Boolean(criterion.requiresE2E);
    const measured = coverage ?? { line: 88, branch: 71 };
    const report = {
      criterionId: criterion.id,
      sourceDigest: source.sourceDigest,
      candidateSha: FIXTURE_SHA,
      completedAt,
      mode,
      outcome: 'passed',
      tests: { discovered: 2, executed: 2, passed: 2, failed: 0, skipped: 0 },
      e2eTestIds: e2e ? ['browser-e2e-one'] : undefined,
      coverage: e2e ? measured : undefined
    };
    const review = { criterionId: criterion.id, sourceDigest: source.sourceDigest, candidateSha: FIXTURE_SHA, reviewer: 'independent reviewer', decision: 'accepted' };
    await put(root, 'evidence/report.json', report);
    await put(root, 'evidence/review.json', review);
    record.evidence.push(
      { kind: 'test-report', path: 'evidence/report.json', sha256: sha256(await fs.readFile(path.join(root, 'evidence/report.json'))) },
      { kind: 'review', path: 'evidence/review.json', sha256: sha256(await fs.readFile(path.join(root, 'evidence/review.json'))) }
    );
    if (e2e) {
      await put(root, 'evidence/browser-e2e-one.png', PNG);
      record.evidence.push({ kind: 'screenshot', testId: 'browser-e2e-one', capture: 'terminal', path: 'evidence/browser-e2e-one.png', sha256: sha256(PNG) });
      const raw = { candidateSha: FIXTURE_SHA, source: 'node --experimental-test-coverage', linePercent: measured.line, branchPercent: measured.branch };
      await put(root, 'evidence/coverage.json', raw);
      record.evidence.push({ kind: 'coverage', path: 'evidence/coverage.json', sha256: sha256(await fs.readFile(path.join(root, 'evidence/coverage.json'))) });
    }
  }
  if (writeMap) await put(root, scope.criterionMap, { schema: MAP_SCHEMA, candidateSha: FIXTURE_SHA, updatedAt: FIXTURE_NOW, criteria: mapCriteria });
  if (writeManifest) await put(root, scope.evidenceManifest, { schema: EVIDENCE_SCHEMA, candidate: { sha: FIXTURE_SHA, generatedAt: FIXTURE_NOW, mode }, criteria: recordFor ? { [recordFor]: record } : {} });

  const probe = {
    candidate: { sha: FIXTURE_SHA },
    ancestor: true,
    async read(file) {
      try { return await fs.readFile(path.join(root, file)); } catch (error) { if (error.code === 'ENOENT') return null; throw error; }
    }
  };
  return { root, sha: FIXTURE_SHA, probe, now: FIXTURE_NOW, criterion, source, maxAge: DEFAULT_EVIDENCE_MAX_AGE_SECONDS, record, recordFor };
}
