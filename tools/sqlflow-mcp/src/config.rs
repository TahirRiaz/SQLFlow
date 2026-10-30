//! Persisted MCP configuration and access token.
//!
//! Two files under `~/.sqlflow/`:
//!   * `mcp-config.json`: the control-plane URL.
//!   * `mcp-token.json`: the bearer token, its scopes, its expiry, and whether it renews.
//!
//! The token file is written owner-only where the platform supports it. Both are read at startup and rewritten on
//! change, so a device-auth session survives restarts. An interactive session token renews itself at the control
//! plane's `POST /api/v1/auth/renew` on the same schedule the GUI uses (see [`RENEW_LEAD_SECS`]), so it stays
//! short-lived on the wire while the person behind it stays signed in up to the control plane's absolute cap.
//!
//! The `mcp` in both names is the server's state prefix ([`StateStore`]). A host that
//! composes this server under a name of its own gives it a prefix of its own, so two
//! servers on one machine, signed in to two control planes, never read each other's
//! token.
//!
//! A person signs in once for a control plane, whichever client they sign in with: `~/.sqlflow/credentials.json`, the
//! file `sqlflow login` keeps, holds one credential per control-plane URL, and this server reads and writes it beside its
//! own token file ([`SharedCredentials`]). `tests/shared-credentials.json` holds the CLI's reader and this one to one
//! format.

use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use base64::Engine;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

pub const DEFAULT_URL: &str = "http://localhost:8080";

/// How long before expiry a renewable session is rolled onto a fresh token. Matches the GUI's `RENEW_LEAD_MS`
/// (gui/src/auth/AuthContext.tsx), so a headless client and a browser tab follow one expiration policy.
pub const RENEW_LEAD_SECS: i64 = 5 * 60;

/// This close to expiry a token is treated as already gone: too little runway to trust a renewal round trip. Matches
/// the GUI's `RENEW_FLOOR_MS`.
pub const EXPIRY_FLOOR_SECS: i64 = 30;

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct McpConfig {
    #[serde(rename = "controlPlaneUrl")]
    pub control_plane_url: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TokenCache {
    pub access_token: String,
    #[serde(default)]
    pub scope: String,
    pub expires_at: Option<DateTime<Utc>>,
    /// True for an interactive session token (a JWT carrying `auth_time`), which this client rolls at
    /// `POST /api/v1/auth/renew` before it lapses. False for a personal access token, the break-glass bootstrap
    /// token, or any credential the control plane refuses to renew: those live exactly as long as they were issued
    /// for. A token file written by an older client (which carried `token_id`/`renewable` for a self-minted personal
    /// access token) loads with this false, so that token is used until its own expiry and never extended.
    #[serde(default)]
    pub renews: bool,
}

/// The claims payload of a bearer token that is a JWT, or `None` for anything that is not a three-segment JWT with a
/// JSON payload (which covers the opaque `sqlf_` personal access token).
///
/// This reads the payload without verifying the signature, which is correct for the only things it is used for:
/// deciding whether a credential is worth presenting and whether it is worth trying to renew. The control plane
/// remains the authority on validity.
fn jwt_claims(token: &str) -> Option<serde_json::Value> {
    let mut parts = token.split('.');
    let (_header, payload, _signature) = (parts.next()?, parts.next()?, parts.next()?);
    if parts.next().is_some() {
        return None;
    }
    let bytes = URL_SAFE_NO_PAD.decode(payload).ok()?;
    serde_json::from_slice(&bytes).ok()
}

fn jwt_instant(token: &str, claim: &str) -> Option<DateTime<Utc>> {
    DateTime::from_timestamp(jwt_claims(token)?.get(claim)?.as_i64()?, 0)
}

/// The `exp` claim of a bearer token that is a JWT, as an absolute instant.
///
/// A session token minted by the control plane states its own lifetime, so a credential handed to us without
/// separate expiry metadata (the `SQLFLOW_CONTROL_PLANE_TOKEN` environment variable, or a paste through
/// `set_access_token`) can still be dated honestly instead of being treated as never-expiring. `None` for an opaque
/// personal access token: its lifetime is known only to the server, and no local opinion is the truthful answer.
pub fn jwt_expiry(token: &str) -> Option<DateTime<Utc>> {
    jwt_instant(token, "exp")
}

/// The `auth_time` claim of a bearer token that is a JWT: when the person behind an interactive session actually
/// signed in. Its presence is exactly what makes the control plane willing to renew the token.
pub fn jwt_auth_time(token: &str) -> Option<DateTime<Utc>> {
    jwt_instant(token, "auth_time")
}

impl TokenCache {
    /// A cache entry for a bearer whose only metadata is itself: dated from its own `exp`, and renewable exactly
    /// when it carries `auth_time`.
    pub fn from_bearer(access_token: String, scope: String) -> Self {
        TokenCache {
            expires_at: jwt_expiry(&access_token),
            renews: jwt_auth_time(&access_token).is_some(),
            access_token,
            scope,
        }
    }

    pub fn is_expired(&self, skew_secs: i64) -> bool {
        self.is_expired_at(Utc::now(), skew_secs)
    }

    fn is_expired_at(&self, now: DateTime<Utc>, skew_secs: i64) -> bool {
        match self.expires_at {
            Some(exp) => now + chrono::Duration::seconds(skew_secs) >= exp,
            None => false,
        }
    }

    /// Whether this session should be rolled now: it renews, it is inside the renewal lead, and it is still far
    /// enough from expiry for a renewal round trip to land. A token past that floor can no longer authenticate its
    /// own renewal and needs a fresh sign-in instead.
    pub fn renewal_due(&self, now: DateTime<Utc>) -> bool {
        let Some(exp) = self.expires_at else {
            return false;
        };
        self.renews
            && !self.is_expired_at(now, EXPIRY_FLOOR_SECS)
            && now + chrono::Duration::seconds(RENEW_LEAD_SECS) >= exp
    }
}

fn sqlflow_dir() -> Option<PathBuf> {
    dirs::home_dir().map(|h| h.join(".sqlflow"))
}

/// The prefix SQLFlow's own server keeps its state files under.
pub const DEFAULT_STATE_PREFIX: &str = "mcp";

/// One control plane's credential in the shared file, as `StoredCredential` in the CLI's `CredentialStore` writes it.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SharedCredential {
    token: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    token_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    username: Option<String>,
    created_utc: DateTime<Utc>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    expires_utc: Option<DateTime<Utc>>,
    #[serde(default)]
    renews: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    scope: Option<String>,
}

/// The file a person's sign-in is shared through: every client of a control plane reads the credential kept for its URL,
/// so signing in to one signs in the others. Keyed by URL, so two control planes never read each other's credential.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SharedCredentials {
    path: PathBuf,
}

impl SharedCredentials {
    /// The file: `SQLFLOW_CREDENTIALS_FILE` when set, as the CLI reads it, else under the user's profile. None for this
    /// crate's own unit tests with no file named, which must not write to the person's.
    pub fn located() -> Option<Self> {
        let path = match std::env::var("SQLFLOW_CREDENTIALS_FILE") {
            Ok(path) if !path.trim().is_empty() => PathBuf::from(path),
            _ if cfg!(test) => return None,
            _ => sqlflow_dir()?.join("credentials.json"),
        };
        Some(SharedCredentials { path })
    }

    pub fn at(path: PathBuf) -> Self {
        SharedCredentials { path }
    }

    /// When the file last changed, so a server reads it only when another client has written it.
    pub fn stamp(&self) -> Option<std::time::SystemTime> {
        std::fs::metadata(&self.path).and_then(|m| m.modified()).ok()
    }

    fn read(&self) -> serde_json::Map<String, serde_json::Value> {
        std::fs::read_to_string(&self.path)
            .ok()
            .and_then(|text| serde_json::from_str(&text).ok())
            .unwrap_or_default()
    }

    fn write(&self, entries: &serde_json::Map<String, serde_json::Value>) -> std::io::Result<()> {
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        // Written beside it and moved over it, so a client reading the file never meets half of one.
        let staged = self.path.with_extension("json.new");
        std::fs::write(&staged, serde_json::to_string_pretty(entries).unwrap_or_default())?;
        restrict_permissions(&staged);
        std::fs::rename(&staged, &self.path)
    }

    /// The credential kept for a control plane.
    pub fn load(&self, url: &str) -> Option<TokenCache> {
        let entry = self.read().remove(&credential_key(url))?;
        let credential: SharedCredential = serde_json::from_value(entry).ok()?;
        Some(TokenCache {
            access_token: credential.token,
            scope: credential.scope.unwrap_or_else(|| "read operate author".to_string()),
            expires_at: credential.expires_utc,
            renews: credential.renews,
        })
    }

    /// Keeps a credential for a control plane, leaving every other control plane's as it is. The same token keeps who it
    /// is and its server-side id, so a session renewing does not forget them.
    pub fn save(&self, url: &str, token: &TokenCache) -> std::io::Result<()> {
        let mut entries = self.read();
        let key = credential_key(url);
        let held = entries
            .get(&key)
            .and_then(|entry| serde_json::from_value::<SharedCredential>(entry.clone()).ok())
            .filter(|held| held.token == token.access_token);
        let credential = SharedCredential {
            token: token.access_token.clone(),
            token_id: held.as_ref().and_then(|held| held.token_id.clone()),
            username: held.as_ref().and_then(|held| held.username.clone()),
            created_utc: held.map(|held| held.created_utc).unwrap_or_else(Utc::now),
            expires_utc: token.expires_at,
            renews: token.renews,
            scope: Some(token.scope.clone()),
        };
        entries.insert(key, serde_json::to_value(credential).unwrap_or_default());
        self.write(&entries)
    }

    /// Forgets the credential of a control plane; the others stay.
    pub fn clear(&self, url: &str) -> std::io::Result<()> {
        let mut entries = self.read();
        if entries.remove(&credential_key(url)).is_some() {
            self.write(&entries)?;
        }
        Ok(())
    }

    /// The control plane the file holds, when it holds exactly one: who signed in to one has said which they mean.
    pub fn only_url(&self) -> Option<String> {
        let entries = self.read();
        let mut urls = entries.keys();
        match (urls.next(), urls.next()) {
            (Some(url), None) => Some(url.clone()),
            _ => None,
        }
    }
}

/// The key a control plane's credential is kept under: scheme and host in lower case, a default port left out and no
/// trailing slash, as the CLI's `CredentialStore.Normalize` makes it, so both name one entry.
pub fn credential_key(url: &str) -> String {
    let key = match reqwest::Url::parse(url) {
        Ok(parsed) => format!("{}{}", parsed.origin().ascii_serialization(), parsed.path()),
        Err(_) => url.to_string(),
    };
    key.trim_end_matches('/').to_lowercase()
}

/// Where one server keeps its control-plane URL and its token: `<prefix>-config.json` and
/// `<prefix>-token.json` under `~/.sqlflow/`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StateStore {
    prefix: String,
}

impl Default for StateStore {
    fn default() -> Self {
        StateStore {
            prefix: DEFAULT_STATE_PREFIX.to_string(),
        }
    }
}

impl StateStore {
    /// A store under `prefix`, which becomes part of two file names: lowercase letters, digits and
    /// hyphens, starting with a letter, at most 64 characters.
    pub fn new(prefix: &str) -> Result<Self, String> {
        let valid = !prefix.is_empty()
            && prefix.len() <= 64
            && prefix.starts_with(|c: char| c.is_ascii_lowercase())
            && prefix
                .chars()
                .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-');
        if !valid {
            return Err(format!(
                "'{prefix}' is not a state prefix: lowercase letters, digits and hyphens, starting with a \
                 letter, at most 64 characters"
            ));
        }
        Ok(StateStore {
            prefix: prefix.to_string(),
        })
    }

    pub fn prefix(&self) -> &str {
        &self.prefix
    }

    fn config_path(&self) -> Option<PathBuf> {
        sqlflow_dir().map(|d| d.join(format!("{}-config.json", self.prefix)))
    }

    fn token_path(&self) -> Option<PathBuf> {
        sqlflow_dir().map(|d| d.join(format!("{}-token.json", self.prefix)))
    }

    pub fn load_config(&self) -> McpConfig {
        let Some(path) = self.config_path() else {
            return McpConfig::default();
        };
        match std::fs::read_to_string(&path) {
            Ok(text) => serde_json::from_str(&text).unwrap_or_default(),
            Err(_) => McpConfig::default(),
        }
    }

    pub fn save_config(&self, cfg: &McpConfig) -> std::io::Result<()> {
        let Some(path) = self.config_path() else {
            return Ok(());
        };
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let text = serde_json::to_string_pretty(cfg).unwrap_or_default();
        std::fs::write(path, text)
    }

    pub fn load_token(&self) -> Option<TokenCache> {
        let path = self.token_path()?;
        let text = std::fs::read_to_string(&path).ok()?;
        serde_json::from_str(&text).ok()
    }

    pub fn save_token(&self, token: &TokenCache) -> std::io::Result<()> {
        let Some(path) = self.token_path() else {
            return Ok(());
        };
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let text = serde_json::to_string_pretty(token).unwrap_or_default();
        std::fs::write(&path, text)?;
        restrict_permissions(&path);
        Ok(())
    }

    pub fn clear_token(&self) -> std::io::Result<()> {
        if let Some(path) = self.token_path() {
            if path.exists() {
                std::fs::remove_file(path)?;
            }
        }
        Ok(())
    }
}

#[cfg(unix)]
fn restrict_permissions(path: &std::path::Path) {
    use std::os::unix::fs::PermissionsExt;
    let _ = std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600));
}

#[cfg(not(unix))]
fn restrict_permissions(_path: &std::path::Path) {
    // On Windows the file inherits the user profile ACL; no extra action.
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The file both this server and the CLI read, in one format (`CredentialStoreTests` reads the same one).
    fn fixture() -> SharedCredentials {
        SharedCredentials::at(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests/shared-credentials.json"))
    }

    fn scratch(name: &str) -> SharedCredentials {
        let path = std::env::temp_dir().join(format!("sqlflow-mcp-{}-{name}.json", std::process::id()));
        let _ = std::fs::remove_file(&path);
        SharedCredentials::at(path)
    }

    #[test]
    fn the_shared_file_reads_a_cli_token_and_a_servers_session() {
        let token = fixture().load("https://CONTROL.example.com/").expect("the CLI's entry");
        assert_eq!(token.access_token, "sqlf_a-personal-access-token");
        assert!(!token.renews);
        let session = fixture().load("http://localhost:5000").expect("the session's entry");
        assert_eq!(session.access_token, "a.session.token");
        assert!(session.renews);
        assert_eq!(session.scope, "read operate author");
        assert!(fixture().load("https://other.example.com").is_none());
    }

    #[test]
    fn one_key_names_a_control_plane_however_its_url_is_written() {
        assert_eq!(credential_key("https://Host.example.com/"), "https://host.example.com");
        assert_eq!(credential_key("https://host.example.com:443"), "https://host.example.com");
        assert_eq!(credential_key("http://localhost:5000/"), "http://localhost:5000");
    }

    #[test]
    fn a_control_plane_keeps_its_own_credential_and_leaves_the_others() {
        let store = scratch("own");
        let token = |value: &str| TokenCache { access_token: value.to_string(), scope: "read".to_string(), expires_at: None, renews: true };
        store.save("https://a.example.com", &token("a")).unwrap();
        store.save("https://b.example.com", &token("b")).unwrap();

        assert_eq!(store.load("https://a.example.com").unwrap().access_token, "a");
        assert_eq!(store.load("https://b.example.com").unwrap().access_token, "b");

        store.clear("https://a.example.com").unwrap();
        assert!(store.load("https://a.example.com").is_none());
        assert_eq!(store.load("https://b.example.com").unwrap().access_token, "b");
        assert_eq!(store.only_url().as_deref(), Some("https://b.example.com"));
    }

    #[test]
    fn a_session_renewing_keeps_who_it_is_only_when_it_is_the_same_token() {
        let store = scratch("renew");
        let session = TokenCache { access_token: "a".to_string(), scope: "read".to_string(), expires_at: None, renews: true };
        store.save("https://a.example.com", &session).unwrap();
        let first = store.read();
        store.save("https://a.example.com", &session).unwrap();
        assert_eq!(first["https://a.example.com"]["createdUtc"], store.read()["https://a.example.com"]["createdUtc"]);
    }

    #[test]
    fn a_file_that_is_missing_or_not_json_holds_nothing() {
        let store = scratch("missing");
        assert!(store.load("https://a.example.com").is_none());
        assert!(store.only_url().is_none());
        std::fs::write(&store.path, "not json").unwrap();
        assert!(store.load("https://a.example.com").is_none());
    }

    /// A signature-less JWT carrying the given claims payload; only the payload segment is ever read.
    fn jwt(claims: &serde_json::Value) -> String {
        let payload = URL_SAFE_NO_PAD.encode(serde_json::to_vec(claims).unwrap());
        format!("header.{payload}.signature")
    }

    fn session(expires_in_secs: i64, renews: bool) -> TokenCache {
        TokenCache {
            access_token: "header.payload.signature".to_string(),
            scope: "read".to_string(),
            expires_at: Some(Utc::now() + chrono::Duration::seconds(expires_in_secs)),
            renews,
        }
    }

    #[test]
    fn reads_the_expiry_a_session_jwt_states_for_itself() {
        // The control plane's session token dates itself; an env/pasted credential must be dated from it
        // rather than treated as never-expiring.
        let exp = Utc::now() + chrono::Duration::hours(12);
        let parsed = jwt_expiry(&jwt(&serde_json::json!({ "sub": "admin", "exp": exp.timestamp() })));
        assert_eq!(parsed.map(|d| d.timestamp()), Some(exp.timestamp()));
    }

    #[test]
    fn an_expired_session_jwt_is_dated_in_the_past() {
        // The case that broke a real session: a 12-hour token read hours after it lapsed must report expired,
        // not authenticated.
        let exp = Utc::now() - chrono::Duration::hours(8);
        let cache = TokenCache::from_bearer(jwt(&serde_json::json!({ "exp": exp.timestamp() })), "read".into());
        assert!(cache.is_expired(30));
    }

    #[test]
    fn an_opaque_personal_access_token_has_no_local_expiry_and_does_not_renew() {
        // A sqlf_ PAT is not a JWT: it states nothing, and inventing an expiry for it would be a guess.
        assert!(jwt_expiry("sqlf_abc123").is_none());
        let cache = TokenCache::from_bearer("sqlf_abc123".into(), "read".into());
        assert!(!cache.renews);
        assert!(cache.expires_at.is_none());
    }

    #[test]
    fn a_malformed_or_unclaimed_token_yields_no_expiry() {
        assert!(jwt_expiry("not.a.jwt").is_none());
        assert!(jwt_expiry("too.many.segments.here").is_none());
        assert!(jwt_expiry("").is_none());
        // Well-formed JWT, but no exp claim: nothing to read, so no local opinion.
        assert!(jwt_expiry(&jwt(&serde_json::json!({ "sub": "admin" }))).is_none());
    }

    #[test]
    fn only_a_token_carrying_auth_time_renews() {
        let exp = (Utc::now() + chrono::Duration::hours(12)).timestamp();
        let signed_in = (Utc::now() - chrono::Duration::days(2)).timestamp();

        // An interactive session (GUI login, or a device approved from one) carries auth_time.
        let interactive = TokenCache::from_bearer(
            jwt(&serde_json::json!({ "exp": exp, "auth_time": signed_in })), "read".into());
        assert!(interactive.renews);
        assert_eq!(jwt_auth_time(&interactive.access_token).map(|d| d.timestamp()), Some(signed_in));

        // The bootstrap token, an assistant run's delegated token, or a device approved by a PAT: no auth_time.
        let fixed = TokenCache::from_bearer(jwt(&serde_json::json!({ "exp": exp })), "read".into());
        assert!(!fixed.renews);
    }

    #[test]
    fn renewal_is_due_only_inside_the_lead_and_above_the_floor() {
        let now = Utc::now();
        // Hours of life left: leave it.
        assert!(!session(3 * 3600, true).renewal_due(now));
        // Four minutes left: inside the five-minute lead, roll it.
        assert!(session(4 * 60, true).renewal_due(now));
        // Ten seconds left: under the floor, too late to trust a round trip.
        assert!(!session(10, true).renewal_due(now));
        // Already expired: needs a sign-in, not a renewal.
        assert!(!session(-60, true).renewal_due(now));
    }

    #[test]
    fn a_token_that_does_not_renew_is_never_due() {
        assert!(!session(4 * 60, false).renewal_due(Utc::now()));
        let undated = TokenCache { expires_at: None, ..session(0, true) };
        assert!(!undated.renewal_due(Utc::now()));
    }

    #[test]
    fn a_token_file_from_an_older_client_loads_as_non_renewable() {
        // The previous client stored a self-minted 90-day personal access token it rotated forever. That token is
        // honoured until its own expiry, but never extended: it must not read as a renewable session.
        let legacy = r#"{
            "access_token": "sqlf_legacy",
            "scope": "read operate author",
            "expires_at": "2030-01-01T00:00:00Z",
            "token_id": "0b6c6f5e-7d1a-4a57-9b77-3d1b8d9a2f10",
            "renewable": true
        }"#;
        let cache: TokenCache = serde_json::from_str(legacy).unwrap();
        assert!(!cache.renews);
        assert_eq!(cache.access_token, "sqlf_legacy");
    }

    #[test]
    fn the_default_store_keeps_the_file_names_it_always_had() {
        // An upgrade must find the sign-in an earlier build stored.
        let store = StateStore::default();
        let config = store.config_path().expect("a home directory");
        let token = store.token_path().expect("a home directory");
        assert_eq!(config.file_name().unwrap(), "mcp-config.json");
        assert_eq!(token.file_name().unwrap(), "mcp-token.json");
    }

    #[test]
    fn a_host_prefix_names_files_of_its_own() {
        let store = StateStore::new("probe-mcp").expect("a valid prefix");
        assert_eq!(store.config_path().unwrap().file_name().unwrap(), "probe-mcp-config.json");
        assert_eq!(store.token_path().unwrap().file_name().unwrap(), "probe-mcp-token.json");
        assert_ne!(store.token_path(), StateStore::default().token_path());
    }

    #[test]
    fn a_prefix_that_could_leave_the_state_folder_is_refused() {
        for bad in ["", "../etc", "a/b", "A", "1mcp", "mcp token", "mcp.json"] {
            let refused = StateStore::new(bad).expect_err(bad);
            assert!(refused.contains("not a state prefix"), "{refused}");
        }
        assert!(StateStore::new(&"a".repeat(65)).is_err());
    }
}
