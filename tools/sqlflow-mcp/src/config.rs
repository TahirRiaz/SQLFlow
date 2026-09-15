//! Persisted MCP configuration and access token.
//!
//! Two files under `~/.sqlflow/`:
//!   * `mcp-config.json` — the control-plane URL.
//!   * `mcp-token.json`  — the bearer token, its scopes, and expiry.
//!
//! The token file is written owner-only where the platform supports it. Both
//! are read at startup and rewritten on change, so a device-auth session
//! survives restarts (matching the DeltaForge MCP token cache).

use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use base64::Engine;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

pub const DEFAULT_URL: &str = "http://localhost:8080";

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
    /// The catalog id of the personal access token, when this credential is a managed PAT we minted (so we can
    /// revoke it after rotating). Absent for a pasted token or a short-lived device token.
    #[serde(default)]
    pub token_id: Option<String>,
    /// True for a managed PAT the server minted for us: one we rotate on our own before it expires, so the user
    /// never has to sign in again while the client stays in use. A pasted secret or device token is not renewable.
    #[serde(default)]
    pub renewable: bool,
}

/// The `exp` claim of a bearer token that is a JWT, as an absolute instant.
///
/// A session token minted by the control plane states its own lifetime in its payload, so a credential handed to
/// us without separate expiry metadata (the `SQLFLOW_CONTROL_PLANE_TOKEN` environment variable, or a paste through
/// `set_access_token`) can still be dated honestly instead of being treated as never-expiring. Returns `None` for
/// anything that is not a three-segment JWT with a numeric `exp`, which covers the opaque `sqlf_` personal access
/// token: its lifetime is known only to the server, and `None` (no local opinion) is the truthful answer there.
/// This reads the payload without verifying the signature, which is correct for the only thing it is used for:
/// deciding whether a credential is worth presenting. The server remains the authority on validity.
pub fn jwt_expiry(token: &str) -> Option<DateTime<Utc>> {
    let mut parts = token.split('.');
    let (_header, payload, _signature) = (parts.next()?, parts.next()?, parts.next()?);
    if parts.next().is_some() {
        return None;
    }
    let bytes = URL_SAFE_NO_PAD.decode(payload).ok()?;
    let claims: serde_json::Value = serde_json::from_slice(&bytes).ok()?;
    DateTime::from_timestamp(claims.get("exp")?.as_i64()?, 0)
}

impl TokenCache {
    pub fn is_expired(&self, skew_secs: i64) -> bool {
        match self.expires_at {
            Some(exp) => Utc::now() + chrono::Duration::seconds(skew_secs) >= exp,
            None => false,
        }
    }

    /// Whether this managed PAT has entered its rotation window: it is still valid but expires within
    /// <paramref name="window_days"/>, so it should be replaced now while it can still authenticate the mint of its
    /// successor. A non-renewable or already-expired token never qualifies (the former is not ours to rotate, the
    /// latter can no longer mint a replacement and needs a fresh sign-in).
    pub fn should_rotate(&self, window_days: i64) -> bool {
        if !self.renewable || self.token_id.is_none() {
            return false;
        }
        match self.expires_at {
            Some(exp) => {
                let now = Utc::now();
                now < exp && now + chrono::Duration::days(window_days) >= exp
            }
            None => false,
        }
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

    fn token(renewable: bool, id: Option<&str>, expires_in_days: Option<i64>) -> TokenCache {
        TokenCache {
            access_token: "sqlf_x".to_string(),
            scope: "read operate".to_string(),
            expires_at: expires_in_days.map(|d| Utc::now() + chrono::Duration::days(d)),
            token_id: id.map(|s| s.to_string()),
            renewable,
        }
    }

    /// A signature-less JWT carrying the given claims payload; only the payload segment is ever read.
    fn jwt(claims: &serde_json::Value) -> String {
        let payload = URL_SAFE_NO_PAD.encode(serde_json::to_vec(claims).unwrap());
        format!("header.{payload}.signature")
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
        let cache = TokenCache {
            access_token: jwt(&serde_json::json!({ "exp": exp.timestamp() })),
            scope: "read".to_string(),
            expires_at: jwt_expiry(&jwt(&serde_json::json!({ "exp": exp.timestamp() }))),
            token_id: None,
            renewable: false,
        };
        assert!(cache.is_expired(30));
    }

    #[test]
    fn an_opaque_personal_access_token_has_no_local_expiry() {
        // A sqlf_ PAT is not a JWT: it states nothing, and inventing an expiry for it would be a guess.
        assert!(jwt_expiry("sqlf_abc123").is_none());
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
    fn rotates_only_a_renewable_token_inside_its_window() {
        // Managed, expiring in 5 days: inside the 14-day window -> rotate.
        assert!(token(true, Some("id"), Some(5)).should_rotate(14));
    }

    #[test]
    fn does_not_rotate_a_token_with_ample_life() {
        // Managed but 60 days out: well outside the window -> leave it.
        assert!(!token(true, Some("id"), Some(60)).should_rotate(14));
    }

    #[test]
    fn does_not_rotate_a_pasted_or_device_token() {
        // Not renewable (a pasted secret / device token), even inside the window: not ours to rotate.
        assert!(!token(false, None, Some(1)).should_rotate(14));
        // Renewable in shape but missing an id (cannot be revoked) is likewise skipped.
        assert!(!token(true, None, Some(1)).should_rotate(14));
    }

    #[test]
    fn does_not_rotate_an_already_expired_token() {
        // Past expiry: a mint would 401, so this needs a fresh sign-in, not a rotation.
        assert!(!token(true, Some("id"), Some(-1)).should_rotate(14));
    }

    #[test]
    fn never_expiring_managed_token_is_not_rotated() {
        assert!(!token(true, Some("id"), None).should_rotate(14));
    }
}
