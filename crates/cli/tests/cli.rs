use std::{
    fs,
    path::Path,
    process::{Command, Output},
};
fn acs(dir: &Path, args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_acs"))
        .arg("--root")
        .arg(dir)
        .args(args)
        .output()
        .unwrap()
}
fn ok(dir: &Path, args: &[&str]) -> Vec<u8> {
    let output = acs(dir, args);
    assert!(
        output.status.success(),
        "acs {args:?}: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    output.stdout
}
fn ids(dir: &Path, agent: &str, mode: &str) -> Vec<String> {
    let output = ok(dir, &["export"]);
    let view: serde_json::Value = serde_json::from_slice(&output).unwrap();
    view["draft"][agent]["library"][mode]
        .as_array()
        .unwrap()
        .iter()
        .map(|p| p["id"].as_str().unwrap().to_owned())
        .collect()
}

#[test]
fn manages_both_clients_without_launching_and_requires_repair_confirmation() {
    let dir = tempfile::tempdir().unwrap();
    ok(
        dir.path(),
        &[
            "add",
            "codex",
            "custom",
            "example API",
            "--url",
            "https://api.example.com/v1",
            "--key",
            "example-key",
            "--model",
            "example-model",
        ],
    );
    let id = ids(dir.path(), "codex", "custom_providers")[0].clone();
    assert!(String::from_utf8_lossy(&ok(dir.path(), &["ls"])).contains("example API"));
    // ID 前缀定位到该配置并改名，不需要再写客户端和模式。
    ok(dir.path(), &["mv", &id[..8], "renamed example"]);
    assert!(String::from_utf8_lossy(&ok(dir.path(), &["ls"])).contains("renamed example"));
    // 同一个配置可以按名称再次定位。
    ok(
        dir.path(),
        &["edit", "renamed example", "--model", "example-other-model"],
    );
    let view: serde_json::Value = serde_json::from_slice(&ok(dir.path(), &["export"])).unwrap();
    assert_eq!(
        view["draft"]["codex"]["custom_fields"][&id]["model"],
        "example-other-model"
    );
    ok(dir.path(), &["use", "renamed example"]);
    // 客户端选择只影响 --agent 指定的那一个。
    ok(
        dir.path(),
        &[
            "add",
            "claude",
            "custom",
            "example Claude",
            "--url",
            "https://claude.example.com",
            "--key",
            "example-claude-key",
        ],
    );
    let launch = ok(dir.path(), &["run", "--dry-run"]);
    let plan: serde_json::Value = serde_json::from_slice(&launch).unwrap();
    assert_eq!(plan["args"], serde_json::json!([]));
    let desktop = ok(
        dir.path(),
        &[
            "run",
            "--desktop",
            "--executable",
            "example-client",
            "--dry-run",
        ],
    );
    let plan: serde_json::Value = serde_json::from_slice(&desktop).unwrap();
    assert!(plan["args"][0]
        .as_str()
        .unwrap()
        .starts_with("--host-resolver-rules="));
    let claude = ok(dir.path(), &["--agent", "claude", "run", "--dry-run"]);
    let plan: serde_json::Value = serde_json::from_slice(&claude).unwrap();
    assert_eq!(plan["remove_env"][0], "ANTHROPIC_API_KEY");
    let exported = ok(dir.path(), &["export"]);
    let export = dir.path().join("example-draft.json");
    fs::write(&export, &exported).unwrap();
    fs::write(dir.path().join("codex/.env"), "EXAMPLE=changed").unwrap();
    assert!(!acs(dir.path(), &["apply", export.to_str().unwrap()])
        .status
        .success());
    assert!(!acs(dir.path(), &["repair"]).status.success());
    assert!(!dir.path().join("codex/backups_state").exists());
    assert!(acs(dir.path(), &["repair", "--yes"]).status.success());
}

#[test]
fn imported_api_id_prefix_switches_without_an_explicit_kind() {
    let dir = tempfile::tempdir().unwrap();
    fs::create_dir_all(dir.path().join("codex")).unwrap();
    fs::write(dir.path().join("codex/config.toml"), "model_provider='custom'\nmodel='example-model'\nmodel_reasoning_effort='medium'\n[model_providers.custom]\nname='example'\nbase_url='https://api.example.com/v1'\nwire_api='responses'\nrequires_openai_auth=true\n").unwrap();
    fs::write(
        dir.path().join("codex/auth.json"),
        r#"{"auth_mode":"apikey","OPENAI_API_KEY":"example-key"}"#,
    )
    .unwrap();
    let id = ids(dir.path(), "codex", "custom_providers")[0].clone();
    // 导入的不写旧位置，配置库只出现在新的 acs 目录。
    assert!(!dir.path().join("codex/launcher-profiles").exists());
    ok(dir.path(), &["use", &id[..8]]);
    assert!(dir.path().join("acs/profiles.json").exists());
}

#[test]
fn ambiguous_and_unknown_targets_report_a_clear_error() {
    let dir = tempfile::tempdir().unwrap();
    for name in ["example one", "example two"] {
        ok(
            dir.path(),
            &[
                "add",
                "codex",
                "custom",
                name,
                "--url",
                "https://api.example.com/v1",
                "--key",
                "example-key",
                "--model",
                "example-model",
            ],
        );
    }
    let ambiguous = acs(dir.path(), &["use", "example"]);
    assert!(!ambiguous.status.success());
    let message = String::from_utf8_lossy(&ambiguous.stderr);
    assert!(message.contains("匹配到多项配置"), "{message}");
    let missing = acs(dir.path(), &["use", "example missing"]);
    assert!(!missing.status.success());
    assert!(String::from_utf8_lossy(&missing.stderr).contains("找不到配置"));
    // 官方账号的凭据由客户端登录维护，不接受直接编辑。
    ok(dir.path(), &["add", "codex", "official", "example account"]);
    let official = acs(dir.path(), &["edit", "example account", "--model", "x"]);
    assert!(!official.status.success());
    assert!(String::from_utf8_lossy(&official.stderr).contains("官方客户端登录维护"));
}

#[test]
fn proxy_command_reads_and_writes_the_selected_client_only() {
    let dir = tempfile::tempdir().unwrap();
    ok(dir.path(), &["add", "codex", "official", "example account"]);
    assert_eq!(
        String::from_utf8_lossy(&ok(dir.path(), &["proxy"])).trim(),
        ""
    );
    ok(dir.path(), &["proxy", "http://127.0.0.1:7890"]);
    assert_eq!(
        String::from_utf8_lossy(&ok(dir.path(), &["proxy"])).trim(),
        "http://127.0.0.1:7890"
    );
    // 两个客户端各自保留独立代理偏好。
    assert_eq!(
        String::from_utf8_lossy(&ok(dir.path(), &["--agent", "claude", "proxy"])).trim(),
        ""
    );
    // 非法代理地址在保存前被拒绝，不落盘。
    assert!(!acs(dir.path(), &["proxy", "http://example.com/path"])
        .status
        .success());
}
