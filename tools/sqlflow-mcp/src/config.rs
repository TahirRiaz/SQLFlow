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

fn config_path() -> Option<PathBuf> {
    sqlflow_dir().map(|d| d.join("mcp-config.json"))
}

fn token_path() -> Option<PathBuf> {
    sqlflow_dir().map(|d| d.join("mcp-token.json"))
}

pub fn load_config() -> McpConfig {
    let Some(path) = config_path() else {
        return McpConfig::default();
    };
    match std::fs::read_to_string(&path) {
        Ok(text) => serde_json::from_str(&text).unwrap_or_default(),
        Err(_) => McpConfig::default(),
    }
}

pub fn save_config(cfg: &McpConfig) -> std::io::Result<()> {
    let Some(path) = config_path() else {
        return Ok(());
    };
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let text = serde_json::to_string_pretty(cfg).unwrap_or_default();
    std::fs::write(path, text)
}

pub fn load_token() -> Option<TokenCache> {
    let path = token_path()?;
    let text = std::fs::read_to_string(&path).ok()?;
    serde_json::from_str(&text).ok()
}

pub fn save_token(token: &TokenCache) -> std::io::Result<()> {
    let Some(path) = token_path() else {
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

pub fn clear_token() -> std::io::Result<()> {
    if let Some(path) = token_path() {
        if path.exists() {
            std::fs::remove_file(path)?;
        }
    }
    Ok(())
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
}
