import fs from 'node:fs/promises';
import path from 'node:path';
import { sha256 as digest, acceptedCriterion, candidateFromManifest, candidateStaleness, mappingCoverage } from './evidence.mjs';

export { digest };
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

export async function buildSnapshot({root,scope,meta,issues,queue,evidenceRoot=null,probe=null,now=Date.now()}) {
  // Criterion source is read from the pinned candidate tree (`root`). Evidence artifacts and the
  // manifest are read from the committed snapshot (`evidenceRoot`), so an artifact can never be
  // credited because it happens to sit in an uncommitted working tree.
  const artifactRoot = evidenceRoot ?? root;
  const manifestRead = path => sourceFile(artifactRoot, path);
  let manifest = null;
  let evidenceError = null;
  try {
    const raw = await sourceFile(artifactRoot, scope.evidenceManifest);
    if (raw) manifest = JSON.parse(raw);
    else evidenceError = `No evidence manifest at ${scope.evidenceManifest} in the committed snapshot; no criterion is credited.`;
  } catch { evidenceError = 'Evidence manifest unavailable or invalid; no criterion is credited.'; }
  const pinned = candidateFromManifest(manifest);
  if (!pinned.ok && !evidenceError) evidenceError = pinned.reason;
  let map = null;
  let mapError = null;
  try {
    const raw = await sourceFile(artifactRoot, scope.criterionMap);
    if (raw) map = JSON.parse(raw);
    else mapError = `No criterion-to-test map at ${scope.criterionMap} in the committed snapshot.`;
  } catch { mapError = 'Criterion-to-test map unavailable or invalid.'; }
  const sources = new Map();
  for (const slice of scope.slices) for (const criterion of slice.criteria) sources.set(criterion.id, await criterionSource(root, criterion));
  const mapping = mappingCoverage({ scope, map, sources });
  const probeFor = probe ?? { candidate: { sha: meta.sourceSha }, ancestor: true, read: manifestRead };
  const identity = pinned.ok ? candidateStaleness({ manifestCandidate: pinned.candidate, probe: probeFor }) : { ok: false, reason: pinned.reason };
  const slices = [];
  for (const slice of scope.slices) {
    const criteria = [];
    for (const criterion of slice.criteria) {
      const source = sources.get(criterion.id);
      const record = pinned.ok ? pinned.records[criterion.id] : undefined;
      const acceptance = pinned.ok && identity.ok
        ? await acceptedCriterion({ criterion, source, record, manifest, map, probe: probeFor, now, evidenceMaxAgeSeconds: scope.evidenceMaxAgeSeconds ?? undefined })
        : { complete: false, reason: identity.reason, evidencePaths: [], coverage: null, mapping: null, evidenceState: 'missing' };
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
  const scopeIds=new Set(scope.slices.flatMap(s=>s.criteria.map(c=>c.id)));
  const contested=pinned.ok?Object.keys(pinned.records).filter(id=>!scopeIds.has(id)):[];
  const markdown=await sourceFile(root,'.squad/team.md');
  const allIssues=issues?.items ?? null;
  const issueByNumber=new Map((allIssues??[]).map(i=>[i.number,i]));
  const hydrate = item => { const issue=issueByNumber.get(item.number); return {number:item.number,title:item.title,state:item.state,url:issue?.url??`https://github.com/${scope.repository}/issues/${item.number}`,owner:issue?.labels.find(l=>l.name.startsWith('squad:'))?.name.slice(6)??'Unknown',priority:issue?.labels.find(l=>l.name.startsWith('priority:'))?.name??'Unprioritized'}; };
  const ageSeconds=Math.max(0,Math.floor((Date.now()-Date.parse(meta.generatedAt))/1000));
  return {
    meta:{...meta,scopeStatus:scope.status,scopeNote:scope.note,maxAgeSeconds:scope.maxAgeSeconds},
    mvp:{percent:percent(accepted,total),accepted,total,sourcePresent,sourceTotal,sourcePercent:percent(sourcePresent,sourceTotal),label:'Evidence-linked acceptance',note:evidenceError??'Equal-weight explicit criteria with qualifying source-linked reports and recorded independent review. No acceptance record means no credited completion, not proof that no implementation exists. Not a line/branch coverage metric.',slices,
      candidate: pinned.ok ? { ...pinned.candidate, ancestor: identity.ok } : null,
      evidence:{ state: evidenceError ? 'unavailable' : (identity.ok ? (accepted > 0 ? 'current' : 'empty') : 'stale'), error: evidenceError ?? (identity.ok ? null : identity.reason), contested: contested.length, manifest: scope.evidenceManifest },
      mapping:{ ...mapping, error: mapError, map: scope.criterionMap, rows: mapping.rows.map(row => ({ ...row, title: scope.slices.flatMap(slice => slice.criteria).find(criterion => criterion.id === row.id)?.title ?? row.id })) }},
    team:parseTeam(markdown?.toString()??'',scope.duties),systems:scope.systems,
    queue:{available:(queue?.available??[]).map(hydrate),blocked:(queue?.blocked??[]).map(item=>({...hydrate(item),reason:[...(item.unresolved_dependencies??[]),...(item.unresolved_blockers??[])].map(ref=>typeof ref==='object'?`#${ref.resolved?.number??ref.reference??ref.number??'unknown'}${ref.missing?' (missing)':''}`:`#${ref}`).join(', ')||'See dependency section'})),nextAvailable:queue?.next_available?.number??null,error:meta.queueError??null},
    program:referenceProgram(scope,allIssues,issues?.incomplete??true),
    schedules:[{role:'PO',cron:'0 9,13,17 * * *',note:'Configured local job; requires app/machine. Live enabled state is verified in Automations, not inferred here.'},{role:'Commitments Facilitator',cron:'0 18 * * *',note:'Configured daily in-app snapshot; no read receipt or email guarantee.'}],
    risks:[{title:'MVP denominator needs PO approval',detail:scope.note,owner:'PO'},{title:'Deployment remains gated',detail:'This dashboard neither grants approval nor implements a deployment gate. Missing local evidence and candidate-specific approval remain blockers.',owner:'Coordinator'},{title:'Fresh source is not fresh execution',detail:'A recent clone and issue fetch do not rerun tests or validate cloud services. Acceptance evidence must match criterion source digests.',owner:'Tester'},{title:'Live meetings and finance sources unresolved',detail:'Do not infer calendar/voice/email delivery, organization-wide commitments or CapEx/OpEx figures from repository files.',owner:'Commitments Facilitator'}],
    freshness:{ageSeconds,stale:ageSeconds>scope.maxAgeSeconds}
  };
}
