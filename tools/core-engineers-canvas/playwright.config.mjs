import { defineConfig } from '@playwright/test';

const runId = process.env.CORE_ENGINEERS_RUN_ID ?? new Date().toISOString().replace(/[:.]/g, '-');
if (!/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(runId)) throw new Error('Invalid browser-test run ID');
process.env.CORE_ENGINEERS_RUN_ID = runId;
const artifactRoot = `../../artifacts/core-engineers-canvas/runs/${runId}`;

export default defineConfig({
  testDir: '.',
  testMatch: 'browser.spec.mjs',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 120000,
  outputDir: `${artifactRoot}/e2e`,
  reporter: [
    ['list'],
    ['json', { outputFile: `${artifactRoot}/e2e-results.json` }],
    ['html', { outputFolder: `${artifactRoot}/playwright-report`, open: 'never' }]
  ],
  use: {
    browserName: 'chromium',
    viewport: { width: 1280, height: 900 },
    screenshot: 'on',
    trace: 'on'
  }
});
