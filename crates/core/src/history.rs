use crate::storage::{read, write, Store};
use anyhow::{bail, Context, Result};
use rusqlite::{backup::Backup, Connection, OpenFlags};
use serde::Serialize;
use serde_json::Value;
use std::{
    fs,
    io::{BufRead, BufReader, Write},
    path::{Path, PathBuf},
    time::Duration,
};

#[derive(Clone, Serialize)]
pub struct Progress {
    pub phase: String,
    pub completed: usize,
    pub total: usize,
}
#[derive(Serialize)]
pub struct Report {
    pub changed: usize,
    pub backup: Option<PathBuf>,
}
struct Rollout {
    path: PathBuf,
    original: tempfile::NamedTempFile,
    updated: tempfile::NamedTempFile,
    modified: filetime::FileTime,
}
fn report(progress: &impl Fn(Progress), phase: &str, completed: usize, total: usize) {
    progress(Progress {
        phase: phase.into(),
        completed,
        total,
    });
}

pub fn repair(store: &Store, progress: impl Fn(Progress)) -> Result<Report> {
    let _lock = store.lock()?;
    let mut paths = Vec::new();
    for name in ["sessions", "archived_sessions"] {
        let root = store.roots.codex.join(name);
        if !root.exists() {
            continue;
        }
        for entry in walkdir::WalkDir::new(root).follow_links(false) {
            let entry = entry?;
            if entry.file_type().is_file() && entry.path().extension().is_some_and(|x| x == "jsonl")
            {
                paths.push(entry.into_path());
            }
        }
    }
    paths.sort();
    let mut rollouts = Vec::new();
    report(&progress, "扫描对话", 0, paths.len());
    for (i, path) in paths.iter().enumerate() {
        if let Some(change) = prepare_rollout(path)? {
            rollouts.push(change);
        }
        report(&progress, "扫描对话", i + 1, paths.len());
    }
    let databases = databases(&store.roots.codex)?;
    let mut connection = Connection::open_in_memory()?;
    connection.busy_timeout(Duration::from_secs(3))?;
    // A single transaction spans attached databases; SQL errors roll all databases back.
    for (i, path) in databases.iter().enumerate() {
        connection.execute(
            &format!("ATTACH DATABASE ?1 AS db{i}"),
            [path.to_string_lossy().as_ref()],
        )?;
    }
    let tx = connection.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)?;
    let mut tables = Vec::new();
    let mut rows = 0;
    for i in 0..databases.len() {
        for table in ["threads", "local_thread_catalog"] {
            if has_provider_column(&tx, &format!("db{i}"), table)? {
                let name = format!("db{i}.{table}");
                rows += tx.query_row(
                    &format!("SELECT COUNT(*) FROM {name} WHERE model_provider IS NOT 'custom'"),
                    [],
                    |r| r.get::<_, usize>(0),
                )?;
                tables.push(name);
            }
        }
        report(&progress, "统计数据库", i + 1, databases.len());
    }
    let total = rows + rollouts.len();
    if total == 0 {
        tx.rollback()?;
        report(&progress, "修复对话", 0, 0);
        return Ok(Report {
            changed: 0,
            backup: None,
        });
    }
    let backup = store
        .roots
        .codex
        .join("backups_state/provider-sync")
        .join(uuid::Uuid::new_v4().simple().to_string());
    fs::create_dir_all(&backup)?;
    let backup_total = rollouts.len() + databases.len();
    let mut completed = 0;
    for rollout in &rollouts {
        let destination = backup.join(rollout.path.strip_prefix(&store.roots.codex)?);
        fs::create_dir_all(destination.parent().unwrap())?;
        fs::copy(rollout.original.path(), destination)?;
        completed += 1;
        report(&progress, "备份对话", completed, backup_total);
    }
    for path in &databases {
        let destination = backup.join(path.strip_prefix(&store.roots.codex)?);
        fs::create_dir_all(destination.parent().unwrap())?;
        let source = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY)?;
        let mut dest = Connection::open(destination)?;
        Backup::new(&source, &mut dest)?.run_to_completion(128, Duration::from_millis(10), None)?;
        completed += 1;
        report(&progress, "备份对话", completed, backup_total);
    }
    if let Some(config) = read(&store.roots.codex.join("config.toml"))? {
        write(&backup.join("config.toml"), Some(&config))?;
    }
    write(
        &backup.join("manifest.json"),
        Some(&serde_json::to_vec_pretty(
            &serde_json::json!({"provider":"custom", "rollouts":rollouts.iter().map(|r| r.path.strip_prefix(&store.roots.codex).unwrap()).collect::<Vec<_>>(),"databases":databases.iter().map(|p| p.strip_prefix(&store.roots.codex).unwrap()).collect::<Vec<_>>()}),
        )?),
    )?;
    let mut applied = Vec::new();
    let operation = (|| -> Result<()> {
        let mut done = 0;
        report(&progress, "修复对话", 0, total);
        // Execute every SQL update before touching rollout files or committing.
        for table in &tables {
            done += tx.execute(&format!("UPDATE {table} SET model_provider='custom' WHERE model_provider IS NOT 'custom'"), [])?;
            report(&progress, "修复对话", done, total);
        }
        for (i, rollout) in rollouts.iter().enumerate() {
            let before = fs::read(rollout.original.path())?;
            if read(&rollout.path)?.as_deref() != Some(before.as_slice()) {
                bail!("修复期间对话发生变化：{}", rollout.path.display())
            }
            write(&rollout.path, Some(&fs::read(rollout.updated.path())?))?;
            applied.push(i);
            filetime::set_file_mtime(&rollout.path, rollout.modified)?;
            done += 1;
            report(&progress, "修复对话", done, total);
        }
        tx.commit()?;
        Ok(())
    })();
    if let Err(error) = operation {
        let mut failures = Vec::new();
        for i in applied.into_iter().rev() {
            let rollout = &rollouts[i];
            let restore = (|| -> Result<()> {
                if read(&rollout.path)? != read(rollout.updated.path())? {
                    bail!("{} 再次变化，未覆盖外部修改", rollout.path.display())
                }
                write(&rollout.path, Some(&fs::read(rollout.original.path())?))?;
                filetime::set_file_mtime(&rollout.path, rollout.modified)?;
                Ok(())
            })();
            if let Err(e) = restore {
                failures.push(e.to_string());
            }
        }
        bail!(
            "对话修复失败：{error:#}。备份：{}。回滚错误：{}",
            backup.display(),
            if failures.is_empty() {
                "无".into()
            } else {
                failures.join("；")
            }
        );
    }
    Ok(Report {
        changed: total,
        backup: Some(backup),
    })
}
fn prepare_rollout(path: &Path) -> Result<Option<Rollout>> {
    let mut original = tempfile::NamedTempFile::new()?;
    let mut updated = tempfile::NamedTempFile::new()?;
    let file = fs::File::open(path)?;
    let modified = filetime::FileTime::from_last_modification_time(&file.metadata()?);
    let mut reader = BufReader::new(file);
    let mut line = String::new();
    let mut changed = false;
    while reader.read_line(&mut line)? > 0 {
        original.write_all(line.as_bytes())?;
        let mut output = line.clone();
        if let Ok(mut record) = serde_json::from_str::<Value>(line.trim_start_matches('\u{feff}')) {
            if record["type"] == "session_meta"
                && record["payload"].is_object()
                && record["payload"]["model_provider"] != "custom"
            {
                record["payload"]["model_provider"] = Value::String("custom".into());
                let ending = if line.ends_with("\r\n") {
                    "\r\n"
                } else if line.ends_with('\n') {
                    "\n"
                } else {
                    ""
                };
                output = format!(
                    "{}{}{}",
                    if line.starts_with('\u{feff}') {
                        "\u{feff}"
                    } else {
                        ""
                    },
                    serde_json::to_string(&record)?,
                    ending
                );
                changed = true;
            }
        }
        updated.write_all(output.as_bytes())?;
        line.clear();
    }
    if changed {
        Ok(Some(Rollout {
            path: path.into(),
            original,
            updated,
            modified,
        }))
    } else {
        Ok(None)
    }
}
fn databases(root: &Path) -> Result<Vec<PathBuf>> {
    let mut paths = Vec::new();
    for directory in [root.to_path_buf(), root.join("sqlite")] {
        if !directory.exists() {
            continue;
        }
        for entry in fs::read_dir(&directory)? {
            let entry = entry?;
            let p = entry.path();
            if !entry.file_type()?.is_file() {
                continue;
            }
            let name = p.file_name().unwrap().to_string_lossy();
            let ext = p.extension().and_then(|s| s.to_str()).unwrap_or("");
            if (directory == root && name.starts_with("state_") && ext == "sqlite")
                || (directory != root && ["db", "sqlite", "sqlite3"].contains(&ext))
            {
                let connection = Connection::open_with_flags(&p, OpenFlags::SQLITE_OPEN_READ_ONLY)
                    .with_context(|| format!("读取数据库 {}", p.display()))?;
                if has_provider_column(&connection, "main", "threads")?
                    || has_provider_column(&connection, "main", "local_thread_catalog")?
                {
                    paths.push(p);
                }
            }
        }
    }
    paths.sort();
    if paths.len() > 10 {
        bail!("数据库超过 SQLite 单事务附加上限，未修改历史")
    }
    Ok(paths)
}

fn has_provider_column(connection: &Connection, schema: &str, table: &str) -> Result<bool> {
    let columns: Vec<String> = connection
        .prepare(&format!("PRAGMA {schema}.table_info({table})"))?
        .query_map([], |r| r.get(1))?
        .collect::<rusqlite::Result<_>>()?;
    Ok(columns.iter().any(|column| column == "model_provider"))
}
