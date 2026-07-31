// Runs the pure service-blueprint-graph layout checks against vendored copies of the
// MockBusinessApp seed service blueprints (payment-demo.json, money-modeller.json — the two
// with real Split/Join gateway topology worth checking). Uses Vite in SSR mode so the
// editor's .js-specifier TS imports resolve without a bundling step.
import { readFileSync } from 'fs';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';
import { createServer } from 'vite';

const here = dirname(fileURLToPath(import.meta.url));
const seedDir = join(here, '..', 'src', 'service-blueprint-editor', 'fixtures');
const seed = name => JSON.parse(readFileSync(join(seedDir, name), 'utf8'));

const server = await createServer({
  configFile: false,
  root: join(here, '..'),
  logLevel: 'error',
  optimizeDeps: { noDiscovery: true },
  server: { middlewareMode: true, preTransformRequests: false },
});

try {
  const mod = await server.ssrLoadModule('/src/service-blueprint-editor/graph/service-blueprint-graph-layout.test.ts');
  const failures = mod.run({
    paymentDemo: seed('payment-demo.json'),
    moneyModeller: seed('money-modeller.json'),
  });
  process.exitCode = failures > 0 ? 1 : 0;
} finally {
  await server.close();
}
