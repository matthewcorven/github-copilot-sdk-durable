import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { request } from 'node:http';
import { digest,percent,sourceFile,criterionSource,parseTeam,referenceProgram,buildSnapshot } from './model.mjs';
import { createDashboardServer,validateScope } from './server.mjs';
import { buildEvidenceFixture } from './evidence.fixture.mjs';

const scope=JSON.parse(await fs.readFile(new URL('./scope.json',import.meta.url),'utf8'));
async function fixture(t){const dir=await fs.mkdtemp(path.join(os.tmpdir(),'core-engineers-test-'));t.after(()=>fs.rm(dir,{recursive:true,force:true}));return dir;}
async function put(root,file,value){await fs.mkdir(path.dirname(path.join(root,file)),{recursive:true});await fs.writeFile(path.join(root,file),typeof value==='string'?value:JSON.stringify(value));}

test('percentages use actual denominators; zero scope is unknown',()=>{assert.equal(percent(2,3),66.7);assert.equal(percent(0,18),0);assert.equal(percent(0,0),null);});
test('scope has explicit unique criteria, a criterion map and complete member duties',()=>{assert.equal(validateScope(scope).slices.length,6);assert.equal(scope.slices.flatMap(s=>s.criteria).length,18);assert.equal(scope.criterionMap,'docs/core-engineers-criterion-map.json');assert.ok(Number.isSafeInteger(scope.evidenceMaxAgeSeconds));for(const member of ['squad','po','lead','core-engineer','polyglot-engineer','tester','docs','commitments-facilitator','scribe','ralph','rai','fact-checker'])assert.ok(scope.duties[member]);});
test('duplicate criterion scope is rejected',()=>{const bad=structuredClone(scope);bad.slices[1].criteria[0].id=bad.slices[0].criteria[0].id;assert.throws(()=>validateScope(bad),/duplicate/);});
test('unsafe evidence paths in scope are rejected',()=>{const bad=structuredClone(scope);bad.evidenceManifest='../outside.json';assert.throws(()=>validateScope(bad),/repository-relative/);});
test('source digest is stable but changes with content and missing references',async t=>{const root=await fixture(t),criterion={id:'x',sourcePaths:['code.cs']};const missing=await criterionSource(root,criterion);await put(root,'code.cs','one');const first=await criterionSource(root,criterion);assert.equal((await criterionSource(root,criterion)).sourceDigest,first.sourceDigest);await put(root,'code.cs','two');assert.notEqual(first.sourceDigest,(await criterionSource(root,criterion)).sourceDigest);assert.notEqual(missing.sourceDigest,first.sourceDigest);});
test('source paths refuse traversal, git, env and symlink escape',async t=>{const root=await fixture(t),outside=await fixture(t);await put(outside,'outside.txt','fixture');await fs.symlink(path.join(outside,'outside.txt'),path.join(root,'link'));for(const file of ['../outside','/etc/passwd','.git/config','.env','link'])await assert.rejects(sourceFile(root,file));assert.equal(await sourceFile(root,'missing'),null);});
test('reference issue progress excludes epics and deduplicates cross-labelled work',()=>{const issue=(number,state,labels)=>({number,state,title:`issue${number}`,labels:labels.map(name=>({name})),url:`https://github.com/x/y/issues/${number}`});const p=referenceProgram(scope,[issue(1,'CLOSED',['band:a','band:b']),issue(2,'OPEN',['band:a']),issue(3,'CLOSED',['band:a','type:epic'])]);assert.equal(p.total,2);assert.equal(p.closed,1);assert.equal(p.percent,50);assert.equal(p.slices[0].percent,50);assert.equal(p.slices[1].percent,100);assert.equal(referenceProgram(scope,[],true).percent,null);});
test('roster parsing includes coordinator and does not treat headers as members',()=>{const rows=parseTeam('# Team\n## Coordinator\n| Name | Role | Notes |\n|---|---|---|\n| Squad | Coordinator | Core |\n## Members\n| Name | Role | Charter | Status |\n|---|---|---|---|\n| PO | Product Owner | `.squad/agents/po/charter.md` | Active |\n## Other\n| fake | no | no | no |',scope.duties);assert.equal(rows.length,2);assert.equal(rows[1].charterPath,'.squad/agents/po/charter.md');});

test('a committed candidate with no manifest credits nothing and says why',async t=>{
  const f=await buildEvidenceFixture(t,{writeManifest:false});
  const data=await buildSnapshot({root:f.root,scope,meta:{generatedAt:new Date().toISOString(),sourceSha:f.sha},issues:null,queue:null,evidenceRoot:f.root,probe:f.probe,now:Date.parse(f.now)});
  assert.equal(data.mvp.accepted,0);
  assert.equal(data.mvp.percent,0);
  assert.equal(data.mvp.evidence.state,'unavailable');
  assert.match(data.mvp.evidence.error,/No evidence manifest/);
  assert.ok(data.mvp.slices.flatMap(s=>s.criteria).every(c=>c.complete===false));
});

test('a criterion-to-test map with no entry cannot render as complete',async t=>{
  const f=await buildEvidenceFixture(t,{writeMap:false});
  const data=await buildSnapshot({root:f.root,scope,meta:{generatedAt:new Date().toISOString(),sourceSha:f.sha},issues:null,queue:null,evidenceRoot:f.root,probe:f.probe,now:Date.parse(f.now)});
  assert.equal(data.mvp.accepted,0);
  assert.ok(data.mvp.mapping.total>0);
  assert.equal(data.mvp.mapping.complete,false);
  assert.equal(data.mvp.mapping.unaccounted,data.mvp.mapping.total);
  assert.match(data.mvp.mapping.error,/No criterion-to-test map/);
});

test('stale candidate evidence is flagged and never credited',async t=>{
  const f=await buildEvidenceFixture(t,{recordFor:'durable-recovery'});
  const data=await buildSnapshot({root:f.root,scope,meta:{generatedAt:new Date().toISOString(),sourceSha:'b'.repeat(40)},issues:null,queue:null,evidenceRoot:f.root,probe:{...f.probe,candidate:{sha:'b'.repeat(40)}},now:Date.parse(f.now)});
  assert.equal(data.mvp.accepted,0);
  assert.equal(data.mvp.evidence.state,'stale');
  assert.match(data.mvp.evidence.error,/not the probed candidate|not a committed ancestor/);
  assert.equal(data.mvp.candidate.sha,'c'.repeat(40));
  assert.equal(data.mvp.candidate.ancestor,false);
});

test('mapping coverage separates mapped E2E criteria from explicit gaps',async t=>{
  const f=await buildEvidenceFixture(t,{mapFor:'durable-recovery'});
  const data=await buildSnapshot({root:f.root,scope,meta:{generatedAt:new Date().toISOString(),sourceSha:f.sha},issues:null,queue:null,evidenceRoot:f.root,probe:f.probe,now:Date.parse(f.now)});
  assert.equal(data.mvp.mapping.complete,true);
  const row=data.mvp.mapping.rows.find(item=>item.id==='durable-recovery');
  assert.equal(row.mapped,true);
  assert.match(row.reason,/E2E test/);
  assert.ok(data.mvp.mapping.rows.filter(item=>!item.mapped).every(item=>item.gap));
});

test('snapshot shows unknown issue data separately from zero accepted MVP criteria',async t=>{const root=await fixture(t);await put(root,'.squad/team.md','## Members\n| Name | Role | Charter | Status |\n|---|---|---|---|\n| PO | Product Owner | .squad/agents/po/charter.md | Active |');const data=await buildSnapshot({root,scope,meta:{generatedAt:new Date().toISOString(),sourceSha:'a'.repeat(40)},issues:null,queue:null});assert.equal(data.mvp.total,18);assert.equal(data.mvp.percent,0);assert.equal(data.program.percent,null);assert.equal(data.team[0].name,'PO');});
test('dependency labels use authoritative reference shapes without exposing nested bodies',async t=>{const root=await fixture(t);await put(root,'.squad/team.md','');const queue={blocked:[{number:9,title:'blocked',unresolved_dependencies:[{reference:'8',resolved:{number:8,body:'private fixture'},missing:false}],unresolved_blockers:[{reference:'7',missing:true}]}]};const data=await buildSnapshot({root,scope,meta:{generatedAt:new Date().toISOString()},issues:null,queue});assert.equal(data.queue.blocked[0].reason,'#8, #7 (missing)');assert.equal(data.queue.blocked[0].unresolved_dependencies,undefined);});

test('loopback dashboard serves UI and rejects cross-origin/invalid-host writes',async t=>{const server=createDashboardServer();await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));t.after(()=>new Promise(resolve=>server.close(resolve)));const base=`http://127.0.0.1:${server.address().port}`;const page=await fetch(base);assert.equal(page.status,200);assert.match(await page.text(),/Core Engineers/);assert.match(page.headers.get('content-security-policy'),/default-src 'self'/);assert.equal((await fetch(`${base}/api/refresh`,{method:'POST'})).status,403);assert.equal((await fetch(`${base}/api/refresh`,{method:'POST',headers:{'X-Core-Engineers-Refresh':'1',Origin:'https://untrusted.invalid'}})).status,403);const hostileHostStatus=await new Promise((resolve,reject)=>{const req=request(base,{headers:{Host:'untrusted.invalid'}},res=>{res.resume();resolve(res.statusCode);});req.on('error',reject);req.end();});assert.equal(hostileHostStatus,403);assert.equal((await fetch(`${base}/api/source?path=.mcp.json`)).status,403);assert.equal((await fetch(`${base}/unknown`)).status,404);});
