import { svelte } from '@sveltejs/vite-plugin-svelte'
import { defineConfig } from 'vite'

// The WPF host looks for the dev server on this exact port (see MainWindow.ResolveStartUriAsync).
export default defineConfig({
  plugins: [svelte()],
  server: { port: 5173, strictPort: true },
})
