// Tauri 原生命令封装：直接调用 Rust 侧 PyO3 桥接命令。
// 无 HTTP 服务、无端口——所有数据经由 Tauri invoke 通道传递。

import { invoke } from '@tauri-apps/api/core'

export const api = {
  // 状态 / 仪表盘
  status: (configPath) => invoke('backend_status', { configPath }),

  // 配置
  getConfig: (configPath) => invoke('backend_config', { configPath }),
  updateConfig: (values, configPath) =>
    invoke('backend_update_config', { values, configPath }),

  // 认证
  authStatus: (configPath) => invoke('backend_auth_status', { configPath }),
  authRefresh: (configPath) => invoke('backend_auth_refresh', { configPath }),
  authLogin: (configPath) => invoke('backend_auth_login', { configPath }),
  authLoginStatus: () => invoke('backend_auth_login_status'),

  // 同步
  syncStart: ({ games, force = false, max_photos, page_size } = {}) =>
    invoke('backend_sync_start', {
      games,
      force,
      maxPhotos: max_photos,
      pageSize: page_size,
    }),
  syncProgress: () => invoke('backend_sync_progress'),
  syncStop: () => invoke('backend_sync_stop'),

  // 照片
  photos: (params = {}) =>
    invoke('backend_photos', {
      game: params.game || null,
      month: params.month || null,
      q: params.q || null,
      limit: params.limit,
      offset: params.offset,
    }),
  photoMeta: (photoId) => invoke('backend_photo_meta', { photoId }),
  photoImage: (photoId) => invoke('backend_photo_image', { photoId }),
  openPath: (path) => invoke('backend_open_path', { path }),
}

