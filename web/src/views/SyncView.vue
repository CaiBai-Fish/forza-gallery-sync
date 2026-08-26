<script setup>
import { onBeforeUnmount, onMounted, ref, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '../api.js'
import { useGames } from '../useGames.js'

const cfg = ref({ enabled_games: [], token: '', supported_games: [] })
const games = ref([])
const gameOptions = computed(() => cfg.value.supported_games || [])
const { name, ensureGames } = useGames()
const { t } = useI18n()
const force = ref(false)
const maxPhotos = ref('')
const pageSize = ref('')

const prog = ref(null)
const message = ref('')
const actionError = ref('')
const loading = ref(true)

const running = computed(() => prog.value?.running)

async function loadConfig() {
  loading.value = true
  try {
    cfg.value = await api.getConfig()
    games.value = [...cfg.value.enabled_games]
  } catch (e) {
    actionError.value = e.message
  } finally {
    loading.value = false
  }
}

async function poll() {
  try {
    prog.value = await api.syncProgress()
  } catch (e) {
    /* 忽略轮询错误 */
  }
}

let timer = null
onMounted(async () => {
  await loadConfig()
  ensureGames()
  await poll()
  timer = setInterval(poll, 1000)
})
onBeforeUnmount(() => clearInterval(timer))

function toggleGame(g) {
  const i = games.value.indexOf(g)
  if (i >= 0) games.value.splice(i, 1)
  else games.value.push(g)
}

async function start() {
  actionError.value = ''
  try {
    const res = await api.syncStart({
      games: games.value,
      force: force.value,
      max_photos: maxPhotos.value ? Number(maxPhotos.value) : undefined,
      page_size: pageSize.value ? Number(pageSize.value) : undefined,
    })
    message.value = res.message
  } catch (e) {
    actionError.value = e.message
  }
}

async function stop() {
  actionError.value = ''
  try {
    const res = await api.syncStop()
    message.value = res.message
  } catch (e) {
    actionError.value = e.message
  }
}

const percent = computed(() => {
  if (!prog.value || !prog.value.total) return 0
  return Math.round((prog.value.done / prog.value.total) * 100)
})

function fmtTime(iso) {
  if (!iso) return '—'
  return new Date(iso).toLocaleString('zh-CN', { hour12: false })
}
</script>

<template>
  <div class="sync">
    <div class="panel">
      <h3 class="panel-title">{{ t('sync.title') }}</h3>

      <div class="field">
        <label class="field-label">{{ t('sync.selectGames') }}</label>
        <div class="game-opts">
          <button
            v-for="g in gameOptions"
            :key="g.id"
            class="btn game-opt"
            :class="{ active: games.includes(g.id) }"
            @click="toggleGame(g.id)"
          >
            {{ g.name }}
          </button>
        </div>
      </div>

      <div class="field-row">
        <div class="field">
          <label class="field-label">{{ t('sync.maxPhotos') }}</label>
          <input v-model="maxPhotos" class="input" type="number" min="1" :placeholder="t('sync.maxPhotosPlaceholder')" />
        </div>
        <div class="field">
          <label class="field-label">{{ t('sync.pageSize') }}</label>
          <input v-model="pageSize" class="input" type="number" min="1" :placeholder="t('sync.pageSizePlaceholder')" />
        </div>
      </div>

      <div class="field">
        <label class="check">
          <input v-model="force" type="checkbox" />
          <span>{{ t('sync.forceRedownload') }}</span>
        </label>
      </div>

      <div class="actions">
        <button class="btn success" :disabled="running || !games.length" @click="start">▶ {{ t('sync.start') }}</button>
        <button class="btn danger" :disabled="!running" @click="stop">■ {{ t('sync.stop') }}</button>
      </div>

      <div v-if="actionError" class="err">{{ actionError }}</div>
      <div v-if="message" class="msg">{{ message }}</div>
    </div>

    <div class="panel">
      <h3 class="panel-title">{{ t('sync.progress') }}</h3>

      <div v-if="!prog || !prog.running" class="idle">
        <template v-if="prog && prog.message">{{ prog.message }}</template>
        <template v-else>{{ t('sync.idle') }}</template>
        <div v-if="prog && prog.finished_at" class="dim">{{ t('sync.finishedAt', { time: fmtTime(prog.finished_at) }) }}</div>
      </div>

      <template v-else>
        <div class="progress-head">
          <span class="chip active-chip">{{ name(prog.game) }}</span>
          <span class="prog-count">{{ prog.done }} / {{ prog.total }}</span>
          <span class="prog-percent">{{ percent }}%</span>
        </div>
        <div class="progress-track">
          <div class="progress-fill" :style="{ width: percent + '%' }"></div>
        </div>
        <div class="prog-stats">
          <div class="pstat"><span class="pstat-n ok">+{{ prog.synced }}</span><span class="pstat-l">{{ t('sync.added') }}</span></div>
          <div class="pstat"><span class="pstat-n">⏭ {{ prog.skipped }}</span><span class="pstat-l">{{ t('sync.skipped') }}</span></div>
          <div class="pstat"><span class="pstat-n" :class="{ bad: prog.failed }">✕ {{ prog.failed }}</span><span class="pstat-l">{{ t('sync.failed') }}</span></div>
        </div>
        <div class="prog-msg">{{ prog.message }}</div>
        <div v-if="prog.cancel_requested" class="msg warn">{{ t('sync.cancelling') }}</div>

        <div v-if="prog.failed_items && prog.failed_items.length" class="fail-list">
          <div v-for="(f, i) in prog.failed_items" :key="i" class="fail-item">
            <span class="fail-url" :title="f.url">{{ f.url }}</span>
            <span class="fail-reason">{{ f.reason }}</span>
          </div>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.sync {
  padding: 18px;
  display: grid;
  grid-template-columns: 340px 1fr;
  gap: 16px;
  align-items: start;
}
@media (max-width: 760px) {
  .sync {
    grid-template-columns: 1fr;
  }
}
.panel {
  background: var(--card);
  border: 1px solid var(--win-border);
  border-radius: var(--radius);
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.panel-title {
  font-size: 14px;
  font-weight: 600;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  flex: 1;
}
.field-row {
  display: flex;
  gap: 12px;
}
.field-label {
  font-size: 12px;
  color: var(--text-dim);
}

.game-opts {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.game-opt {
  flex: 1 1 130px;
  justify-content: center;
  font-weight: 700;
  letter-spacing: 1px;
}
.game-opt.active {
  background: linear-gradient(135deg, var(--accent), var(--accent-2));
  border-color: transparent;
  color: #fff;
}

.check {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  cursor: pointer;
  color: var(--text);
}
.check input {
  accent-color: var(--accent);
  width: 15px;
  height: 15px;
}

.actions {
  display: flex;
  gap: 10px;
}
.err {
  color: var(--danger);
  font-size: 13px;
}
.msg {
  color: var(--success);
  font-size: 13px;
}
.msg.warn {
  color: var(--warn);
}

.idle {
  color: var(--text-dim);
  font-size: 13px;
  padding: 12px 0;
}
.dim {
  color: var(--text-dim);
  font-size: 12px;
  margin-top: 6px;
}

.progress-head {
  display: flex;
  align-items: center;
  gap: 10px;
}
.active-chip {
  background: linear-gradient(135deg, var(--accent), var(--accent-2));
  color: #fff;
  border: none;
}
.prog-count {
  font-size: 15px;
  font-weight: 600;
}
.prog-percent {
  margin-left: auto;
  font-size: 20px;
  font-weight: 700;
  background: linear-gradient(135deg, var(--accent), var(--accent-2));
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}

.progress-track {
  height: 10px;
  background: rgba(255, 255, 255, 0.08);
  border-radius: 99px;
  overflow: hidden;
}
.progress-fill {
  height: 100%;
  border-radius: 99px;
  background: linear-gradient(90deg, var(--accent), var(--accent-2));
  transition: width 0.4s ease;
}

.prog-stats {
  display: flex;
  gap: 12px;
  margin-top: 4px;
}
.pstat {
  flex: 1;
  text-align: center;
  background: rgba(255, 255, 255, 0.04);
  border-radius: 8px;
  padding: 10px 6px;
}
.pstat-n {
  display: block;
  font-size: 20px;
  font-weight: 700;
}
.pstat-n.ok {
  color: var(--success);
}
.pstat-n.bad {
  color: var(--danger);
}
.pstat-l {
  font-size: 11px;
  color: var(--text-dim);
}
.prog-msg {
  font-size: 13px;
}

.fail-list {
  max-height: 180px;
  overflow: auto;
  border-top: 1px solid rgba(255, 255, 255, 0.06);
  padding-top: 8px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.fail-item {
  display: flex;
  gap: 8px;
  font-size: 12px;
}
.fail-url {
  color: var(--text-dim);
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.fail-reason {
  color: var(--danger);
  max-width: 40%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
