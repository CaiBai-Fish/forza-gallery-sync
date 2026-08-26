<script setup>
import { onBeforeUnmount, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const props = defineProps({
  visible: { type: Boolean, default: false },
  x: { type: Number, default: 0 },
  y: { type: Number, default: 0 },
  items: { type: Array, default: () => [] },
})
const emit = defineEmits(['close'])

function close() {
  emit('close')
}
function onKey(e) {
  if (e.key === 'Escape') close()
}

onMounted(() => {
  window.addEventListener('keydown', onKey)
  window.addEventListener('resize', close)
  window.addEventListener('blur', close)
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKey)
  window.removeEventListener('resize', close)
  window.removeEventListener('blur', close)
})
</script>

<template>
  <Teleport to="body">
    <div
      v-if="visible"
      class="ctx-overlay"
      @mousedown="close"
      @contextmenu.prevent="close"
      @wheel="close"
    >
      <div
        class="ctx-menu"
        :style="{ left: x + 'px', top: y + 'px' }"
        @mousedown.stop
        @contextmenu.stop
      >
        <button
          v-for="(item, i) in items"
          :key="i"
          class="ctx-item"
          @click="item.action ? (item.action(), close()) : close()"
        >
          <span v-if="item.icon" class="ctx-icon">{{ item.icon }}</span>
          <span class="ctx-label">{{ item.label }}</span>
        </button>
        <div v-if="!items.length" class="ctx-empty">{{ t('gallery.ctxEmpty') }}</div>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.ctx-overlay {
  position: fixed;
  inset: 0;
  z-index: 999999;
}
.ctx-menu {
  position: fixed;
  min-width: 178px;
  padding: 6px;
  background: #1a2138;
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 10px;
  box-shadow: 0 12px 36px rgba(0, 0, 0, 0.5);
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.ctx-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 12px;
  border: none;
  border-radius: 7px;
  background: transparent;
  color: var(--text);
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: background 0.1s;
}
.ctx-item:hover {
  background: rgba(46, 168, 255, 0.18);
}
.ctx-icon {
  width: 18px;
  text-align: center;
  font-size: 15px;
}
.ctx-label {
  flex: 1;
}
.ctx-empty {
  padding: 8px 12px;
  color: var(--text-dim);
  font-size: 12px;
}
</style>
