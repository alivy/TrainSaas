import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url))
    }
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5080',
        changeOrigin: true
      }
    }
  },
  build: {
    // Element Plus 全量引入（计划约束）：vendor 单独分包后体积仍 >500KB，属预期；按需加载留待后续优化
    chunkSizeWarningLimit: 1200,
    rollupOptions: {
      output: {
        manualChunks: {
          'element-plus': ['element-plus'],
          vue: ['vue', 'vue-router', 'pinia']
        }
      }
    }
  },
  test: {
    environment: 'jsdom',
    globals: true
  }
})
