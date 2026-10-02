// Fails when a back-compat shim for an older blueprint shape is reintroduced. There is one
// blueprint shape (the C# ServiceBlueprint's): no aliases, fallbacks, compat getters or
// "legacy" containers. Add to BANNED only for a shape that has been deliberately removed.
import { readdirSync, readFileSync, statSync } from 'fs';
import { dirname, join, relative } from 'path';
import { fileURLToPath } from 'url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..');

const BANNED = [
  { name: 'the word "legacy" (say what the thing actually is, or delete it)', pattern: /\blegacy\b/i, exts: ['.ts', '.cs', '.md'] },
  { name: 'compat getter helper', pattern: /defineCompatGetter/, exts: ['.ts'] },
  { name: 'legacy-named normaliser or builder', pattern: /\b(normalise|build)Legacy\w*/, exts: ['.ts'] },
  { name: 'removed editor type (metadata/transition containers)', pattern: /\b(AuthoredTransition|ServiceBlueprintDefinitionMetadata|ServiceBlueprintStateMetadata|ServiceBlueprintTransitionMetadata|ServiceBlueprintConditionDefinition)\b/, exts: ['.ts'] },
  { name: 'authorNote alias for description', pattern: /\bauthorNote\b/, exts: ['.ts'] },
  { name: 'read from a nested metadata container', pattern: /\bmetadata\??\.(gateways|tags|handoffs|queueKey|queueName|actions|roleGates|stageType|description|actor|editorComment|schemaVersion)\b/, exts: ['.ts'] },
  { name: 'Legacy-prefixed property or member', pattern: /\bLegacy[A-Z]\w*/, exts: ['.cs'] },
  { name: 'removed model type or fallback (Metadata containers)', pattern: /\b(StageMetadata|ServiceBlueprintMetadata)\b|Metadata\?\.(Gateways|QueueKey)/, exts: ['.cs'] },
];

const ROOTS = [
  join(repo, 'Wayfinder.Editor.Client', 'src'),
  join(repo, 'Wayfinder'),
  join(repo, 'Wayfinder.Engine'),
  join(repo, 'Wayfinder.Rendering.GovUk'),
  join(repo, 'Wayfinder.Tests'),
  join(repo, 'docs'),
];
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', 'dist']);

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    if (SKIP_DIRS.has(entry)) continue;
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) yield* walk(full);
    else yield full;
  }
}

const findings = [];
for (const root of ROOTS) {
  for (const file of walk(root)) {
    const rules = BANNED.filter(rule => rule.exts.some(ext => file.endsWith(ext)));
    if (rules.length === 0) continue;
    readFileSync(file, 'utf8').split('\n').forEach((line, index) => {
      for (const rule of rules) {
        if (rule.pattern.test(line)) findings.push(`${relative(repo, file)}:${index + 1}  ${rule.name}: ${line.trim().slice(0, 120)}`);
      }
    });
  }
}

if (findings.length > 0) {
  console.error(`Legacy blueprint shapes are not allowed (see CLAUDE.md, "No back-compat shims"):\n${findings.join('\n')}`);
  process.exit(1);
}
console.log('No legacy blueprint shapes found.');
