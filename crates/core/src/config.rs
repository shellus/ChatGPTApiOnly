use crate::storage::{revision, Snapshot, Store};
use anyhow::{bail, Context, Result};
use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::{BTreeMap, HashSet};
use toml_edit::{value, DocumentMut, Item, Table};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Mode {
    #[default]
    Official,
    Custom,
}

#[derive(Clone, Default, Debug, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct Profile {
    pub id: String,
    pub name: Option<String>,
    pub official_auth: Option<String>,
    pub account_id: Option<String>,
    pub email: Option<String>,
    pub account_name: Option<String>,
    pub custom_key: Option<String>,
    pub custom_model: Option<String>,
    pub custom_effort: Option<String>,
    pub model_providers_toml: Option<String>,
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}
#[derive(Clone, Default, Debug, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct Library {
    pub official_accounts: Vec<Profile>,
    pub custom_providers: Vec<Profile>,
    pub selected_official: String,
    pub selected_custom: String,
    pub official_model: Option<String>,
    pub official_effort: Option<String>,
    pub official_proxy_url: Option<String>,
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}
#[derive(Clone, Default, Debug, PartialEq, Serialize, Deserialize)]
pub struct CustomFields {
    pub provider_name: String,
    pub base_url: String,
    pub api_key: String,
    pub model: String,
    pub effort: String,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Draft {
    pub mode: Mode,
    pub library: Library,
    pub custom_fields: BTreeMap<String, CustomFields>,
}
#[derive(Clone, Serialize, Deserialize)]
pub struct View {
    pub revision: String,
    pub draft: Draft,
    pub config_dir: String,
}
pub struct Session {
    pub store: Store,
    pub baseline: Snapshot,
    pub original: Draft,
}

fn text(bytes: &Option<Vec<u8>>) -> Result<&str> {
    Ok(std::str::from_utf8(bytes.as_deref().unwrap_or_default())?.trim_start_matches('\u{feff}'))
}
pub fn parse_doc(text: &str) -> Result<DocumentMut> {
    text.parse().context("config.toml 格式无效")
}
fn string(doc: &DocumentMut, key: &str) -> String {
    doc.get(key)
        .and_then(Item::as_str)
        .unwrap_or_default()
        .into()
}
pub fn mode(doc: &DocumentMut) -> Mode {
    if doc
        .get("model_provider")
        .and_then(Item::as_str)
        .unwrap_or("openai")
        == "openai"
    {
        Mode::Official
    } else {
        Mode::Custom
    }
}
fn auth(text: &str) -> Result<Value> {
    if text.trim().is_empty() {
        return Ok(json!({}));
    }
    let v: Value = serde_json::from_str(text).context("auth.json 格式无效")?;
    if !v.is_object() {
        bail!("auth.json 必须是对象")
    }
    Ok(v)
}
fn nonempty(v: &Value) -> bool {
    v.as_str().is_some_and(|s| !s.trim().is_empty())
}
pub fn valid_tokens(v: &Value) -> bool {
    v["auth_mode"] == "chatgpt"
        && nonempty(&v["tokens"]["access_token"])
        && nonempty(&v["tokens"]["refresh_token"])
        && nonempty(&v["tokens"]["account_id"])
}

impl Library {
    pub fn profiles(&self, mode: Mode) -> &Vec<Profile> {
        if mode == Mode::Official {
            &self.official_accounts
        } else {
            &self.custom_providers
        }
    }
    pub fn profiles_mut(&mut self, mode: Mode) -> &mut Vec<Profile> {
        if mode == Mode::Official {
            &mut self.official_accounts
        } else {
            &mut self.custom_providers
        }
    }
    pub fn selection(&self, mode: Mode) -> &str {
        if mode == Mode::Official {
            &self.selected_official
        } else {
            &self.selected_custom
        }
    }
    pub fn select(&mut self, mode: Mode, id: String) {
        if mode == Mode::Official {
            self.selected_official = id;
        } else {
            self.selected_custom = id;
        }
    }
    pub fn selected(&self, mode: Mode) -> Option<&Profile> {
        self.profiles(mode)
            .iter()
            .find(|p| p.id == self.selection(mode))
    }
    pub fn validate(&self) -> Result<()> {
        for m in [Mode::Official, Mode::Custom] {
            let mut ids = HashSet::new();
            for p in self.profiles(m) {
                if p.id.trim().is_empty() || !ids.insert(&p.id) {
                    bail!("配置 ID 缺失或重复")
                }
            }
            if (ids.is_empty() && !self.selection(m).is_empty())
                || (!ids.is_empty() && self.selected(m).is_none())
            {
                bail!("选中的配置不存在")
            }
        }
        Ok(())
    }
}
impl Draft {
    pub fn add(&mut self, mode: Mode, name: &str, copy: Option<&str>) -> Result<String> {
        if name.trim().is_empty() {
            bail!("请输入配置名称")
        }
        let mut p = if let Some(id) = copy {
            if mode == Mode::Official {
                bail!("官方账号请通过登录添加")
            }
            self.library
                .profiles(mode)
                .iter()
                .find(|p| p.id == id)
                .context("待复制配置不存在")?
                .clone()
        } else {
            Profile::default()
        };
        let fields = copy
            .and_then(|id| self.custom_fields.get(id))
            .cloned()
            .unwrap_or(CustomFields {
                provider_name: "custom".into(),
                base_url: String::new(),
                api_key: String::new(),
                model: String::new(),
                effort: "medium".into(),
            });
        p.id = uuid::Uuid::new_v4().simple().to_string();
        p.name = Some(name.trim().into());
        let id = p.id.clone();
        self.library.profiles_mut(mode).push(p);
        self.library.select(mode, id.clone());
        if mode == Mode::Custom {
            self.custom_fields.insert(id.clone(), fields);
        }
        Ok(id)
    }
    pub fn delete(&mut self, mode: Mode, id: &str) -> Result<()> {
        let list = self.library.profiles_mut(mode);
        let index = list
            .iter()
            .position(|p| p.id == id)
            .context("待删除配置不存在")?;
        list.remove(index);
        let first = list.first().map(|p| p.id.clone()).unwrap_or_default();
        if self.library.selection(mode) == id {
            self.library.select(mode, first);
        }
        if mode == Mode::Custom {
            self.custom_fields.remove(id);
        }
        Ok(())
    }
}
impl CustomFields {
    pub fn from_profile(p: &Profile) -> Result<Self> {
        let d = parse_doc(p.model_providers_toml.as_deref().unwrap_or_default())?;
        let get = |key| {
            d.get("model_providers")
                .and_then(|v| v.get("custom"))
                .and_then(|v| v.get(key))
                .and_then(Item::as_str)
                .unwrap_or_default()
                .to_owned()
        };
        Ok(Self {
            provider_name: get("name"),
            base_url: get("base_url"),
            api_key: p.custom_key.clone().unwrap_or_default(),
            model: p.custom_model.clone().unwrap_or_default(),
            effort: p.custom_effort.clone().unwrap_or_else(|| "medium".into()),
        })
    }
    pub fn validate(&self) -> Result<()> {
        let url = url::Url::parse(&self.base_url).context("API 地址无效")?;
        if url.scheme() != "https"
            || url.host_str().is_none()
            || !url.path().ends_with("/v1")
            || url.query().is_some()
            || url.fragment().is_some()
            || !url.username().is_empty()
            || url.password().is_some()
        {
            bail!("API 地址须为 https://主机/v1，可包含 /v1 前的路径")
        }
        if [
            &self.provider_name,
            &self.api_key,
            &self.model,
            &self.effort,
        ]
        .iter()
        .any(|s| s.trim().is_empty())
        {
            bail!("请填写提供者名称、API Key、模型和思考层级")
        }
        Ok(())
    }
    pub fn apply(&self, p: &mut Profile) -> Result<()> {
        let mut doc = parse_doc(p.model_providers_toml.as_deref().unwrap_or_default())?;
        ensure_custom(&mut doc)?;
        doc["model_providers"]["custom"]["name"] = value(&self.provider_name);
        doc["model_providers"]["custom"]["base_url"] = value(&self.base_url);
        doc["model_providers"]["custom"]["wire_api"] = value("responses");
        doc["model_providers"]["custom"]["requires_openai_auth"] = value(true);
        p.model_providers_toml = Some(provider_snapshot(&doc));
        p.custom_key = Some(self.api_key.clone());
        p.custom_model = Some(self.model.clone());
        p.custom_effort = Some(self.effort.clone());
        Ok(())
    }
}
fn ensure_custom(doc: &mut DocumentMut) -> Result<()> {
    if doc.get("model_providers").is_none() {
        let mut table = Table::new();
        table.set_implicit(true);
        doc["model_providers"] = Item::Table(table);
    }
    if !doc["model_providers"].is_table() {
        bail!("model_providers 必须使用 TOML 表")
    }
    if doc["model_providers"].get("custom").is_none() {
        doc["model_providers"]["custom"] = Item::Table(Table::new());
    }
    if !doc["model_providers"]["custom"].is_table() {
        bail!("model_providers.custom 必须使用 TOML 表")
    }
    Ok(())
}
fn provider_snapshot(doc: &DocumentMut) -> String {
    let mut out = DocumentMut::new();
    if let Some(p) = doc.get("model_providers") {
        out["model_providers"] = p.clone();
    }
    out.to_string()
}

impl Session {
    pub fn open(store: Store) -> Result<Self> {
        let baseline = store.snapshot()?;
        let doc = parse_doc(text(&baseline[0])?)?;
        let active_auth = auth(text(&baseline[1])?)?;
        let mut library: Library = if baseline[2].is_none() {
            Library::default()
        } else {
            let v: Value =
                serde_json::from_str(text(&baseline[2])?).context("modes.json 格式无效")?;
            if !v["official_accounts"].is_array() || !v["custom_providers"].is_array() {
                bail!("modes.json 配置结构不正确")
            }
            serde_json::from_value(v)?
        };
        library.validate()?;
        let active_mode = mode(&doc);
        capture_official(&mut library, &doc, &active_auth)?;
        if active_mode == Mode::Custom {
            let mut p = Profile {
                id: uuid::Uuid::new_v4().simple().to_string(),
                name: Some("导入的 API".into()),
                model_providers_toml: Some(provider_snapshot(&doc)),
                custom_key: active_auth["OPENAI_API_KEY"].as_str().map(str::to_owned),
                custom_model: Some(string(&doc, "model")),
                custom_effort: Some(string(&doc, "model_reasoning_effort")),
                ..Default::default()
            };
            let fields = CustomFields::from_profile(&p)?;
            let existing = library.custom_providers.iter().position(|old| {
                CustomFields::from_profile(old)
                    .is_ok_and(|f| f.base_url == fields.base_url && f.api_key == fields.api_key)
            });
            if let Some(i) = existing {
                p.id = library.custom_providers[i].id.clone();
                p.name = library.custom_providers[i].name.clone();
                p.extra = library.custom_providers[i].extra.clone();
                library.custom_providers[i] = p.clone();
            } else {
                library.custom_providers.push(p.clone());
            }
            library.selected_custom = p.id;
        }
        let custom_fields = library
            .custom_providers
            .iter()
            .map(|p| Ok((p.id.clone(), CustomFields::from_profile(p)?)))
            .collect::<Result<_>>()?;
        let original = Draft {
            mode: active_mode,
            library,
            custom_fields,
        };
        Ok(Self {
            store,
            baseline,
            original,
        })
    }
    pub fn view(&self) -> View {
        View {
            revision: revision(&self.baseline),
            draft: self.original.clone(),
            config_dir: self.store.root.to_string_lossy().into(),
        }
    }
    pub fn check(&self, rev: &str) -> Result<()> {
        if rev != revision(&self.baseline) {
            bail!("读取版本已过期，请重新读取配置")
        }
        self.store.check(&self.baseline)
    }
    pub fn save(&mut self, rev: &str, draft: &Draft) -> Result<View> {
        let _lock = self.store.lock()?;
        self.check(rev)?;
        let mut doc = parse_doc(text(&self.baseline[0])?)?;
        validate_conflicts(&doc, draft.mode)?;
        let mut library = draft.library.clone();
        library.validate()?;
        for p in &mut library.custom_providers {
            draft
                .custom_fields
                .get(&p.id)
                .context("API 表单缺失")?
                .apply(p)?;
        }
        let target = library
            .selected(draft.mode)
            .context("请先添加并选择配置")?
            .clone();
        let proxy = normalize_proxy(library.official_proxy_url.as_deref().unwrap_or_default())?;
        library.official_proxy_url = Some(proxy.clone());
        let mut next_auth;
        doc.remove("model_providers");
        if draft.mode == Mode::Official {
            next_auth = auth(
                target
                    .official_auth
                    .as_deref()
                    .unwrap_or("{\"auth_mode\":\"chatgpt\",\"OPENAI_API_KEY\":null}"),
            )?;
            if next_auth["auth_mode"] != "chatgpt" {
                bail!("官方凭据模式无效")
            }
            if !next_auth["tokens"].is_null() && !valid_tokens(&next_auth) {
                bail!("官方凭据不完整，请核对 auth.json")
            }
            next_auth["OPENAI_API_KEY"] = Value::Null;
            doc["model_provider"] = value("openai");
            for (k, v) in [
                ("model", &library.official_model),
                ("model_reasoning_effort", &library.official_effort),
            ] {
                doc.remove(k);
                if let Some(v) = v.as_deref().filter(|v| !v.trim().is_empty()) {
                    doc[k] = value(v);
                }
            }
            ensure_custom(&mut doc)?;
            let name = library
                .selected(Mode::Custom)
                .map(CustomFields::from_profile)
                .transpose()?
                .map(|f| f.provider_name)
                .filter(|s| !s.is_empty())
                .unwrap_or("custom".into());
            doc["model_providers"]["custom"]["name"] = value(name);
            doc["model_providers"]["custom"]["wire_api"] = value("responses");
            doc["model_providers"]["custom"]["requires_openai_auth"] = value(true);
        } else {
            let fields = CustomFields::from_profile(&target)?;
            fields.validate()?;
            let providers = parse_doc(target.model_providers_toml.as_deref().unwrap_or_default())?;
            doc["model_providers"] = providers["model_providers"].clone();
            validate_custom_overrides(&doc)?;
            doc["model_provider"] = value("custom");
            doc["model"] = value(&fields.model);
            doc["model_reasoning_effort"] = value(&fields.effort);
            next_auth = json!({"auth_mode":"apikey","OPENAI_API_KEY":fields.api_key});
        }
        let env = proxy_env(
            text(&self.baseline[3])?,
            if draft.mode == Mode::Official {
                &proxy
            } else {
                ""
            },
        )?;
        let after = vec![
            Some(doc.to_string().into_bytes()),
            Some(serde_json::to_vec_pretty(&next_auth)?),
            Some(serde_json::to_vec_pretty(&library)?),
            if self.baseline[3].is_none() && env.is_empty() {
                None
            } else {
                Some(env.into_bytes())
            },
        ];
        self.store.commit(&self.baseline, &after)?;
        self.baseline = after;
        self.original = Draft {
            mode: draft.mode,
            library,
            custom_fields: draft.custom_fields.clone(),
        };
        Ok(self.view())
    }
    pub fn launch_settings(&self, rev: &str, draft: &Draft) -> Result<crate::launch::Settings> {
        self.check(rev)?;
        if draft != &self.original {
            bail!("有未保存修改，请先保存配置")
        }
        let doc = parse_doc(text(&self.baseline[0])?)?;
        let active_mode = mode(&doc);
        validate_conflicts(&doc, active_mode)?;
        let credentials = auth(text(&self.baseline[1])?)?;
        if active_mode == Mode::Custom {
            if string(&doc, "model_provider") != "custom" {
                bail!("活动 provider 不是 custom，请先保存配置")
            }
            let p = Profile {
                model_providers_toml: Some(provider_snapshot(&doc)),
                custom_key: credentials["OPENAI_API_KEY"].as_str().map(str::to_owned),
                custom_model: Some(string(&doc, "model")),
                custom_effort: Some(string(&doc, "model_reasoning_effort")),
                ..Default::default()
            };
            CustomFields::from_profile(&p)?.validate()?;
            validate_custom_overrides(&doc)?;
            let provider = doc.get("model_providers").and_then(|p| p.get("custom"));
            if credentials["auth_mode"] != "apikey"
                || provider
                    .and_then(|p| p.get("wire_api"))
                    .and_then(Item::as_str)
                    != Some("responses")
                || provider
                    .and_then(|p| p.get("requires_openai_auth"))
                    .and_then(Item::as_bool)
                    != Some(true)
            {
                bail!("活动 API 认证或协议不完整，请先保存配置")
            }
        } else {
            if nonempty(&credentials["OPENAI_API_KEY"])
                || credentials.get("auth_mode").is_some_and(|v| v != "chatgpt")
            {
                bail!("活动认证不是官方模式，请先保存配置")
            }
            if !credentials["tokens"].is_null() && !valid_tokens(&credentials) {
                bail!("官方凭据不完整，请核对 auth.json")
            }
        }
        Ok(crate::launch::Settings {
            mode: active_mode,
            proxy: normalize_proxy(
                self.original
                    .library
                    .official_proxy_url
                    .as_deref()
                    .unwrap_or_default(),
            )?,
            config_dir: self.store.root.clone(),
        })
    }
}
fn capture_official(library: &mut Library, doc: &DocumentMut, credentials: &Value) -> Result<()> {
    if mode(doc) == Mode::Official {
        library.official_model = doc.get("model").and_then(Item::as_str).map(str::to_owned);
        library.official_effort = doc
            .get("model_reasoning_effort")
            .and_then(Item::as_str)
            .map(str::to_owned);
    }
    if !valid_tokens(credentials) {
        if credentials["tokens"].is_object()
            && credentials["tokens"]
                .as_object()
                .is_some_and(|t| !t.is_empty())
        {
            bail!("官方凭据不完整；未视为退出登录，也未覆盖配置库")
        }
        if mode(doc) == Mode::Official {
            let selected = library.selected_official.clone();
            if let Some(p) = library
                .official_accounts
                .iter_mut()
                .find(|p| p.id == selected)
            {
                p.official_auth = None;
            }
        }
        return Ok(());
    }
    let account_id = credentials["tokens"]["account_id"].as_str().unwrap();
    let claims = credentials["tokens"]["id_token"]
        .as_str()
        .and_then(|s| s.split('.').nth(1))
        .and_then(|s| URL_SAFE_NO_PAD.decode(s.trim_end_matches('=')).ok())
        .and_then(|b| serde_json::from_slice::<Value>(&b).ok())
        .unwrap_or(Value::Null);
    let index = library
        .official_accounts
        .iter()
        .position(|p| p.account_id.as_deref() == Some(account_id))
        .or_else(|| {
            library
                .official_accounts
                .iter()
                .position(|p| p.id == library.selected_official && p.account_id.is_none())
        });
    let i = index.unwrap_or_else(|| {
        library.official_accounts.push(Profile {
            id: uuid::Uuid::new_v4().simple().to_string(),
            name: Some(claims["email"].as_str().unwrap_or("官方账号").into()),
            ..Default::default()
        });
        library.official_accounts.len() - 1
    });
    let p = &mut library.official_accounts[i];
    p.account_id = Some(account_id.into());
    p.email = claims["email"].as_str().map(str::to_owned);
    p.account_name = claims["name"].as_str().map(str::to_owned);
    p.official_auth = Some(serde_json::to_string(credentials)?);
    library.selected_official = p.id.clone();
    Ok(())
}
pub fn validate_conflicts(doc: &DocumentMut, mode: Mode) -> Result<()> {
    for key in ["forced_login_method", "profile"] {
        if doc.contains_key(key) {
            bail!("配置冲突：{key}，请先核对 config.toml")
        }
    }
    if doc
        .get("cli_auth_credentials_store")
        .is_some_and(|i| i.as_str() != Some("file"))
    {
        bail!("cli_auth_credentials_store 必须为 file 或不设置")
    }
    if mode == Mode::Official {
        for key in ["chatgpt_base_url", "openai_base_url"] {
            if doc.contains_key(key) {
                bail!("配置冲突：{key} 覆盖官方路由")
            }
        }
    }
    if doc.get("model_providers").is_some_and(|p| !p.is_table()) {
        bail!("model_providers 必须使用 TOML 表")
    }
    Ok(())
}
fn validate_custom_overrides(doc: &DocumentMut) -> Result<()> {
    if let Some(p) = doc.get("model_providers").and_then(|p| p.get("custom")) {
        for key in [
            "env_key",
            "experimental_bearer_token",
            "env_key_instructions",
        ] {
            if p.get(key).is_some() {
                bail!("custom.{key} 与 auth.json 认证冲突，请先核对")
            }
        }
        for key in ["http_headers", "env_http_headers"] {
            if let Some(headers) = p.get(key).and_then(Item::as_table_like) {
                if headers.iter().any(|(k, _)| {
                    k.eq_ignore_ascii_case("authorization") || k.eq_ignore_ascii_case("api-key")
                }) {
                    bail!("custom.{key} 存在认证覆盖")
                }
            }
        }
    }
    Ok(())
}
pub fn normalize_proxy(raw: &str) -> Result<String> {
    if raw.trim().is_empty() {
        return Ok(String::new());
    }
    let u = url::Url::parse(raw.trim()).context("代理地址无效")?;
    if u.scheme() != "http"
        || u.host_str().is_none()
        || !u.username().is_empty()
        || u.password().is_some()
        || u.path() != "/"
        || u.query().is_some()
        || u.fragment().is_some()
        || u.port() == Some(0)
    {
        bail!("代理须为 http://主机:端口，不含账号密码、路径或查询参数")
    }
    Ok(u.as_str().trim_end_matches('/').into())
}
pub fn proxy_variables(proxy: &str) -> Vec<(&'static str, String)> {
    vec![
        ("HTTP_PROXY", proxy.into()),
        ("HTTPS_PROXY", proxy.into()),
        ("ALL_PROXY", proxy.into()),
        ("NO_PROXY", "localhost,127.0.0.1,::1".into()),
        ("NODE_USE_ENV_PROXY", "1".into()),
    ]
}
pub fn proxy_env(original: &str, proxy: &str) -> Result<String> {
    const BEGIN: &str = "# BEGIN CHATGPT API ONLY PROXY";
    const END: &str = "# END CHATGPT API ONLY PROXY";
    let mut pos = 0;
    let mut markers = Vec::new();
    for line in original.split_inclusive('\n') {
        let bare = line.trim_end_matches(['\r', '\n']);
        if bare == BEGIN || bare == END {
            markers.push((pos, pos + line.len(), bare));
        }
        pos += line.len();
    }
    let mut clean = original.to_owned();
    if !markers.is_empty() {
        if markers.len() != 2 || markers[0].2 != BEGIN || markers[1].2 != END {
            bail!(".env 的代理标记不完整、顺序错误或重复")
        }
        let mut start = markers[0].0;
        if start > 0 && original.as_bytes()[start - 1] == b'\n' {
            start -= 1;
            if start > 0 && original.as_bytes()[start - 1] == b'\r' {
                start -= 1;
            }
        }
        let prefix = &original[..start];
        let suffix = &original[markers[1].1..];
        clean = format!(
            "{prefix}{}{suffix}",
            if !prefix.is_empty()
                && !suffix.is_empty()
                && !prefix.ends_with('\n')
                && !suffix.starts_with('\n')
            {
                "\n"
            } else {
                ""
            }
        );
    }
    if proxy.is_empty() {
        return Ok(clean);
    }
    let nl = if clean.contains("\r\n") { "\r\n" } else { "\n" };
    clean.push_str(nl);
    clean.push_str(BEGIN);
    clean.push_str(nl);
    for (k, v) in proxy_variables(proxy) {
        clean.push_str(&format!("{k}={v}{nl}"));
    }
    clean.push_str(END);
    clean.push_str(nl);
    Ok(clean)
}
