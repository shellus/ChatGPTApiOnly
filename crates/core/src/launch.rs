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
        for root in roots {
            let path = root.join("Codex.app/Contents/MacOS/Codex");
            if path.is_file() {
                return Ok(path);
            }
        }
    }
    bail!("未找到 Codex 桌面客户端；请安装客户端，或在 CLI 使用 --executable 指定路径")
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
