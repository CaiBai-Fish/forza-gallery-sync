import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 桌面应用前端（Tauri dev 模式加载），不再需要 HTTP 后端代理
export default defineConfig({
  plugins: [vue()],
  // Tauri 生产环境通过自定义协议(tauri://localhost)加载前端，
  // 必须用相对路径，否则 /assets/... 绝对路径无法解析导致白屏
  base: './',
  server: {
    // Tauri/WebView2 解析 localhost 时会优先走 IPv4，
    // 固定监听 127.0.0.1，避免 Vite 只绑到 [::1] 导致连接被拒绝
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
  },
  watch: {
    // 这些目录会在 Tauri 开发/打包时被大量复制，不需要 Vite 监听并触发刷新
    ignored: ['**/src-tauri/target/**', '**/runtime/**'],
  },
})
