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
        // Standalone service blueprint editor host page (V1 planning walkthrough).
        // wayfinder-elements.js (the other public bundle, src/index.ts) is NOT built here —
        // see vite.wayfinder-elements.config.ts for why it needs its own build step.
        'service-blueprint-editor': 'service-blueprint-editor.html',
      },
      output: {
        format: 'es',
        entryFileNames: '[name].js',
        chunkFileNames: '[name]-[hash].js',
      },
    },
  },
});
