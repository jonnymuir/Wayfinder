// Runs server-diagnostic-location.test.ts — pure checks, no fixtures needed. Uses Vite in SSR
// mode so the editor's .js-specifier TS imports resolve without a bundling step, same as
// run-calculation-diagnostics-tests.mjs.
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import { createServer } from 'vite';

const here = dirname(fileURLToPath(import.meta.url));

const server = await createServer({
  configFile: false,
  root: join(here, '..'),
  logLevel: 'error',
  optimizeDeps: { noDiscovery: true },
  server: { middlewareMode: true, preTransformRequests: false },
});

try {
  const mod = await server.ssrLoadModule('/src/service-blueprint-editor/server-diagnostic-location.test.ts');
  const failures = mod.run();
  process.exitCode = failures > 0 ? 1 : 0;
} finally {
  await server.close();
}
