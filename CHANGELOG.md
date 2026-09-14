# 更新日志 (Changelog)

本仓库所有值得注意的变更都会记录在此文件。
格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

> GitHub Actions（`.github/workflows/build-release.yml`）发布 Release 时，
> 会自动读取本文件对应版本的章节作为发布说明。

## [Unreleased]

- （预留：下一个版本的变更）

## [0.5.0] - 2026-09-14

### 变更
- **发布方式改为直接输出程序**：取消 MSI 打包与独立的 Setup 安装程序；`web\make-gui.ps1` 直接产出 GUI 版
  （`web\dist\ForzaGallerySync-<版本>-win-x64\` 及同名 `.zip`），`web\make-cli.ps1` 继续产出 CLI 单文件
- **内嵌 Python 运行时纳入 playwright**（含驱动 `node.exe`、`greenlet`、`pyee`）：打包版「浏览器一键登录」
  开箱即用，无需用户另装 Python 包（运行时 zip 31MB → 68MB，GUI 发布 zip 96MB → 132MB）
- **GUI 首次运行自行准备 Python 运行时**：程序目录为**干净目录**（只含发布清单 `app-files.txt` 所列文件与
  程序自己生成的文件）时把运行时解压到程序目录（便携模式），否则解压到默认安装目录
  `%LOCALAPPDATA%\Programs\ForzaGallerySync`（可用 `FORZA_SYNC_INSTALL_DIR` 覆盖；程序目录不可写时回退安装目录）
- 解压出的运行时按内嵌 zip 的 SHA256（`python\.runtime-id`）校验，程序升级后自动重新解压，避免旧运行时残留
- 数据库/配置跟随运行时目录：便携模式放程序目录，安装模式放默认安装目录（沿用 `FORZA_SYNC_APP_DIR`）
- 发布清单 `app-files.txt` 由 `make-gui.ps1` 生成并随发布目录/zip 一起分发
- `make-gui.ps1` 在 `forza_sync` 源码 / `requirements.txt` / `make-runtime.ps1` 更新时自动重新生成运行时 zip（`-ForceRuntime` 可强制）

### 修复
- `FORZA_SYNC_APP_DIR` 对嵌入式解释器不可见（.NET 侧写环境变量不会被 Python 的 `os.environ` 读取）：
  改为同时写入 Python 侧 `os.environ`，桌面版数据库/配置才会正确落在程序目录或安装目录

### 移除
- 移除 MSI 打包（`web\make-msi.ps1`、`web\msi-generate.ps1`、`installer\msi\`）与 WiX 工具依赖（`dotnet-tools.json`）
- 移除 Setup 安装程序（`installer\`、`web\make-installer.ps1`）——GUI 自己解压运行时后不再需要

## [0.4.2] - 2026-08-30

### 新增
- **MSI 安装包**：新增标准 Windows Installer 分发（`web\make-msi.ps1` → `web\dist\ForzaGallerySync-0.4.2.msi`，per-user、x64，WiX v4 构建；支持静默安装/卸载与组策略分发）
- MSI 卸载时自动把照片数据库保留到 `%APPDATA%\forza-sync\`，不会因卸载丢失

## [0.4.1] - 2026-08-30

### 修复
- CLI（Nuitka）在标准 CPython 下构建因 `sqlite3.dll` 同名冲突失败（`make-cli.ps1` 在 sqlite3.dll 位于 `DLLs` 目录时跳过显式打包，交由 Nuitka 自动处理）

### 变更
- GUI 安装程序 / CLI / 应用 exe 增加应用图标（复用历史版本图标，多尺寸 16~256）

## [0.4.0] - 2026-08-30

### 新增
- 检查更新：桌面版设置页新增「关于与更新」卡片与「检查更新」按钮，通过 GitHub Releases API 对比当前版本与最新版本
- 检查更新支持可选的 GitHub token（代码内 `GITHUB_TOKEN` 常量，或环境变量 `FORZA_SYNC_GITHUB_TOKEN` / `GITHUB_TOKEN`）：
  未认证限流 60 次/时，认证后提升到 5000 次/时
- 纯后端 CLI 单文件发布（Nuitka：`make-cli.ps1` → `cli-dist/forza-sync.exe`），脱离 Python 环境运行

### 变更
- 桌面版打包改为**安装程序模式**：`make-installer.ps1` → `web/dist/ForzaGallerySync-Setup-<版本>.exe`，
  安装时把 Python/.NET 运行时解压到安装目录，运行时直接使用安装目录里的环境
- 数据库默认位置移到**安装目录**（便携化）；首次使用自动迁移旧配置目录中的数据库；卸载时自动把数据库保留到用户配置目录
- `playwright`（浏览器登录）合并进 `requirements.txt`（原 `requirements-login.txt` 删除）
- 移除源码中的 `E:/conda/envs/FGS` 绝对路径（可移植化）：打包脚本默认从 PATH 自动探测 Python，
  可用 `-Python` / `-PythonEnv` 显式指定；`PythonHost` 未找到运行时给出明确提示
- GitHub Actions workflow 重写：同时构建并发布 GUI 安装包 + CLI 单文件（`v*` tag 或手动触发创建 Release）
- 内嵌 Python 运行时缓存增加 SHA256 一致性校验，运行时重新打包/升级后自动重新解压

### 修复
- FGS 环境缺失 `certifi` 导致 `import requests` 失败（已补装并加入依赖）
- 运行时缓存未感知内嵌 zip 更新，导致新服务函数缺失（「未知服务函数 check_update」报错）

## [0.3.0]

### 变更
- 桌面管理控制台前端由 Vue 3 + Tauri 替换为 **WinUI 3（C# + XAML）**
- 架构：Python.NET 内嵌 Python（替代 PyO3），复用 `forza_sync` 全部后端逻辑，仍无 HTTP 服务、无端口
- 页面：仪表盘 / 照片库 / 同步 / 设置 四个模块完整复刻
- 说明：旧 Tauri 前端保留在 `web-legacy/` 目录供参考

## [0.2.1]

### 修复
- 桌面版改用 Windows GUI 子系统，双击启动不再闪现命令行窗口（零控制台）
- CLI 中文输出按控制台代码页自动编码（GBK/UTF-8 自适应），兼容中文系统 PowerShell

### 说明
- CLI 在交互式终端的输出顺序（提供 `cmd /c start "" /wait` / `Start-Process -Wait` 同步方式）

## [0.1.0]

### 新增
- 首个版本：Forza 照片同步 CLI + 桌面管理控制台（Tauri + PyO3 内嵌 Python，无 HTTP 服务）
