// Runs every src/**/*.unit.test.ts: pure-logic checks, one `run(): number` (failure count) export per module.
// New tests go here rather than getting their own runner script. Vite's SSR loader resolves the editor's
// .js-specifier TS imports without a bundling step.
import { readdirSync, statSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) yield* walk(path);
    else if (name.endsWith('.unit.test.ts')) yield path;
  }
}

const server = await createServer({
  configFile: false,
  root,
  logLevel: 'error',
  optimizeDeps: { noDiscovery: true },
  server: { middlewareMode: true, preTransformRequests: false },
});

let failures = 0;
try {
  for (const file of walk(join(root, 'src'))) {
    console.log(`# ${relative(root, file)}`);
    failures += (await server.ssrLoadModule(`/${relative(root, file)}`)).run();
  }
} finally {
  await server.close();
}
process.exitCode = failures > 0 ? 1 : 0;
