use anyhow::{bail, Context, Result};
use clap::{Parser, Subcommand, ValueEnum};
use launcher_core::{Draft, Mode, Session, Store};
use std::{
    io::{self, Read, Write},
    path::PathBuf,
};

#[derive(Parser)]
#[command(
    version,
    about = "管理 Codex 官方账号、自定义 API 和代理；只有 launch 显式启动客户端"
)]
struct Cli {
    /// Codex 配置目录；默认 CODEX_HOME 或 ~/.codex
    #[arg(long, global = true)]
    config_dir: Option<PathBuf>,
    #[command(subcommand)]
    command: Action,
}
#[derive(Clone, Copy, ValueEnum)]
enum Kind {
    Official,
    Custom,
}
impl From<Kind> for Mode {
    fn from(k: Kind) -> Self {
        match k {
            Kind::Official => Mode::Official,
            Kind::Custom => Mode::Custom,
        }
    }
}
#[derive(Subcommand)]
enum Action {
    /// 显示模式、配置 ID 和选中状态
    List,
    /// 输出完整编辑草稿（包含凭据），可配合 apply 使用
    Export,
    /// 应用 export 的 JSON 文件，校验文件内 revision
    Apply { file: PathBuf },
    /// 添加官方账号占位或 API 配置；保存但不启动
    Add {
        #[arg(value_enum)]
        kind: Kind,
        name: String,
        #[arg(long)]
        provider: Option<String>,
        #[arg(long)]
        url: Option<String>,
        /// 从文件读取 Key；使用 - 从标准输入读取
        #[arg(long)]
        key_file: Option<PathBuf>,
        #[arg(long)]
        model: Option<String>,
        #[arg(long)]
        effort: Option<String>,
        #[arg(long)]
        copy: Option<String>,
    },
    /// 编辑 API 配置并保存
    Edit {
        id: String,
        #[arg(long)]
        provider: Option<String>,
        #[arg(long)]
        url: Option<String>,
        #[arg(long)]
        key_file: Option<PathBuf>,
        #[arg(long)]
        model: Option<String>,
        #[arg(long)]
        effort: Option<String>,
    },
    /// 保存并切换模式及配置；不启动
    Use {
        #[arg(value_enum)]
        kind: Kind,
        id: String,
    },
    Rename {
        #[arg(value_enum)]
        kind: Kind,
        id: String,
        name: String,
    },
    Delete {
        #[arg(value_enum)]
        kind: Kind,
        id: String,
        #[arg(long)]
        yes: bool,
    },
    /// 设置官方代理偏好；空字符串禁用
    Proxy { url: String },
    /// 显式修复本地历史 provider ID 为 custom，先备份
    Repair {
        #[arg(long)]
        yes: bool,
    },
    /// 启动已保存配置；默认启动 codex CLI，--desktop 启动桌面客户端
    Launch {
        #[arg(long)]
        desktop: bool,
        #[arg(long)]
        executable: Option<PathBuf>,
        #[arg(long)]
        dry_run: bool,
        #[arg(last = true)]
        args: Vec<String>,
    },
}
fn main() {
    if let Err(error) = run() {
        eprintln!("操作失败：{error:#}");
        std::process::exit(1);
    }
}
fn confirm(yes: bool, message: &str) -> Result<()> {
    if yes {
        return Ok(());
    }
    eprint!("{message} [y/N] ");
    io::stderr().flush()?;
    let mut input = String::new();
    io::stdin().read_line(&mut input)?;
    if !input.trim().eq_ignore_ascii_case("y") {
        bail!("已取消，未修改文件")
    }
    Ok(())
}
fn key(path: PathBuf) -> Result<String> {
    let mut text = String::new();
    if path.as_os_str() == "-" {
        io::stdin().read_to_string(&mut text)?;
    } else {
        text = std::fs::read_to_string(path)?;
    }
    Ok(text.trim_end_matches(['\r', '\n']).into())
}
fn run() -> Result<()> {
    let cli = Cli::parse();
    let store = if let Some(path) = cli.config_dir {
        Store::new(path)
    } else {
        Store::discover()?
    };
    let result = execute(store.clone(), cli.command);
    if let Err(error) = &result {
        store.log("CLI", &format!("{error:#}"));
    }
    result
}
fn execute(store: Store, action: Action) -> Result<()> {
    if let Action::Repair { yes } = action {
        confirm(yes, "将 sessions、archived_sessions 和 SQLite 历史的 provider ID 改为 custom；请先关闭正在写入历史的 Codex。继续？")?;
        let report = launcher_core::history::repair(&store, |p| {
            eprintln!("{} {}/{}", p.phase, p.completed, p.total)
        })?;
        println!("{}", serde_json::to_string_pretty(&report)?);
        return Ok(());
    }
    let mut session = Session::open(store)?;
    let view = session.view();
    let mut draft: Draft = view.draft;
    match action {
        Action::List => {
            let entries = |mode| {
                draft.library.profiles(mode).iter().map(|p| serde_json::json!({"id":p.id,"name":p.name.as_deref().filter(|s| !s.trim().is_empty()).unwrap_or("未命名配置"),"selected":p.id == draft.library.selection(mode),"email":p.email})).collect::<Vec<_>>()
            };
            println!(
                "{}",
                serde_json::to_string_pretty(
                    &serde_json::json!({"mode":draft.mode,"official_accounts":entries(Mode::Official),"custom_providers":entries(Mode::Custom),"official_proxy_url":draft.library.official_proxy_url})
                )?
            );
            return Ok(());
        }
        Action::Export => {
            println!("{}", serde_json::to_string_pretty(&session.view())?);
            return Ok(());
        }
        Action::Apply { file } => {
            let v: launcher_core::View = serde_json::from_slice(&std::fs::read(file)?)?;
            session.save(&v.revision, &v.draft)?;
            println!("已保存配置；未启动客户端");
            return Ok(());
        }
        Action::Add {
            kind,
            name,
            provider,
            url,
            key_file,
            model,
            effort,
            copy,
        } => {
            let mode = kind.into();
            let id = draft.add(mode, &name, copy.as_deref())?;
            if mode == Mode::Custom {
                let f = draft.custom_fields.get_mut(&id).unwrap();
                if let Some(v) = provider {
                    f.provider_name = v;
                }
                if let Some(v) = url {
                    f.base_url = v;
                }
                if let Some(v) = key_file {
                    f.api_key = key(v)?;
                }
                if let Some(v) = model {
                    f.model = v;
                }
                if let Some(v) = effort {
                    f.effort = v;
                }
            }
            draft.mode = mode;
            eprintln!("配置 ID：{id}");
        }
        Action::Edit {
            id,
            provider,
            url,
            key_file,
            model,
            effort,
        } => {
            let f = draft.custom_fields.get_mut(&id).context("API 配置不存在")?;
            if let Some(v) = provider {
                f.provider_name = v;
            }
            if let Some(v) = url {
                f.base_url = v;
            }
            if let Some(v) = key_file {
                f.api_key = key(v)?;
            }
            if let Some(v) = model {
                f.model = v;
            }
            if let Some(v) = effort {
                f.effort = v;
            }
        }
        Action::Use { kind, id } => {
            draft.mode = kind.into();
            draft.library.select(draft.mode, id);
        }
        Action::Rename { kind, id, name } => {
            if name.trim().is_empty() {
                bail!("请输入名称")
            }
            draft
                .library
                .profiles_mut(kind.into())
                .iter_mut()
                .find(|p| p.id == id)
                .context("配置不存在")?
                .name = Some(name.trim().into());
        }
        Action::Delete { kind, id, yes } => {
            confirm(yes, "删除配置并保存？")?;
            draft.delete(kind.into(), &id)?;
        }
        Action::Proxy { url } => {
            draft.library.official_proxy_url = Some(url);
        }
        Action::Launch {
            desktop,
            executable,
            dry_run,
            args,
        } => {
            let settings = session.launch_settings(&view.revision, &draft)?;
            let executable = match executable {
                Some(p) => p,
                None if desktop => launcher_core::launch::desktop_executable()?,
                None => launcher_core::launch::cli_executable(),
            };
            let plan = settings.plan(executable, desktop, args)?;
            if dry_run {
                println!("{}", serde_json::to_string_pretty(&plan)?);
            } else if desktop {
                plan.spawn()?;
            } else {
                let status = plan.spawn()?.wait()?;
                std::process::exit(status.code().unwrap_or(1));
            }
            return Ok(());
        }
        Action::Repair { .. } => unreachable!(),
    }
    session.save(&view.revision, &draft)?;
    println!("已保存配置；未启动客户端");
    Ok(())
}
