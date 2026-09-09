//! Persisted MCP configuration and access token.
//!
//! Two files under `~/.sqlflow/`:
//!   * `mcp-config.json` — the control-plane URL.
//!   * `mcp-token.json`  — the bearer token, its scopes, and expiry.
//!
//! The token file is written owner-only where the platform supports it. Both
//! are read at startup and rewritten on change, so a device-auth session
//! survives restarts (matching the DeltaForge MCP token cache).

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
