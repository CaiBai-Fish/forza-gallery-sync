; ============================================================
;  Forza Gallery Sync - 安装程序脚本（Inno Setup 6）
;
;  由 web/make-installer.ps1 调用 ISCC.exe 编译，参数：
;    /DAppVersion=1.0.4 /DSourceDir=<发布目录> /DOutputDir=<输出目录>
;
;  设计要点（都与本项目的自动更新机制配套）：
;
;  1) **per-user 安装，不需要管理员**：装到 %LOCALAPPDATA%\Programs\ForzaGallerySync。
;     - 不弹 UAC，普通用户可装可卸；
;     - 更关键的是：该目录对当前用户**可写** —— 程序内置的自动更新是"下载新版本
;       文件覆盖程序目录"，装在 Program Files 下会因不可写而只能退化成手动更新。
;  2) **固定目录名**（不含版本号）：自动更新覆盖的就是这个目录；升级时也不会
;     在 Program Files 之类的位置堆出一串 ForzaGallerySync-1.0.3-win-x64 目录。
;  3) 安装前关闭正在运行的程序（文件被占用会导致覆盖失败）。
;  4) 卸载时按照 Inno 的常规语义处理：保留用户数据（%APPDATA%\forza-sync 下的
;     配置与数据库），因为那是用户的照片索引与登录凭据，不该被卸载程序删掉。
;  5) 安装包本身也是发布产物之一，会进 hashes 清单做 SHA256 校验。
; ============================================================

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\ForzaGallerySync-0.0.0-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

; 简体中文语言文件由仓库自带（官方 Inno 不自带中文），位于本脚本同级的 Languages\ 下。
; 由 make-installer.ps1 在文件存在时用 /DHasChinese 打开；缺失时退回仅英文。
#define ChineseIsl AddBackslash(SourcePath) + "Languages\ChineseSimplified.isl"

#define AppName "Forza Gallery Sync"
#define AppExeName "forza-gallery-sync.exe"
#define AppPublisher "CaiBai-Fish"
#define AppUrl "https://github.com/CaiBai-Fish/forza-gallery-sync"

[Setup]
AppId={{8F3A9C41-2B7E-4D5A-9C1F-6E0B7A4D3C52}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} 安装程序
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

; 安装程序自身的图标 = 应用图标（多尺寸 ICO，含 32bpp DIB 条目）。
; 由 make-installer.ps1 在文件存在时用 /DIcoFile 传入；缺失时自动省略，
; 不让"图标文件没带上"把整个安装包构建搞失败。
#ifdef IcoFile
SetupIconFile={#IcoFile}
#endif

; per-user、免管理员（见文件头说明 1、2）
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\ForzaGallerySync
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; 卸载信息（控制面板"应用和功能"里显示）
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; 运行中的程序占用 exe/dll，直接覆盖会失败。交给 Inno 的标准机制：它检测到文件被占用
; 时会提示用户关闭，而不是安装程序自己去强杀进程——用户可能正在同步，强杀有损坏
; 数据库写入的风险。
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll

; 输出：ForzaGallerySync-<版本>-setup.exe
OutputDir={#OutputDir}
OutputBaseFilename=ForzaGallerySync-{#AppVersion}-setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
; 只带简中与英文，与程序保留的 WinUI 语言资源一致
#ifdef HasChinese
Name: "chinese"; MessagesFile: "{#ChineseIsl}"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "launchapp"; Description: "{cm:LaunchProgram,{#AppName}}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 整目录打包：应用、WinUI/Windows App SDK 组件、内嵌 Python 运行时、语言资源
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; 安装完成后可选立即启动（默认不勾选）
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent; Tasks: launchapp
