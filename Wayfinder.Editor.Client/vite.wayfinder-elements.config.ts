import { defineConfig } from 'vite';

// A dedicated build step for wayfinder-elements.js only. Unlike the standalone
// service-blueprint-editor.html page, hosts that embed the editor (e.g.
// Wayfinder.Umbraco.Client's wayfinder-elements-bundle.ts) dynamically import this module and
// read specific NAMED exports off it at runtime (hydrateServiceBlueprintDefinition,
// ServiceBlueprintSaveError, etc.) — not just registering custom elements as a side effect.
//
// Building this alongside vite.service-blueprint-editor.config.ts's other entry (which has no
// `preserveEntrySignatures` set) lets Rollup rename every export to a single letter — confirmed
// live: a real Wayfinder.Umbraco consumer's destructured `{ hydrateServiceBlueprintDefinition }`
// silently resolved to `undefined` and threw "... is not a function" deep in a minified call
// site, because the real export was renamed to something like `h`. The Umbraco backoffice
// manifests bundle (vite.wayfinder-service-blueprint-manifests.config.ts, a different repo) hit
// the identical failure mode for the same underlying reason and already documents the fix:
// `preserveEntrySignatures: 'strict'` — but set globally it re-chunks every entry, so this one
// gets its own build step instead of forcing that setting onto the HTML entry too.
export default defineConfig({
  resolve: {
    dedupe: ['react', 'react-dom'],
  },
  build: {
    outDir: '../Wayfinder.Editor/wwwroot/dist',
    // Never wipe the directory here — vite.service-blueprint-editor.config.ts's own build
    // already populated it with the standalone page; this step only adds to it.
    emptyOutDir: false,
    sourcemap: true,
    rollupOptions: {
      input: {
        // Self-contained ES module registering the three public custom elements (no HTML
        // wrapper) — for hosts that embed the editor into their own page, e.g. an Umbraco
        // backoffice extension manifest loading it by URL the same way Umbraco loads any
        // other extension bundle. React/Lit/@xyflow ship bundled in, same as the standalone
        // page — there's no host bundler here to dedupe against, just a browser loading a URL.
        'wayfinder-elements': 'src/index.ts',
      },
      output: {
        format: 'es',
        entryFileNames: '[name].js',
        chunkFileNames: '[name]-[hash].js',
      },
      preserveEntrySignatures: 'strict',
    },
  },
});
