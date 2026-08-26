import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 桌面应用前端（Tauri dev 模式加载），不再需要 HTTP 后端代理
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
  },
})
