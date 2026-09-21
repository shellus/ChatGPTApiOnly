use launcher_core::{
    config::{normalize_proxy, proxy_env},
    history, CustomFields, Mode, Session, Store,
};
use rusqlite::Connection;
use serde_json::{json, Value};
use std::{cell::RefCell, fs};

fn fixture() -> (tempfile::TempDir, Store) {
    let dir = tempfile::tempdir().unwrap();
    let store = Store::new(dir.path().to_owned());
    (dir, store)
}
fn custom(session: &mut Session) -> String {
    let mut draft = session.original.clone();
    draft.mode = Mode::Custom;
    let id = draft.add(Mode::Custom, "example API", None).unwrap();
    draft.custom_fields.insert(
        id.clone(),
        CustomFields {
            provider_name: "example provider".into(),
            base_url: "https://api.example.com/v1".into(),
            api_key: "example-key".into(),
            model: "example-model".into(),
            effort: "medium".into(),
        },
    );
    session.save(&session.view().revision, &draft).unwrap();
    id
}
fn official(session: &mut Session) -> String {
    let mut draft = session.original.clone();
    draft.mode = Mode::Official;
    let id = draft.add(Mode::Official, "example account", None).unwrap();
    session.save(&session.view().revision, &draft).unwrap();
    id
}
fn credentials() -> Value {
    json!({"auth_mode":"chatgpt","tokens":{"access_token":"example-access","refresh_token":"example-refresh","account_id":"example-account","id_token":"example-id"}})
}
fn read(store: &Store, path: &str) -> String {
    fs::read_to_string(store.root.join(path)).unwrap()
}
fn seed_rollout(store: &Store) -> String {
    fs::create_dir_all(store.root.join("sessions")).unwrap();
    let input = "{\"type\":\"session_meta\",\"payload\":{\"model_provider\":\"old\",\"name\":\"example\"}}\r\n{\"type\":\"event_msg\",\"payload\":\"example\"}\n";
    fs::write(store.root.join("sessions/example.jsonl"), input).unwrap();
    input.into()
}
fn database(store: &Store, filename: &str, fail: bool) -> Connection {
    let path = store.root.join(filename);
    fs::create_dir_all(path.parent().unwrap()).unwrap();
    let db = Connection::open(path).unwrap();
    db.execute_batch("CREATE TABLE threads (model_provider TEXT); INSERT INTO threads VALUES ('old'),(NULL); CREATE TABLE local_thread_catalog (model_provider TEXT); INSERT INTO local_thread_catalog VALUES ('old');").unwrap();
    if fail {
        db.execute_batch("CREATE TRIGGER fail BEFORE UPDATE ON threads BEGIN SELECT RAISE(ABORT, 'example failure'); END;").unwrap();
    }
    db
}

#[test]
fn opening_and_drafts_do_not_write() {
    let (_dir, store) = fixture();
    let session = Session::open(store.clone()).unwrap();
    let before = store.snapshot().unwrap();
    let mut draft = session.original.clone();
    let id = draft.add(Mode::Custom, "example", None).unwrap();
    draft.delete(Mode::Custom, &id).unwrap();
    assert_eq!(before, store.snapshot().unwrap());
    assert!(!store.root.join("launcher-profiles").exists());
}
#[test]
fn saves_never_repair_and_launch_never_saves() {
    let (_dir, store) = fixture();
    let input = seed_rollout(&store);
    let mut s = Session::open(store.clone()).unwrap();
    custom(&mut s);
    assert_eq!(read(&store, "sessions/example.jsonl"), input);
    let before = store.snapshot().unwrap();
    let settings = s.launch_settings(&s.view().revision, &s.original).unwrap();
    assert_eq!(settings.mode, Mode::Custom);
    assert_eq!(before, store.snapshot().unwrap());
    let mut dirty = s.original.clone();
    dirty.library.custom_providers[0].name = Some("changed".into());
    assert!(s.launch_settings(&s.view().revision, &dirty).is_err());
    assert_eq!(before, store.snapshot().unwrap());
}
#[test]
fn each_external_file_change_blocks_save_and_launch() {
    for path in [
        "config.toml",
        "auth.json",
        "launcher-profiles/modes.json",
        ".env",
    ] {
        let (_dir, store) = fixture();
        let mut s = Session::open(store.clone()).unwrap();
        custom(&mut s);
        fs::write(store.root.join(path), "example external change").unwrap();
        let before = store.snapshot().unwrap();
        assert!(s.save(&s.view().revision, &s.original.clone()).is_err());
        assert!(s.launch_settings(&s.view().revision, &s.original).is_err());
        assert_eq!(before, store.snapshot().unwrap());
    }
}
#[test]
fn launch_does_not_recover_missing_active_key_from_profile() {
    let (_dir, store) = fixture();
    let mut s = Session::open(store.clone()).unwrap();
    custom(&mut s);
    fs::write(store.root.join("auth.json"), r#"{"auth_mode":"apikey"}"#).unwrap();
    let s = Session::open(store).unwrap();
    assert!(s.launch_settings(&s.view().revision, &s.original).is_err());
}
#[test]
fn provider_snapshot_roundtrip_preserves_nested_unknown_values() {
    let (_dir, store) = fixture();
    let mut s = Session::open(store.clone()).unwrap();
    let id = custom(&mut s);
    let mut draft = s.original.clone();
    draft.library.custom_providers[0].model_providers_toml.as_mut().unwrap().push_str("\n[model_providers.custom.http_headers]\n\"X-Example\" = \"test\"\n[model_providers.other]\nname='example other'\nbase_url='https://other.example.com/v1'\n");
    s.save(&s.view().revision, &draft).unwrap();
    let original = read(&store, "config.toml");
    official(&mut s);
    let clean = read(&store, "config.toml");
    assert!(clean.contains("[model_providers.custom]"));
    assert!(!clean.contains("base_url"));
    assert!(!clean.contains("X-Example"));
    s.save(&s.view().revision, &s.original.clone()).unwrap();
    let mut draft = s.original.clone();
    draft.mode = Mode::Custom;
    draft.library.selected_custom = id;
    s.save(&s.view().revision, &draft).unwrap();
    let restored = read(&store, "config.toml");
    assert!(restored.contains("X-Example"));
    assert!(restored.contains("other.example.com"));
    assert_eq!(
        original
            .parse::<toml_edit::DocumentMut>()
            .unwrap()
            .to_string(),
        restored
            .parse::<toml_edit::DocumentMut>()
            .unwrap()
            .to_string()
    );
}
#[test]
fn auth_refresh_capture_and_logout_do_not_restore_old_tokens() {
    let (_dir, store) = fixture();
    let mut s = Session::open(store.clone()).unwrap();
    official(&mut s);
    fs::write(
        store.root.join("auth.json"),
        serde_json::to_vec(&credentials()).unwrap(),
    )
    .unwrap();
    let mut s = Session::open(store.clone()).unwrap();
    let id = s.original.library.selected_official.clone();
    custom(&mut s);
    let mut draft = s.original.clone();
    draft.mode = Mode::Official;
    draft.library.selected_official = id.clone();
    s.save(&s.view().revision, &draft).unwrap();
    assert_eq!(
        serde_json::from_str::<Value>(&read(&store, "auth.json")).unwrap()["tokens"]
            ["access_token"],
        "example-access"
    );
    fs::remove_file(store.root.join("auth.json")).unwrap();
    let mut s = Session::open(store.clone()).unwrap();
    assert!(s
        .original
        .library
        .selected(Mode::Official)
        .unwrap()
        .official_auth
        .is_none());
    s.save(&s.view().revision, &s.original.clone()).unwrap();
    assert!(!read(&store, "auth.json").contains("example-access"));
}
#[test]
fn partial_tokens_block_without_clearing_library() {
    let (_dir, store) = fixture();
    let mut s = Session::open(store.clone()).unwrap();
    official(&mut s);
    fs::write(
        store.root.join("auth.json"),
        r#"{"auth_mode":"chatgpt","tokens":{"access_token":"example"}}"#,
    )
    .unwrap();
    let before = store.snapshot().unwrap();
    assert!(Session::open(store.clone()).is_err());
    assert_eq!(before, store.snapshot().unwrap());
}

#[test]
fn imported_official_identity_is_stable_without_writing() {
    let (_dir, store) = fixture();
    fs::write(
        store.root.join("auth.json"),
        serde_json::to_vec(&credentials()).unwrap(),
    )
    .unwrap();
    let before = store.snapshot().unwrap();
    let first = Session::open(store.clone()).unwrap();
    let second = Session::open(store.clone()).unwrap();
    assert_eq!(
        first.original.library.selected_official,
        second.original.library.selected_official
    );
    assert_eq!(before, store.snapshot().unwrap());
}
#[test]
fn external_api_import_preserves_existing_profile_identity() {
    let (_dir, store) = fixture();
    let mut s = Session::open(store.clone()).unwrap();
    let old_id = custom(&mut s);
    let doc = read(&store, "config.toml").replace("api.example.com", "other.example.com");
    fs::write(store.root.join("config.toml"), doc).unwrap();
    let s = Session::open(store).unwrap();
    assert_eq!(s.original.library.custom_providers.len(), 2);
    assert_ne!(s.original.library.selected_custom, old_id);
    assert_eq!(
        s.original.custom_fields[&old_id].base_url,
        "https://api.example.com/v1"
    );
}
#[test]
fn proxy_validation_roundtrip_and_parent_environment_unchanged() {
    for invalid in [
        "https://example.com:80",
        "http://user:pass@example.com:80",
        "http://example.com/path",
        "http://example.com:0",
        "http://example.com:80?q=1",
    ] {
        assert!(normalize_proxy(invalid).is_err(), "{invalid}");
    }
    for original in ["", "EXAMPLE=1", "EXAMPLE=1\n", "EXAMPLE=1\r\n"] {
        let updated = proxy_env(original, "http://127.0.0.1:7890").unwrap();
        assert_eq!(
            proxy_env(&updated, "http://127.0.0.1:7890").unwrap(),
            updated
        );
        assert_eq!(proxy_env(&updated, "").unwrap(), original);
    }
    assert!(proxy_env("# END CHATGPT API ONLY PROXY\n", "").is_err());
    let env_before: std::collections::BTreeMap<_, _> = std::env::vars_os().collect();
    let settings = launcher_core::launch::Settings {
        mode: Mode::Official,
        proxy: "http://127.0.0.1:7890".into(),
        config_dir: "example".into(),
    };
    let plan = settings.plan("example".into(), true, vec![]).unwrap();
    let _command = plan.command();
    assert!(plan.args[0].starts_with("--proxy-server="));
    assert!(plan.remove_env.contains(&"OPENAI_API_KEY".into()));
    assert_eq!(env_before, std::env::vars_os().collect());
    let cli = settings.plan("example".into(), false, vec![]).unwrap();
    assert!(cli.args.is_empty());
}
#[test]
fn invalid_proxy_marker_blocks_all_four_file_changes() {
    let (_dir, store) = fixture();
    let mut s = Session::open(store.clone()).unwrap();
    official(&mut s);
    fs::write(store.root.join(".env"), "# BEGIN CHATGPT API ONLY PROXY\n").unwrap();
    let mut s = Session::open(store.clone()).unwrap();
    let before = store.snapshot().unwrap();
    assert!(s.save(&s.view().revision, &s.original.clone()).is_err());
    assert_eq!(before, store.snapshot().unwrap());
}
#[test]
fn conflicting_routing_is_not_silently_removed() {
    for setting in [
        "profile='example'",
        "forced_login_method='chatgpt'",
        "cli_auth_credentials_store='keyring'",
        "openai_base_url='https://example.com/v1'",
    ] {
        let (_dir, store) = fixture();
        fs::write(store.root.join("config.toml"), setting).unwrap();
        let mut s = Session::open(store.clone()).unwrap();
        let mut draft = s.original.clone();
        draft.add(Mode::Official, "example", None).unwrap();
        let before = store.snapshot().unwrap();
        assert!(s.save(&s.view().revision, &draft).is_err());
        assert_eq!(before, store.snapshot().unwrap());
    }
}
#[test]
fn repair_success_backup_metadata_and_idempotence() {
    let (_dir, store) = fixture();
    let original = seed_rollout(&store);
    let db = database(&store, "state_5.sqlite", false);
    db.execute_batch("PRAGMA journal_mode=WAL;").unwrap();
    let events = RefCell::new(Vec::new());
    let result = history::repair(&store, |p| events.borrow_mut().push(p)).unwrap();
    assert_eq!(result.changed, 4);
    let backup = result.backup.unwrap();
    assert_eq!(
        fs::read_to_string(backup.join("sessions/example.jsonl")).unwrap(),
        original
    );
    let backup_db = Connection::open(backup.join("state_5.sqlite")).unwrap();
    assert_eq!(
        backup_db
            .query_row(
                "SELECT COUNT(*) FROM threads WHERE model_provider IS NOT 'custom'",
                [],
                |r| r.get::<_, i64>(0)
            )
            .unwrap(),
        2
    );
    let result = read(&store, "sessions/example.jsonl");
    assert!(result.contains("\"name\":\"example\""));
    assert!(result.contains("\r\n"));
    assert_eq!(history::repair(&store, |_| {}).unwrap().changed, 0);
    assert!(events.borrow().iter().all(|p| p.completed <= p.total));
}
#[test]
fn second_database_failure_rolls_back_all_databases_and_rollouts() {
    let (_dir, store) = fixture();
    let original = seed_rollout(&store);
    let a = database(&store, "sqlite/a.db", false);
    let b = database(&store, "sqlite/b.db", true);
    assert!(history::repair(&store, |_| {}).is_err());
    assert_eq!(read(&store, "sessions/example.jsonl"), original);
    for db in [a, b] {
        assert_eq!(
            db.query_row(
                "SELECT COUNT(*) FROM threads WHERE model_provider IS NOT 'custom'",
                [],
                |r| r.get::<_, i64>(0)
            )
            .unwrap(),
            2
        );
    }
    assert!(store.root.join("backups_state/provider-sync").exists());
}

#[test]
fn unrelated_databases_are_not_attached_or_backed_up() {
    let (_dir, store) = fixture();
    let _history = database(&store, "state_5.sqlite", false);
    fs::create_dir_all(store.root.join("sqlite")).unwrap();
    let mut unrelated = Vec::new();
    for i in 0..12 {
        let db = Connection::open(store.root.join(format!("sqlite/example-{i}.db"))).unwrap();
        db.execute_batch("CREATE TABLE settings (value TEXT); INSERT INTO settings VALUES ('example'); BEGIN IMMEDIATE;").unwrap();
        unrelated.push(db);
    }
    let result = history::repair(&store, |_| {}).unwrap();
    assert_eq!(result.changed, 3);
    assert!(!result.backup.unwrap().join("sqlite").exists());
    for db in unrelated {
        db.execute_batch("ROLLBACK;").unwrap();
    }
}
#[test]
fn changing_rollout_aborts_sql_transaction() {
    let (_dir, store) = fixture();
    seed_rollout(&store);
    let db = database(&store, "state_5.sqlite", false);
    let result = history::repair(&store, |p| {
        if p.phase == "修复对话" && p.completed == 0 {
            fs::write(
                store.root.join("sessions/example.jsonl"),
                "example external write",
            )
            .unwrap();
        }
    });
    assert!(result.is_err());
    assert_eq!(
        read(&store, "sessions/example.jsonl"),
        "example external write"
    );
    assert_eq!(
        db.query_row(
            "SELECT COUNT(*) FROM threads WHERE model_provider IS NOT 'custom'",
            [],
            |r| r.get::<_, i64>(0)
        )
        .unwrap(),
        2
    );
}
#[test]
fn failed_rollout_after_first_write_restores_first_and_sql() {
    let (_dir, store) = fixture();
    let original = seed_rollout(&store);
    fs::write(store.root.join("sessions/second.jsonl"), &original).unwrap();
    let db = database(&store, "state_5.sqlite", false);
    let result = history::repair(&store, |p| {
        if p.phase == "修复对话" && p.completed == 4 {
            fs::write(
                store.root.join("sessions/second.jsonl"),
                "example external write",
            )
            .unwrap();
        }
    });
    assert!(result.is_err());
    assert_eq!(read(&store, "sessions/example.jsonl"), original);
    assert_eq!(
        db.query_row(
            "SELECT COUNT(*) FROM threads WHERE model_provider IS NOT 'custom'",
            [],
            |r| r.get::<_, i64>(0)
        )
        .unwrap(),
        2
    );
}
