//! Forza Gallery Sync 桌面应用。
//!
//! 架构：Tauri（Rust + WebView2）窗口加载 Vue 前端，通过 PyO3 在进程内
//! 嵌入 Python 解释器，直接调用 :mod:`forza_sync.service` 的纯函数。
//! 完全无 HTTP 服务、无端口、无网络监听。

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

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

pub fn run() {
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
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while building tauri application")
        .run(|_app_handle, _event| {});
}
