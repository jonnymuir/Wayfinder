// Guards the class of bug CodeQL cannot see here: building HTML from data in client code. CodeQL's
// js/xss does not treat fetch() responses as untrusted, and its data flow loses the trail through a
// textContent -> innerHTML "escape" helper, so a stored XSS in the bulk-data review shipped with every
// scan green (see the security review: it is reproduced with CodeQL 2.27.1 and the CI suite).
//
// Two rules, over the shipped browser code (Rendering.GovUk's static JS and the editor's TS):
//   1. A dangerous HTML sink (.innerHTML/.outerHTML =, insertAdjacentHTML, document.write, unsafeHTML,
//      .srcdoc =, createContextualFragment, dangerouslySetInnerHTML) may only take a literal. Anything
//      built from data needs a `// html-sink-ok: <why this is safe>` comment on the line or just above,
//      so a new one is a deliberate, reviewed act. The reason must name the escaping that makes it safe.
//   2. `return el.innerHTML` is banned: reading innerHTML back as an "escaper" does not escape quotes,
//      which is exactly the bulk-data review bug (value="…" broke out of its attribute).
import { readdirSync, readFileSync, statSync } from 'fs';
import { dirname, join, relative } from 'path';
import { fileURLToPath } from 'url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..');
const ROOTS = [join(repo, 'Wayfinder.Rendering.GovUk', 'wwwroot', 'js'), join(repo, 'Wayfinder.Editor.Client', 'src')];
const SKIP_DIRS = new Set(['node_modules', 'dist', 'bin', 'obj', 'fixtures']);
const SKIP_FILE = /\.(test|spec|stories)\.ts$/;

const SINK = /(\.(innerHTML|outerHTML|srcdoc)\s*=(?!=)|\binsertAdjacentHTML\s*\(|\bdocument\.write(ln)?\s*\(|\bunsafeHTML\s*\(|\bcreateContextualFragment\s*\(|\bdangerouslySetInnerHTML\b)/;
const ESCAPE_BY_READBACK = /\breturn\s+[\w.$]+\.innerHTML\b/;
const MARKER = /html-sink-ok:\s*\S+/;

// Static RHS: '' / "text" / `text` with no interpolation, and nothing concatenated after it.
function takesOnlyALiteral(line) {
  const rhs = line.slice(line.search(SINK)).replace(/^[^=(]*[=(]\s*/, '');
  return /^(''|""|'[^'\\]*'|"[^"\\]*"|`[^`$\\]*`)\s*[;)]?\s*(\/\/.*)?$/.test(rhs.trim());
}

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    if (SKIP_DIRS.has(entry)) continue;
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) yield* walk(full);
    else if (/\.(js|mjs|ts)$/.test(entry) && !SKIP_FILE.test(entry)) yield full;
  }
}

const findings = [];
for (const root of ROOTS) {
  for (const file of walk(root)) {
    const lines = readFileSync(file, 'utf8').split('\n');
    lines.forEach((line, index) => {
      const code = line.replace(/^\s*(\/\/.*)$/, '');
      const where = `${relative(repo, file)}:${index + 1}`;
      if (ESCAPE_BY_READBACK.test(code)) {
        findings.push(`${where}  innerHTML read back as an escaper (does not escape quotes): ${line.trim().slice(0, 100)}`);
      }
      if (SINK.test(code) && !takesOnlyALiteral(code)) {
        const nearby = lines.slice(Math.max(0, index - 3), index + 1).join('\n');
        if (!MARKER.test(nearby)) {
          findings.push(`${where}  HTML sink built from data without an "html-sink-ok: <reason>" comment: ${line.trim().slice(0, 100)}`);
        }
      }
    });
  }
}

if (findings.length > 0) {
  console.error(`Unsafe HTML sinks (see CLAUDE.md, security rules):\n${findings.join('\n')}`);
  process.exit(1);
}
console.log('No unmarked HTML sinks built from data.');
