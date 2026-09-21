#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
use launcher_core::{Draft, Session, Store, View};
use std::sync::Mutex;
use tauri::{Emitter, Manager};

struct State {
    session: Mutex<Option<Session>>,
    store: Store,
}
fn failure(state: &State, stage: &str, error: impl std::fmt::Display) -> String {
    let text = format!("{error}");
    state.store.log(stage, &text);
    text
}

#[tauri::command]
fn load(state: tauri::State<State>) -> Result<View, String> {
    let mut guard = state
        .session
        .try_lock()
        .map_err(|_| "操作正在执行，请稍后重试".to_string())?;
    let session = Session::open(state.store.clone())
        .map_err(|e| failure(&state, "读取配置", format!("{e:#}")))?;
    let view = session.view();
    *guard = Some(session);
    Ok(view)
}
#[tauri::command]
fn save(state: tauri::State<State>, revision: String, draft: Draft) -> Result<View, String> {
    let mut guard = state
        .session
        .try_lock()
        .map_err(|_| "操作正在执行".to_string())?;
    guard
        .as_mut()
        .ok_or("请先读取配置")?
        .save(&revision, &draft)
        .map_err(|e| failure(&state, "保存配置", format!("{e:#}")))
}
#[tauri::command]
fn launch(
    app: tauri::AppHandle,
    state: tauri::State<State>,
    revision: String,
    draft: Draft,
) -> Result<(), String> {
    let guard = state
        .session
        .try_lock()
        .map_err(|_| "操作正在执行".to_string())?;
    let settings = guard
        .as_ref()
        .ok_or("请先读取配置")?
        .launch_settings(&revision, &draft)
        .map_err(|e| failure(&state, "启动校验", format!("{e:#}")))?;
    launcher_core::launch::launch_desktop(settings)
        .map_err(|e| failure(&state, "启动客户端", format!("{e:#}")))?;
    app.exit(0);
    Ok(())
}
#[tauri::command]
async fn repair(
    app: tauri::AppHandle,
    state: tauri::State<'_, State>,
) -> Result<launcher_core::history::Report, String> {
    let store = state.store.clone();
    tauri::async_runtime::spawn_blocking(move || {
        let state = app.state::<State>();
        let _guard = state
            .session
            .try_lock()
            .map_err(|_| "操作正在执行".to_string())?;
        launcher_core::history::repair(&store, |progress| {
            let _ = app.emit("repair-progress", progress);
        })
        .map_err(|e| failure(&state, "修复对话", format!("{e:#}")))
    })
    .await
    .map_err(|e| e.to_string())?
}
#[tauri::command]
fn close(app: tauri::AppHandle) {
    app.exit(0);
}
#[tauri::command]
fn open_download(updates: bool) -> Result<(), String> {
    launcher_core::launch::open_download(updates).map_err(|e| format!("{e:#}"))
}

fn main() {
    let store = Store::discover().expect("无法确定配置目录");
    tauri::Builder::default()
        .manage(State {
            session: Mutex::new(None),
            store,
        })
        .invoke_handler(tauri::generate_handler![
            load,
            save,
            launch,
            repair,
            close,
            open_download
        ])
        .on_window_event(|window, event| {
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.emit("request-close", ());
            }
        })
        .run(tauri::generate_context!())
        .expect("桌面窗口启动失败");
}
