// Drives the Band A page in a real browser: the only entry point the baseline has.
//
//   node driver.js <spec.json> <result.json>
//
// The spec is a small command list. Every step is recorded with what it observed, so the shell script
// that invoked it can say "the card appeared", "the dashboard settled", or "a second card appeared"
// from evidence rather than from a guess. The process exits 0 whenever the driver ran to completion,
// and non-zero only when the driver itself could not run: a missing browser, a page that did not load,
// a selector the page does not have. Those are harness errors, and the fault scripts say so.

const fs = require('fs');

const [specPath, resultPath] = process.argv.slice(2);
if (!specPath || !resultPath) {
  console.error('usage: node driver.js <spec.json> <result.json>');
  process.exit(2);
}

const spec = JSON.parse(fs.readFileSync(specPath, 'utf8'));
const { chromium } = require(spec.playwright);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const result = { steps: [], console: [], errors: [], ok: false };

function record(kind, detail, extra) {
  result.steps.push(Object.assign({ kind, detail }, extra || {}));
}

async function bodyText(page) {
  try {
    return await page.locator('body').innerText();
  } catch (e) {
    return '';
  }
}

// The dashboard is the only place both variants put their counts, so it is read as three numbers and
// a comparison never depends on where on the page they are rendered.
function dashboard(text) {
  const m = text.match(/DASHBOARD\s*([\s\S]*)/i);
  if (!m) return null;
  const nums = (m[1].match(/\d+/g) || []).map(Number).slice(0, 3);
  if (nums.length < 3) return null;
  return { processing: nums[0], completed: nums[1], rejected: nums[2] };
}

// Cards carry the enquiry text; that is the one identity both variants expose to a reader.
async function cards(page) {
  return page.$$eval('button.absolute.z-20', (els) =>
    els.map((el) => {
      const spans = el.querySelectorAll('span');
      const inner = el.querySelectorAll('div');
      return {
        title: spans.length ? spans[spans.length - 1].innerText.trim() : el.innerText.trim(),
        intent: inner.length ? inner[inner.length - 1].innerText.trim() : '',
      };
    })
  );
}

async function openMenu(page) {
  await page.locator('div.absolute.top-3.right-8 > button').first().click();
  await page.waitForTimeout(400);
}

async function option(page, text) {
  const locator = page.locator('div.absolute.right-0.top-full button', { hasText: text }).first();
  if ((await locator.count()) === 0) {
    throw new Error(`the page offers no sample enquiry matching ${JSON.stringify(text)}`);
  }
  return locator;
}

async function main() {
  if (!spec.url) throw new Error('spec.url is required');
  const launch = { headless: true };
  if (spec.executablePath) launch.executablePath = spec.executablePath;
  const browser = await chromium.launch(launch);
  const page = await browser.newPage();
  page.on('console', (m) => {
    if (m.type() === 'error') result.console.push(m.text());
  });
  page.on('pageerror', (e) => result.errors.push(String(e)));

  try {
    await page.goto(spec.url, { waitUntil: 'domcontentloaded', timeout: 30000 });
    await page.waitForFunction(
      () => document.body && document.body.innerText.toUpperCase().includes('DASHBOARD'),
      null,
      { timeout: 30000 }
    );
    // The interactive circuit connects after the server render; the click handlers only exist once it
    // has, so the driver starts from a page that can actually be clicked.
    await page
      .waitForFunction(() => document.body.innerText.indexOf('An unhandled error') === -1, null, { timeout: 15000 })
      .catch(() => {});
    await sleep(1500);
    record('connected', await page.title(), { dashboard: dashboard(await bodyText(page)) });

    for (const step of spec.actions || []) {
      if (step.kind === 'submit') {
        await openMenu(page);
        await (await option(page, step.text)).click();
        await page.waitForTimeout(300);
        record('submit', step.text, { dashboard: dashboard(await bodyText(page)) });
      } else if (step.kind === 'submit-twice') {
        // Two clicks on the same entry with no pause between them: the duplicate a double-submitted
        // form produces, not two submissions separated by a decision.
        await openMenu(page);
        await (await option(page, step.text)).dblclick();
        record('submit-twice', step.text);
      } else if (step.kind === 'submit-concurrently') {
        // Two different entries, each clicked as soon as the menu reopens: the shape of two customers
        // submitting at once from the browser the page is written for.
        await openMenu(page);
        await (await option(page, step.texts[0])).click();
        await page.waitForTimeout(150);
        await openMenu(page);
        await (await option(page, step.texts[1])).click();
        record('submit-concurrently', step.texts.join(' | '), { dashboard: dashboard(await bodyText(page)) });
      } else if (step.kind === 'click-card') {
        const card = page.locator('button.absolute.z-20', { hasText: step.text }).first();
        if ((await card.count()) === 0) throw new Error(`no card matching ${JSON.stringify(step.text)}`);
        await card.click();
        await page.waitForTimeout(400);
        record('click-card', step.text);
      } else if (step.kind === 'wait-for-card') {
        const deadline = Date.now() + (step.timeoutMs || 60000);
        let seen = 0;
        while (Date.now() < deadline) {
          seen = (await page.locator('button.absolute.z-20', { hasText: step.text }).count()) || 0;
          if (seen >= (step.count || 1)) break;
          await sleep(200);
        }
        record('wait-for-card', step.text, { count: seen });
        if (seen < (step.count || 1)) throw new Error(`card ${JSON.stringify(step.text)} never appeared`);
      } else if (step.kind === 'wait-dashboard') {
        const deadline = Date.now() + (step.timeoutMs || 120000);
        let dash = null;
        while (Date.now() < deadline) {
          dash = dashboard(await bodyText(page));
          if (dash && Object.keys(step.expect || {}).every((k) => dash[k] === step.expect[k])) break;
          await sleep(250);
        }
        record('wait-dashboard', JSON.stringify(step.expect), { dashboard: dash });
        if (!dash || !Object.keys(step.expect || {}).every((k) => dash[k] === step.expect[k])) {
          throw new Error(`dashboard never reached ${JSON.stringify(step.expect)}; last ${JSON.stringify(dash)}`);
        }
      } else if (step.kind === 'wait') {
        await sleep(step.ms || 1000);
        record('wait', String(step.ms || 1000));
      } else if (step.kind === 'snapshot') {
        const text = await bodyText(page);
        record('snapshot', step.label || '', {
          dashboard: dashboard(text),
          cards: await cards(page),
          excerpt: text.replace(/\s+/g, ' ').slice(0, 500),
        });
      } else {
        throw new Error(`unknown step kind ${JSON.stringify(step.kind)}`);
      }
    }

    const text = await bodyText(page);
    result.final = { dashboard: dashboard(text), cards: await cards(page) };
    result.ok = true;
  } finally {
    await browser.close().catch(() => {});
  }
}

main()
  .then(() => {
    fs.writeFileSync(resultPath, JSON.stringify(result, null, 2));
    process.exit(0);
  })
  .catch((e) => {
    result.errors.push(String((e && e.message) || e));
    try {
      fs.writeFileSync(resultPath, JSON.stringify(result, null, 2));
    } catch (_) {}
    console.error('driver: ' + ((e && e.message) || e));
    process.exit(1);
  });
