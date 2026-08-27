//! Forza Gallery Sync 桌面应用。
//!
//! 架构：Tauri（Rust + WebView2）窗口加载 Vue 前端，通过 PyO3 在进程内
//! 嵌入 Python 解释器，直接调用 :mod:`forza_sync.service` 的纯函数。
//! 完全无 HTTP 服务、无端口、无网络监听。
//!
//! 使用 Windows 控制台子系统（不设置 `windows_subsystem`），同一 exe 支持两种模式：
//! - 无命令行参数：启动 GUI 窗口（双击启动、独占控制台时自动隐藏，不弹出黑色窗口）
//! - 带命令行参数：headless 命令行模式，供脚本 / 定时任务在无窗口下使用

use std::path::Path;
use std::sync::Arc;

use pyo3::prelude::*;
use pyo3::types::PyModule;
use serde_json::{json, Value};
use tauri::{Manager, State};

/// 持有已初始化的 forza_sync.service 模块。
struct PyState {
    module: Py<PyModule>,
}

fn project_root() -> Option<String> {
    if let Ok(root) = std::env::var("FORZA_SYNC_PROJECT_ROOT") {
        return Some(root);
    }
    let mut dir = std::env::current_exe().ok()?.parent()?.to_path_buf();
    loop {
        if dir.join("forza_sync").join("__init__.py").exists() {
            return Some(dir.to_string_lossy().into_owned());
        }
        if !dir.pop() {
            return None;
        }
    }
}

/// 定位 Python 运行时目录。优先级：
/// 1. 环境变量 `FORZA_SYNC_PYTHON_HOME`
/// 2. 随包内嵌运行时（Windows 上 resource_dir = exe 目录，运行时在 `exe 目录/python/`）
/// 3. 回退本地开发用 conda 环境
fn python_home(resource_dir: Option<&Path>) -> String {
    if let Ok(home) = std::env::var("FORZA_SYNC_PYTHON_HOME") {
        return home;
    }
    if let Some(dir) = resource_dir {
        let embedded = dir.join("python");
        if embedded.join("Lib").exists() {
            return embedded.to_string_lossy().into_owned();
        }
    }
    "E:/conda/envs/FGS".into()
}

/// 初始化嵌入式 Python 解释器并导入 service 模块。
fn init_python(resource_dir: Option<&Path>) -> Result<Py<PyModule>, String> {
    // 嵌入式 Python 需显式指定 home（含标准库 Lib/ 与 site-packages），
    // 否则解释器无法定位 Lib 目录。
    let home = python_home(resource_dir);
    std::env::set_var("PYTHONHOME", &home);
    std::env::set_var("PYTHONNOUSERSITE", "1");

    // 扩展模块依赖的 DLL 搜索目录（conda: Library/bin；内嵌 python.org: DLLs）
    let search_dirs = vec![format!("{home}/Library/bin"), format!("{home}/DLLs")];
    let path = std::env::var("PATH").unwrap_or_default();
    let mut parts: Vec<&str> = search_dirs.iter().map(|s| s.as_str()).collect();
    if !path.is_empty() {
        parts.push(path.as_str());
    }
    std::env::set_var("PATH", parts.join(";"));

    pyo3::prepare_freethreaded_python();
    Python::with_gil(|py| {
        // 把存在的 DLL 目录加入搜索（os.add_dll_directory 是官方可靠方式），
        // 供 _sqlite3 等扩展模块找到其依赖的 sqlite3.dll 等
        let os_mod = py.import("os").map_err(|e| e.to_string())?;
        for d in &search_dirs {
            if Path::new(d).exists() {
                os_mod
                    .call_method1("add_dll_directory", (d,))
                    .map_err(|e| e.to_string())?;
            }
        }

        let sys = py.import("sys").map_err(|e| e.to_string())?;
        let path = sys.getattr("path").map_err(|e| e.to_string())?;
        if let Some(root) = project_root() {
            path.call_method1("insert", (0, root))
                .map_err(|e| e.to_string())?;
        }
        let module = py.import("forza_sync.service").map_err(|e| e.to_string())?;
        Ok(module.into())
    })
}

/// 调用 service.call_service(name, args)，返回解析后的 JSON。
fn py_call_json(state: &PyState, name: &str, args: &Value) -> Result<Value, String> {
    Python::with_gil(|py| {
        let module = state.module.bind(py);
        let helper = module.getattr("call_service").map_err(|e| e.to_string())?;
        let args_str = args.to_string();
        let result: String = helper
            .call1((name, args_str))
            .map_err(|e| format!("{name}: {e}"))?
            .extract()
            .map_err(|e| e.to_string())?;
        serde_json::from_str(&result).map_err(|e| e.to_string())
    })
}

/// 调用 service.call_bytes(name, args)，返回原始字节（如图片）。
fn py_call_bytes(state: &PyState, name: &str, args: &Value) -> Result<Vec<u8>, String> {
    Python::with_gil(|py| {
        let module = state.module.bind(py);
        let helper = module.getattr("call_bytes").map_err(|e| e.to_string())?;
        let args_str = args.to_string();
        let bytes: Vec<u8> = helper
            .call1((name, args_str))
            .map_err(|e| format!("{name}: {e}"))?
            .extract()
            .map_err(|e| e.to_string())?;
        Ok(bytes)
    })
}

/// 在阻塞线程池中执行 Python 调用，避免阻塞 UI 主线程。
async fn run_py<F, R>(state: Arc<PyState>, f: F) -> Result<R, String>
where
    F: FnOnce(&PyState) -> Result<R, String> + Send + 'static,
    R: Send + 'static,
{
    tauri::async_runtime::spawn_blocking(move || f(&state))
        .await
        .map_err(|e| e.to_string())?
}

// ---------------------------------------------------------------------------
// Tauri 命令（全部转发到 Python service）
// ---------------------------------------------------------------------------

#[tauri::command]
async fn backend_status(
    state: State<'_, Arc<PyState>>,
    config_path: Option<String>,
) -> Result<Value, String> {
    let st = state.inner().clone();
    run_py(st, move |s| py_call_json(s, "get_status", &json!({ "config_path": config_path }))).await
}

#[tauri::command]
async fn backend_config(
    state: State<'_, Arc<PyState>>,
    config_path: Option<String>,
) -> Result<Value, String> {
    let st = state.inner().clone();
    run_py(st, move |s| py_call_json(s, "get_config", &json!({ "config_path": config_path }))).await
}

#[tauri::command]
async fn backend_update_config(
    state: State<'_, Arc<PyState>>,
    values: Value,
    config_path: Option<String>,
) -> Result<Value, String> {
    let st = state.inner().clone();
    run_py(
        st,
        move |s| py_call_json(s, "update_config", &json!({ "values": values, "config_path": config_path })),
    )
    .await
}

#[tauri::command]
async fn backend_auth_status(
    state: State<'_, Arc<PyState>>,
    config_path: Option<String>,
) -> Result<Value, String> {
    let st = state.inner().clone();
    run_py(
        st,
        move |s| py_call_json(s, "auth_status", &json!({ "config_path": config_path })),
    )
    .await
}

#[tauri::command]
async fn backend_auth_refresh(
    state: State<'_, Arc<PyState>>,
    config_path: Option<String>,
) -> Result<Value, String> {
    let st = state.inner().clone();
    run_py(
        st,
        move |s| py_call_json(s, "auth_refresh", &json!({ "config_path": config_path })),
    )
    .await
}

#[tauri::command]
async fn backend_auth_login(
    state: State<'_, Arc<PyState>>,
    config_path: Option<String>,
) -> Result<Value, String> {
    let st = state.inner().clone();
    run_py(
        st,
        move |s| py_call_json(s, "auth_login", &json!({ "config_path": config_path })),
    )
    .await
}

#[tauri::command]
async fn backend_auth_login_status(state: State<'_, Arc<PyState>>) -> Result<Value, String> {
    let st = state.inner().clone();
    run_py(st, move |s| py_call_json(s, "auth_login_status", &json!({}))).await
}

#[tauri::command]
async fn backend_sync_start(
    state: State<'_, Arc<PyState>>,
    games: Option<Vec<String>>,
    force: Option<bool>,
    max_photos: Option<i64>,
    page_size: Option<i64>,
    config_path: Option<String>,
) -> Result<Value, String> {
    let st = state.inner().clone();
    let mut args = json!({ "games": games, "force": force.unwrap_or(false), "config_path": config_path });
    if let Some(v) = max_photos {
        args["max_photos"] = json!(v);
    }
    if let Some(v) = page_size {
        args["page_size"] = json!(v);
    }
    run_py(st, move |s| py_call_json(s, "sync_start", &args)).await
}

#[tauri::command]
async fn backend_sync_progress(state: State<'_, Arc<PyState>>) -> Result<Value, String> {
    let st = state.inner().clone();
    run_py(st, move |s| py_call_json(s, "sync_progress", &json!({}))).await
}

#[tauri::command]
async fn backend_sync_stop(state: State<'_, Arc<PyState>>) -> Result<Value, String> {
    let st = state.inner().clone();
    run_py(st, move |s| py_call_json(s, "sync_stop", &json!({}))).await
}

#[tauri::command]
async fn backend_photos(
    state: State<'_, Arc<PyState>>,
    game: Option<String>,
    month: Option<String>,
    q: Option<String>,
    limit: Option<i64>,
    offset: Option<i64>,
    config_path: Option<String>,
) -> Result<Value, String> {
    let st = state.inner().clone();
    let mut args = json!({ "game": game, "month": month, "q": q, "config_path": config_path });
    if let Some(v) = limit {
        args["limit"] = json!(v);
    }
    if let Some(v) = offset {
        args["offset"] = json!(v);
    }
    run_py(st, move |s| py_call_json(s, "list_photos", &args)).await
}

#[tauri::command]
async fn backend_photo_meta(
    state: State<'_, Arc<PyState>>,
    photo_id: String,
    config_path: Option<String>,
) -> Result<Value, String> {
    let st = state.inner().clone();
    run_py(
        st,
        move |s| py_call_json(s, "photo_meta", &json!({ "photo_id": photo_id, "config_path": config_path })),
    )
    .await
}

#[tauri::command]
async fn backend_photo_image(
    state: State<'_, Arc<PyState>>,
    photo_id: String,
    config_path: Option<String>,
) -> Result<Vec<u8>, String> {
    let st = state.inner().clone();
    run_py(
        st,
        move |s| py_call_bytes(s, "photo_image", &json!({ "photo_id": photo_id, "config_path": config_path })),
    )
    .await
}

/// 在系统资源管理器中打开目录 / 定位文件。
#[tauri::command]
fn backend_open_path(path: String) -> Result<(), String> {
    let p = std::path::Path::new(&path);
    if p.is_dir() {
        std::process::Command::new("explorer")
            .arg(&path)
            .spawn()
            .map_err(|e| format!("无法打开目录: {e}"))?;
    } else {
        std::process::Command::new("explorer")
            .arg(format!("/select,{}", path))
            .spawn()
            .map_err(|e| format!("无法定位文件: {e}"))?;
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// 应用入口
// ---------------------------------------------------------------------------

/// 隐藏独立控制台窗口。仅当进程独占该控制台（即双击启动、Windows 自动
/// 分配的新控制台）时隐藏；从终端 / 脚本启动时共享父控制台，不隐藏，
/// 以便命令行日志可见。
#[cfg(windows)]
struct ConsoleHandles {
    _stdin: Option<std::fs::File>,
    _stdout: Option<std::fs::File>,
    _stderr: Option<std::fs::File>,
}

#[cfg(windows)]
fn attach_console_if_available() -> ConsoleHandles {
    use std::ffi::c_void;
    use std::fs::OpenOptions;
    use std::os::windows::io::AsRawHandle;

    extern "system" {
        fn AttachConsole(dw_process_id: u32) -> i32;
        fn GetStdHandle(n_std_handle: u32) -> *mut c_void;
        fn SetStdHandle(n_std_handle: u32, h_handle: *mut c_void) -> i32;
    }

    const ATTACH_PARENT_PROCESS: u32 = 0xFFFF_FFFF;
    const STD_INPUT_HANDLE: u32 = 0xFFFF_FFF6;
    const STD_OUTPUT_HANDLE: u32 = 0xFFFF_FFF5;
    const STD_ERROR_HANDLE: u32 = 0xFFFF_FFF4;

    let is_invalid = |handle: *mut c_void| handle.is_null() || handle as isize == -1;

    unsafe {
        let existing_stdout = GetStdHandle(STD_OUTPUT_HANDLE);
        let existing_stderr = GetStdHandle(STD_ERROR_HANDLE);

        if is_invalid(existing_stdout) || is_invalid(existing_stderr) {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }
    }

    let existing_stdout = unsafe { GetStdHandle(STD_OUTPUT_HANDLE) };
    let existing_stderr = unsafe { GetStdHandle(STD_ERROR_HANDLE) };
    let existing_stdin = unsafe { GetStdHandle(STD_INPUT_HANDLE) };

    let stdout = if is_invalid(existing_stdout) {
        OpenOptions::new().write(true).open("CONOUT$").ok()
    } else {
        None
    };
    let stderr = if is_invalid(existing_stderr) {
        stdout.as_ref().and_then(|file| file.try_clone().ok())
    } else {
        None
    };
    let stdin = if is_invalid(existing_stdin) {
        OpenOptions::new().read(true).open("CONIN$").ok()
    } else {
        None
    };

    unsafe {
        if let Some(file) = &stdout {
            SetStdHandle(STD_OUTPUT_HANDLE, file.as_raw_handle() as *mut c_void);
        }
        if let Some(file) = &stderr {
            SetStdHandle(STD_ERROR_HANDLE, file.as_raw_handle() as *mut c_void);
        }
        if let Some(file) = &stdin {
            SetStdHandle(STD_INPUT_HANDLE, file.as_raw_handle() as *mut c_void);
        }
    }

    ConsoleHandles {
        _stdin: stdin,
        _stdout: stdout,
        _stderr: stderr,
    }
}

/// 当前控制台输出代码页对应的 Python 编码名。
/// PowerShell/cmd 在中文系统默认使用 GBK（cp936），直接以 UTF-8 写入会乱码；
/// 按控制台代码页编码可自动适配（GBK 控制台→gbk，UTF-8 控制台→utf-8）。
#[cfg(windows)]
fn console_output_encoding() -> String {
    extern "system" {
        fn GetConsoleOutputCP() -> u32;
    }
    unsafe {
        let cp = GetConsoleOutputCP();
        match cp {
            0 | 65001 => "utf-8".to_string(),
            _ => format!("cp{cp}"),
        }
    }
}

#[cfg(windows)]
fn configure_python_stdio(py: Python<'_>, console: &ConsoleHandles) -> PyResult<()> {
    use std::os::windows::io::AsRawHandle;

    let sys = py.import("sys")?;
    let enc = console_output_encoding();

    let output_handle = console
        ._stdout
        .as_ref()
        .map(|file| file.as_raw_handle() as i64)
        .or_else(|| {
            let handle = std::io::stdout().as_raw_handle() as i64;
            if handle != 0 && handle != -1 {
                Some(handle)
            } else {
                None
            }
        });

    if let Some(handle) = output_handle {
        let msvcrt = py.import("msvcrt")?;
        let io = py.import("io")?;
        if let Ok(old_stdout) = sys.getattr("stdout") {
            let _ = old_stdout.call_method0("detach");
        }
        if let Ok(old_stderr) = sys.getattr("stderr") {
            let _ = old_stderr.call_method0("detach");
        }
        let fd = msvcrt.call_method1("open_osfhandle", (handle, 0x4001))?;
        let raw = io.call_method1("FileIO", (fd, "w"))?;
        let stream = io.call_method1("TextIOWrapper", (raw, enc.as_str()))?;
        sys.setattr("stdout", &stream)?;
        sys.setattr("stderr", &stream)?;
    }

    let input_handle = console
        ._stdin
        .as_ref()
        .map(|file| file.as_raw_handle() as i64)
        .or_else(|| {
            let handle = std::io::stdin().as_raw_handle() as i64;
            if handle != 0 && handle != -1 {
                Some(handle)
            } else {
                None
            }
        });

    if let Some(handle) = input_handle {
        let msvcrt = py.import("msvcrt")?;
        let io = py.import("io")?;
        if let Ok(old_stdin) = sys.getattr("stdin") {
            let _ = old_stdin.call_method0("detach");
        }
        let fd = msvcrt.call_method1("open_osfhandle", (handle, 0x4000))?;
        let raw = io.call_method1("FileIO", (fd, "r"))?;
        let stream = io.call_method1("TextIOWrapper", (raw, enc.as_str()))?;
        sys.setattr("stdin", stream)?;
    }

    Ok(())
}

/// headless 命令行模式：初始化嵌入式 Python 并运行 CLI（forza_sync.cli.main），
/// 返回进程退出码。复用 init_python 的环境准备，因此 CLI 与 GUI 使用相同的
/// 运行时定位逻辑。
#[cfg(windows)]
fn run_cli(args: &[String], resource_dir: Option<&Path>, console: &ConsoleHandles) -> i32 {
    let run = || -> Result<i32, String> {
        // 环境准备 + 导入 service 模块（同时验证 Python 可正常启动）
        init_python(resource_dir)?;
        Python::with_gil(|py| {
            configure_python_stdio(py, console).map_err(|e| e.to_string())?;
            let sys = py.import("sys").map_err(|e| e.to_string())?;
            // sys.argv = ["forza-sync", ...args]
            let mut argv = Vec::with_capacity(args.len() + 1);
            argv.push("forza-sync".to_string());
            argv.extend_from_slice(args);
            sys.setattr("argv", argv).map_err(|e| e.to_string())?;

            let cli = py.import("forza_sync.cli").map_err(|e| e.to_string())?;
            let main = cli.getattr("main").map_err(|e| e.to_string())?;
            // argparse 的 --help/--version 会抛出 SystemExit，需按退出码处理
            let result = main.call1((args.to_vec(),));
            let _ = sys.getattr("stdout").and_then(|stream| stream.call_method0("flush"));
            let _ = sys.getattr("stderr").and_then(|stream| stream.call_method0("flush"));
            let code: i32 = match result {
                Ok(value) => value.extract::<i32>().map_err(|e| e.to_string())?,
                Err(err) if err.is_instance_of::<pyo3::exceptions::PySystemExit>(py) => {
                    err.value(py)
                        .getattr("code")
                        .ok()
                        .and_then(|c| c.extract::<i32>().ok())
                        .unwrap_or(0)
                }
                Err(err) => return Err(err.to_string()),
            };
            Ok(code)
        })
    };
    match run() {
        Ok(code) => code,
        Err(e) => {
            eprintln!("初始化 Python 失败: {e}");
            1
        }
    }
}

/// 隐藏本进程独占的控制台窗口。仅当进程独占该控制台（双击启动、Windows 自动
/// 分配的新控制台，或定时任务无终端上下文）时隐藏；从终端 / 脚本启动时共享
/// 父控制台，不隐藏，以保证命令行输出同步可见。
#[cfg(windows)]
fn hide_owned_console() {
    use std::ffi::c_void;

    extern "system" {
        fn GetConsoleWindow() -> *mut c_void;
        fn GetConsoleProcessList(lpdw_process_list: *mut u32, dw_process_count: u32) -> u32;
        fn ShowWindow(h_wnd: *mut c_void, n_cmd_show: i32) -> i32;
    }

    const SW_HIDE: i32 = 0;

    unsafe {
        let hwnd = GetConsoleWindow();
        if hwnd.is_null() {
            return;
        }
        let mut pids = [0u32; 4];
        // 共享该控制台的进程数：<=1 表示本进程独占（非终端启动）
        let count = GetConsoleProcessList(pids.as_mut_ptr(), pids.len() as u32);
        if count <= 1 {
            ShowWindow(hwnd, SW_HIDE);
        }
    }
}

pub fn run() {
    // 双击启动（独占控制台）时立即隐藏控制台窗口，避免 GUI 模式弹出黑色窗口；
    // 从终端启动（共享控制台）则保留，保证 CLI 输出同步可见。
    #[cfg(windows)]
    hide_owned_console();

    let args: Vec<String> = std::env::args().skip(1).collect();

    // headless 命令行模式：带参数时不启动 GUI，直接运行 Python CLI
    if !args.is_empty() {
        let resource_dir = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|d| d.to_path_buf()));
        #[cfg(windows)]
        let _console = attach_console_if_available();
        let code = run_cli(&args, resource_dir.as_deref(), &_console);
        std::process::exit(code);
    }

    // GUI 模式：若独占控制台已在 run() 开头隐藏；从终端启动则保留
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .invoke_handler(tauri::generate_handler![
            backend_status,
            backend_config,
            backend_update_config,
            backend_auth_status,
            backend_auth_refresh,
            backend_auth_login,
            backend_auth_login_status,
            backend_sync_start,
            backend_sync_progress,
            backend_sync_stop,
            backend_photos,
            backend_photo_meta,
            backend_photo_image,
            backend_open_path,
        ])
        .setup(|app| {
            let resource_dir = app.path().resource_dir().ok();
            let module = init_python(resource_dir.as_deref())
                .map_err(|e| format!("初始化 Python 失败: {e}"))?;
            app.manage(Arc::new(PyState { module }));
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.show();
                let _ = window.set_focus();
            }
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while building tauri application")
        .run(|_app_handle, _event| {});
}
