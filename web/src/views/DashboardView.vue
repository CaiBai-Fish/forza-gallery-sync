<script setup>
import { inject, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '../api.js'
import { useGames } from '../useGames.js'

const navigate = inject('navigate')
const { name, ensureGames } = useGames()
const { t } = useI18n()

const loading = ref(true)
const error = ref('')
const data = ref(null)

async function load() {
  loading.value = true
  error.value = ''
  try {
    data.value = await api.status()
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

let timer = null
onMounted(() => {
  load()
  ensureGames()
  timer = setInterval(load, 8000)
})
onBeforeUnmount(() => clearInterval(timer))

function fmtTime(iso) {
  if (!iso) return '—'
  const d = new Date(iso)
  return d.toLocaleString('zh-CN', { hour12: false })
}

function fmtDuration(secs) {
  if (secs == null) return t('common.unknown')
  if (secs >= 3600) return t('dashboard.durationHours', { h: Math.floor(secs / 3600), m: Math.floor((secs % 3600) / 60) })
  if (secs >= 60) return t('dashboard.durationMinutes', { m: Math.floor(secs / 60) })
  return t('dashboard.durationSeconds', { s: Math.floor(secs) })
}

const stats = () => {
  const d = data.value
  const total = d.photos.total
  const maxMonth = d.photos.by_month[0]
  return [
    { label: t('dashboard.statPhotos'), value: total.toLocaleString(), icon: '🖼️', accent: '#2ea8ff' },
    { label: t('dashboard.statGames'), value: d.config.enabled_games.length, icon: '🎮', accent: '#7b5cff' },
    { label: t('dashboard.statLastSync'), value: maxMonth ? fmtTime(d.sync_state.find(s => s.game === maxMonth.game)?.last_sync_at) : t('dashboard.notSyncedYet'), icon: '🕐', accent: '#3ddc97', small: true },
    { label: t('dashboard.statToken'), value: d.token.has_token ? (d.token.expired ? t('common.expired') : t('common.valid')) : t('common.notConfigured'), icon: '🔑', accent: d.token.has_token ? '#3ddc97' : '#ff5c5c' },
  ]
}
</script>

<template>
  <div class="dash">
    <div v-if="loading && !data" class="placeholder">{{ t('app.loading') }}</div>
    <div v-else-if="error && !data" class="placeholder error">{{ error }}</div>

    <template v-else-if="data">
      <!-- 统计卡片 -->
      <div class="stat-grid">
        <div v-for="s in stats()" :key="s.label" class="card stat" :style="{ '--acc': s.accent }">
          <div class="stat-icon">{{ s.icon }}</div>
          <div class="stat-meta">
            <div class="stat-value" :class="{ small: s.small }">{{ s.value }}</div>
            <div class="stat-label">{{ s.label }}</div>
          </div>
        </div>
      </div>

      <div class="dash-cols">
        <!-- 左列：按游戏统计 + 最近同步 -->
        <div class="dash-col">
          <div class="card">
            <h3 class="card-title">{{ t('dashboard.byGame') }}</h3>
            <div v-if="!data.photos.by_game.length" class="empty">{{ t('dashboard.noRecords') }}</div>
            <div v-else class="game-bars">
              <div v-for="g in data.photos.by_game" :key="g.game" class="game-bar">
                <span class="game-name">{{ name(g.game) }}</span>
                <div class="bar-track">
                  <div class="bar-fill" :style="{ width: Math.max(6, (g.count / Math.max(...data.photos.by_game.map(x=>x.count))) * 100) + '%' }"></div>
                </div>
                <span class="game-count">{{ g.count }}</span>
              </div>
            </div>
          </div>

          <div class="card">
            <h3 class="card-title">{{ t('dashboard.recentSync') }}</h3>
            <div v-if="!data.sync_state.length" class="empty">{{ t('dashboard.noSyncRecords') }}</div>
            <div v-else class="sync-rows">
              <div v-for="s in data.sync_state" :key="s.game" class="sync-row">
                <span class="chip">{{ name(s.game) }}</span>
                <div class="sync-info">
                  <div class="sync-line">{{ fmtTime(s.last_sync_at) }}</div>
                  <div class="sync-sub">{{ t('dashboard.pulled', { total: s.total_records, synced: s.synced_records }) }}</div>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- 右列：Token 状态 + 快速操作 -->
        <div class="dash-col">
          <div class="card token-card">
            <h3 class="card-title">{{ t('dashboard.tokenTitle') }}</h3>
            <div class="token-row">
              <span class="chip" :class="data.token.has_token ? 'ok' : 'bad'">
                {{ data.token.has_token ? (data.token.expired ? t('common.expired') : t('common.valid')) : t('common.notConfigured') }}
              </span>
              <span class="chip">access: {{ data.token.masked_token }}</span>
              <span class="chip" :class="{ ok: data.token.has_refresh_token }">
                {{ t('dashboard.refreshToken') }}: {{ data.token.has_refresh_token ? t('common.configured') : t('common.none') }}
              </span>
            </div>
            <div class="token-detail">
              <div class="td-label">{{ t('dashboard.expiresIn') }}</div>
              <div class="td-value">{{ fmtDuration(data.token.expires_in) }}</div>
            </div>
            <button class="btn primary" @click="navigate('settings')">{{ t('dashboard.goSettings') }}</button>
          </div>

          <div class="card">
            <h3 class="card-title">{{ t('dashboard.quickActions') }}</h3>
            <div class="quick-actions">
              <button class="btn" @click="navigate('sync')">🔄 {{ t('dashboard.startSync') }}</button>
              <button class="btn" @click="navigate('gallery')">🖼️ {{ t('dashboard.browseGallery') }}</button>
              <button class="btn" @click="load">↻ {{ t('dashboard.refresh') }}</button>
            </div>
          </div>

          <div class="card">
            <h3 class="card-title">{{ t('dashboard.configOverview') }}</h3>
            <div class="kv">
              <span class="kv-k">{{ t('dashboard.downloadDir') }}</span>
              <span class="kv-v" :title="data.config.download_dir">{{ data.config.download_dir }}</span>
            </div>
            <div class="kv">
              <span class="kv-k">{{ t('dashboard.database') }}</span>
              <span class="kv-v" :title="data.config.database_path">{{ data.config.database_path }}</span>
            </div>
            <div class="kv">
              <span class="kv-k">{{ t('dashboard.concurrency') }}</span>
              <span class="kv-v">{{ t('dashboard.threads', { n: data.config.workers }) }} · {{ t('dashboard.perPage', { scheme: data.config.pagination, size: data.config.page_size }) }}</span>
            </div>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.dash {
  padding: 18px;
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-height: 100%;
}
.placeholder {
  padding: 40px;
  text-align: center;
  color: var(--text-dim);
}
.placeholder.error {
  color: var(--danger);
}

.stat-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 14px;
}
.stat {
  display: flex;
  align-items: center;
  gap: 14px;
}
.stat-icon {
  font-size: 28px;
  width: 52px;
  height: 52px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 12px;
  background: color-mix(in srgb, var(--acc) 16%, transparent);
  box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--acc) 35%, transparent);
}
.stat-value {
  font-size: 24px;
  font-weight: 700;
  line-height: 1.1;
}
.stat-value.small {
  font-size: 16px;
}
.stat-label {
  font-size: 12px;
  color: var(--text-dim);
  margin-top: 2px;
}

.dash-cols {
  display: grid;
  grid-template-columns: 1.4fr 1fr;
  gap: 16px;
  align-items: start;
}
@media (max-width: 860px) {
  .dash-cols {
    grid-template-columns: 1fr;
  }
}
.dash-col {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.card-title {
  font-size: 14px;
  font-weight: 600;
  margin-bottom: 12px;
  color: var(--text);
}
.empty {
  color: var(--text-dim);
  font-size: 13px;
  padding: 8px 0;
}

.game-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 10px;
}
.game-name {
  width: 42px;
  font-weight: 700;
  font-size: 13px;
}
.bar-track {
  flex: 1;
  height: 8px;
  background: rgba(255, 255, 255, 0.08);
  border-radius: 99px;
  overflow: hidden;
}
.bar-fill {
  height: 100%;
  border-radius: 99px;
  background: linear-gradient(90deg, var(--accent), var(--accent-2));
  transition: width 0.4s;
}
.game-count {
  font-size: 13px;
  color: var(--text-dim);
  width: 36px;
  text-align: right;
}

.sync-row {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.05);
}
.sync-row:last-child {
  border-bottom: none;
}
.sync-info {
  flex: 1;
}
.sync-line {
  font-size: 13px;
}
.sync-sub {
  font-size: 12px;
  color: var(--text-dim);
}

.chip.ok {
  color: var(--success);
  background: rgba(61, 220, 151, 0.12);
}
.chip.bad {
  color: var(--danger);
  background: rgba(255, 92, 92, 0.12);
}

.token-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 12px;
}
.token-detail {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 10px 12px;
  background: rgba(255, 255, 255, 0.04);
  border-radius: 8px;
  margin-bottom: 12px;
}
.td-label {
  font-size: 12px;
  color: var(--text-dim);
}
.td-value {
  font-size: 15px;
  font-weight: 600;
}

.quick-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.kv {
  display: flex;
  gap: 10px;
  padding: 6px 0;
  font-size: 13px;
}
.kv-k {
  color: var(--text-dim);
  flex: 0 0 64px;
}
.kv-v {
  flex: 1;
  word-break: break-all;
}
</style>
