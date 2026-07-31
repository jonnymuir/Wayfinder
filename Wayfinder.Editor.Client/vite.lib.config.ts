import { defineConfig } from 'vite';

export default defineConfig({
  build: {
    // Library entry consumed by other apps (e.g. Wayfinder.Umbraco's own backoffice
    // bundle), as opposed to vite.service-blueprint-editor.config.ts's standalone HTML
    // app bundle for Wayfinder.Editor. Peer deps stay external so a host app importing
    // this alongside its own React/Lit doesn't ship two copies.
    lib: {
      entry: 'src/index.ts',
      formats: ['es'],
      fileName: () => 'index.js',
    },
    outDir: 'dist',
    emptyOutDir: false,
    sourcemap: true,
    rollupOptions: {
      // Matches 'lit', 'lit/decorators.js', 'react-dom/client', '@xyflow/react', etc.
      // but not the '@xyflow/react/dist/style.css?inline' CSS-as-string import, which
      // needs to stay bundled since a host has no module to resolve it against.
      external: (id) =>
        /^(lit|react|react-dom|@xyflow\/react)(\/|$)/.test(id) && !id.endsWith('.css?inline'),
    },
  },
});
