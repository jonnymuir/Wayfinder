import { defineConfig } from '@playwright/test';

const PORT = 5299;
const BASE_URL = `http://127.0.0.1:${PORT}`;

export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  retries: process.env.CI ? 1 : 0,
  // The backend under test is one shared in-memory singleton process — a fixed pair of demo
  // user identities, one "single" instance per (tenant, user, blueprint). There's no per-test
  // tenant isolation (this is a transient reference app, not a multi-tenant one), so specs
  // across every file must run one at a time, not just serially within a file, or two specs
  // logged in as the same demo user race on the same instance.
  fullyParallel: false,
  workers: 1,
  use: {
    baseURL: BASE_URL,
    trace: 'on-first-retry'
  },
  webServer: {
    // Note: the service blueprint editor route needs Wayfinder.Editor.Client's compiled bundle
    // (`npm run build` in ../Wayfinder.Editor.Client) to already exist on disk — this only
    // builds the .NET host, not the editor's own npm project.
    command: `dotnet run --project ../Wayfinder.ReferenceApp --urls ${BASE_URL}`,
    url: BASE_URL,
    env: { ASPNETCORE_ENVIRONMENT: 'Development' },
    reuseExistingServer: !process.env.CI,
    timeout: 120_000
  }
});
