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
;     - 更关键的是：该目录对当前用户**可写** —— 内置的自动更新会下载官方的
;       setup.exe 并静默运行它来升级（本安装程序自己），装在 Program Files 下
;       会因为不可写而只能退化成手动更新。
;  2) **固定目录名**（不含版本号）：升级时覆盖的是同一个目录，不会在 Program Files
;     之类的位置堆出一串 ForzaGallerySync-1.0.4-win-x64 目录。
;  3) 安装前关闭正在运行的程序（文件被占用会导致覆盖失败）。
;  4) 卸载时询问是否删除用户数据（默认保留）：配置与照片索引库在
;     %APPDATA%\forza-sync 下，那是用户的照片索引与登录凭据，默认不该被删掉。
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

; 互斥体名：应用侧与安装/卸载侧。
; ★ 必须在文件头用 #define（ISPP 预处理阶段就能看到），不能放到 [Code] 的 const 里 ——
;   [Setup] 段里的 {#名字} 只能替换 #define 定义的符号，引用 [Code] 的常量会替换失败。
#define AppMutexName "Local\ForzaGallerySync.SingleInstance.v1"
#define SetupMutexName "Local\ForzaGallerySync.Setup.v1"
#define UninstallMutexName "Local\ForzaGallerySync.Uninstall.v1"

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

; 运行中的程序占用 exe/dll，直接覆盖会失败。
; AppMutex 用应用自己的单实例互斥体名（见 web/Services/SingleInstance.cs）：
; Setup 与 Uninstall 启动时都会检查它，程序在运行就提示用户先关闭。
AppMutex={#AppMutexName}

; 防止安装程序被同时启动多次：两个安装程序同时跑会互相覆盖程序文件与卸载注册表项，
; 最后谁也装不干净。这是 Inno 的官方指令（Setup 启动时检查，已存在就提示
; "Setup is currently running" 并等待），不需要自己写 CreateMutex/CheckForMutexes。
; 实测教训：手写那套很容易踩两个 API 的形状陷阱 ——
;   procedure CreateMutex(const Name: String);          // 无返回值
;   function  CheckForMutexes(Mutexes: String): Boolean; // 返回 Boolean，不是数组
; 我按"返回数组、用长度判断"写会直接编译失败（Unknown type 'TMutex' / Type mismatch）。
SetupMutex={#SetupMutexName}

; 程序在运行时安装/卸载会把文件覆盖或删到一半，交给 Inno 的机制处理：
; 它检测到占用会提示用户关闭，而不是安装程序自己去强杀进程。
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

; 语言选择：默认（yes）会在开箱时弹"选择安装语言"对话框，用户多点一步、也拖慢了流程。
; 这里改成 auto —— Setup 先按 Windows 界面语言自动挑，**匹配到就直接用、不弹框**，
; 只有系统语言不在下面的 [Languages] 列表里时才显示选择对话框。
; 配合 UsePreviousLanguage（默认 yes）：同一程序之前装过，就沿用上次选的语言。
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage
UsePreviousLanguage=yes

[Languages]
; 只带简中与英文，与程序保留的 WinUI 语言资源一致
#ifdef HasChinese
Name: "chinese"; MessagesFile: "{#ChineseIsl}"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

; 开始菜单快捷方式做成可选项（默认创建）：有些用户只用桌面快捷方式或直接跑 exe，
; 不希望开始菜单被塞东西。取消勾选后连"卸载"入口也不建——卸载仍可从
; 设置 → 应用（控制面板）或安装目录里的 unins000.exe 走，不影响可卸载性。
[Tasks]
Name: "startmenu"; Description: "创建开始菜单快捷方式"; GroupDescription: "快捷方式："
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "快捷方式："
Name: "launchapp"; Description: "{cm:LaunchProgram,{#AppName}}"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
; 整目录打包：应用、WinUI/Windows App SDK 组件、内嵌 Python 运行时、语言资源
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenu
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"; Tasks: startmenu
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; 安装完成后可选立即启动（默认不勾选）
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent; Tasks: launchapp

[Code]
// ============================================================
//  卸载体验：是否删除用户数据
// ============================================================
// 说明：安装程序自身的单实例、以及"程序在运行时不许安装/卸载"这两件事，
// 都由 [Setup] 段的官方指令处理（SetupMutex / AppMutex），不需要在这里写代码。
// 这里只处理"卸载时问用户要不要删数据"。

var
  // 卸载时用户的选择。★ 必须用布尔值保存，不能把复选框控件留到后面再读：
  // 窗体 Free 掉之后那个控件引用就是悬空指针，在 CurUninstallStepChanged 里访问会
  // 触发 "Access violation ... module '_unins.tmp'" 并把卸载整个搞失败（踩过）。
  DeleteUserData: Boolean;

// 安装前若程序正在运行，给一个"强制关闭并继续"的出口。
// 这里只问一遍然后让 Setup 继续：AppMutex 会在随后再检查一次，应用已经退出就正常放行。
// 不用 CreateMutex/CheckForMutexes 自己判单实例 —— 那是 [Setup] 段 SetupMutex 的活。
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  if CheckForMutexes('{#AppMutexName}') then
  begin
    if MsgBox('{#AppName} 正在运行。安装需要先关闭它。' + #13#10 + #13#10 +
              '要立即结束该程序并继续安装吗？' + #13#10 +
              '（如果它正在同步照片，建议先回到程序里等待或取消——强制结束可能中断数据库写入。）',
              mbConfirmation, MB_YESNO) = IDYES then
      Exec('taskkill.exe', '/IM {#AppExeName} /T /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // 点"否"时不再弹别的框：后面 AppMutex 的官方提示会让用户去关闭程序。
  end;
end;

// 卸载时询问"是否删除用户数据"。
// 用独立窗体而不是往卸载向导里插页：向导的控件挂载点在不同 Inno 版本里不一致，
// 而 InitializeUninstall 里自建窗体的时机是稳定可用的。
// 返回 False 表示用户取消卸载。
function AskDeleteUserData(): Boolean;
var
  Form: TSetupForm;
  Text1, Text2: TNewStaticText;
  CheckBox: TNewCheckBox;
  OkButton, CancelButton: TButton;
begin
  DeleteUserData := False;
  Result := False;

  // CreateCustomForm(Width, Height, ShowCaption, ShowCloseButton)
  Form := CreateCustomForm(ScaleX(460), ScaleY(250), True, True);
  try
    Form.Caption := '卸载 {#AppName}';
    Form.Position := poScreenCenter;

    Text1 := TNewStaticText.Create(Form);
    with Text1 do
    begin
      Parent := Form;
      Left := ScaleX(16);
      Top := ScaleY(14);
      Width := Form.ClientWidth - ScaleX(32);
      Height := ScaleY(22);
      AutoSize := False;
      Caption := '是否同时删除用户数据？';
      Font.Style := [fsBold];
    end;

    Text2 := TNewStaticText.Create(Form);
    with Text2 do
    begin
      Parent := Form;
      Left := ScaleX(16);
      Top := ScaleY(40);
      Width := Form.ClientWidth - ScaleX(32);
      Height := ScaleY(74);
      AutoSize := False;
      WordWrap := True;
      Caption := '包含登录凭据、照片索引数据库与下载记录。' + #13#10 +
                 '实际位置：' + #13#10 +
                 '  %APPDATA%\forza-sync\config.json' + #13#10 +
                 '  ' + ExpandConstant('{app}') + '\forza_sync.db' + #13#10 +
                 '（你的照片文件在你自己指定的下载目录里，不受此选项影响。）';
    end;

    CheckBox := TNewCheckBox.Create(Form);
    with CheckBox do
    begin
      Parent := Form;
      Left := ScaleX(16);
      Top := ScaleY(146);
      Width := Form.ClientWidth - ScaleX(32);
      Caption := '同时删除上述用户数据（不可恢复）';
      Checked := False;   // 默认保留
    end;

    OkButton := TNewButton.Create(Form);
    with OkButton do
    begin
      Parent := Form;
      Caption := '继续卸载';
      ModalResult := mrOk;
      Default := True;
      Width := ScaleX(96);
      Left := Form.ClientWidth - ScaleX(16) - Width - ScaleX(104);
      Top := Form.ClientHeight - ScaleY(14) - Height;
    end;

    CancelButton := TNewButton.Create(Form);
    with CancelButton do
    begin
      Parent := Form;
      Caption := '取消';
      ModalResult := mrCancel;
      Cancel := True;
      Width := ScaleX(96);
      Left := Form.ClientWidth - ScaleX(16) - Width;
      Top := Form.ClientHeight - ScaleY(14) - Height;
    end;

    if Form.ShowModal = mrOk then
    begin
      DeleteUserData := CheckBox.Checked;   // 取完值再释放窗体
      Result := True;
    end;
  finally
    Form.Free;
  end;
end;

// 卸载前确认程序已退出：文件被占用会让卸载残留一堆删不掉的文件。
// 只提示、由用户决定是否强制关闭——用户可能正在同步，强杀会中断数据库写入。
//
// 注意这里不再做"卸载程序自身单实例"的手写判定：[Setup] 段的 AppMutex 已经让
// Setup 与 Uninstall 在启动时检查应用互斥体；而卸载程序之间同时跑，Windows 自己
// 就会挡（卸载器会删除自身与 unins000.dat，第二个实例拿不到文件即失败）。
// 手写 CreateMutex/CheckForMutexes 那套还特别容易踩 API 形状：
//   procedure CreateMutex(const Name: String);           // 无返回值
//   function  CheckForMutexes(Mutexes: String): Boolean;  // 返回 Boolean，不是数组
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;

  // 静默卸载不能弹窗（会被 SUPPRESSMSGBOXES 吞掉并可能触发 fatal），
  // 因此保留用户数据、不去动进程——和之前的行为一致。
  if UninstallSilent then
  begin
    Result := True;
    Exit;
  end;

  while CheckForMutexes('{#AppMutexName}') do
  begin
    if MsgBox('{#AppName} 正在运行，卸载前需要先关闭它。' + #13#10 + #13#10 +
              '要立即结束该程序并继续卸载吗？' + #13#10 +
              '（如果它正在同步照片，建议先回到程序里等待或取消——强制结束可能中断数据库写入。）',
              mbConfirmation, MB_YESNO) = IDNO then
      Exit;   // Result 保持 False：中止卸载

    if Exec('taskkill.exe', '/IM {#AppExeName} /T /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      Sleep(800);   // 等进程真正退出，循环里会再确认一次
    end
    else
    begin
      MsgBox('无法结束运行中的程序，请手动关闭后重试卸载。', mbError, MB_OK);
      Exit;
    end;
  end;

  // 程序已停止，再问用户数据怎么处理
  Result := AskDeleteUserData();
end;

// 用户勾选"删除用户数据"时执行清理。
// 放在 usUninstall 阶段：此时程序文件尚未开始删除，而决策已经拿到。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  I: Integer;
  DbFile, DbSuffix: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if DeleteUserData then
    begin
      DelTree(ExpandConstant('{userappdata}\forza-sync'), True, True, True);
      // 程序目录不可写时配置管理器会退到 {localappdata}\forza-sync，两处都清，
      // 避免"删了数据但看起来还在生效"。
      DelTree(ExpandConstant('{localappdata}\forza-sync'), True, True, True);

      // 数据库按设计放在程序目录（便于随目录整体携带），旧版本还可能把它留在
      // 配置目录里，所以配置目录已由上面的 DelTree 覆盖，这里处理程序目录。
      // {app} 在卸载器里展开为实际安装目录，路径拼接是安全的。
      //
      // 这里不能 DelTree({app})：程序文件这时尚未删除，清空该目录会让卸载器
      // 之后写 unins000.dat 失败，反而把卸载搞坏。
      for I := 0 to 2 do
      begin
        case I of
          0: DbSuffix := '';
          1: DbSuffix := '-wal';
          2: DbSuffix := '-shm';
        end;
        DbFile := ExpandConstant('{app}') + '\forza_sync.db' + DbSuffix;
        if FileExists(DbFile) then
        begin
          if not DeleteFile(DbFile) then
            MsgBox('无法删除数据库文件（可能仍被其它程序占用）：' + #13#10 + DbFile,
                   mbError, MB_OK);
        end;
      end;
    end;
  end;
end;
