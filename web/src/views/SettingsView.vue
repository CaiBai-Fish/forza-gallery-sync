<script setup>
import { onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { open } from '@tauri-apps/plugin-dialog'
import { api } from '../api.js'

const { t } = useI18n()

const config = ref(null)
const auth = ref(null)
const loading = ref(true)
const saving = ref(false)
const error = ref('')
const okMsg = ref('')

// 登录状态
const loginState = ref('idle')
const loginMsg = ref('')
const refreshing = ref(false)

const form = reactive({
  download_dir: '',
  page_size: 50,
  pagination: 'auto',
  timeout: 30,
  retries: 3,
  workers: 4,
  verify_ssl: true,
  user_agent: '',
  enabled_games: [],
})

async function load() {
  loading.value = true
  error.value = ''
  try {
    config.value = await api.getConfig()
    auth.value = await api.authStatus()
    Object.assign(form, {
      download_dir: config.value.download_dir,
      page_size: config.value.page_size,
      pagination: config.value.pagination,
      timeout: config.value.timeout,
      retries: config.value.retries,
      workers: config.value.workers,
      verify_ssl: config.value.verify_ssl,
      user_agent: config.value.user_agent,
      enabled_games: [...config.value.enabled_games],
    })
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

async function save() {
  saving.value = true
  error.value = ''
  okMsg.value = ''
  try {
    config.value = await api.updateConfig({ ...form })
    okMsg.value = t('settings.saved')
    setTimeout(() => (okMsg.value = ''), 2500)
  } catch (e) {
    error.value = e.message
  } finally {
    saving.value = false
  }
}

async function refreshToken() {
  refreshing.value = true
  error.value = ''
  okMsg.value = ''
  try {
    const res = await api.authRefresh()
    okMsg.value = res.message
    auth.value = await api.authStatus()
  } catch (e) {
    error.value = e.message
  } finally {
    refreshing.value = false
  }
}

async function startLogin() {
  error.value = ''
  try {
    const res = await api.authLogin()
    loginState.value = 'running'
    loginMsg.value = res.message
  } catch (e) {
    error.value = e.message
  }
}

async function pollLogin() {
  try {
    const s = await api.authLoginStatus()
    loginState.value = s.state
    loginMsg.value = s.message
    if (s.state === 'success') {
      auth.value = await api.authStatus()
      await load()
    }
  } catch (e) {
    /* 忽略 */
  }
}

let timer = null
onMounted(async () => {
  await load()
  await pollLogin()
  timer = setInterval(pollLogin, 2000)
})
onBeforeUnmount(() => clearInterval(timer))

function toggleGame(g) {
  const i = form.enabled_games.indexOf(g)
  if (i >= 0) form.enabled_games.splice(i, 1)
  else form.enabled_games.push(g)
}

function fmtDuration(secs) {
  if (secs == null) return t('common.unknown')
  if (secs >= 3600) return t('dashboard.durationHours', { h: Math.floor(secs / 3600), m: Math.floor((secs % 3600) / 60) })
  if (secs >= 60) return t('dashboard.durationMinutes', { m: Math.floor(secs / 60) })
  return t('dashboard.durationSeconds', { s: Math.floor(secs) })
}

async function pickDir() {
  try {
    const dir = await open({ directory: true, title: t('settings.pickDirTitle') })
    if (dir) form.download_dir = String(dir)
  } catch {
    /* 用户取消 */
  }
}
</script>

<template>
  <div v-if="loading" class="placeholder">{{ t('app.loading') }}</div>
  <div v-else class="settings">
    <!-- Token 卡片 -->
    <div class="card token-card">
      <h3 class="card-title">{{ t('settings.accountToken') }}</h3>
      <div class="token-line">
        <span class="chip" :class="auth && auth.has_token ? (auth.expired ? 'bad' : 'ok') : 'bad'">
          {{ auth && auth.has_token ? (auth.expired ? t('common.expired') : t('common.valid')) : t('common.notConfigured') }}
        </span>
        <span class="chip">access: {{ config.masked_token }}</span>
        <span class="chip" :class="{ ok: auth && auth.has_refresh_token }">{{ t('settings.refresh') }}: {{ auth && auth.has_refresh_token ? t('common.configured') : t('common.none') }}</span>
        <span class="chip" v-if="auth && auth.expires_in != null">{{ t('settings.expiresIn') }} {{ fmtDuration(auth.expires_in) }}</span>
      </div>

      <div class="token-actions">
        <button class="btn primary" :disabled="loginState === 'running'" @click="startLogin">
          {{ loginState === 'running' ? t('settings.loggingIn') : '🌐 ' + t('settings.browserLogin') }}
        </button>
        <button class="btn" :disabled="refreshing || !(auth && auth.has_refresh_token)" @click="refreshToken">
          {{ refreshing ? t('settings.refreshing') : '↻ ' + t('settings.refreshToken') }}
        </button>
      </div>

      <div v-if="loginState !== 'idle'" class="login-box" :class="loginState">
        <span v-if="loginState === 'running'" class="spinner"></span>
        {{ loginMsg }}
      </div>

      <div class="hint">{{ t('settings.loginHint') }}</div>
    </div>

    <!-- 配置表单 -->
    <div class="card">
      <h3 class="card-title">{{ t('settings.syncSettings') }}</h3>
      <div class="form-grid">
        <div class="field span2">
          <label class="field-label">{{ t('settings.downloadDir') }}</label>
          <div class="dir-row">
            <input v-model="form.download_dir" class="input" :placeholder="t('settings.downloadDirPlaceholder')" />
            <button class="btn" @click="pickDir">📂 {{ t('settings.pickDir') }}</button>
          </div>
        </div>
        <div class="field">
          <label class="field-label">{{ t('settings.pageSize') }}</label>
          <input v-model.number="form.page_size" class="input" type="number" min="1" />
        </div>
        <div class="field">
          <label class="field-label">{{ t('settings.pagination') }}</label>
          <select v-model="form.pagination" class="select">
            <option value="auto">{{ t('pagination.auto') }}</option>
            <option value="page">{{ t('pagination.page') }}</option>
            <option value="skip">{{ t('pagination.skip') }}</option>
            <option value="offset">{{ t('pagination.offset') }}</option>
            <option value="page_number">{{ t('pagination.pageNumber') }}</option>
            <option value="none">{{ t('pagination.none') }}</option>
          </select>
        </div>
        <div class="field">
          <label class="field-label">{{ t('settings.workers') }}</label>
          <input v-model.number="form.workers" class="input" type="number" min="1" />
        </div>
        <div class="field">
          <label class="field-label">{{ t('settings.retries') }}</label>
          <input v-model.number="form.retries" class="input" type="number" min="0" />
        </div>
        <div class="field">
          <label class="field-label">{{ t('settings.timeout') }}</label>
          <input v-model.number="form.timeout" class="input" type="number" min="1" />
        </div>
        <div class="field">
          <label class="field-label">{{ t('settings.userAgent') }}</label>
          <input v-model="form.user_agent" class="input" />
        </div>

        <div class="field span2">
          <label class="field-label">{{ t('settings.enabledGames') }}</label>
          <div class="game-opts">
            <button v-for="g in config.supported_games" :key="g.id" class="btn game-opt" :class="{ active: form.enabled_games.includes(g.id) }" @click="toggleGame(g.id)">
              {{ g.name }}
            </button>
          </div>
        </div>

        <label class="check span2">
          <input v-model="form.verify_ssl" type="checkbox" />
          <span>{{ t('settings.verifySsl') }}</span>
        </label>
      </div>

      <div class="actions">
        <button class="btn primary" :disabled="saving" @click="save">{{ saving ? t('settings.saving') : '💾 ' + t('settings.save') }}</button>
        <button class="btn ghost" @click="load">↻ {{ t('settings.reload') }}</button>
      </div>
      <div v-if="error" class="err">{{ error }}</div>
      <div v-if="okMsg" class="ok">{{ okMsg }}</div>
    </div>
  </div>
</template>

<style scoped>
.placeholder {
  padding: 40px;
  text-align: center;
  color: var(--text-dim);
}
.settings {
  padding: 18px;
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.card-title {
  font-size: 14px;
  font-weight: 600;
  margin-bottom: 12px;
}
.token-line {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 14px;
}
.chip.ok {
  color: var(--success);
  background: rgba(61, 220, 151, 0.12);
}
.chip.bad {
  color: var(--danger);
  background: rgba(255, 92, 92, 0.12);
}
.token-actions {
  display: flex;
  gap: 10px;
  flex-wrap: wrap;
}
.login-box {
  margin-top: 12px;
  padding: 10px 12px;
  border-radius: 8px;
  font-size: 13px;
  display: flex;
  align-items: center;
  gap: 8px;
  background: rgba(255, 255, 255, 0.05);
}
.login-box.success {
  color: var(--success);
  background: rgba(61, 220, 151, 0.12);
}
.login-box.error {
  color: var(--danger);
  background: rgba(255, 92, 92, 0.12);
}
.spinner {
  width: 14px;
  height: 14px;
  border: 2px solid rgba(255, 255, 255, 0.25);
  border-top-color: var(--accent);
  border-radius: 50%;
  animation: spin 0.8s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
.hint {
  margin-top: 12px;
  font-size: 12px;
  color: var(--text-dim);
  line-height: 1.5;
}

.form-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 14px;
  margin-bottom: 16px;
}
.span2 {
  grid-column: span 2;
}
.dir-row {
  display: flex;
  gap: 8px;
}
.dir-row .input {
  flex: 1;
}
.field {
  display: flex;
  flex-direction: column;
  gap: 6px;
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
}
.check input {
  accent-color: var(--accent);
  width: 15px;
  height: 15px;
}
.actions {
  display: flex;
  gap: 10px;
  align-items: center;
}
.err {
  color: var(--danger);
  font-size: 13px;
  margin-top: 8px;
}
.ok {
  color: var(--success);
  font-size: 13px;
  margin-top: 8px;
}
</style>
