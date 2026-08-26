<script setup>
import { onBeforeUnmount, ref, watch } from 'vue'
import { api } from '../api.js'

const props = defineProps({
  photoId: { type: String, required: true },
  alt: { type: String, default: '' },
})

const src = ref('')
let objectUrl = null

async function load() {
  if (objectUrl) {
    URL.revokeObjectURL(objectUrl)
    objectUrl = null
    src.value = ''
  }
  try {
    const bytes = await api.photoImage(props.photoId)
    // Tauri 将 Rust Vec<u8> 序列化为数字数组，需显式转成 Uint8Array，
    // 否则 Blob 会把数组字符串化导致图片损坏
    const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes)
    const blob = new Blob([view], { type: 'image/jpeg' })
    objectUrl = URL.createObjectURL(blob)
    src.value = objectUrl
  } catch {
    /* 图片加载失败时留空占位 */
  }
}

watch(() => props.photoId, load, { immediate: true })

onBeforeUnmount(() => {
  if (objectUrl) URL.revokeObjectURL(objectUrl)
})
</script>

<template>
  <img v-if="src" :src="src" :alt="alt" loading="lazy" />
  <div v-else class="img-placeholder"></div>
</template>

<style scoped>
.img-placeholder {
  width: 100%;
  height: 100%;
  background: rgba(255, 255, 255, 0.04);
}
</style>
