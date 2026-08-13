import { defineConfig } from '@playwright/test';

// Not a test config — a recording tool. Deliberately excluded from CI: no CI script references
// this file, and the demo spec's own path is excluded from both playwright.config.ts (testIgnore)
// and playwright.live.config.ts (testMatch). Produces docs/demos/support-systems-end-to-end.webm.
export default defineConfig({
  testDir: './tests/demo',
  testMatch: /support-systems-demo\.spec\.ts/,
  fullyParallel: false,
  workers: 1,
  timeout: 12 * 60_000,
  expect: { timeout: 30_000 },
  use: {
    ignoreHTTPSErrors: true,
    // Headless Chromium throttles rendering on a backgrounded tab, which has been observed
    // (in Umbraco.Prism's own takes) to visually freeze a recorded video on one frame while the
    // automation underneath keeps working correctly. Set here rather than relying on remembering
    // a --headed flag.
    headless: false,
    // The spec creates and records its own single shared page in beforeAll so every act lands in
    // one continuous video — `use.video` would be a no-op.
    trace: 'off'
  }
});
