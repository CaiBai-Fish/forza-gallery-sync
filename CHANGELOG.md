# 更新日志 (Changelog)

本仓库所有值得注意的变更都会记录在此文件。
格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

> GitHub Actions（`.github/workflows/build-release.yml`）发布 Release 时，
> 会自动读取本文件对应版本的章节作为发布说明。

## [未发布]

### 修复
- **部分错误提示没有接入多语言，英文界面会显示中文**：`UpdateService` 与
  `IncrementalUpdateService` 里 12 处异常消息是硬编码中文，而这些消息会直接显示给
  用户（下载失败、更新包损坏、脚本启动/生成失败、哈希清单缺失、哈希校验失败、
  内嵌资源缺失、增量资产空包等）。对应的文案键其实早已存在（`Upd_*` / `Inc_*`），
  只是没接上，现在改为 `StringLocalizer.Get/Format`。
  排查手段固化成了 `tools/check_i18n_keys.py`：扫描 C# 里所有
  `StringLocalizer.Get/Format` 调用并核对键是否存在于两种语言的字典里
  （`StringLocalizer` 取不到键时会原样返回键名，界面会直接显示键名）。

## [1.0.5] - 2026-09-15

### 修复
- **卸载时勾选"删除用户数据"不会删掉照片索引库**：数据库按设计放在**程序目录**
  （`<安装目录>\forza_sync.db`，便于随目录整体携带），但卸载逻辑只清
  `%APPDATA%\forza-sync` 与 `%LOCALAPPDATA%\forza-sync`，于是数据库被留在盘上——
  用户以为数据已删除，实际仍然存在。卸载日志里的
  `Failed to delete directory (145)`（145 = 目录非空）就是这个残留导致的。
  现在 `usUninstall` 阶段会一并删除 `{app}\forza_sync.db` 及其 `-wal` / `-shm`
  附属文件；删除失败（被占用）时给出明确提示而不是静默跳过。
  卸载对话框列出的数据位置也同步改为实际路径（原先写的是 `%APPDATA%` 下的旧位置）。
  实测：勾选删除后数据库与配置全部清除、程序目录随之删净（修复前该目录因残留数据库而残留）。

### 新增（工具）
- `tools/verify_release_hashes.py <版本>`：把 `hashes` 分支的清单与 Release 上的实际文件
  逐字节核对 SHA-256（客户端自动更新比对的正是这些值），发布后一条命令即可确认不会 404 / 拒装。
- `tools/inspect_db.py` / `tools/dump_db.py`：检查索引库是否含用户数据及内容，便于判断残留数据能否清理。

### 文档
- 修正 README 中"更新默认走增量更新"的过时说法：默认是下载并运行官方安装程序，
  增量/完整包只是它不可用时的回退路径；设置页那个开关也早已改名为回退策略。
- 修正卸载数据路径描述：数据库在 `<安装目录>\forza_sync.db`（不在 `%APPDATA%`），
  README 与 CHANGELOG 原先写的是旧位置，与"运行时落点"表格自相矛盾。
- 发布说明的资产清单补上**安装程序**（此前 CI 生成的 Release 说明只列了 zip 与 CLI，
  用户看不到 setup.exe 的存在），已同步修正 1.0.4 的 Release 说明。

## [1.0.4] - 2026-09-15

### 变更
- **更新方式改为"下载官方安装程序并运行它"**（原先是在应用内解压覆盖程序目录）：
  - 点「下载并安装」直接下载 `ForzaGallerySync-<版本>-setup.exe`、校验 SHA256，
    然后退出应用、由独立脚本静默安装（`/SILENT`，显示进度窗口），装完自动启动新版本；
    安装失败或用户取消时自动启动原版本，安装包保留供手动重试。
  - **安装位置随原形态**：原本是免安装版（zip 解压运行）→ 带 `/DIR=<当前目录>` 装回原处、
    保持免安装；原本是安装版 → 不带 `/DIR`，交给 Inno 用它记录的安装目录，避免同一版本
    出现两个安装位置。判据用卸载注册表项，而不是"目录里有没有 unins000.exe"。
  - **点按钮即确认，不再弹二次确认框**：只有在程序目录不可写（装了也覆盖不了）时才给出
    手动下载出口。哈希清单拿不到、下载失败等情况会回退到增量更新或完整包。
  - 设置页的开关语义随之调整：原"优先增量更新"改为**回退策略**（安装程序不可用时的选择）。
  - `UpdateService` 新增 `DownloadInstallerAsync` / `LaunchInstaller` / `IsInstalled`，
    新增内嵌脚本 `Resources/run-installer.ps1`；下载与清单地址支持用
    `FORZA_SYNC_UPDATE_BASE_URL` / `FORZA_SYNC_HASHES_BASE_URL` 指向本地服务，
    便于离线端到端验证（实测：本地模拟 Release → 下载 512 KB → 哈希校验通过 →
    生成脚本，免安装版带 `/DIR`、安装版不带，脚本语法 OK）。
### 新增
- **标准 EXE 安装程序**：`ForzaGallerySync-<版本>-setup.exe`（Inno Setup 6，向导式、简中/英文界面）。
  - **per-user 安装、免管理员**：装到 `%LOCALAPPDATA%\Programs\ForzaGallerySync`，不弹 UAC；
    该位置对当前用户可写，程序内置的自动更新在安装版上照常工作（更新方式见上方"变更"）。
  - 固定目录名（不带版本号）：升级即覆盖同一目录，不会堆出多个版本目录。
  - 自带桌面 / 开始菜单快捷方式与标准卸载入口；卸载默认**保留用户数据**
    （`<安装目录>\forza_sync.db` 的照片索引库与 `%APPDATA%\forza-sync` 的配置），
    可选择一并删除。
  - 安装前由 Inno 自己提示关闭运行中的程序，不强行结束进程（用户可能正在同步）。
  - 安装包同样进 `hashes` 分支清单，可校验 SHA256；CI 里用 chocolatey 装 Inno Setup 编译。
  - 新增 `web/make-installer.ps1` 与 `web/Resources/installer.iss`；简体中文语言文件
    `web/Resources/Languages/ChineseSimplified.isl` 随仓库提供（官方 Inno 不自带中文），
    缺失时自动退回英文界面，不让构建失败。
  - **语言选择对话框默认不再弹出**：`ShowLanguageDialog=auto` + `LanguageDetectionMethod=uilanguage`
    + `UsePreviousLanguage=yes`，按 Windows 界面语言自动匹配，匹配不到才问、重装沿用上次选择。
    实测 zh-CN 系统启动 2 秒直进向导、界面为中文（不再多点一步）。
  - **安装程序单实例**（Inno 官方 `SetupMutex`）：重复启动提示已有安装在进行，避免两个安装程序
    互相覆盖程序文件与卸载注册表项。程序正在运行时安装/卸载由 `AppMutex` 拦截，
    并额外提供"强制结束该程序并继续"的出口（官方 AppMutex 只提示、不处理）。
    注意官方 `SetupMutex` 的检查发生在语言对话框之后，所以"两个实例同时停在语言选择界面"
    是正常现象，不代表失效。
  - 开始菜单快捷方式做成**可选项**（默认创建），桌面图标同样可选；取消勾选不影响可卸载性。
  - **卸载时询问是否删除用户数据**（默认保留）：对话框列出实际路径
    （`<安装目录>\forza_sync.db` 与 `%APPDATA%\forza-sync\config.json`），并说明照片文件
    在下载目录里、不受该选项影响。不勾选时保留，重装后免于重新登录。
    > 注：1.0.4 的卸载只清了 `%APPDATA%` / `%LOCALAPPDATA%` 下的 `forza-sync`，
    > 漏掉了程序目录里的数据库——已在 1.0.5 修掉（见对应条目）。
  - 新增 `web/verify-installer.ps1`：自动断言静默安装/卸载、卸载窗体控件齐全、
    取消卸载时数据保留等 12 项。"勾选删除数据"那条分支不做自动化——Inno 的 `TSetupForm`
    控件未暴露 UI Automation 的 Invoke/Toggle 模式，自动化只能按坐标点击，
    而这会真的删掉用户的凭据与索引库，改由人工确认（脚本末尾打印步骤）。
- **增量更新**：更新时不再每次都重下约 130 MB 的完整包，只下载**相对上一版真正变化**的文件。
  发布目录解压后约 269 MB，其中 Python 运行时 77 MB、.NET / Windows SDK 运行时 182 MB
  跨版本几乎不变；实测一次版本更新真正变化的只有应用自身那几个文件。
  - CI 为需要独立资产的每个文件生成一个单文件 zip 并发到 Release，
    同时把逐文件清单 `increment.json`（`path` / `size` / `sha256`）发布到 `hashes` 分支；
    只给"上一版没有或内容变了"的文件发资产（110 个文件全发会到 92 MB，接近完整包而失去意义）。
  - 客户端**只比对本地实际文件的哈希**，不依赖"已安装版本"记录，因此不需要任何本地基线清单。
  - 变化的小文件（低于 256 KB）打成一个容器包，避免逐个小文件发资产（会有几百个），
    也避免"一个小文件变了就整体回退完整包"。
  - 逐个文件做 SHA256 校验；任一环节不成立（无清单、版本不符、哈希不符、下载失败、
    本地缺失又无来源）都**自动回退完整包**，完整包那条路径与之前完全一致。
  - 更新后的替换流程与完整包同构：增量包同样带 `ForzaGallerySync-<版本>-win-x64/` 顶层目录，
    "只覆盖、不删除"，并且不覆盖正在运行的更新脚本自身。

### 修复
- **打包出的 Python 运行时缺少 `playwright`，浏览器登录功能不可用**（1.0.3 的包中招）：
  没有 playwright 就无法调起浏览器完成登录。原因是运行时检查只"警告后继续"，
  缺陷包一路发到了 Release。现在改为三层硬断言，任何一层失败都中止打包：
  - 打包前用 `importlib.util.find_spec` 探测 8 个必需依赖（requests / urllib3 / certifi /
    idna / charset_normalizer / playwright / greenlet / pyee），缺失直接 `throw` 并给出修复命令；
  - 归档后正则断言关键条目在位（注意 zip 内真实路径是 `Lib\site-packages\playwright\driver\node.exe`，
    分隔符是 `\`，正则必须写 `[\\/]`，否则会把齐备的依赖误报成缺失）；
  - 交付包端到端验证：解压全新空目录并启动，`.runtime-id` 必须等于内嵌 zip 的 SHA-256，
    再实际 `import playwright` 并用系统浏览器通道启动成功。
  实测结果：`python-runtime.zip` 60.1 MB / 3312 条目（playwright 185 项、driver 118 项），
  便携包 123.1 MB、安装包 113.9 MB，首次启动 8.1 秒解压完成，playwright 成功调起 Edge 153。
- **旧运行时不会被自动替换的问题**：运行时目录内记录来源 zip 哈希的 `.runtime-id`
  与当前内嵌 zip 不一致时会删除旧目录并全新解压，因此升级后无需手动清理旧运行时。
- **卸载程序会以 `Access violation` 失败且什么都不删**：卸载时把复选框控件的引用留到
  `CurUninstallStepChanged` 里再读，而那个窗体此时已经被 `Free` 掉，引用成了悬空指针。
  改为在窗体释放**之前**把用户的选择存进布尔变量。
- **发布目录里缺 `Assets\`**：`<Content Include="Assets\**\*"/>` 只把图标文件放进 `bin`，
  **进不了 publish 输出**（publish 走 `ResolvedFileToPublish`），导致发布版的
  `Assets\app-icon.png` 不存在 —— 主窗口标题栏那个 `<Image Source="Assets/app-icon.png"/>`
  在安装版/便携版里加载不到，表现为标题栏图标缺失。现在在 csproj 里显式把 Assets
  补进 `ResolvedFileToPublish`，并在 CI 加断言防止回归。
- 小文件的"是否需要更新"改为一律计算 SHA256：原先按体积走捷径，
  小文件改变内容而体积不变（改常量、改文本）会被静默漏掉。
- 更新自检对"空增量包"补充失败判定：原先仅断言条目都在预期顶层目录下，
  0 条目时会全部通过，曾因此漏掉"打包根目录不存在 → 产出空 zip"的缺陷。

### 新增（工具）
- `web/make-increment.ps1`：生成增量资产与清单，CI 与本地共用同一实现，便于本地验证清单格式。
- 增量更新自检入口（`FORZA_SYNC_INC_TEST=1`）：把清单比对 → 下载 → 逐文件校验 → 重打包
  → 生成替换脚本整条链路跑完并断言，不覆盖任何正在使用的文件。
  配合 `FORZA_SYNC_INC_MANIFEST_FILE` / `FORZA_SYNC_INC_DOWNLOAD_BASE` 可指向本地服务，
  离线即可端到端验证（实测：本地下载 29.11 MB 增量包，完整包为 130 MB）。
- `web/organize-release.ps1`：发布目录**校验**（裁剪是否生效、必需语言资源与程序文件是否在位）。
- `tools/check_runtime_zip.py`：断言 `python-runtime.zip` 里登录所需依赖齐备（含 playwright 与 driver）。
- `tools/check_extracted_runtime.py`：对**已解压**的运行时做真实导入与浏览器启动验证
  （运行时目录不含 `python.exe`，脚本临时借用打包环境的解释器，验完删除）。

### 改进
- **发布目录在编译期完成裁剪**：WinUI 自带的多语言资源有 83 个目录（166 个文件 / 3.3 MB），
  每个只含 `.mui`，之前的发布目录因此有 86 个目录、498 个文件。
  现在在 `ForzaGallerySync.csproj` 里通过 Windows App SDK 的 `MicrosoftWindowsAppSDKFilesExcluded`
  扩展点排除用不到的语言，`dotnet publish` 直接产出精简结构（保留 `zh-CN` / `zh-TW` / `en-us`），
  不再依赖"打包后删文件"。注意 `<SatelliteResourceLanguages>` 对这类 `.mui` 无效——
  它们由 Windows App SDK 的 targets 用通配符复制，不走 .NET 附属程序集机制。
- 尝试过把托管程序集与语言资源目录"分类"到 `runtime\` / `resources\` 子目录，
  **实测不可行**（.NET 主机在托管代码前退出 `0x80008009` / `0xE0434352`；
  WinUI 抛 `COMException：资源加载器缓存没有已加载的 MUI 项`）。
  自包含 WinUI 发布目录的扁平布局是平台约束，只能"减少文件"不能"重新分类"，
  该结论已记录在 `AGENTS.md` 与 `web/organize-release.ps1` 顶部的注释里（换 SDK 版本后需复测）。

## [1.0.3] - 2026-09-15

### 新增
- **单实例限制**：同一登录会话内只允许一个窗口。重复启动时第二个实例**不建窗口**，
  用 `RegisterWindowMessage` + `PostMessage(HWND_BROADCAST)` 唤醒已运行实例，
  然后显式结束自己的进程（`Environment.Exit(0)`，退出码 0）。
  - 唤醒时若窗口处于最小化状态会先还原；`SetForegroundWindow` 被系统前台锁挡下时
    改用 Z 序置顶，并在日志里区分"被前台锁挡住"与"句柄失效"两种结果。
  - 用 `Local\` 用户级命名互斥体，只限制当前登录会话；上一个实例被强杀时
    按 `AbandonedMutexException` 处理（继续启动，而不是卡住）。
- **版本探测扩为五路**：`releases/latest` API → `tags` API → `releases/latest` 的
  302 跳转 → `tags` 页面 HTML → CHANGELOG 保底（自身再有 raw → blob 页面 → 本地副本三级回退）。
  `api.github.com` 匿名请求按出口 IP 限流（60 次/小时），单一 API 路径实测经常直接 403。
- **安装前确认对话框**：确认之后会退出应用并覆盖程序文件，对话框会说明哈希校验、
  "只覆盖不删除"以及程序目录不可写时的替代做法。
- 更新检查结果里新增 `probe_source`（本次命中的探测路径）与 `attempts`
  （每一路的成功/失败与原因），便于排查"为什么走了某一路"。

### 修复
- **CI 无法发布哈希清单**（1.0.2 的修复无效）：runner 上 token 拼进 git remote URL 会被
  `Password authentication is not supported` 拒绝，`gh auth setup-git` 又因需要
  `/dev/tty` 而失败。改为用 REST API 单文件提交（`gh api --method PUT`），
  只依赖 `contents:write` 权限；发布后再回读校验远端内容与本地哈希一致。
- **拿不到哈希清单时不再放行安装**：原先"清单不可用 → 跳过校验但继续安装"，
  现在按"拿不到清单不自动安装"处理，只给手动下载出口。
- 哈希清单改为规范格式 `<版本>.txt`（每行 `<sha256>  <文件名>`，与 `sha256sum` 一致），
  客户端优先读它，`hashes.json` 作为同分支的次选。
- 更新说明（CHANGELOG 章节）改为独立获取：版本判断不再与"取不取得到发布说明"耦合，
  走 API 路径时也能显示更新日志。
- 探测失败时 `error` 里带全部路径的具体原因，并区分 403 / 超时 / 不可达。
- 发布工作流增加 **tag 与工程版本号一致性校验**（不一致直接失败），
  并交叉核对 `pyproject.toml` 与 `forza_sync/__init__.py` 的版本号。

## [1.0.2] - 2026-09-15

### 修复
- **哈希来源优先级纠正**：`hashes` 分支（静态文件、不消耗 GitHub API 配额）改为优先，
  Releases API 的 `digest` 仅在其不可用时兜底。API 有 60 次/小时的匿名限流，
  原先的优先顺序会在额度用尽时把校验拖到兜底路径上。
- **CI 的 hashes 发布步骤认证失败**：把 token 拼进 git remote URL 会被
  `Password authentication is not supported` 拒绝，改用 `gh auth setup-git` 配置凭证。

## [1.0.1] - 2026-09-15

### 新增
- **自动更新**：设置页「关于与更新」在发现新版本后出现「下载并更新」按钮，
  流程为下载官方发布包 → **SHA256 校验** → 退出应用 → 覆盖程序文件 → 自动重启。
  - 校验走**双源**：优先用 GitHub Releases API 的 `digest.sha256`（平台计算、与上传字节绑定，
    最权威），失败时回退 `hashes` 分支的静态清单（不消耗 API 配额）。
    两源都取不到时跳过校验但记录告警，不阻断更新。
  - `hashes` 清单由 CI 发布到**独立分支**，不随 `git clone` 下到工作区，
    主分支历史也不会被历次哈希撑大。
  - 替换脚本 `web/Resources/apply-update.ps1` 内嵌为资源，运行时展开到临时目录执行：
    先等旧进程退出（运行中的 exe/dll 被占用，应用无法覆盖自身），
    再**只覆盖、不删除**——多余文件保留，避免误删导致程序起不来；
    覆盖失败逐条写日志并提示手动更新，不静默失败。
  - 程序目录不可写时给出明确提示并转为手动更新。
- 诊断开关 `FORZA_SYNC_UPDATE_SIMULATE=1`：只走完下载 + 校验 + 生成脚本并做语法自检，
  不退出、不覆盖文件，便于排查更新链路。

### 修复
- 替换脚本原先以 **UTF-8 无 BOM** 写盘，而它由 Windows PowerShell 5.1 执行，
  5.1 会按系统 ANSI 代码页解码 → 脚本里的中文变乱码、引号错配 →
  **整个脚本语法错误、根本无法运行**。改为 UTF-8 带 BOM。
- 脚本模板占位符原先直接字符串替换，路径含单引号时会截断引号、破坏脚本语法，
  现按 PowerShell 单引号字符串规则转义。

## [1.0.0] - 2026-09-15

### 变更
- **更新说明改用开源 Markdown 控件渲染**：引入 `CommunityToolkit.WinUI.UI.Controls.Markdown`
  （MIT，底层是开源的 Markdig），设置页的 CHANGELOG 章节现在渲染成格式化文本，
  不再显示 `###`、`**`、反引号等 Markdown 标记
- **照片操作统一并补全**（新增 `web/Services/PhotoActions.cs`，总览页与照片库共用）：
  - 新增「复制图片」：把图片本身写入剪贴板，可直接粘贴到聊天 / 画图 / Office；同时放入文件路径，
    方便只接受文本的程序
  - 「打开文件」改为**用系统默认图片应用打开**（原先是 explorer 的"选中文件"，并不是打开图片）；
    另设「打开所在文件夹」用于定位
  - 右键菜单覆盖三处：总览页缩略图、照片库缩略图，以及两处的大图（详情 / 预览）
  - 原图字节按需缓存，复制与显示共用一次读取；缩略图则用完即弃，避免一页几十张缩略图各自
    常驻一份全尺寸原图
- **重构桌面端前端 UI（仍为 WinUI 3）**：导航改为按「图库 / 任务」分组的 `NavigationView`，
  新增自定义标题栏（同步进行中显示进度）与导航页脚常驻账号状态
- **页面职责重新划分，消除重复配置**：
  - 「仪表盘」→「总览」：只保留只读统计（照片总量、按游戏分布、最近同步、最新照片预览），
    移除与设置页重复的 Token / 配置概览卡片
  - 「同步」页只保留**本次任务参数**（选游戏 / 数量上限 / 强制重下），
    移除与设置页重复的「每页数量」；新增已用时长与预计剩余时间、空闲时展示上次结果
  - 「设置」页整合账号登录、下载目录（含数据库 / 配置文件路径）、网络与并发、启用游戏、检查更新
- **主题适配**：全部颜色改为 `ThemeResource` + `ThemeDictionaries`（浅色 / 深色两套语义色），
  跟随系统主题；新增 `web/Styles/Controls.xaml` 统一卡片 / 文本 / 按钮样式，移除页面内写死的颜色
- **图标规范化**：界面 emoji 图标统一替换为 Segoe Fluent Icons 字形，避免跨主题渲染不一致；
  窗口标题栏改用应用图标（从 `Assets/forza-gallery-sync.ico` 无损提取 256px 条目为 `Assets/app-icon.png`）
- 照片库缩略图按可用宽度自适应（均分列宽，消除每行末尾的空隙），详情信息栏宽度 340 → 360
- **缩略图 ↔ 大图使用共享元素（Hero）转场**：新增 `web/Services/HeroTransition.cs` 统一实现，
  总览页「最新照片」与照片库详情共用同一套动画

### 修复
- **总览页把页脚账号状态错误地显示为「未登录」**：根因是 `auth_status` 的返回值用了裸的
  `JsonSerializer.Deserialize`（默认命名策略），无法把 Python 的 `has_token` 映射到 `HasToken`，
  于是静默拿到默认值 `false`。现统一改用 `Models.Json`（`SnakeCaseLower`），并且账号状态改由
  主窗口轮询统一维护，页面不再各自判断（原先总览页会在数据尚未加载时用默认值覆盖状态）
- **放大动画在目标页面已显示完成之后才播放**：原先要等 `photo_meta` 与全尺寸原图加载完成才显示
  详情/预览层，用户会先看到页面"空一下"、然后目标内容整块出现，动画像是事后补播。
  现在改为：先用已知信息与缓存的缩略图立即铺好布局并**同时起帧**，原图与元数据在后台加载完成后再
  无缝替换（带淡入）；卡片框架（标题栏 / 信息栏 / 底部操作）随动画一起淡入，不再提前露出
- **放大动画结束时闪烁**：先后修掉两处成因——
  1. 动画结束后先淡入缩略图、待原图就绪再淡入原图，形成两次画面变化。现在动画结束后先在
     450ms 内等原图，就绪则直接以原图收尾；确实未就绪时才用缩略图占位，随后以交叉淡入替换
     （替换时下层始终有内容）。同时让 `LoadFullImageAsync` 真正等到位图解码完成再返回。
  2. hero 覆盖层原先在动画内部就自行隐藏，而目标大图要下一帧才画出来，中间存在"什么都没画"
     的一帧。现在动画结束后**不立即隐藏**（`HeroTransition.PlayAsync(..., hideWhenDone: false)`），
     等目标图片淡入并真正画出这一帧后再交接隐藏。
- **自定义标题栏内容贴着窗口左缘**：左留白改为取 `NavigationView.CompactPaneLength`
  （48px），与导航栏自身的左侧节奏一致。不再使用系统标题栏按钮的内缩量（多按钮时接近
  140px，会把标题推到中间显得奇怪）
- **检查更新改为读 CHANGELOG**：用仓库根目录的 `CHANGELOG.md` 判断最新版本，不消耗 GitHub API 配额、
  不会再遇到 rate limit。三级来源，任一成功即返回：
  1. 远程 CHANGELOG，**依次尝试 3 个镜像**（`raw.githubusercontent.com` → `cdn.jsdelivr.net` →
     `fastly.jsdelivr.net`），规避单一 raw 域名在部分网络下不可用
  2. 本地 `CHANGELOG.md`：从程序/安装目录、可执行文件目录逐级向上、当前工作目录、
     `forza_sync` 包目录及仓库根依次查找
  3. GitHub Releases API：仅当上面都失败时兜底

  设置页会把该版本的更新说明一并展示。若最终仍回退到 API 并遇到限流，提示会同时给出
  **每一级失败的具体原因**（以前只会看到一个莫名的 "rate limit exceeded"）以及额度的恢复时间。
  `CHANGELOG.md` 不随程序打包
- **开发时 `FORZA_SYNC_PYTHON_HOME` 被安装目录的运行时遮蔽**：机器上装过打包版后，安装目录里的旧
  运行时会一直胜出，导致改了代码却仍在跑旧版本（现象非常隐蔽）。现在显式设置的
  `FORZA_SYNC_PYTHON_HOME` 优先于安装目录 runtime
- **数据库报「unable to open database file」现在可自愈且可读**：
  - `PRAGMA journal_mode=WAL` 失败（目录只读、文件系统不支持 WAL）时降级为默认日志模式，不再整体失败
  - `FORZA_SYNC_APP_DIR` 指向的目录不可写时回退到配置目录（探测改为实际试写文件，
    因为 Windows 受限令牌 / 沙箱下 `os.access(W_OK)` 会误报可写）
  - 打开失败时给出包含具体路径与排查方向的中文提示（权限 / 被云同步或旧进程占用 / 文件损坏）
- 记录并规避本项目 XAML 编译器的两个坑（详见 README「UI 结构与开发约定」）：
  `GridView.ItemWidth/ItemHeight` 与 `InfoBar.IsOpen` 上的 `x:Bind` 都会让代码生成阶段失败

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
