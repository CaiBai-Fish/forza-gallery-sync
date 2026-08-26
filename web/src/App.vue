<script setup>
import { computed, onMounted, provide, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import DashboardView from './views/DashboardView.vue'
import GalleryView from './views/GalleryView.vue'
import SyncView from './views/SyncView.vue'
import SettingsView from './views/SettingsView.vue'

const { t } = useI18n()

onMounted(() => {
  // 屏蔽 WebView2 / 浏览器默认右键菜单（自定义菜单由各视图提供）
  window.addEventListener('contextmenu', (e) => e.preventDefault())
})

const current = ref('dashboard')

const views = {
  dashboard: DashboardView,
  gallery: GalleryView,
  sync: SyncView,
  settings: SettingsView,
}

const nav = computed(() => [
  { id: 'dashboard', title: t('app.nav.dashboard'), icon: '📊' },
  { id: 'gallery', title: t('app.nav.gallery'), icon: '🖼️' },
  { id: 'sync', title: t('app.nav.sync'), icon: '🔄' },
  { id: 'settings', title: t('app.nav.settings'), icon: '⚙️' },
])

const currentView = computed(() => views[current.value])

function navigate(id) {
  current.value = id
}

// 供各视图内部按钮跳转导航
provide('navigate', navigate)
</script>

<template>
  <div class="app">
    <aside class="sidebar">
      <div class="brand">
        <span class="brand-name">{{ t('app.brand') }}</span>
      </div>

      <nav class="nav">
        <button
          v-for="item in nav"
          :key="item.id"
          class="nav-item"
          :class="{ active: current === item.id }"
          @click="navigate(item.id)"
        >
          <span class="nav-icon">{{ item.icon }}</span>
          <span class="nav-label">{{ item.title }}</span>
        </button>
      </nav>

      <div class="sidebar-footer">
        <span class="kbd">{{ t('app.footer') }}</span>
        <span class="version">{{ t('app.version') }}</span>
      </div>
    </aside>

    <main class="content">
      <component :is="currentView" />
    </main>
  </div>
</template>

<style scoped>
.app {
  display: flex;
  height: 100%;
  width: 100%;
  background: linear-gradient(160deg, var(--desktop-bg-1), var(--desktop-bg-2) 60%, var(--desktop-bg-3));
}

/* ---------- 侧边栏 ---------- */
.sidebar {
  width: 220px;
  flex: 0 0 220px;
  display: flex;
  flex-direction: column;
  background: rgba(10, 14, 28, 0.85);
  border-right: 1px solid var(--win-border);
  backdrop-filter: blur(12px);
}

.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 20px 18px;
  border-bottom: 1px solid var(--win-border);
}
.brand-name {
  font-size: 15px;
  font-weight: 700;
  background: linear-gradient(135deg, var(--accent), var(--accent-2));
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}

.nav {
  flex: 1;
  padding: 12px 10px;
  display: flex;
  flex-direction: column;
  gap: 4px;
  overflow-y: auto;
}
.nav-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 11px 14px;
  border: none;
  border-radius: 9px;
  background: transparent;
  color: var(--text-dim);
  font-size: 14px;
  text-align: left;
  transition: background 0.14s, color 0.14s;
}
.nav-item:hover {
  background: rgba(255, 255, 255, 0.07);
  color: var(--text);
}
.nav-item.active {
  background: linear-gradient(135deg, rgba(46, 168, 255, 0.22), rgba(123, 92, 255, 0.22));
  color: var(--text);
  box-shadow: inset 0 0 0 1px rgba(46, 168, 255, 0.35);
}
.nav-icon {
  font-size: 18px;
  width: 22px;
  text-align: center;
}
.nav-label {
  font-weight: 500;
}

.sidebar-footer {
  padding: 14px 18px;
  border-top: 1px solid var(--win-border);
  display: flex;
  flex-direction: column;
  gap: 4px;
  color: var(--text-dim);
  font-size: 12px;
}
.version {
  font-size: 11px;
  opacity: 0.7;
}

/* ---------- 内容区 ---------- */
.content {
  flex: 1;
  min-width: 0;
  overflow: auto;
}
</style>
