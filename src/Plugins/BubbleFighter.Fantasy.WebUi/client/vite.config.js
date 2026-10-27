import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// The preview server can point at any isolated local Host without changing the production build.
const backend = process.env.BFF_WEBUI_BACKEND || 'http://127.0.0.1:8080'
const apiProxy = {
  target: backend,
  changeOrigin: true,
  configure(proxy) {
    proxy.on('proxyReq', (outgoing, incoming) => {
      // Translate only the dev page's own origin. Foreign origins retain their value and are rejected by the API.
      if (incoming.headers.origin === `http://${incoming.headers.host}`) outgoing.setHeader('Origin', backend)
    })
  }
}

// The build lands in ../wwwroot, which the WebUi plugin embeds and which is committed so a
// checkout without Node still serves the interface. Relative asset URLs (base './') keep it
// working under any mount path, and hash routing means the server needs no rewrite rules.
export default defineConfig({
  base: './',
  plugins: [vue()],
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 600
  },
  server: {
    port: 5173,
    // `npm run dev` talks to a running server's admin API on its default address.
    proxy: { '/api': apiProxy, '/tutorial': backend }
  }
})
