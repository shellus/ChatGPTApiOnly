use std::{
    fs,
    process::{Command, Output},
};
fn cli(dir: &std::path::Path, args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_chatgpt-api-only"))
        .arg("--config-dir")
        .arg(dir)
        .args(args)
        .output()
        .unwrap()
}
#[test]
fn cli_manages_profiles_without_launch_and_requires_repair_confirmation() {
    let dir = tempfile::tempdir().unwrap();
    let key = dir.path().join("example-key.txt");
    fs::write(&key, "example-key").unwrap();
    let added = cli(
        dir.path(),
        &[
            "add",
            "custom",
            "example API",
            "--url",
            "https://api.example.com/v1",
            "--key-file",
            key.to_str().unwrap(),
            "--model",
            "example-model",
        ],
    );
    assert!(
        added.status.success(),
        "{}",
        String::from_utf8_lossy(&added.stderr)
    );
    let list = cli(dir.path(), &["list"]);
    let list: serde_json::Value = serde_json::from_slice(&list.stdout).unwrap();
    let id = list["custom_providers"][0]["id"].as_str().unwrap();
    assert!(
        cli(dir.path(), &["rename", "custom", id, "renamed example"])
            .status
            .success()
    );
    let launch = cli(dir.path(), &["launch", "--dry-run"]);
    assert!(launch.status.success());
    let plan: serde_json::Value = serde_json::from_slice(&launch.stdout).unwrap();
    assert_eq!(plan["args"], serde_json::json!([]));
    let desktop = cli(
        dir.path(),
        &[
            "launch",
            "--desktop",
            "--executable",
            "example-client",
            "--dry-run",
        ],
    );
    assert!(desktop.status.success());
    let plan: serde_json::Value = serde_json::from_slice(&desktop.stdout).unwrap();
    assert!(plan["args"][0]
        .as_str()
        .unwrap()
        .starts_with("--host-resolver-rules="));
    let exported = cli(dir.path(), &["export"]);
    let export = dir.path().join("example-draft.json");
    fs::write(&export, &exported.stdout).unwrap();
    fs::write(dir.path().join(".env"), "EXAMPLE=changed").unwrap();
    assert!(!cli(dir.path(), &["apply", export.to_str().unwrap()])
        .status
        .success());
    assert!(!cli(dir.path(), &["repair"]).status.success());
    assert!(!dir.path().join("backups_state").exists());
    assert!(cli(dir.path(), &["repair", "--yes"]).status.success());
}

#[test]
fn imported_api_id_from_list_can_be_used_by_the_next_command() {
    let dir = tempfile::tempdir().unwrap();
    fs::write(dir.path().join("config.toml"), "model_provider='custom'\nmodel='example-model'\nmodel_reasoning_effort='medium'\n[model_providers.custom]\nname='example'\nbase_url='https://api.example.com/v1'\nwire_api='responses'\nrequires_openai_auth=true\n").unwrap();
    fs::write(
        dir.path().join("auth.json"),
        r#"{"auth_mode":"apikey","OPENAI_API_KEY":"example-key"}"#,
    )
    .unwrap();
    let list = cli(dir.path(), &["list"]);
    assert!(list.status.success());
    let list: serde_json::Value = serde_json::from_slice(&list.stdout).unwrap();
    let id = list["custom_providers"][0]["id"].as_str().unwrap();
    assert!(!dir.path().join("launcher-profiles/modes.json").exists());
    let switched = cli(dir.path(), &["use", "custom", id]);
    assert!(
        switched.status.success(),
        "{}",
        String::from_utf8_lossy(&switched.stderr)
    );
}
