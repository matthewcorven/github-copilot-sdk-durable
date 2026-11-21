import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:http';
import { execFile as callbackExecFile } from 'node:child_process';
import { promisify } from 'node:util';
import { randomUUID } from 'node:crypto';
import { buildSnapshot, criterionSource, digest, percent, slug, sourceFile } from './model.mjs';
import { EVIDENCE_SCHEMA, MAP_SCHEMA } from './evidence.mjs';

const execFile=promisify(callbackExecFile);
const here=path.dirname(fileURLToPath(import.meta.url));
export const repositoryRoot=path.resolve(here,'../..');
const cacheRoot=path.join(repositoryRoot,'.scratch/core-engineers-canvas');
const cloneRoot=path.join(cacheRoot,'source');
let refreshPromise;

async function command(file,args,cwd=repositoryRoot) {
  return (await execFile(file,args,{cwd,timeout:120000,maxBuffer:16*1024*1024,env:{...process.env,GIT_TERMINAL_PROMPT:'0',GH_PAGER:'cat',GIT_PAGER:'cat'}})).stdout;
}
async function readJson(file) { try{return JSON.parse(await fs.readFile(file,'utf8'));}catch(error){if(error.code==='ENOENT')return null;throw error;} }
async function writeJson(file,value) {
  const temp=`${file}.${randomUUID()}.tmp`;
  await fs.writeFile(temp,JSON.stringify(value,null,2)+'\n',{mode:0o600});
  await fs.rename(temp,file);
}

export function validateScope(scope) {
  if(scope.schema!=='core-engineers-scope/v1'||!['provisional','approved'].includes(scope.status)||scope.repository!=='matthewcorven/github-copilot-sdk-durable'||!Number.isSafeInteger(scope.maxAgeSeconds)||scope.maxAgeSeconds<1||!Array.isArray(scope.slices)||scope.slices.length===0)throw new Error('Invalid scope configuration');
  const ids=new Set(),sliceIds=new Set();
  for(const slice of scope.slices){
    if(!slice.id||sliceIds.has(slice.id)||!slice.owner||!Array.isArray(slice.criteria)||!slice.criteria.length)throw new Error('Invalid or duplicate scope slice');
    sliceIds.add(slice.id);
    for(const item of slice.criteria){if(!item.id||ids.has(item.id)||!Array.isArray(item.sourcePaths)||!item.sourcePaths.length||item.sourcePaths.some(p=>typeof p!=='string'||path.isAbsolute(p)||p.split(/[\\/]/).includes('..')))throw new Error('Invalid or duplicate criterion');ids.add(item.id);}
  }
  for(const value of [scope.evidenceManifest,scope.criterionMap]){if(value!==undefined&&(typeof value!=='string'||path.isAbsolute(value)||value.split(/[\\/]/).includes('..')))throw new Error('Evidence manifest and criterion map must be repository-relative paths');}
  if(scope.evidenceMaxAgeSeconds!==undefined&&(!Number.isSafeInteger(scope.evidenceMaxAgeSeconds)||scope.evidenceMaxAgeSeconds<1))throw new Error('Invalid evidence freshness limit');
  return scope;
}

const candidateInputs=new Set();
export function trackCandidateInput(value) {
  if(!value)return;
  candidateInputs.add(value);
}

// A criterion's source and its evidence artifacts are separate identities. The source must come
// from the exact committed candidate tree; the artifacts must come from committed content, never
// from the working tree that happens to hold the current checkout.
export async function candidateProbe() {
  const candidateSha=(await command('git',['rev-parse','--verify','HEAD'])).trim();
  const headSha=(await command('git',['rev-parse','--verify','HEAD'])).trim();
  const ancestor=await command('git',['merge-base','--is-ancestor',candidateSha,headSha]).then(()=>true).catch(()=>false);
  trackCandidateInput(`candidate\n${candidateSha}`);
  const read=async file=>{
    const prefix=`committed:${candidateSha}:${file}`;
    try {
      const content=await command('git',['show',`${candidateSha}:${file}`]);
      trackCandidateInput(prefix);
      return Buffer.from(content);
    } catch(error) {
      trackCandidateInput(prefix);
      if(error.code===128||/exists on disk, but not in|does not exist|bad object|invalid object|unknown revision|pathspec/i.test(String(error.stderr??error.message)))return null;
      throw error;
    }
  };
  return { candidate:{sha:candidateSha}, ancestor, read };
}

export async function refreshCache({offline=false}={}) {
  if(refreshPromise)return refreshPromise;
  refreshPromise=(async()=>{
    await fs.mkdir(cacheRoot,{recursive:true,mode:0o700});
    const lockPath=path.join(cacheRoot,'refresh.lock');
    const lock=await fs.open(lockPath,'wx',0o600).catch(error=>{if(error.code==='EEXIST')throw new Error('Another refresh owns the cache lock; retry after it finishes. Uncertain/stale locks require owner verification.');throw error;});
    await lock.writeFile(JSON.stringify({pid:process.pid,startedAt:new Date().toISOString()}));
    try {
    const sourceSha=(await command('git',['rev-parse','--verify','HEAD'])).trim();
    let exists=true;try{await fs.access(path.join(cloneRoot,'.git'));}catch{exists=false;}
    if(!exists)await command('git',['-c','core.hooksPath=/dev/null','clone','--quiet','--no-hardlinks','--no-checkout',repositoryRoot,cloneRoot]);
    await command('git',['-c','core.hooksPath=/dev/null','-C',cloneRoot,'fetch','--quiet','--no-tags',repositoryRoot,sourceSha]);
    await command('git',['-c','core.hooksPath=/dev/null','-C',cloneRoot,'checkout','--quiet','--detach',sourceSha]);
    const cloneSha=(await command('git',['-C',cloneRoot,'rev-parse','HEAD'])).trim();
    if(cloneSha!==sourceSha)throw new Error('Read-cache clone does not match source SHA');
    if((await command('git',['-C',cloneRoot,'status','--porcelain'])).trim())throw new Error('Read-cache clone has been modified; refusing untrusted snapshot');
    const scopeBytes=await fs.readFile(path.join(here,'scope.json'));
    const scope=validateScope(JSON.parse(scopeBytes));
    const probe=await candidateProbe();
    // Evidence artifacts must be committed: a file only in the working tree cannot be pinned to
    // the candidate, so its hash would prove nothing about what the candidate contains.
    const dirty=await command('git',['status','--porcelain']).then(text=>text.trim()).catch(()=>'');
    const uncommitted=dirty.split('\n').filter(Boolean).filter(line=>[scope.evidenceManifest,scope.criterionMap].some(file=>line.includes(file)));
    let issues=await readJson(path.join(cacheRoot,'issues.json'));
    let queue=await readJson(path.join(cacheRoot,'queue.json'));
    let githubError=null,queueError=null;
    if(!offline){
      const helper=path.join(process.env.HOME??'', '.copilot/installed-plugins/corven-agent-tools/github-issues/skills/issue-work-queue/scripts/issue_queue.py');
      try{queue=JSON.parse(await command('python3',[helper,'--repo',scope.repository,'--limit','1000','--format','json','--mode','status']));await writeJson(path.join(cacheRoot,'queue.json'),queue);}catch(error){queueError=`Authoritative queue refresh failed (${error.code??'invalid response'}). Any displayed queue is a dated cache, not current admission.`;}
      try{const items=JSON.parse(await command('gh',['issue','list','--repo',scope.repository,'--state','all','--limit','1000','--json','number,title,state,labels,url,createdAt,updatedAt,body']));issues={fetchedAt:new Date().toISOString(),incomplete:items.length>=1000,items};await writeJson(path.join(cacheRoot,'issues.json'),issues);}catch(error){githubError=`GitHub issue refresh failed (${error.code??'invalid response'}); retained issue cache may be stale.`;}
    }else{githubError='Offline refresh: GitHub and authoritative queue were not fetched.';queueError='Offline refresh: no fresh admission decision.';}
    const meta={generatedAt:new Date().toISOString(),sourceSha,branch:(await command('git',['branch','--show-current'])).trim()||'detached',sourceDirty:Boolean((await command('git',['status','--porcelain'])).trim()),clonePath:cloneRoot,scopeDigest:digest(scopeBytes),scopeOrigin:'tools/core-engineers-canvas/scope.json (working configuration; separately hashed)',evidence:{manifest:scope.evidenceManifest,criterionMap:scope.criterionMap,candidateSha:probe.candidate.sha,candidateIsHead:probe.ancestor,uncommitted},githubFetchedAt:issues?.fetchedAt??null,queueGeneratedAt:queue?.generated_at??null,latestRefreshError:githubError,queueError};
    const snapshot=await buildSnapshot({root:cloneRoot,scope,meta,issues,queue,evidenceRoot:cloneRoot,probe,now:Date.parse(meta.generatedAt)});
    await writeJson(path.join(cacheRoot,'snapshot.json'),snapshot);
    return snapshot;
    } finally {
      await lock.close();
      await fs.unlink(lockPath);
    }
  })().finally(()=>{refreshPromise=null;});
  return refreshPromise;
}

export async function currentSnapshot() {
  if(refreshPromise)await refreshPromise.catch(()=>{});
  const snapshot=await readJson(path.join(cacheRoot,'snapshot.json'));
  if(!snapshot)return null;
  const ageSeconds=Math.max(0,Math.floor((Date.now()-Date.parse(snapshot.meta.generatedAt))/1000));
  let sourceChanged=true;
  try{sourceChanged=(await command('git',['rev-parse','HEAD'])).trim()!==snapshot.meta.sourceSha;}catch{}
  const scopeChanged=digest(await fs.readFile(path.join(here,'scope.json')))!==snapshot.meta.scopeDigest;
  const sourceAge=value=>value?Math.max(0,Math.floor((Date.now()-Date.parse(value))/1000)):null;
  const githubAgeSeconds=sourceAge(snapshot.meta.githubFetchedAt),queueAgeSeconds=sourceAge(snapshot.meta.queueGeneratedAt);
  const remoteStale=[githubAgeSeconds,queueAgeSeconds].some(age=>age===null||!Number.isFinite(age)||age>snapshot.meta.maxAgeSeconds);
  const evidenceStale=snapshot.mvp?.evidence?.state==='stale';
  snapshot.freshness={ageSeconds,githubAgeSeconds,queueAgeSeconds,sourceChanged,scopeChanged,evidenceStale,stale:ageSeconds>snapshot.meta.maxAgeSeconds||sourceChanged||scopeChanged||remoteStale||evidenceStale||Boolean(snapshot.meta.latestRefreshError)||Boolean(snapshot.meta.queueError)};
  return snapshot;
}

export function allowedSources(snapshot) {
  return new Set([
    ...snapshot.mvp.slices.flatMap(s=>s.criteria.flatMap(c=>[...c.sourcePaths,...c.evidencePaths])),
    ...snapshot.systems.flatMap(s=>s.paths),...snapshot.team.map(m=>m.charterPath).filter(Boolean)
  ]);
}

export function createDashboardServer() {
  return createServer(async(req,res)=>{
    res.setHeader('Cache-Control','no-store');
    res.setHeader('X-Content-Type-Options','nosniff');
    res.setHeader('Content-Security-Policy',"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'");
    const json=(status,value)=>{res.writeHead(status,{'Content-Type':'application/json; charset=utf-8'});res.end(JSON.stringify(value));};
    try{
      const host=req.headers.host??'';
      if(!/^(127\.0\.0\.1|localhost):\d+$/.test(host))return json(403,{error:'Loopback host required'});
      const url=new URL(req.url,`http://${host}`);
      if(req.method==='GET'&&url.pathname==='/api/snapshot'){const snapshot=await currentSnapshot();return json(snapshot?200:503,snapshot??{error:'No snapshot. Run refresh first.'});}
      if(req.method==='POST'&&url.pathname==='/api/refresh'){
        if(req.headers['x-core-engineers-refresh']!=='1'||(req.headers.origin&&req.headers.origin!==`http://${host}`))return json(403,{error:'Same-origin refresh header required'});
        return json(200,await refreshCache());
      }
      if(req.method==='GET'&&url.pathname==='/api/source'){
        const snapshot=await currentSnapshot(),relative=url.searchParams.get('path');
        if(!snapshot||!allowedSources(snapshot).has(relative))return json(403,{error:'Only snapshot-referenced source paths may be read'});
        const cacheBusy=async()=>{try{await fs.access(path.join(cacheRoot,'refresh.lock'));return true;}catch{return false;}};
        if(await cacheBusy())return json(409,{error:'Read-cache refresh in progress; retry source view shortly'});
        if((await command('git',['-C',cloneRoot,'rev-parse','HEAD'])).trim()!==snapshot.meta.sourceSha)return json(409,{error:'Clone and published snapshot differ; refresh required'});
        const content=await sourceFile(cloneRoot,relative);
        if(await cacheBusy()||(await command('git',['-C',cloneRoot,'rev-parse','HEAD'])).trim()!==snapshot.meta.sourceSha)return json(409,{error:'Clone changed while reading; refresh required'});
        if(!content)return json(404,{error:'Referenced source is absent from this clone'});
        if(content.includes(0))return json(415,{error:'Binary evidence is hashed but not rendered as text'});
        return json(200,{path:relative,content:content.toString('utf8')});
      }
      const assets=new Map([['/',['index.html','text/html; charset=utf-8']],['/app.mjs',['app.mjs','text/javascript; charset=utf-8']],['/styles.css',['styles.css','text/css; charset=utf-8']]]);
      if(req.method==='GET'&&assets.has(url.pathname)){const [file,type]=assets.get(url.pathname);res.writeHead(200,{'Content-Type':type});return res.end(await fs.readFile(path.join(here,'public',file)));}
      return json(404,{error:'Not found'});
    }catch(error){process.stderr.write(`Core Engineers request failed: ${error.message}\n`);return json(500,{error:'Refresh or source read failed; previous snapshot retained. See local server log.'});}
  });
}

// Scaffolds the criterion-to-test map for a new candidate commit. It never invents a test: a
// criterion it cannot source keeps its previous entry (now stale) and an unmapped criterion gets
// an explicit gap that the Tester must replace with a real mapping or confirm as a gap.
export async function syncCriterionMap({write=async()=>{}}={}) {
  const scope=validateScope(JSON.parse(await fs.readFile(path.join(here,'scope.json'))));
  const exists=await fs.access(path.join(repositoryRoot,scope.criterionMap)).then(()=>true).catch(()=>false);
  let previous=null;
  if(exists){try{previous=JSON.parse(await fs.readFile(path.join(repositoryRoot,scope.criterionMap),'utf8'));}catch{previous=null;}}
  const previousEntries=previous?.schema===MAP_SCHEMA?previous.criteria:{};
  const criteria={};
  for(const slice of scope.slices)for(const criterion of slice.criteria){
    const source=await criterionSource(cloneRoot,criterion);
    const old=previousEntries[criterion.id];
    if(source.present.length!==criterion.sourcePaths.length){
      criteria[criterion.id]=old
        ?{...old,sourceDigest:source.sourceDigest,note:`Unmapped: a required source reference is missing as of ${source.sourceDigest}; previous entry retained but stale.`}
        :{sourceDigest:source.sourceDigest,title:criterion.title,requiresE2E:Boolean(criterion.requiresE2E),gap:'Unmapped: a required source reference is missing.',tests:[]};
      continue;
    }
    if(old){criteria[criterion.id]={...old,sourceDigest:source.sourceDigest,title:criterion.title,requiresE2E:Boolean(criterion.requiresE2E)};continue;}
    criteria[criterion.id]={sourceDigest:source.sourceDigest,title:criterion.title,requiresE2E:Boolean(criterion.requiresE2E),gap:`Unmapped: no test has been recorded for "${criterion.title}" yet.`,tests:[]};
  }
  const map={schema:MAP_SCHEMA,candidateSha:(await command('git',['rev-parse','--verify','HEAD'])).trim(),updatedAt:new Date().toISOString(),criteria};
  await write(scope.criterionMap,map);
  return {path:scope.criterionMap,mapped:Object.values(criteria).filter(entry=>entry.tests?.length).length,gaps:Object.values(criteria).filter(entry=>!entry.tests?.length).length};
}

if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
  if(process.argv.includes('--sync-map')){
    const result=await syncCriterionMap({write:async(file,value)=>{const target=path.join(repositoryRoot,file);await fs.mkdir(path.dirname(target),{recursive:true});await fs.writeFile(target,JSON.stringify(value,null,2)+'\n');}});
    process.stdout.write(JSON.stringify(result,null,2)+'\n');
    process.exit(0);
  }else if(process.argv.includes('--refresh')||process.argv.includes('--offline')){
    const snapshot=await refreshCache({offline:process.argv.includes('--offline')});
    process.stdout.write(JSON.stringify({generatedAt:snapshot.meta.generatedAt,sourceSha:snapshot.meta.sourceSha,mvp:snapshot.mvp.percent,criteria:snapshot.mvp.total,candidate:snapshot.mvp.candidate,evidence:snapshot.mvp.evidence,mapping:{total:snapshot.mvp.mapping.total,mapped:snapshot.mvp.mapping.mapped,gaps:snapshot.mvp.mapping.gaps,stale:snapshot.mvp.mapping.stale,unaccounted:snapshot.mvp.mapping.unaccounted,complete:snapshot.mvp.mapping.complete},program:snapshot.program.percent,githubError:snapshot.meta.latestRefreshError,queueError:snapshot.meta.queueError},null,2)+'\n');
  }else{
    const server=createDashboardServer();
    server.listen(0,'127.0.0.1',()=>process.stdout.write(`Core Engineers: http://127.0.0.1:${server.address().port}/\n`));
    const close=()=>server.close(()=>process.exit(0));process.on('SIGTERM',close);process.on('SIGINT',close);
  }
}
