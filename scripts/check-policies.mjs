// Enforces the repo's size and complexity policies (see CLAUDE.md, "Keep things small").
//
//   node scripts/check-policies.mjs            fail if a file or C# method grew past its budget or baseline, or a new
//                                              `as unknown as` cast appeared (a cast that hides a model mismatch)
//   node scripts/check-policies.mjs --update   lower the baseline to today's sizes (never raises it)
//   node scripts/check-policies.mjs --stale    fail if a complexity-baseline entry no longer has a finding
//                                              (slow: rebuilds without the C# baseline and re-runs Biome)
//
// Budgets: C# file 400 lines, C# method 60 lines; TypeScript/JavaScript file 500 lines. Anything already over is
// recorded in .policy/size-baseline.json at its current size and may only shrink.
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';

const root = new URL('..', import.meta.url).pathname.replace(/\/$/, '');
const baselinePath = join(root, '.policy', 'size-baseline.json');
const FILE_BUDGET = { '.cs': 400, '.ts': 500, '.tsx': 500, '.js': 500, '.mjs': 500 };
const METHOD_BUDGET = 60;
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', 'generated', 'storybook-static', 'test-results', '.git', 'dist', 'fixtures', 'Wayfinder.Tests', 'tests', 'lib']);
const SKIP_FILE = /\.(test|stories|spec)\.(ts|tsx)$|\.d\.ts$/;

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    if (SKIP_DIRS.has(name)) continue;
    const path = join(dir, name);
    const info = statSync(path);
    if (info.isDirectory()) yield* walk(path);
    else if (FILE_BUDGET[name.slice(name.lastIndexOf('.'))] && !SKIP_FILE.test(name)) yield path;
  }
}

/** Longest C# member in lines, judged by formatted-code structure: a member starts at an indented modifier line and ends at the matching closing brace. */
function longestCSharpMethod(lines) {
  let longest = 0;
  const start = /^(\s{4,12})(public|private|protected|internal)\b[^;=]*\([^;]*$/;
  for (let i = 0; i < lines.length; i++) {
    const match = start.exec(lines[i]);
    if (!match || /\b(class|record|struct|interface|enum)\b/.test(lines[i])) continue;
    const indent = match[1];
    let end = -1;
    for (let j = i; j < lines.length && j < i + 2000; j++) {
      if (j > i && lines[j] === `${indent}}`) { end = j; break; }
      if (j > i && lines[j].startsWith(indent) && /;\s*$/.test(lines[j]) && !lines[j].startsWith(`${indent} `) && j > i) { end = j; break; }
    }
    if (end > 0) longest = Math.max(longest, end - i + 1);
  }
  return longest;
}

function measure() {
  const files = {};
  const methods = {};
  const casts = {};
  for (const path of walk(root)) {
    const rel = relative(root, path);
    const lines = readFileSync(path, 'utf8').split('\n');
    const count = lines.at(-1) === '' ? lines.length - 1 : lines.length;
    const budget = FILE_BUDGET[path.slice(path.lastIndexOf('.'))];
    if (count > budget) files[rel] = count;
    if (/\.tsx?$/.test(path)) {
      const found = lines.filter((line) => line.includes('as unknown as')).length;
      if (found > 0) casts[rel] = found;
    }
    if (path.endsWith('.cs')) {
      const longest = longestCSharpMethod(lines);
      if (longest > METHOD_BUDGET) methods[rel] = longest;
    }
  }
  return { files, methods, casts };
}

function compare(kind, actual, baseline, budget, errors, tighten) {
  for (const [path, size] of Object.entries(actual)) {
    const allowed = baseline[path];
    if (allowed === undefined) errors.push(`${path}: ${size} ${kind} is over the budget of ${budget}. Split it (new code must stay within budget).`);
    else if (size > allowed) errors.push(`${path}: grew to ${size} ${kind} (baseline ${allowed}). It may only shrink.`);
    else if (size < allowed) tighten.push(`${path}: ${allowed} -> ${size}`);
  }
  for (const path of Object.keys(baseline)) {
    if (!(path in actual)) tighten.push(`${path}: now within budget, remove from baseline`);
  }
}

function checkSizes(update) {
  const actual = measure();
  const baseline = existsSync(baselinePath) ? JSON.parse(readFileSync(baselinePath, 'utf8')) : { files: {}, methods: {}, casts: {} };
  baseline.casts ??= {};
  if (update) {
    const next = { files: {}, methods: {}, casts: {} };
    for (const kind of ['files', 'methods', 'casts']) {
      for (const [path, size] of Object.entries(actual[kind])) next[kind][path] = Math.min(size, baseline[kind][path] ?? size);
    }
    writeFileSync(baselinePath, `${JSON.stringify(next, null, 2)}\n`);
    console.log(`Baseline written: ${Object.keys(next.files).length} large files, ${Object.keys(next.methods).length} files with a long method.`);
    return 0;
  }
  const errors = [];
  const tighten = [];
  compare('lines', actual.files, baseline.files, 'the file budget', errors, tighten);
  compare('lines (longest method)', actual.methods, baseline.methods, `${METHOD_BUDGET}`, errors, tighten);
  compare('`as unknown as` casts', actual.casts, baseline.casts, '0', errors, tighten);
  for (const message of errors) console.error(`FAIL ${message}`);
  if (errors.length === 0 && tighten.length > 0) {
    console.error('FAIL baseline is stale; run `node scripts/check-policies.mjs --update` and commit it:');
    for (const message of tighten) console.error(`  ${message}`);
    return 1;
  }
  if (errors.length === 0) console.log('Size policies hold.');
  return errors.length === 0 ? 0 : 1;
}

function runStaleChecks() {
  const failures = [];

  // C#: rebuild without the baseline block; every baselined file must show a finding.
  const editorconfig = join(root, '.editorconfig');
  const original = readFileSync(editorconfig, 'utf8');
  const block = /# BEGIN complexity baseline[^\n]*\n([\s\S]*?)# END complexity baseline/.exec(original);
  const listed = block ? [...block[1].matchAll(/^\[(.+)\]$/gm)].map((m) => m[1]) : [];
  try {
    writeFileSync(editorconfig, original.replace(block[0], ''));
    let output = '';
    try {
      output = execFileSync('dotnet', ['build', 'Wayfinder.slnx', '-c', 'Release', '--no-incremental', '-p:TreatWarningsAsErrors=false'], { cwd: root, encoding: 'utf8', maxBuffer: 1 << 28 });
    } catch (error) {
      output = `${error.stdout ?? ''}${error.stderr ?? ''}`;
    }
    for (const path of listed) {
      if (!new RegExp(`${path.replaceAll('.', '\\.')}\\(\\d+,\\d+\\): warning CA150[26]`).test(output)) failures.push(`${path}: no CA1502/CA1506 finding any more; remove its section from the .editorconfig baseline.`);
    }
  } finally {
    writeFileSync(editorconfig, original);
  }

  // TypeScript: strip the baseline override from a temp Biome config; every listed file must show a finding.
  const clientDir = join(root, 'Wayfinder.Editor.Client');
  const config = readFileSync(join(clientDir, 'biome.jsonc'), 'utf8')
    .split('\n').filter((line) => !line.trim().startsWith('//')).join('\n');
  const parsed = JSON.parse(config);
  const baselineOverride = parsed.overrides.at(-1);
  parsed.overrides = parsed.overrides.slice(0, -1);
  const temp = mkdtempSync(join(tmpdir(), 'biome-'));
  const tempConfig = join(clientDir, 'biome.stale-check.json');
  try {
    writeFileSync(tempConfig, JSON.stringify(parsed));
    copyFileSync(tempConfig, join(temp, 'biome.json'));
    let output = '';
    try {
      output = execFileSync('npx', ['biome', 'lint', `--config-path=${tempConfig}`, '--max-diagnostics=5000', '--colors=off'], { cwd: clientDir, encoding: 'utf8', maxBuffer: 1 << 28 });
    } catch (error) {
      output = `${error.stdout ?? ''}${error.stderr ?? ''}`;
    }
    for (const path of baselineOverride.includes) {
      if (!new RegExp(`^${path.replaceAll('.', '\\.')}:\\d+:\\d+ lint/complexity/noExcessive`, 'm').test(output)) failures.push(`${path}: no complexity finding any more; remove it from biome.jsonc's baseline override.`);
    }
  } finally {
    rmSync(tempConfig, { force: true });
    rmSync(temp, { recursive: true, force: true });
  }

  for (const message of failures) console.error(`FAIL ${message}`);
  if (failures.length === 0) console.log('Complexity baselines are tight.');
  return failures.length === 0 ? 0 : 1;
}

const mode = process.argv[2];
process.exitCode = mode === '--stale' ? runStaleChecks() : checkSizes(mode === '--update');
