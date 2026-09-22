use crate::{
    config::{normalize_proxy, proxy_variables},
    Mode,
};
use anyhow::{bail, Context, Result};
use serde::Serialize;
use std::{
    path::PathBuf,
    process::{Child, Command},
};

pub const RESOLVER_RULES: &str = "MAP chatgpt.com 0.0.0.0, MAP *.chatgpt.com 0.0.0.0, MAP chat.openai.com 0.0.0.0, MAP *.openai.com 0.0.0.0, MAP *.oaistatic.com 0.0.0.0";
pub struct Settings {
    pub mode: Mode,
    pub proxy: String,
    pub config_dir: PathBuf,
}
#[derive(Debug, Serialize)]
pub struct Plan {
    pub executable: PathBuf,
    pub args: Vec<String>,
    pub env: Vec<(String, String)>,
    pub remove_env: Vec<String>,
}
impl Settings {
    pub fn plan(&self, executable: PathBuf, desktop: bool, args: Vec<String>) -> Result<Plan> {
        let mut plan = Plan {
            executable,
            args,
            env: vec![(
                "CODEX_HOME".into(),
                self.config_dir.to_string_lossy().into(),
            )],
            remove_env: Vec::new(),
        };
        if self.mode == Mode::Custom {
            if desktop {
                plan.args
                    .push(format!("--host-resolver-rules={RESOLVER_RULES}"));
            }
        } else {
            plan.remove_env
                .extend(["OPENAI_API_KEY".into(), "OPENAI_BASE_URL".into()]);
            let proxy = normalize_proxy(&self.proxy)?;
            if !proxy.is_empty() {
                if desktop {
                    plan.args.push(format!("--proxy-server={proxy}"));
                }
                plan.env.extend(
                    proxy_variables(&proxy)
                        .into_iter()
                        .map(|(k, v)| (k.into(), v)),
                );
            }
        }
        Ok(plan)
    }
}
impl Plan {
    pub fn command(&self) -> Command {
        let mut cmd = Command::new(&self.executable);
        cmd.args(&self.args);
        if let Some(home) = dirs::home_dir() {
            cmd.current_dir(home);
        }
        for key in &self.remove_env {
            cmd.env_remove(key);
        }
        for (key, value) in &self.env {
            cmd.env(key, value);
        }
        cmd
    }
    pub fn spawn(&self) -> Result<Child> {
        self.command()
            .spawn()
            .with_context(|| format!("无法启动 {}", self.executable.display()))
    }
}

pub fn desktop_executable() -> Result<PathBuf> {
    #[cfg(windows)]
    {
        use winreg::{enums::HKEY_CURRENT_USER, RegKey};
        let key = RegKey::predef(HKEY_CURRENT_USER).open_subkey("Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages").context("未找到客户端安装信息，请打开应用商店安装")?;
        let mut candidates = Vec::new();
        for name in key.enum_keys() {
            let name = name?;
            if !name.starts_with("OpenAI.Codex_") {
                continue;
            }
            let package = key.open_subkey(&name)?;
            let root: String = package.get_value("PackageRootFolder")?;
            let path = PathBuf::from(root).join("app/ChatGPT.exe");
            if path.is_file() {
                let version: Vec<u32> = name
                    .split('_')
                    .nth(1)
                    .unwrap_or("")
                    .split('.')
                    .map(|n| n.parse().unwrap_or(0))
                    .collect();
                candidates.push((version, path));
            }
        }
        candidates.sort_by(|a, b| b.0.cmp(&a.0));
        if let Some((_, path)) = candidates.into_iter().next() {
            return Ok(path);
        }
    }
    #[cfg(target_os = "macos")]
    {
        let mut roots = vec![PathBuf::from("/Applications")];
        if let Some(home) = dirs::home_dir() {
            roots.push(home.join("Applications"));
        }
        if let Some(path) = macos_desktop_executable_in(roots) {
            return Ok(path);
        }
        // Spotlight also finds renamed apps and installations outside Applications.
        // NUL separators preserve paths containing whitespace or newlines.
        if let Ok(output) = Command::new("/usr/bin/mdfind")
            .args(["-0", "kMDItemCFBundleIdentifier == 'com.openai.codex'"])
            .output()
        {
            use std::os::unix::ffi::OsStrExt;
            if output.status.success() {
                for path in output
                    .stdout
                    .split(|byte| *byte == 0)
                    .filter(|p| !p.is_empty())
                {
                    if let Some(executable) = macos_bundle_executable(std::path::Path::new(
                        std::ffi::OsStr::from_bytes(path),
                    )) {
                        return Ok(executable);
                    }
                }
            }
        }
    }
    bail!("未找到 Codex 桌面客户端；请安装客户端，或在 CLI 使用 --executable 指定路径")
}

#[cfg(target_os = "macos")]
fn macos_desktop_executable_in(roots: impl IntoIterator<Item = PathBuf>) -> Option<PathBuf> {
    for root in roots {
        for name in ["ChatGPT.app", "Codex.app"] {
            if let Some(executable) = macos_bundle_executable(&root.join(name)) {
                return Some(executable);
            }
        }
        // Bounded fallback works even with Spotlight disabled. Do not descend
        // into app bundles or follow directory symlinks recursively.
        for entry in walkdir::WalkDir::new(&root)
            .max_depth(3)
            .sort_by_file_name()
            .into_iter()
            .filter_entry(|entry| {
                entry.depth() == 0 || entry.path().extension().is_none_or(|ext| ext != "app")
            })
            .filter_map(Result::ok)
        {
            // filter_entry prunes bundles; inspect the direct children instead.
            if !entry.file_type().is_dir() {
                continue;
            }
            let Ok(children) = std::fs::read_dir(entry.path()) else {
                continue;
            };
            let mut bundles: Vec<_> = children
                .filter_map(Result::ok)
                .map(|child| child.path())
                .filter(|path| path.extension().is_some_and(|ext| ext == "app"))
                .collect();
            bundles.sort();
            for bundle in bundles {
                if let Some(executable) = macos_bundle_executable(&bundle) {
                    return Some(executable);
                }
            }
        }
    }
    None
}

#[cfg(target_os = "macos")]
fn macos_bundle_executable(bundle: &std::path::Path) -> Option<PathBuf> {
    use std::os::unix::fs::PermissionsExt;
    let info = plist::Value::from_file(bundle.join("Contents/Info.plist")).ok()?;
    let dictionary = info.as_dictionary()?;
    if dictionary.get("CFBundleIdentifier")?.as_string()? != "com.openai.codex" {
        return None;
    }
    let name = dictionary.get("CFBundleExecutable")?.as_string()?;
    // CFBundleExecutable is a filename, never a relative or absolute path.
    if name.is_empty() || name.contains('/') || name == "." || name == ".." {
        return None;
    }
    let executable = bundle.join("Contents/MacOS").join(name);
    let metadata = executable.metadata().ok()?;
    (metadata.is_file() && metadata.permissions().mode() & 0o111 != 0).then_some(executable)
}
pub fn launch_desktop(settings: Settings) -> Result<()> {
    let executable = desktop_executable()?;
    // Do not fall back to Explorer activation: it drops arguments and child environment.
    // A successful request must actually use the selected mode's network settings.
    settings.plan(executable, true, Vec::new())?.spawn()?;
    Ok(())
}
pub fn cli_executable() -> PathBuf {
    #[cfg(windows)]
    if let Some(path) = std::env::var_os("PATH") {
        for directory in std::env::split_paths(&path) {
            for name in ["codex.exe", "codex.cmd"] {
                let candidate = directory.join(name);
                if candidate.is_file() {
                    return candidate;
                }
            }
        }
    }
    PathBuf::from("codex")
}
pub fn open_download(updates: bool) -> Result<()> {
    #[cfg(windows)]
    {
        let uri = if updates {
            "ms-windows-store://downloadsandupdates"
        } else {
            "ms-windows-store://pdp/?ProductId=9PLM9XGG6VKS"
        };
        if open::that(uri).is_ok() {
            return Ok(());
        }
        open::that("https://apps.microsoft.com/detail/9PLM9XGG6VKS")?;
    }
    #[cfg(not(windows))]
    {
        let _ = updates;
        open::that("https://chatgpt.com/codex")?;
    }
    Ok(())
}

#[cfg(all(test, target_os = "macos"))]
mod macos_tests {
    use super::*;
    use std::fs;

    fn app(root: &std::path::Path, name: &str, executable: &str, identifier: &str) -> PathBuf {
        use std::os::unix::fs::PermissionsExt;
        let executable_name = executable;
        let contents = root.join(name).join("Contents");
        let executable = contents.join("MacOS").join(executable);
        fs::create_dir_all(executable.parent().unwrap()).unwrap();
        fs::write(&executable, b"example").unwrap();
        fs::set_permissions(&executable, fs::Permissions::from_mode(0o755)).unwrap();
        let mut info = plist::Dictionary::new();
        info.insert(
            "CFBundleExecutable".into(),
            plist::Value::String(executable_name.into()),
        );
        info.insert(
            "CFBundleIdentifier".into(),
            plist::Value::String(identifier.into()),
        );
        plist::to_file_xml(contents.join("Info.plist"), &info).unwrap();
        executable
    }

    #[test]
    fn discovers_current_and_legacy_codex_bundle_names() {
        for (bundle, executable) in [("ChatGPT.app", "ChatGPT"), ("Codex.app", "Codex")] {
            let directory = tempfile::tempdir().unwrap();
            let expected = app(directory.path(), bundle, executable, "com.openai.codex");
            assert_eq!(
                macos_desktop_executable_in([directory.path().to_path_buf()]),
                Some(expected)
            );
        }
    }

    #[test]
    fn discovers_renamed_nested_bundle_after_missing_root() {
        let directory = tempfile::tempdir().unwrap();
        let expected = app(
            directory.path(),
            "Tools/My renamed client.app",
            "Different Binary",
            "com.openai.codex",
        );
        assert_eq!(
            macos_desktop_executable_in([
                directory.path().join("missing"),
                directory.path().into()
            ]),
            Some(expected)
        );
    }

    #[test]
    fn rejects_non_executable_and_unsafe_executable_names() {
        use std::os::unix::fs::PermissionsExt;
        let directory = tempfile::tempdir().unwrap();
        let executable = app(directory.path(), "Codex.app", "Codex", "com.openai.codex");
        fs::set_permissions(&executable, fs::Permissions::from_mode(0o644)).unwrap();
        assert!(macos_bundle_executable(&directory.path().join("Codex.app")).is_none());
        app(
            directory.path(),
            "Unsafe.app",
            "../outside",
            "com.openai.codex",
        );
        assert!(macos_bundle_executable(&directory.path().join("Unsafe.app")).is_none());
    }

    #[test]
    fn ignores_non_codex_chatgpt_bundle() {
        let directory = tempfile::tempdir().unwrap();
        app(
            directory.path(),
            "ChatGPT.app",
            "ChatGPT",
            "com.openai.chat",
        );
        assert_eq!(
            macos_desktop_executable_in([directory.path().to_path_buf()]),
            None
        );
    }
}
