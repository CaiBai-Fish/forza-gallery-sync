<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '../api.js'
import PhotoThumb from '../components/PhotoThumb.vue'
import ContextMenu from '../components/ContextMenu.vue'
import { useGames } from '../useGames.js'

const { gamesList, name, ensureGames } = useGames()
const { t } = useI18n()

const loading = ref(true)
const error = ref('')
const photos = ref([])
const total = ref(0)
const page = ref(0)
const pageSize = 48
const game = ref('')
const month = ref('')
const q = ref('')
const months = ref([])

const selected = ref(null) // 详情弹层
const detailLoading = ref(false)
const detailError = ref('')
const downloadDir = ref('')

// 照片右键自定义菜单
const ctx = ref({ visible: false, x: 0, y: 0, items: [] })

function openPhotoMenu(e, p) {
  const local = p.local_path || ''
  const idx = Math.max(local.lastIndexOf('\\'), local.lastIndexOf('/'))
  const dir = idx > 0 ? local.slice(0, idx) : ''
  ctx.value = {
    visible: true,
    x: e.clientX,
    y: e.clientY,
    items: [
      { icon: '📂', label: t('gallery.ctxOpenFile'), action: () => local && api.openPath(local) },
      { icon: '🗂️', label: t('gallery.ctxOpenDir'), action: () => dir && api.openPath(dir) },
      { icon: '👁️', label: t('gallery.ctxViewDetail'), action: () => openDetail(p) },
    ],
  }
}
function closeCtx() {
  ctx.value.visible = false
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const data = await api.photos({
      game: game.value,
      month: month.value,
      q: q.value,
      limit: pageSize,
      offset: page.value * pageSize,
    })
    photos.value = data.items
    total.value = data.total
    collectMonths(data.items)
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

function collectMonths(items) {
  const set = new Set(months.value.map((m) => m.value))
  items.forEach((p) => {
    if (p.month) set.add(p.month)
  })
  months.value = [...set]
    .sort()
    .reverse()
    .map((m) => ({ value: m, label: t('gallery.yearMonth', { y: m.slice(0, 4), m: m.slice(5, 7) }) }))
}

const pages = computed(() => Math.max(1, Math.ceil(total.value / pageSize)))
const pageNumbers = computed(() => {
  const nums = []
  const p = page.value
  for (let i = Math.max(0, p - 2); i <= Math.min(pages.value - 1, p + 2); i++) nums.push(i)
  return nums
})

watch([game, month, q], () => {
  page.value = 0
  load()
})

let debounce = null
function onSearch() {
  clearTimeout(debounce)
  debounce = setTimeout(() => {
    page.value = 0
    load()
  }, 300)
}

async function openDetail(p) {
  selected.value = p
  detailLoading.value = true
  detailError.value = ''
  try {
    const meta = await api.photoMeta(p.photo_id)
    selected.value = { ...p, ...meta }
  } catch (e) {
    detailError.value = e.message
  } finally {
    detailLoading.value = false
  }
}

function fmtTime(iso) {
  if (!iso) return '—'
  return new Date(iso).toLocaleString('zh-CN', { hour12: false })
}

onMounted(() => {
  load()
  ensureGames()
  api.getConfig().then((c) => (downloadDir.value = c.download_dir)).catch(() => {})
})
onBeforeUnmount(() => clearTimeout(debounce))
</script>

<template>
  <div class="gallery">
    <!-- 工具栏 -->
    <div class="toolbar">
      <input v-model="q" class="input search" :placeholder="t('gallery.searchPlaceholder')" @input="onSearch" />
      <select v-model="game" class="select w-auto">
        <option value="">{{ t('gallery.allGames') }}</option>
        <option v-for="g in gamesList" :key="g.id" :value="g.id">{{ g.name }}</option>
      </select>
      <select v-model="month" class="select w-auto">
        <option value="">{{ t('gallery.allMonths') }}</option>
        <option v-for="m in months" :key="m.value" :value="m.value">{{ m.label }}</option>
      </select>
      <button class="btn ghost" @click="load()">↻</button>
      <button class="btn" :disabled="!downloadDir" :title="t('gallery.openDir')" @click="api.openPath(downloadDir)">📂 {{ t('gallery.openDir') }}</button>
      <span class="total">{{ t('gallery.total', { n: total }) }}</span>
    </div>

    <div v-if="loading && !photos.length" class="placeholder">{{ t('app.loading') }}</div>
    <div v-else-if="error" class="placeholder error">{{ error }}</div>
    <div v-else-if="!photos.length" class="placeholder">{{ t('gallery.empty') }}</div>

    <template v-else>
      <!-- 网格 -->
      <div class="grid">
        <div v-for="p in photos" :key="p.photo_id" class="photo-cell" @click="openDetail(p)" @contextmenu.prevent="openPhotoMenu($event, p)">
          <PhotoThumb class="thumb" :photo-id="p.photo_id" :alt="p.title || p.photo_id" />
          <div class="photo-overlay">
            <span class="chip">{{ name(p.game) }}</span>
            <span class="photo-title">{{ p.title || t('common.noTitle') }}</span>
          </div>
        </div>
      </div>

      <!-- 分页 -->
      <div class="pager">
        <button class="btn ghost" :disabled="page === 0" @click="page--; load()">‹</button>
        <button v-for="n in pageNumbers" :key="n" class="btn ghost" :class="{ active: n === page }" @click="page = n; load()">
          {{ n + 1 }}
        </button>
        <button class="btn ghost" :disabled="page >= pages - 1" @click="page++; load()">›</button>
      </div>
    </template>

    <!-- 照片右键菜单 -->
    <ContextMenu :visible="ctx.visible" :x="ctx.x" :y="ctx.y" :items="ctx.items" @close="closeCtx" />

    <!-- 详情弹层 -->
    <div v-if="selected" class="modal-mask" @click.self="selected = null">
      <div class="modal">
        <div class="modal-head">
          <span class="chip">{{ name(selected.game) }}</span>
          <button class="dwin-btn close" @click="selected = null">✕</button>
        </div>
        <div class="modal-body">
          <div class="modal-img">
            <PhotoThumb :photo-id="selected.photo_id" :alt="selected.title" />
          </div>
          <div class="modal-info">
            <h3>{{ selected.title || t('common.noTitle') }}</h3>
            <div class="kv"><span class="kv-k">{{ t('gallery.photoId') }}</span><span class="kv-v">{{ selected.photo_id }}</span></div>
            <div class="kv"><span class="kv-k">{{ t('gallery.submittedAt') }}</span><span class="kv-v">{{ fmtTime(selected.submission_time_utc) }}</span></div>
            <div class="kv"><span class="kv-k">{{ t('gallery.downloadedAt') }}</span><span class="kv-v">{{ fmtTime(selected.downloaded_at) }}</span></div>
            <div class="kv"><span class="kv-k">{{ t('gallery.localPath') }}</span><a class="kv-link" href="#" :title="selected.local_path" @click.prevent="api.openPath(selected.local_path)">{{ selected.local_path }}</a></div>
            <div class="kv"><span class="kv-k">{{ t('gallery.description') }}</span><span class="kv-v">{{ selected.description || t('common.noDescription') }}</span></div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.gallery {
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 14px;
  height: 100%;
}
.placeholder {
  padding: 40px;
  text-align: center;
  color: var(--text-dim);
}
.placeholder.error {
  color: var(--danger);
}

.toolbar {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
}
.search {
  flex: 1;
  min-width: 180px;
}
.w-auto {
  width: auto;
}
.total {
  font-size: 12px;
  color: var(--text-dim);
  margin-left: auto;
}

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
  gap: 12px;
  flex: 1;
  overflow: auto;
  align-content: start;
}
.photo-cell {
  position: relative;
  aspect-ratio: 16 / 10;
  border-radius: 10px;
  overflow: hidden;
  cursor: pointer;
  border: 1px solid var(--win-border);
  background: #0d1224;
}
.thumb {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
  transition: transform 0.2s;
}
.photo-cell:hover .thumb {
  transform: scale(1.05);
}
.photo-overlay {
  position: absolute;
  inset: auto 0 0 0;
  padding: 8px;
  display: flex;
  flex-direction: column;
  gap: 4px;
  background: linear-gradient(transparent, rgba(0, 0, 0, 0.75));
  opacity: 0;
  transition: opacity 0.15s;
}
.photo-cell:hover .photo-overlay {
  opacity: 1;
}
.photo-title {
  font-size: 12px;
  color: #fff;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.pager {
  display: flex;
  gap: 6px;
  justify-content: center;
  align-items: center;
}
.pager .active {
  background: rgba(46, 168, 255, 0.25);
  border-color: rgba(46, 168, 255, 0.5);
}

/* 详情弹层 */
.modal-mask {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.6);
  backdrop-filter: blur(4px);
  z-index: 100000;
  display: flex;
  align-items: center;
  justify-content: center;
}
.modal {
  width: min(880px, 92vw);
  max-height: 86vh;
  background: var(--win-bg);
  border: 1px solid var(--win-border);
  border-radius: 14px;
  box-shadow: var(--shadow);
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
.modal-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 14px;
  border-bottom: 1px solid var(--win-border);
}
.dwin-btn {
  width: 30px;
  height: 26px;
  border: none;
  background: transparent;
  color: var(--text-dim);
  border-radius: 6px;
}
.dwin-btn.close:hover {
  background: var(--danger);
  color: #fff;
}
.modal-body {
  display: flex;
  gap: 16px;
  padding: 16px;
  overflow: auto;
}
@media (max-width: 700px) {
  .modal-body {
    flex-direction: column;
  }
}
.modal-img {
  flex: 1.2;
  min-width: 0;
}
.modal-img img {
  width: 100%;
  border-radius: 10px;
  display: block;
}
.modal-info {
  flex: 1;
  min-width: 240px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.modal-info h3 {
  font-size: 16px;
  margin-bottom: 6px;
  word-break: break-word;
}
.kv {
  display: flex;
  gap: 10px;
  font-size: 12.5px;
  padding: 5px 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
}
.kv-k {
  color: var(--text-dim);
  flex: 0 0 66px;
}
.kv-v {
  flex: 1;
  word-break: break-all;
}
.kv-link {
  flex: 1;
  color: var(--accent);
  word-break: break-all;
  cursor: pointer;
  text-decoration: none;
}
.kv-link:hover {
  text-decoration: underline;
}
</style>
