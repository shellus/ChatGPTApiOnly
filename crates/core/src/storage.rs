use anyhow::{bail, Context, Result};
use fs2::FileExt;
use sha2::{Digest, Sha256};
use std::{
    fs::{self, File, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
};

pub const FILES: [&str; 4] = [
    "config.toml",
    "auth.json",
    "launcher-profiles/modes.json",
    ".env",
];
pub type Snapshot = Vec<Option<Vec<u8>>>;

#[derive(Clone)]
pub struct Store {
    pub root: PathBuf,
}
impl Store {
    pub fn new(root: PathBuf) -> Self {
        Self { root }
    }
    pub fn discover() -> Result<Self> {
        let root = std::env::var_os("CHATGPT_API_ONLY_CONFIG_DIR")
            .or_else(|| std::env::var_os("CODEX_HOME"))
            .map(PathBuf::from)
            .or_else(|| dirs::home_dir().map(|p| p.join(".codex")))
            .context("无法确定 Codex 配置目录")?;
        Ok(Self::new(root))
    }
    pub fn snapshot(&self) -> Result<Snapshot> {
        FILES.iter().map(|p| read(&self.root.join(p))).collect()
    }
    pub fn lock(&self) -> Result<File> {
        let path = self.root.join("launcher-profiles/operation.lock");
        fs::create_dir_all(path.parent().unwrap())?;
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(path)?;
        file.try_lock_exclusive()
            .context("另一项保存或修复正在执行，请稍后重试")?;
        Ok(file)
    }
    pub fn check(&self, baseline: &Snapshot) -> Result<()> {
        if &self.snapshot()? != baseline {
            bail!("配置已被其他程序修改，请重新读取配置；当前草稿尚未保存")
        }
        Ok(())
    }
    pub fn commit(&self, before: &Snapshot, after: &Snapshot) -> Result<()> {
        self.commit_with(before, after, write)
    }
    fn commit_with(
        &self,
        before: &Snapshot,
        after: &Snapshot,
        mut writer: impl FnMut(&Path, Option<&[u8]>) -> Result<()>,
    ) -> Result<()> {
        self.check(before)?;
        let mut written = Vec::new();
        let operation = (|| -> Result<()> {
            for i in [3, 2, 1, 0] {
                if before[i] == after[i] {
                    continue;
                }
                let path = self.root.join(FILES[i]);
                if read(&path)? != before[i] {
                    bail!("{} 在保存期间发生变化", FILES[i]);
                }
                writer(&path, after[i].as_deref())?;
                written.push(i);
            }
            Ok(())
        })();
        if let Err(error) = operation {
            let mut failures = Vec::new();
            for i in written.into_iter().rev() {
                let path = self.root.join(FILES[i]);
                let result = (|| -> Result<()> {
                    if read(&path)? != after[i] {
                        bail!("{} 再次变化，未覆盖外部修改", FILES[i])
                    }
                    write(&path, before[i].as_deref())
                })();
                if let Err(e) = result {
                    failures.push(e.to_string());
                }
            }
            bail!(
                "保存失败：{error:#}。回滚错误：{}",
                if failures.is_empty() {
                    "无".into()
                } else {
                    failures.join("；")
                }
            );
        }
        Ok(())
    }
    pub fn log(&self, stage: &str, error: &str) {
        let dir = self.root.join("launcher-profiles/logs");
        if fs::create_dir_all(&dir).is_ok() {
            if let Ok(mut f) = OpenOptions::new()
                .create(true)
                .append(true)
                .open(dir.join("launcher.log"))
            {
                let _ = writeln!(f, "{:?} [{stage}] {error}", std::time::SystemTime::now());
            }
        }
    }
}

pub fn revision(snapshot: &Snapshot) -> String {
    let mut hash = Sha256::new();
    for file in snapshot {
        match file {
            None => hash.update([0]),
            Some(b) => {
                hash.update([1]);
                hash.update((b.len() as u64).to_le_bytes());
                hash.update(b);
            }
        }
    }
    format!("{:x}", hash.finalize())
}
pub fn read(path: &Path) -> Result<Option<Vec<u8>>> {
    match fs::read(path) {
        Ok(b) => Ok(Some(b)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(e).with_context(|| format!("读取 {}", path.display())),
    }
}
pub fn write(path: &Path, bytes: Option<&[u8]>) -> Result<()> {
    if let Some(bytes) = bytes {
        let parent = path.parent().context("无父目录")?;
        fs::create_dir_all(parent)?;
        let mut tmp = tempfile::NamedTempFile::new_in(parent)?;
        if let Ok(meta) = fs::metadata(path) {
            tmp.as_file().set_permissions(meta.permissions())?;
        }
        tmp.write_all(bytes)?;
        tmp.as_file().sync_all()?;
        tmp.persist(path)
            .map_err(|e| e.error)
            .with_context(|| format!("替换 {}", path.display()))?;
    } else if path.exists() {
        fs::remove_file(path)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn every_write_failure_restores_existing_and_absent_files() {
        for existing in [false, true] {
            for fail_at in 0..4 {
                let dir = tempfile::tempdir().unwrap();
                let store = Store::new(dir.path().into());
                if existing {
                    for name in FILES {
                        write(&store.root.join(name), Some(b"example original")).unwrap();
                    }
                }
                let before = store.snapshot().unwrap();
                let after = vec![Some(b"example changed".to_vec()); 4];
                let mut count = 0;
                let result = store.commit_with(&before, &after, |path, bytes| {
                    let fail = count == fail_at;
                    count += 1;
                    if fail {
                        bail!("example disk failure");
                    }
                    write(path, bytes)
                });
                assert!(result.is_err());
                assert_eq!(store.snapshot().unwrap(), before);
            }
        }
    }
}
