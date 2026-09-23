use crate::{
    config::{normalize_proxy, proxy_variables},
    Agent, Mode, Roots,
};
use anyhow::{bail, Context, Result};
use serde::Serialize;
use std::{
    path::PathBuf,
    process::{Child, Command},
};

pub const RESOLVER_RULES: &str = "MAP chatgpt.com 0.0.0.0, MAP *.chatgpt.com 0.0.0.0, MAP chat.openai.com 0.0.0.0, MAP *.openai.com 0.0.0.0, MAP *.oaistatic.com 0.0.0.0";
const CODEX_KEYS: [&str; 2] = ["OPENAI_API_KEY", "OPENAI_BASE_URL"];
const CLAUDE_KEYS: [&str; 3] = [
    "ANTHROPIC_API_KEY",
    "ANTHROPIC_AUTH_TOKEN",
    "ANTHROPIC_BASE_URL",
];

pub struct Settings {
    pub agent: Agent,
    pub mode: Mode,
    pub proxy: String,
    pub roots: Roots,
    /// 兼容旧版调用方；等同于 `roots.codex`。
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
        if desktop && self.agent == Agent::Claude {
            bail!("Claude 没有桌面客户端，请直接启动 CLI")
        }
        let mut plan = Plan {
            executable,
            args,
            env: Vec::new(),
            remove_env: Vec::new(),
        };
        // 活动认证只能来自配置文件；父进程里的同名变量一律清掉，避免越过所选模式。
        plan.remove_env.extend(match self.agent {
            Agent::Codex => CODEX_KEYS.map(String::from).to_vec(),
            Agent::Claude => CLAUDE_KEYS.map(String::from).to_vec(),
        });
        if self.agent == Agent::Codex {
            plan.env.push((
                "CODEX_HOME".into(),
                self.roots.codex.to_string_lossy().into(),
            ));
        }
        if self.mode == Mode::Custom {
            if desktop {
                plan.args
                    .push(format!("--host-resolver-rules={RESOLVER_RULES}"));
            }
            return Ok(plan);
        }
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

pub fn desktop_executable(agent: Agent) -> Result<PathBuf> {
    if agent == Agent::Claude {
        bail!("Claude 没有桌面客户端，请直接启动 CLI")
    }
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
    bail!("未找到 Codex 桌面客户端；请安装客户端，或在 CLI 使用 --exe 指定路径")
}
pub fn launch_desktop(settings: Settings) -> Result<()> {
    let executable = desktop_executable(settings.agent)?;
    // Do not fall back to Explorer activation: it drops arguments and child environment.
    // A successful request must actually use the selected mode's network settings.
    settings.plan(executable, true, Vec::new())?.spawn()?;
    Ok(())
}
pub fn cli_executable(agent: Agent) -> PathBuf {
    let stem = match agent {
        Agent::Codex => "codex",
        Agent::Claude => "claude",
    };
    #[cfg(windows)]
    if let Some(path) = std::env::var_os("PATH") {
        for directory in std::env::split_paths(&path) {
            for extension in ["exe", "cmd"] {
                let candidate = directory.join(format!("{stem}.{extension}"));
                if candidate.is_file() {
                    return candidate;
                }
            }
        }
    }
    PathBuf::from(stem)
}
pub fn open_download(agent: Agent, updates: bool) -> Result<()> {
    if agent == Agent::Claude {
        open::that("https://docs.claude.com/en/docs/claude-code/setup")?;
        return Ok(());
    }
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
