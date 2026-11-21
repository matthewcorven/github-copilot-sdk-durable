import { test, expect } from '@playwright/test';
import fs from 'node:fs/promises';
import { createDashboardServer, refreshCache } from './server.mjs';

let server;
let base;

test.beforeAll(async () => {
  await refreshCache();
  server = createDashboardServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  base = `http://127.0.0.1:${server.address().port}`;
});

test.afterAll(async () => {
  if (server) await new Promise(resolve => server.close(resolve));
});

test('Core Engineers loads real metrics, refreshes provenance and reads cloned source', async ({ page }, testInfo) => {
  await page.coverage.startJSCoverage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const capture = async name => {
    const file = testInfo.outputPath(`${name}.png`);
    await page.screenshot({ path: file, fullPage: true });
    await testInfo.attach(name, { path: file, contentType: 'image/png' });
  };

  await page.goto(base);
  await expect(page.getByRole('heading', { name: 'Core Engineers', exact: true })).toBeVisible();
  await expect(page.getByText('MVP definition awaits Product Owner approval', { exact: true })).toBeVisible();
  const snapshot = await (await page.request.get(`${base}/api/snapshot`)).json();
  expect(snapshot.mvp.slices).toHaveLength(6);
  expect(snapshot.team).toHaveLength(12);
  expect(snapshot.mvp.percent).toBe(Math.round(snapshot.mvp.accepted * 1000 / snapshot.mvp.total) / 10);
  await expect(page.getByText(`${snapshot.mvp.accepted} / ${snapshot.mvp.total} accepted criteria`, { exact: true })).toBeVisible();
  await expect(page.locator('body')).not.toContainText('#unknown');
  await expect(page.locator('body')).toContainText('Not completion, test coverage, or acceptance.');
  await capture('01-overview');

  await page.getByRole('button', { name: /Refresh cache/ }).click();
  await expect.poll(async () => {
    const current = await (await page.request.get(`${base}/api/snapshot`)).json();
    return Date.parse(current.meta.generatedAt);
  }, { timeout: 90000 }).toBeGreaterThan(Date.parse(snapshot.meta.generatedAt));
  await expect(page.getByRole('button', { name: /Refresh cache/ })).toBeEnabled();
  await capture('02-refreshed');

  const source = page.getByRole('button', { name: 'View source: .squad/agents/po/charter.md', exact: true }).first();
  await source.click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('# PO');
  await expect(dialog).toContainText('Product Owner');
  await capture('03-cloned-source');
  await page.getByRole('button', { name: 'Close source viewer' }).click();
  await expect(dialog).not.toBeVisible();

  await page.setViewportSize({ width: 420, height: 900 });
  await page.evaluate(() => window.scrollTo(0, 0));
  await expect(page.getByRole('heading', { name: 'Core Engineers', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
  await capture('04-narrow-canvas');
  expect(errors).toEqual([]);
  const coverage = await page.coverage.stopJSCoverage();
  const coveragePath = testInfo.outputPath('browser-js-coverage.json');
  await fs.writeFile(coveragePath, JSON.stringify(coverage, null, 2));
  await testInfo.attach('browser-js-coverage', { path: coveragePath, contentType: 'application/json' });
  const provenancePath = testInfo.outputPath('snapshot-provenance.json');
  await fs.writeFile(provenancePath, JSON.stringify(snapshot, null, 2));
  await testInfo.attach('snapshot-provenance', { path: provenancePath, contentType: 'application/json' });
});
