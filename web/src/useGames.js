// 游戏列表与显示名工具：游戏名优先走 i18n 翻译键（games.<id>），
// 未配置翻译时回退到后端返回的显示名或游戏代码。

import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from './api.js'

const gamesList = ref([]) // [{ id, name }]
const gamesMap = ref({}) // id -> 显示名
let loaded = false

export function useGames() {
  const { t } = useI18n()

  async function ensureGames() {
    if (loaded) return
    loaded = true
    try {
      const cfg = await api.getConfig()
      gamesList.value = (cfg.supported_games || []).map((g) => ({
        ...g,
        name: localizedName(g.id, g.name),
      }))
      const m = {}
      gamesList.value.forEach((g) => {
        m[g.id] = g.name
      })
      gamesMap.value = m
    } catch {
      loaded = false
    }
  }

  function localizedName(id, fallback) {
    const key = `games.${id}`
    const localized = t(key)
    return localized === key ? fallback || id || '' : localized
  }

  function name(id) {
    return localizedName(id, gamesMap.value[id])
  }

  return { gamesList, gamesMap, ensureGames, name }
}
