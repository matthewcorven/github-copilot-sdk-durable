import crypto from 'node:crypto';

export const EVIDENCE_SCHEMA = 'core-engineers-evidence/v1';
export const MAP_SCHEMA = 'core-engineers-criterion-map/v1';
export const MODES = Object.freeze(['local', 'ci', 'mock']);
export const CREDITABLE_MODES = Object.freeze(['local', 'ci']);
export const EVIDENCE_KINDS = Object.freeze(['test-report', 'review', 'screenshot', 'coverage', 'runner-log', 'trace']);
export const TEST_KINDS = Object.freeze(['unit', 'integration', 'parity', 'e2e', 'cli']);
export const E2E_TEST_KINDS = Object.freeze(['e2e', 'cli']);
export const DEFAULT_EVIDENCE_MAX_AGE_SECONDS = 7 * 24 * 60 * 60;
const SHA_PATTERN = /^[0-9a-f]{40}$/;
const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

export const sha256 = value => crypto.createHash('sha256').update(value).digest('hex');

export function isIsoTimestamp(value) {
  return typeof value === 'string' && value.trim() !== '' && Number.isFinite(Date.parse(value));
}

export function isCommitSha(value) {
  return typeof value === 'string' && SHA_PATTERN.test(value);
}

const short = sha => typeof sha === 'string' ? sha.slice(0, 12) : String(sha);

/**
 * The manifest is the only place a candidate pin may be declared. Every criterion record is
 * judged against this candidate; a manifest without a full SHA, a time and a mode cannot credit
 * anything, because "recent" and "pinned" would then be indistinguishable from "unknown".
 */
export function candidateFromManifest(manifest) {
  if (!manifest || typeof manifest !== 'object' || Array.isArray(manifest)) {
    return { ok: false, reason: 'No evidence manifest was published; no criterion can be credited.' };
  }
  if (manifest.schema !== EVIDENCE_SCHEMA) {
    return { ok: false, reason: `Evidence manifest schema is not ${EVIDENCE_SCHEMA}.` };
  }
  const candidate = manifest.candidate;
  if (!candidate || typeof candidate !== 'object' || Array.isArray(candidate)) {
    return { ok: false, reason: 'Evidence manifest has no candidate SHA/time/mode block; provenance is unknown.' };
  }
  if (!isCommitSha(candidate.sha)) {
    return { ok: false, reason: 'Evidence manifest candidate SHA must be a full 40-character lowercase commit SHA.' };
  }
  if (!isIsoTimestamp(candidate.generatedAt)) {
    return { ok: false, reason: 'Evidence manifest candidate has no valid generatedAt time.' };
  }
  if (!MODES.includes(candidate.mode)) {
    return { ok: false, reason: `Evidence manifest candidate mode must be one of: ${MODES.join(', ')}.` };
  }
  if (!manifest.criteria || typeof manifest.criteria !== 'object' || Array.isArray(manifest.criteria)) {
    return { ok: false, reason: 'Evidence manifest criteria must be an object keyed by criterion id.' };
  }
  return { ok: true, candidate: { sha: candidate.sha, generatedAt: candidate.generatedAt, mode: candidate.mode }, records: manifest.criteria };
}

/**
 * The probe describes the committed candidate the harness can actually verify: the pinned commit,
 * the current committed HEAD, whether the pin is an ancestor of HEAD, and readers for the
 * candidate tree (criterion source) and the committed snapshot (evidence artifacts).
 */
export function candidateStaleness({ manifestCandidate, probe }) {
  if (!probe || typeof probe !== 'object' || !probe.candidate) {
    return { ok: false, reason: 'No committed candidate probe; provenance cannot be verified.' };
  }
  if (probe.candidate.sha !== manifestCandidate.sha) {
    return { ok: false, reason: `Manifest candidate ${short(manifestCandidate.sha)} is not the probed candidate ${short(probe.candidate.sha)}; stale evidence cannot credit acceptance.` };
  }
  if (probe.ancestor !== true) {
    return { ok: false, reason: `Manifest candidate ${short(manifestCandidate.sha)} is not a committed ancestor of the read-cache HEAD; stale evidence cannot credit acceptance.` };
  }
  return { ok: true };
}

/**
 * A normalized result is only current when it names the pinned candidate, was completed recently
 * and in a mode that actually ran the code. Mocks and simulated runs never credit acceptance.
 */
export function resultProvenance({ report, candidate, now = Date.now(), evidenceMaxAgeSeconds = DEFAULT_EVIDENCE_MAX_AGE_SECONDS }) {
  if (!report || typeof report !== 'object' || Array.isArray(report)) {
    return { ok: false, reason: 'Normalized result is missing.' };
  }
  if (!isCommitSha(report.candidateSha)) {
    return { ok: false, reason: 'Normalized result has no candidate SHA; provenance cannot be verified.' };
  }
  if (report.candidateSha !== candidate.sha) {
    return { ok: false, reason: `Evidence was captured against candidate ${short(report.candidateSha)} but the manifest pins ${short(candidate.sha)}; stale evidence cannot credit acceptance.` };
  }
  if (!isIsoTimestamp(report.completedAt)) {
    return { ok: false, reason: 'Normalized result has no valid completedAt time; staleness cannot be ruled out.' };
  }
  const completed = Date.parse(report.completedAt);
  if (completed - now > 5 * 60 * 1000) {
    return { ok: false, reason: 'Normalized result completion time is in the future; the result is not credible.' };
  }
  if (!MODES.includes(report.mode)) {
    return { ok: false, reason: `Normalized result mode must be one of: ${MODES.join(', ')}.` };
  }
  if (report.mode !== candidate.mode) {
    return { ok: false, reason: 'Normalized result mode disagrees with the manifest candidate mode; provenance is inconsistent.' };
  }
  if (!CREDITABLE_MODES.includes(report.mode)) {
    return { ok: false, reason: `Evidence mode "${report.mode}" is simulated; mocks cannot credit acceptance.` };
  }
  const ageSeconds = Math.max(0, Math.floor((now - completed) / 1000));
  if (ageSeconds > evidenceMaxAgeSeconds) {
    return { ok: false, reason: `Evidence is ${ageSeconds}s old, beyond the ${evidenceMaxAgeSeconds}s freshness limit; stale evidence cannot credit acceptance.` };
  }
  return { ok: true, ageSeconds };
}

/**
 * The criterion-to-test map is the auditable answer to "which test proves this criterion?".
 * An entry whose source digest no longer matches is stale: the criterion changed after the map was
 * written, so the mapping can no longer be trusted either as a test claim or as a gap statement.
 */
export function mappingFor({ map, criterion, source }) {
  const fail = reason => ({ ok: false, reason, tests: [], e2e: [], gap: null });
  if (!map || typeof map !== 'object' || map.schema !== MAP_SCHEMA || !map.criteria || typeof map.criteria !== 'object' || Array.isArray(map.criteria)) {
    return fail('Criterion-to-test map is missing, invalid, or not keyed by criterion id.');
  }
  const entry = map.criteria[criterion.id];
  if (!entry || typeof entry !== 'object' || Array.isArray(entry)) {
    return fail('No criterion-to-test mapping entry; an explicit gap is required.');
  }
  if (!source || entry.sourceDigest !== source.sourceDigest) {
    return fail('Criterion-to-test mapping is stale against the current source digest; regenerate it with --sync-map.');
  }
  const tests = entry.tests === undefined ? [] : entry.tests;
  if (!Array.isArray(tests)) return fail('Criterion-to-test mapping tests must be an array.');
  const seen = new Set();
  for (const item of tests) {
    if (!item || typeof item !== 'object' || Array.isArray(item)) return fail('Criterion-to-test mapping contains an invalid test entry.');
    if (typeof item.id !== 'string' || item.id.trim() === '') return fail('Every mapped test needs a nonempty id.');
    if (seen.has(item.id)) return fail(`Mapped test id "${item.id}" appears more than once.`);
    seen.add(item.id);
    if (!TEST_KINDS.includes(item.kind)) return fail(`Mapped test "${item.id}" has an unsupported kind.`);
    if (typeof item.command !== 'string' || item.command.trim() === '') return fail(`Mapped test "${item.id}" needs the command that runs it.`);
  }
  const gap = typeof entry.gap === 'string' && entry.gap.trim() !== '' ? entry.gap.trim() : null;
  if (!tests.length && !gap) return fail('No mapped test and no explicit gap; the criterion is unaccounted for.');
  return { ok: true, tests, e2e: tests.filter(item => E2E_TEST_KINDS.includes(item.kind)), gap };
}

/**
 * Coverage of the denominator itself: every requiresE2E criterion either maps to at least one
 * E2E test or states a gap. A gap is visible and cannot credit acceptance.
 */
export function mappingCoverage({ scope, map, sources }) {
  const required = scope.slices.flatMap(slice => slice.criteria).filter(criterion => Boolean(criterion.requiresE2E));
  const rows = required.map(criterion => {
    const mapping = mappingFor({ map, criterion, source: sources.get(criterion.id) });
    if (!mapping.ok) return { id: criterion.id, mapped: false, gap: null, stale: /stale/i.test(mapping.reason), reason: mapping.reason };
    if (mapping.e2e.length) return { id: criterion.id, mapped: true, gap: null, stale: false, reason: `${mapping.e2e.length} E2E test(s) mapped; ${mapping.tests.length} test(s) total.` };
    return { id: criterion.id, mapped: false, gap: mapping.gap, stale: false, reason: `Gap: ${mapping.gap}` };
  });
  const mapped = rows.filter(row => row.mapped).length;
  const gaps = rows.filter(row => !row.mapped && row.gap).length;
  return {
    total: required.length,
    mapped,
    gaps,
    stale: rows.filter(row => row.stale).length,
    unaccounted: rows.filter(row => !row.mapped && !row.gap).length,
    complete: rows.length > 0 && rows.every(row => row.mapped || row.gap),
    rows
  };
}

/**
 * The single acceptance decision. Anything missing, stale, simulated or unmapped fails closed:
 * the criterion is reported not accepted with the reason a reader needs to fix it.
 */
export async function acceptedCriterion({ criterion, source, record, manifest, map, probe, now = Date.now(), evidenceMaxAgeSeconds = DEFAULT_EVIDENCE_MAX_AGE_SECONDS }) {
  const fail = reason => ({ complete: false, reason, evidencePaths: [], coverage: null, mapping: null, evidenceState: 'missing' });
  const pinned = candidateFromManifest(manifest);
  if (!pinned.ok) return fail(pinned.reason);
  const identity = candidateStaleness({ manifestCandidate: pinned.candidate, probe });
  if (!identity.ok) return fail(identity.reason);
  if (!record) return fail('No qualifying acceptance record in this committed source snapshot. Source presence is not acceptance.');
  if (record.status !== 'accepted' || record.sourceDigest !== source.sourceDigest) return fail('Acceptance is unaccepted or its source digest is stale.');
  if (source.present.length !== criterion.sourcePaths.length) return fail('A required source reference is missing.');
  if (typeof record.authoredBy !== 'string' || !record.authoredBy.trim() || typeof record.reviewedBy !== 'string' || !record.reviewedBy.trim() || record.authoredBy.trim().toLowerCase() === record.reviewedBy.trim().toLowerCase()) {
    return fail('Independent recorded author/reviewer identities are required.');
  }
  if (!Array.isArray(record.evidence) || record.evidence.length === 0) return fail('Missing evidence references.');
  if (typeof probe.read !== 'function') return fail('No committed-snapshot reader; evidence cannot be hashed.');

  const kinds = new Map();
  const evidencePaths = [];
  for (const item of record.evidence) {
    if (!item || typeof item !== 'object' || Array.isArray(item) || !EVIDENCE_KINDS.includes(item.kind)) return fail('Unsupported or malformed evidence reference.');
    if (typeof item.path !== 'string' || typeof item.sha256 !== 'string') return fail('Every evidence reference needs a path and a SHA256.');
    let content;
    try { content = await probe.read(item.path); } catch { return fail('Unsafe or unreadable evidence reference.'); }
    if (!content || item.sha256 !== sha256(content)) return fail('Evidence is missing from the committed snapshot or does not match its recorded SHA256.');
    const entries = kinds.get(item.kind) ?? [];
    entries.push({ item, content });
    kinds.set(item.kind, entries);
    evidencePaths.push(item.path);
  }
  const reportEntry = kinds.get('test-report')?.[0];
  const reviewEntry = kinds.get('review')?.[0];
  if (!reportEntry || !reviewEntry) return fail('A normalized test report and independent review are required.');
  let report, review;
  try { report = JSON.parse(reportEntry.content); review = JSON.parse(reviewEntry.content); } catch { return fail('Normalized result/review JSON is invalid.'); }
  const provenance = resultProvenance({ report, candidate: pinned.candidate, now, evidenceMaxAgeSeconds });
  if (!provenance.ok) return fail(provenance.reason);
  if (report.criterionId !== criterion.id || report.sourceDigest !== source.sourceDigest || report.outcome !== 'passed') {
    return fail('Result must identify this criterion and this source digest, and must have passed.');
  }
  const counts = report.tests;
  if (!counts || !Number.isSafeInteger(counts.discovered) || counts.discovered < 1 || counts.executed !== counts.discovered || counts.passed !== counts.executed || counts.failed !== 0 || counts.skipped !== 0) {
    return fail('Result must identify this source/criterion and all discovered tests must pass without skips.');
  }
  if (review.criterionId !== criterion.id || review.sourceDigest !== source.sourceDigest || review.candidateSha !== pinned.candidate.sha || review.reviewer !== record.reviewedBy || review.decision !== 'accepted') {
    return fail('Independent review does not match this criterion, source and candidate.');
  }

  if (criterion.requiresE2E) {
    const mapping = mappingFor({ map, criterion, source });
    if (!mapping.ok) return { ...fail(mapping.reason), mapping: { required: true, mapped: false, gap: null, stale: /stale/i.test(mapping.reason) } };
    if (!mapping.e2e.length) return { ...fail(`Explicit gap recorded: ${mapping.gap}. A gap is not acceptance.`), mapping: { required: true, mapped: false, gap: mapping.gap, stale: false } };
    if (!Array.isArray(report.e2eTestIds) || report.e2eTestIds.length === 0 || new Set(report.e2eTestIds).size !== report.e2eTestIds.length) {
      return fail('E2E discovery IDs are required and must be unique.');
    }
    for (const test of mapping.e2e) {
      if (!report.e2eTestIds.includes(test.id)) return fail(`Mapped E2E test "${test.id}" was not executed by the normalized result.`);
    }
    for (const id of report.e2eTestIds) {
      const screenshots = (kinds.get('screenshot') ?? []).filter(entry => entry.item.testId === id);
      if (!screenshots.length) return fail(`A screenshot is required for every discovered E2E test; "${id}" has none.`);
      const png = screenshots[0].content;
      if (png.length < 24 || !png.subarray(0, 8).equals(PNG_SIGNATURE)) return fail('E2E screenshot must be an actual PNG artifact.');
      const mapped = mapping.e2e.find(test => test.id === id);
      if (mapped?.kind === 'cli' && screenshots[0].item.capture !== 'terminal') {
        return fail(`CLI E2E test "${id}" requires a captured terminal screenshot, not only an application screenshot.`);
      }
    }
    const coverage = report.coverage;
    if (!coverage || !Number.isFinite(coverage.line) || !Number.isFinite(coverage.branch) || coverage.line < 0 || coverage.line > 100 || coverage.branch < 0 || coverage.branch > 100) {
      return fail('Measured line/branch coverage is required for every E2E criterion.');
    }
    const coverageEntry = kinds.get('coverage')?.[0];
    if (!coverageEntry) return fail('A hashed raw coverage artifact is required for every E2E criterion.');
    let raw;
    try { raw = JSON.parse(coverageEntry.content); } catch { return fail('Coverage artifact is not valid JSON.'); }
    if (raw.candidateSha !== pinned.candidate.sha) return fail('Coverage artifact was measured for a different candidate; stale evidence cannot credit acceptance.');
    if (!Number.isFinite(raw.linePercent) || !Number.isFinite(raw.branchPercent) || Math.abs(raw.linePercent - coverage.line) > 0.05 || Math.abs(raw.branchPercent - coverage.branch) > 0.05) {
      return fail('Coverage artifact percentages do not match the normalized result; the report is not backed by the raw measurement.');
    }
    return {
      complete: true,
      reason: 'Qualifying source-linked acceptance record, candidate pin, per-E2E screenshots, raw coverage and independent review found. Recorded review is not a deployment authorization.',
      evidencePaths,
      coverage: { line: coverage.line, branch: coverage.branch },
      mapping: { required: true, mapped: true, gap: null, stale: false, tests: mapping.tests.map(test => test.id) },
      evidenceState: 'accepted'
    };
  }

  return {
    complete: true,
    reason: 'Qualifying source-linked acceptance record and artifacts found. Recorded review is not a deployment authorization.',
    evidencePaths,
    coverage: null,
    mapping: { required: false, mapped: null, gap: null, stale: false },
    evidenceState: 'accepted'
  };
}
