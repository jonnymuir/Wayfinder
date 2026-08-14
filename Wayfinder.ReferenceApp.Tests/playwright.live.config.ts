import { defineConfig } from '@playwright/test';

// The live-stack suite (support-systems-live.spec.ts) manages its own AppHost lifecycle via
// LiveAppHost (tests/support/live-app-host.ts) — no `webServer` here, unlike the default
// playwright.config.ts, which boots Wayfinder.ReferenceApp directly and can't exercise Aspire
// service discovery. See docs/guides/support-systems.md for why this needs its own config.
export default defineConfig({
  testDir: './tests',
  testMatch: /support-systems-live\.spec\.ts/,
  timeout: 3 * 60_000,
  retries: 0,
  fullyParallel: false,
  workers: 1,
  use: {
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure'
  }
});
