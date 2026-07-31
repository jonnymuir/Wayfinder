import { defineConfig } from 'vite';

export default defineConfig({
  resolve: {
    // A second React copy (e.g. via Storybook's transitive deps) breaks hooks —
    // force a single instance into the bundle.
    dedupe: ['react', 'react-dom'],
  },
  build: {
    // Sends service-blueprint-editor assets to the Wayfinder.Editor package static web assets.
    outDir: '../Wayfinder.Editor/wwwroot/dist',
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: {
      input: {
        // Standalone service blueprint editor host page (V1 planning walkthrough)
        'service-blueprint-editor': 'service-blueprint-editor.html',
        // Self-contained ES module registering the three public custom elements
        // (no HTML wrapper) — for hosts that embed the editor into their own page,
        // e.g. an Umbraco backoffice extension manifest loading it by URL the same
        // way Umbraco loads any other extension bundle. React/Lit/@xyflow ship
        // bundled in, same as the standalone page above — there's no host bundler
        // here to dedupe against, just a browser loading a URL.
        'wayfinder-elements': 'src/index.ts',
      },
      output: {
        format: 'es',
        entryFileNames: '[name].js',
        chunkFileNames: '[name]-[hash].js',
      },
    },
  },
});
