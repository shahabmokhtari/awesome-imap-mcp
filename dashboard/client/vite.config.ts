import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:3847',
      '/hub': {
        target: 'http://localhost:3847',
        ws: true,
      },
    },
  },
  build: {
    outDir: '../../src/UltimateImapMcp.Dashboard/wwwroot',
    emptyOutDir: true,
  },
})
