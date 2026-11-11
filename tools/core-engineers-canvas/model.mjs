import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';

export const digest = value => crypto.createHash('sha256').update(value).digest('hex');
export const percent = (done, total) => total ? Math.round(done * 1000 / total) / 10 : null;
export const slug = value => value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

export async function sourceFile(root, relative) {
  if (typeof relative !== 'string' || path.isAbsolute(relative) || relative.split(/[\\/]/).some(p => p === '..' || p === '.git' || p === '.mcp.json' || p === '.env' || p.startsWith('.env.')) || relative.includes('\\')) throw new Error('Invalid source path');
  const base = await fs.realpath(root);
  const candidate = path.join(base, relative);
  let actual;
  try { actual = await fs.realpath(candidate); } catch (error) { if (error.code === 'ENOENT') return null; throw error; }
  if (!actual.startsWith(base + path.sep)) throw new Error('Source path escapes read cache');
  const stat = await fs.stat(actual);
  if (!stat.isFile() || stat.size > 2 * 1024 * 1024) throw new Error('Source must be a file under 2 MB');
  return fs.readFile(actual);
}

export async function criterionSource(root, criterion) {
  const parts = [JSON.stringify({id:criterion.id,title:criterion.title,requiresE2E:Boolean(criterion.requiresE2E),sourcePaths:criterion.sourcePaths})];
  const present = [];
  for (const relative of criterion.sourcePaths) {
    const content = await sourceFile(root, relative);
    parts.push(relative, content === null ? 'MISSING' : digest(content));
    if (content !== null) present.push(relative);
  }
  return { sourceDigest: digest(JSON.stringify(parts)), present };
}

export async function acceptedCriterion(root, criterion, source, record) {
  const fail = reason => ({ complete: false, reason, evidencePaths: [] });
  if (!record) return fail('No qualifying acceptance record in this committed source snapshot. Source presence is not acceptance.');
  if (record.status !== 'accepted' || record.sourceDigest !== source.sourceDigest) return fail('Acceptance is unaccepted or its source digest is stale.');
  if (source.present.length !== criterion.sourcePaths.length) return fail('A required source reference is missing.');
  if (typeof record.authoredBy !== 'string' || !record.authoredBy.trim() || typeof record.reviewedBy !== 'string' || !record.reviewedBy.trim() || record.authoredBy.trim().toLowerCase() === record.reviewedBy.trim().toLowerCase()) return fail('Independent recorded author/reviewer identities are required.');
  if (!Array.isArray(record.evidence) || record.evidence.length === 0) return fail('Missing evidence references.');
  const kinds = new Map();
  const evidencePaths = [];
  for (const item of record.evidence) {
    let content;
    try { content = await sourceFile(root, item.path); } catch { return fail('Unsafe or unreadable evidence reference.'); }
    if (!content || item.sha256 !== digest(content)) return fail('Evidence is missing or does not match its recorded SHA256.');
    kinds.set(item.kind, content);
    evidencePaths.push(item.path);
  }
  if (!kinds.has('test-report') || !kinds.has('review')) return fail('A normalized test report and independent review are required.');
  let report, review;
  try { report = JSON.parse(kinds.get('test-report')); review = JSON.parse(kinds.get('review')); } catch { return fail('Normalized result/review JSON is invalid.'); }
  const counts = report.tests;
  if (report.criterionId !== criterion.id || report.sourceDigest !== source.sourceDigest || report.outcome !== 'passed' || !counts || !Number.isSafeInteger(counts.discovered) || counts.discovered < 1 || counts.executed !== counts.discovered || counts.passed !== counts.executed || counts.failed !== 0 || counts.skipped !== 0) return fail('Result must identify this source/criterion and all discovered tests must pass without skips.');
  if (review.criterionId !== criterion.id || review.sourceDigest !== source.sourceDigest || review.reviewer !== record.reviewedBy || review.decision !== 'accepted') return fail('Independent review does not match this criterion/source.');
  if (criterion.requiresE2E) {
    if (!Array.isArray(report.e2eTestIds) || report.e2eTestIds.length === 0 || new Set(report.e2eTestIds).size !== report.e2eTestIds.length) return fail('E2E discovery IDs are required.');
    for (const id of report.e2eTestIds) {
      const screenshot = record.evidence.find(item => item.kind === 'screenshot' && item.testId === id);
      if (!screenshot) return fail('A screenshot is required for every discovered E2E test.');
      const png = await sourceFile(root, screenshot.path);
      if (!png || png.length < 24 || !png.subarray(0, 8).equals(Buffer.from([137,80,78,71,13,10,26,10]))) return fail('E2E screenshot must be an actual PNG artifact.');
    }
    const coverage = report.coverage;
    if (!coverage || !Number.isFinite(coverage.line) || !Number.isFinite(coverage.branch) || coverage.line < 0 || coverage.line > 100 || coverage.branch < 0 || coverage.branch > 100 || !kinds.has('coverage')) return fail('Measured line/branch coverage and a hashed coverage artifact are required.');
  }
  return { complete: true, reason: 'Qualifying source-linked acceptance record and artifacts found. Recorded review is not a deployment authorization.', evidencePaths };
}

export function parseTeam(markdown, duties) {
  const result = [];
  let section = '';
  for (const line of markdown.split('\n')) {
    if (line.startsWith('## ')) section = line.slice(3).trim();
    if (!['Coordinator', 'Members'].includes(section) || !line.startsWith('|') || line.includes('---')) continue;
    const cells = line.split('|').slice(1, -1).map(value => value.trim());
    if (cells[0] === 'Name' || cells.length < 3) continue;
    const id = slug(cells[0]);
    result.push({ id, name: cells[0], role: cells[1], status: section === 'Members' ? cells[3] : 'Core', owns: cells[1], updateDuty: duties[id] ?? 'Unassigned canvas duty: Coordinator must resolve.', charterPath: section === 'Members' ? cells[2].replaceAll('`', '') : null });
  }
  return result;
}

export function referenceProgram(scope, issues, incomplete = false) {
  if (!issues) return { title: 'Reference scenario program (not MVP acceptance)', percent: null, closed: 0, total: 0, incomplete: true, slices: [], note: 'GitHub issue snapshot unavailable.' };
  const slices = scope.programBands.map(band => {
    const rows = issues.filter(issue => band.issues ? band.issues.includes(issue.number) : issue.labels.some(label => label.name === band.label));
    const leaf = rows.filter(issue => !issue.labels.some(label => label.name === 'type:epic' || label.name === 'epic'));
    const closed = leaf.filter(issue => issue.state === 'CLOSED').length;
    const total = leaf.length;
    return { id: band.id, title: band.title, closed, total, percent: total && !incomplete ? percent(closed,total) : null, issues: leaf.map(({number,title,state,url}) => ({number,title,state,url})) };
  });
  const unique = new Map(slices.flatMap(slice => slice.issues).map(issue => [issue.number,issue]));
  const total = unique.size, closed = [...unique.values()].filter(issue => issue.state === 'CLOSED').length;
  return { title: 'Reference scenario program (not MVP acceptance)', percent: total && !incomplete ? percent(closed,total) : null, closed, total, incomplete, slices, note: 'Closed leaf issues / cached labelled leaf issues. Epics excluded; overall deduplicated. Not effort-weighted, accepted quality, code coverage, or deployment readiness. Unlabelled/missing work can be absent; this is not the product MVP denominator.' };
}

export async function buildSnapshot({root,scope,meta,issues,queue}) {
  let records = {};
  let evidenceError = null;
  try {
    const raw = await sourceFile(root,scope.evidenceManifest);
    if (raw) { const parsed = JSON.parse(raw); if (parsed.schema !== 'core-engineers-evidence/v1' || !parsed.criteria || typeof parsed.criteria !== 'object' || Array.isArray(parsed.criteria)) throw new Error('Invalid evidence manifest schema'); records = parsed.criteria; }
  } catch { evidenceError = 'Evidence manifest unavailable or invalid; no criteria credited.'; }
  const slices = [];
  for (const slice of scope.slices) {
    const criteria = [];
    for (const criterion of slice.criteria) {
      const source = await criterionSource(root,criterion);
      const acceptance = await acceptedCriterion(root,criterion,source,records[criterion.id]);
      criteria.push({...criterion,...source,...acceptance});
    }
    const total = criteria.length, accepted = criteria.filter(c=>c.complete).length;
    const referenced = new Set(criteria.flatMap(c=>c.sourcePaths));
    const present = new Set(criteria.flatMap(c=>c.present));
    const sourceTotal = referenced.size;
    const sourcePresent = present.size;
    slices.push({...slice,criteria,total,accepted,percent:percent(accepted,total),sourceTotal,sourcePresent,sourcePercent:percent(sourcePresent,sourceTotal)});
  }
  const total=slices.reduce((sum,s)=>sum+s.total,0),accepted=slices.reduce((sum,s)=>sum+s.accepted,0);
  const sourceTotal=new Set(slices.flatMap(s=>s.criteria.flatMap(c=>c.sourcePaths))).size;
  const sourcePresent=new Set(slices.flatMap(s=>s.criteria.flatMap(c=>c.present))).size;
  const markdown=await sourceFile(root,'.squad/team.md');
  const allIssues=issues?.items ?? null;
  const issueByNumber=new Map((allIssues??[]).map(i=>[i.number,i]));
  const hydrate = item => { const issue=issueByNumber.get(item.number); return {number:item.number,title:item.title,state:item.state,url:issue?.url??`https://github.com/${scope.repository}/issues/${item.number}`,owner:issue?.labels.find(l=>l.name.startsWith('squad:'))?.name.slice(6)??'Unknown',priority:issue?.labels.find(l=>l.name.startsWith('priority:'))?.name??'Unprioritized'}; };
  const ageSeconds=Math.max(0,Math.floor((Date.now()-Date.parse(meta.generatedAt))/1000));
  return {
    meta:{...meta,scopeStatus:scope.status,scopeNote:scope.note,maxAgeSeconds:scope.maxAgeSeconds},
    mvp:{percent:percent(accepted,total),accepted,total,sourcePresent,sourceTotal,sourcePercent:percent(sourcePresent,sourceTotal),label:'Evidence-linked acceptance',note:evidenceError??'Equal-weight explicit criteria with qualifying source-linked reports and recorded independent review. No acceptance record means no credited completion, not proof that no implementation exists. Not a line/branch coverage metric.',slices},
    team:parseTeam(markdown?.toString()??'',scope.duties),systems:scope.systems,
    queue:{available:(queue?.available??[]).map(hydrate),blocked:(queue?.blocked??[]).map(item=>({...hydrate(item),reason:[...(item.unresolved_dependencies??[]),...(item.unresolved_blockers??[])].map(ref=>typeof ref==='object'?`#${ref.resolved?.number??ref.reference??ref.number??'unknown'}${ref.missing?' (missing)':''}`:`#${ref}`).join(', ')||'See dependency section'})),nextAvailable:queue?.next_available?.number??null,error:meta.queueError??null},
    program:referenceProgram(scope,allIssues,issues?.incomplete??true),
    schedules:[{role:'PO',cron:'0 9,13,17 * * *',note:'Configured local job; requires app/machine. Live enabled state is verified in Automations, not inferred here.'},{role:'Commitments Facilitator',cron:'0 18 * * *',note:'Configured daily in-app snapshot; no read receipt or email guarantee.'}],
    risks:[{title:'MVP denominator needs PO approval',detail:scope.note,owner:'PO'},{title:'Deployment remains gated',detail:'This dashboard neither grants approval nor implements a deployment gate. Missing local evidence and candidate-specific approval remain blockers.',owner:'Coordinator'},{title:'Fresh source is not fresh execution',detail:'A recent clone and issue fetch do not rerun tests or validate cloud services. Acceptance evidence must match criterion source digests.',owner:'Tester'},{title:'Live meetings and finance sources unresolved',detail:'Do not infer calendar/voice/email delivery, organization-wide commitments or CapEx/OpEx figures from repository files.',owner:'Commitments Facilitator'}],
    freshness:{ageSeconds,stale:ageSeconds>scope.maxAgeSeconds}
  };
}
