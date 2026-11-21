const $ = (id) => document.getElementById(id);
const list = (value) => Array.isArray(value) ? value.filter((item) => item && typeof item === 'object') : [];
const paths = (value) => Array.isArray(value) ? value.filter((item) => typeof item === 'string' && item.length > 0) : [];
const text = (value, fallback = 'Not recorded') => typeof value === 'string' && value.trim() ? value : fallback;
const count = (value) => Number.isInteger(value) && value >= 0 ? String(value) : '—';
const percent = (value) => typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 100 ? value : null;
const percentage = (value) => percent(value) === null ? '—' : `${value}%`;
const date = (value) => {
  if (!value) return 'Not recorded';
  const timestamp = new Date(value);
  return Number.isNaN(timestamp.getTime()) ? 'Not recorded' : timestamp.toLocaleString();
};
function node(tag, className, content) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (content !== undefined) element.textContent = String(content);
  return element;
}
function append(parent, ...children) { parent.append(...children); return parent; }
function empty(container, message) { container.replaceChildren(node('p', 'empty', message)); }
let snapshot = null;
let receivedAt = 0;
let busy = false;
let lastRequestError = '';
let lastRefreshError = '';
let allowedPaths = new Set();
let sourceController = null;
let signature = '';

async function request(url, options = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 60000);
  const externalSignal = options.signal;
  const abort = () => controller.abort();
  externalSignal?.addEventListener('abort', abort, { once: true });
  try {
    const response = await fetch(url, { ...options, signal: controller.signal, cache: 'no-store', credentials: 'same-origin' });
    let data;
    try { data = await response.json(); } catch { throw new Error('The server returned an unreadable response.'); }
    if (!response.ok) throw new Error(text(data?.error, `Request failed (HTTP ${response.status}).`));
    return data;
  } finally {
    clearTimeout(timer);
    externalSignal?.removeEventListener('abort', abort);
  }
}
function validateSnapshot(data) {
  if (!data || typeof data !== 'object' || !data.meta || typeof data.meta !== 'object' || !data.mvp || typeof data.mvp !== 'object' || !Number.isFinite(Date.parse(data.meta.generatedAt))) {
    throw new Error('The server did not return a valid project snapshot.');
  }
  return data;
}
function sourceButton(path) {
  const label = `View source: ${path}`;
  const button = node('button', 'source-link', label);
  button.type = 'button';
  button.setAttribute('aria-label', label);
  button.dataset.sourcePath = path;
  button.addEventListener('click', () => openSource(path));
  return button;
}
function sourceLinks(values, label) {
  const group = node('div', 'source-links');
  group.append(node('span', 'small muted', label));
  const entries = paths(values);
  if (!entries.length) group.append(node('span', 'small muted', 'None recorded'));
  for (const path of entries) group.append(sourceButton(path));
  return group;
}
function issueLink(issue) {
  const label = `#${count(issue.number)} ${text(issue.title, 'Untitled issue')}`;
  try {
    const url = new URL(issue.url);
    if (url.protocol === 'https:' && url.hostname === 'github.com' && !url.port && !url.username && !url.password && !url.search && !url.hash && /^\/[^/]+\/[^/]+\/issues\/\d+\/?$/.test(url.pathname)) {
      const link = node('a', 'issue-link', label);
      link.href = url.href;
      link.target = '_blank';
      link.rel = 'noopener noreferrer';
      link.setAttribute('aria-label', `${label} (opens in a new tab)`);
      return link;
    }
  } catch { /* Untrusted or missing links remain plain text. */ }
  return node('span', 'issue-link', `${label} · link unavailable`);
}
function progress(value, label) {
  const bar = node('div', 'progress-track');
  bar.setAttribute('role', 'progressbar');
  bar.setAttribute('aria-label', label);
  bar.setAttribute('aria-valuemin', '0');
  bar.setAttribute('aria-valuemax', '100');
  if (percent(value) !== null) bar.setAttribute('aria-valuenow', String(value));
  bar.setAttribute('aria-valuetext', percent(value) === null ? 'Not available' : `${value}%`);
  const fill = node('span', 'progress-fill');
  fill.style.width = `${percent(value) ?? 0}%`;
  bar.append(fill);
  return bar;
}
function collectPaths(data) {
  const result = new Set();
  const add = (values) => paths(values).forEach((path) => result.add(path));
  for (const slice of list(data.mvp?.slices)) for (const criterion of list(slice.criteria)) {
    add(criterion.sourcePaths); add(criterion.evidencePaths);
  }
  for (const member of list(data.team)) add([member.charterPath]);
  for (const system of list(data.systems)) add(system.paths);
  return result;
}
function renderSlices(data) {
  const container = $('slices');
  container.replaceChildren();
  for (const [index, slice] of list(data.mvp.slices).entries()) {
    const card = node('article', 'slice-card panel');
    const heading = node('div', 'slice-heading');
    append(heading, node('h3', '', text(slice.title)), node('span', 'slice-percent', percentage(slice.percent)));
    const badges = node('div', 'badges');
    append(badges, node('span', 'badge', text(slice.owner, 'Owner unassigned')), node('span', 'badge priority', text(slice.priority, 'Priority unset')));
    append(card, heading, badges, progress(slice.percent, `${text(slice.title)} acceptance`), node('p', 'small', `${count(slice.accepted)} / ${count(slice.total)} criteria accepted`), node('p', 'metric-caption', `Source presence: ${percentage(slice.sourcePercent)} · ${count(slice.sourcePresent)} / ${count(slice.sourceTotal)} files · not completion`));
    const details = node('details', 'criteria');
    details.dataset.key = `slice-${text(slice.id, String(index))}`;
    details.append(node('summary', '', `Inspect criteria (${list(slice.criteria).length})`));
    if (!list(slice.criteria).length) details.append(node('p', 'empty', 'No acceptance criteria recorded.'));
    for (const criterion of list(slice.criteria)) {
      const item = node('div', 'criterion');
      append(item, node('span', criterion.complete === true ? 'criterion-status accepted' : 'criterion-status', criterion.complete === true ? '✓ Accepted' : '○ Not accepted'), node('h4', '', text(criterion.title)), node('p', 'small muted', text(criterion.reason, 'No qualifying evidence reason recorded.')), sourceLinks(criterion.sourcePaths, 'Source'), sourceLinks(criterion.evidencePaths, 'Evidence'));
      if (criterion.sourceDigest) item.append(node('p', 'digest small muted', `Source digest: ${text(criterion.sourceDigest)}`));
      details.append(item);
    }
    card.append(details);
    container.append(card);
  }
  if (!container.children.length) empty(container, 'No MVP slices recorded. Scope is still provisional.');
}
function renderQueue(data) {
  const queue = data.queue ?? {};
  $('next-available').textContent = `Next · ${queue.nextAvailable == null ? 'not recorded' : `#${count(queue.nextAvailable)}`}`;
  $('blocker-count').textContent = `${list(queue.blocked).length} recorded`;
  $('queue-error').hidden = !queue.error;
  $('queue-error').textContent = queue.error ? `Queue unavailable or incomplete: ${text(queue.error)}` : '';
  for (const [id, entries, blocked] of [['priorities', list(queue.available), false], ['blockers', list(queue.blocked), true]]) {
    const container = $(id);
    container.replaceChildren();
    for (const issue of entries) {
      const item = node('article', 'queue-item');
      append(item, issueLink(issue), node('p', 'small muted', blocked ? text(issue.reason, 'Dependency reason not recorded') : `${text(issue.priority, 'Priority unset')} · ${text(issue.owner, 'Owner unassigned')}`));
      container.append(item);
    }
    if (!entries.length) empty(container, queue.error ? 'No reliable queue entries available.' : blocked ? 'No blockers recorded in this snapshot.' : 'No available work recorded in this snapshot.');
  }
}
function renderTeam(data) {
  const container = $('team');
  container.replaceChildren();
  for (const member of list(data.team)) {
    const row = node('tr');
    const person = node('th');
    person.scope = 'row';
    append(person, node('strong', '', text(member.name)), node('span', 'table-role', text(member.role)));
    if (allowedPaths.has(member.charterPath)) person.append(sourceButton(member.charterPath));
    append(row, person, append(node('td'), node('span', 'badge', text(member.status))), node('td', '', text(member.owns)), node('td', 'duty', text(member.updateDuty, 'Individual update duty not recorded')));
    container.append(row);
  }
  if (!container.children.length) {
    const cell = node('td', 'empty', 'No team members recorded.');
    cell.colSpan = 4;
    container.append(append(node('tr'), cell));
  }
}
function renderSystems(data) {
  const container = $('systems');
  container.replaceChildren();
  for (const system of list(data.systems)) {
    const card = node('article', 'system-card');
    const route = node('div', 'system-route');
    append(route, node('span', 'badge', text(system.owner, 'Unassigned')), node('span', 'connector', '→'), node('span', 'small muted', 'System area'));
    append(card, route, node('h3', '', text(system.title)), node('p', 'muted small', text(system.description)), sourceLinks(system.paths, 'Source paths'));
    container.append(card);
  }
  if (!container.children.length) empty(container, 'No system ownership mapped in this snapshot.');
}
function renderProgram(data) {
  const program = data.program ?? {};
  $('program-title').textContent = text(program.title, 'Tracked issue progress');
  $('program-percent').textContent = percentage(program.percent);
  $('program-note').textContent = `${count(program.closed)} / ${count(program.total)} tracked issues closed${program.incomplete ? ' · Incomplete issue inventory' : ''}. ${text(program.note, 'No additional program notes recorded.')}`;
  const container = $('program');
  container.replaceChildren();
  for (const [index, slice] of list(program.slices).entries()) {
    const card = node('div', 'program-band');
    append(card, append(node('div', 'slice-heading'), node('h3', '', text(slice.title)), node('strong', '', percentage(slice.percent))), progress(slice.percent, `${text(slice.title)} closed tracked issues`), node('p', 'small muted', `${count(slice.closed)} / ${count(slice.total)} issues closed`));
    const details = node('details');
    details.dataset.key = `program-${text(slice.id, String(index))}`;
    details.append(node('summary', '', 'Tracked issues'));
    for (const issue of list(slice.issues)) details.append(append(node('div', 'tracked-issue'), issueLink(issue), node('span', 'badge', text(issue.state, 'State unknown'))));
    if (!list(slice.issues).length) details.append(node('p', 'empty', 'No issues recorded.'));
    card.append(details);
    container.append(card);
  }
  if (!container.children.length) empty(container, 'No reference-program bands recorded.');
}
function renderRisksAndSchedules(data) {
  const risks = $('risks');
  risks.replaceChildren();
  for (const risk of list(data.risks)) risks.append(append(node('article', 'queue-item'), node('h3', '', text(risk.title)), node('p', 'small muted', text(risk.detail)), node('span', 'small', `Owner · ${text(risk.owner, 'Unassigned')}`)));
  if (!risks.children.length) empty(risks, 'No additional risks recorded. This does not establish release readiness.');
  const schedules = $('schedules');
  schedules.replaceChildren();
  for (const schedule of list(data.schedules)) schedules.append(append(node('article', 'schedule'), append(node('div', 'slice-heading'), node('h3', '', text(schedule.role)), node('code', '', text(schedule.cron, 'No cron recorded'))), node('p', 'small muted', text(schedule.note))));
  if (!schedules.children.length) empty(schedules, 'No schedules recorded.');
}
function render(data) {
  const openDetails = new Set([...document.querySelectorAll('details[open]')].map((item) => item.dataset.key));
  allowedPaths = collectPaths(data);
  $('scope-note').textContent = `${text(data.meta.scopeNote, 'Product Owner approval is pending.')} Provisional criteria are not a claim of total product readiness.`;
  $('mvp-note').textContent = text(data.mvp.note, 'Explicit criteria with qualifying evidence records; not total product readiness.');
  $('acceptance-count').replaceChildren(document.createTextNode(`${count(data.mvp.accepted)} / ${count(data.mvp.total)} `), node('span', '', 'accepted criteria'));
  $('overall-percent').textContent = percentage(data.mvp.percent);
  const ring = $('overall-progress');
  ring.style.setProperty('--progress', `${percent(data.mvp.percent) ?? 0}%`);
  ring.removeAttribute('aria-valuenow');
  if (percent(data.mvp.percent) !== null) ring.setAttribute('aria-valuenow', String(data.mvp.percent));
  ring.setAttribute('aria-valuetext', `${percentage(data.mvp.percent)} evidence-linked acceptance, ${count(data.mvp.accepted)} of ${count(data.mvp.total)} criteria accepted. Scope provisional.`);
  $('source-percent').textContent = percentage(data.mvp.sourcePercent);
  $('source-count').textContent = `${count(data.mvp.sourcePresent)} / ${count(data.mvp.sourceTotal)} referenced files present`;
  $('source-summary').textContent = `${text(data.meta.branch, 'Branch unknown')} · ${text(data.meta.sourceSha, 'SHA unknown').slice(0, 12)} · ${data.meta.sourceDirty ? 'Working tree modified' : 'Read-cache source'}`;
  renderSlices(data); renderQueue(data); renderTeam(data); renderSystems(data); renderProgram(data); renderRisksAndSchedules(data);
  const provenance = $('provenance');
  provenance.replaceChildren();
  for (const [label, value] of [['Source SHA', text(data.meta.sourceSha)], ['Generated', date(data.meta.generatedAt)], ['Scope digest', text(data.meta.scopeDigest)], ['GitHub fetched', date(data.meta.githubFetchedAt)], ['Queue generated', date(data.meta.queueGeneratedAt)], ['Scope', 'Provisional · pending Product Owner approval']]) {
    append(provenance, node('dt', '', label), node('dd', '', value));
  }
  for (const details of document.querySelectorAll('details')) details.open = openDetails.has(details.dataset.key);
}
function renderStatus() {
  const badge = $('freshness-badge');
  const message = $('connection-message');
  const errors = [lastRequestError, lastRefreshError, snapshot?.meta?.latestRefreshError ? `Cache refresh: ${text(snapshot.meta.latestRefreshError)}` : ''].filter(Boolean);
  $('error-message').hidden = !errors.length;
  $('error-message').textContent = [...new Set(errors)].join(' ');
  if (!snapshot) {
    badge.textContent = errors.length ? 'Snapshot unavailable' : 'Connecting';
    badge.className = `badge ${errors.length ? 'warning' : ''}`;
    message.textContent = errors.length ? 'No valid snapshot received. Refresh the cache to try again; metrics remain unknown.' : 'Loading project evidence…';
    return;
  }
  const generatedAge = Math.max(0, (Date.now() - Date.parse(snapshot.meta.generatedAt)) / 1000);
  const recordedAge = typeof snapshot.freshness?.ageSeconds === 'number' ? snapshot.freshness.ageSeconds + (Date.now() - receivedAt) / 1000 : 0;
  const age = Math.max(generatedAge, recordedAge);
  const maxAge = typeof snapshot.meta.maxAgeSeconds === 'number' && snapshot.meta.maxAgeSeconds > 0 ? snapshot.meta.maxAgeSeconds : 3600;
  const stale = snapshot.freshness?.stale === true || age > maxAge;
  badge.textContent = lastRequestError ? 'Connection error · cached view' : stale ? 'Stale snapshot' : 'Snapshot current';
  badge.className = `badge ${stale || lastRequestError ? 'warning' : 'fresh'}`;
  message.textContent = `${lastRequestError ? 'Offline or snapshot fetch failed. Showing last valid data.' : stale ? 'Stale evidence: refresh the cache before relying on this view.' : 'Read-cache snapshot loaded. Acceptance scope is provisional.'} Last valid snapshot: ${date(snapshot.meta.generatedAt)} · age ${Math.floor(age / 60)} min.`;
  message.className = `notice ${stale || lastRequestError ? 'caution' : ''}`;
}
async function loadSnapshot(refresh = false) {
  if (busy) return;
  busy = true;
  $('refresh').disabled = true;
  $('refresh').textContent = refresh ? '↻ Refreshing cache…' : '↻ Checking snapshot…';
  try {
    const data = validateSnapshot(await request(refresh ? '/api/refresh' : '/api/snapshot', refresh ? { method: 'POST', headers: { 'X-Core-Engineers-Refresh': '1' } } : {}));
    snapshot = data;
    receivedAt = Date.now();
    lastRequestError = '';
    if (refresh) lastRefreshError = '';
    const nextSignature = JSON.stringify(data);
    if (signature !== nextSignature) { render(data); signature = nextSignature; }
  } catch (error) {
    const detail = error.name === 'AbortError' ? 'Request timed out. Please try again.' : error.message;
    if (refresh) lastRefreshError = `Refresh failed: ${detail} Last valid data retained.`;
    else lastRequestError = `Snapshot fetch failed: ${detail}`;
  } finally {
    busy = false;
    $('refresh').disabled = false;
    $('refresh').textContent = '↻ Refresh cache';
    renderStatus();
  }
}
async function openSource(path) {
  if (!allowedPaths.has(path)) return;
  sourceController?.abort();
  const controller = new AbortController();
  sourceController = controller;
  $('source-title').textContent = path;
  $('source-status').textContent = 'Loading approved source from the read cache…';
  $('source-content').textContent = '';
  if (!$('source-dialog').open) $('source-dialog').showModal();
  try {
    const data = await request(`/api/source?path=${encodeURIComponent(path)}`, { signal: controller.signal });
    if (controller.signal.aborted) return;
    if (data?.path !== path || typeof data?.content !== 'string') throw new Error('The source response did not match the requested file.');
    $('source-content').textContent = data.content;
    $('source-status').textContent = 'Read-only source · content shown as plain text';
  } catch (error) {
    if (controller.signal.aborted) return;
    $('source-status').textContent = `Source unavailable: ${error.name === 'AbortError' ? 'Request timed out.' : error.message}`;
  }
}
$('refresh').addEventListener('click', () => loadSnapshot(true));
$('close-source').addEventListener('click', () => $('source-dialog').close());
$('source-dialog').addEventListener('close', () => sourceController?.abort());
window.addEventListener('online', () => loadSnapshot());
window.addEventListener('offline', () => { lastRequestError = 'Browser reports offline. Last valid data retained.'; renderStatus(); });
loadSnapshot();
setInterval(() => loadSnapshot(), 30000);
setInterval(renderStatus, 15000);
