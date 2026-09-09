//! HTTP client for the SQLFlow control plane (`/api/v1`).
//!
//! Read tools (catalog, lineage, runs, schedules, search, summary) proxy the
//! control plane's authenticated read surface; the trigger/cancel tools use the
//! operate surface. Authentication is an OAuth 2.0 device-authorization grant
//! (RFC 8628) against the endpoints added to `AuthEndpoints.cs`, with a token
//! paste (`set_access_token` / `SQLFLOW_CONTROL_PLANE_TOKEN`) as a fallback.

use crate::config::{self, TokenCache};
use anyhow::{anyhow, bail, Context, Result};
use serde::Deserialize;
use serde_json::{json, Value};
use std::sync::RwLock;

tokio::task_local! {
    /// The bearer token of the inbound request when the MCP server runs over HTTP.
    /// The HTTP dispatch (`SqlFlowMcp::call_tool`) scopes it around each tool call, so
    /// every control-plane request runs as the connecting caller; it is never set over
    /// stdio. While present it is the only credential used: the locally stored token
    /// and its rotation logic are bypassed, because the caller's token is not ours to
    /// cache or rotate.
    pub static HTTP_BEARER: String;
}

/// The device-authorization response returned by `POST /api/v1/auth/device`.
#[derive(Debug, Clone, Deserialize)]
pub struct DeviceAuth {
    #[serde(rename = "deviceCode")]
    pub device_code: String,
    #[serde(rename = "userCode")]
    pub user_code: String,
    #[serde(rename = "verificationUri")]
    pub verification_uri: String,
    #[serde(rename = "verificationUriComplete")]
    pub verification_uri_complete: Option<String>,
    #[serde(rename = "expiresIn")]
    pub expires_in: i64,
    #[serde(default = "default_interval")]
    pub interval: i64,
}

fn default_interval() -> i64 {
    5
}

/// Outcome of one device-token poll.
pub enum PollOutcome {
    Approved(TokenCache),
    Pending,
    SlowDown,
    Denied,
    Expired,
}

#[derive(Deserialize)]
struct TokenResponse {
    access_token: String,
    #[serde(default)]
    scope: String,
    #[serde(default)]
    expires_in: Option<i64>,
}

#[derive(Deserialize)]
struct ErrorResponse {
    error: String,
}

/// The `POST /api/v1/me/tokens` response: the listing view of the created token plus its one-time secret.
#[derive(Deserialize)]
struct CreatedAccessToken {
    token: AccessTokenInfo,
    secret: String,
}

#[derive(Deserialize)]
struct AccessTokenInfo {
    id: String,
    #[serde(default)]
    scopes: Vec<String>,
    #[serde(rename = "expiresUtc", default)]
    expires_utc: Option<chrono::DateTime<chrono::Utc>>,
}

/// The lifetime a self-managed PAT is minted with, and how close to expiry it is rotated. The client mints a token
/// good for `PAT_LIFETIME_DAYS`, then silently replaces it once fewer than `PAT_ROTATE_WINDOW_DAYS` remain, so a
/// long-running client never lapses into a re-login as long as it is used within the rotation window.
const PAT_LIFETIME_DAYS: i64 = 90;
const PAT_ROTATE_WINDOW_DAYS: i64 = 14;

pub struct ControlPlane {
    http: reqwest::Client,
    base_url: RwLock<String>,
    token: RwLock<Option<TokenCache>>,
    /// The device code of an in-flight `login`, so `check_auth_status` can poll
    /// without the caller re-supplying it.
    pending_device_code: RwLock<Option<String>>,
    /// Single-flights token rotation so two concurrent requests entering the rotation window mint one replacement,
    /// not two.
    rotate_lock: tokio::sync::Mutex<()>,
}

impl ControlPlane {
    /// Build the client from persisted config/token, overridden by env vars.
    pub fn from_env() -> Self {
        let cfg = config::load_config();
        let base_url = std::env::var("SQLFLOW_CONTROL_PLANE_URL")
            .ok()
            .or(cfg.control_plane_url)
            .unwrap_or_else(|| config::DEFAULT_URL.to_string());
        let token = std::env::var("SQLFLOW_CONTROL_PLANE_TOKEN")
            .ok()
            .map(|t| TokenCache {
                access_token: t,
                scope: "read operate author".to_string(),
                expires_at: None,
                token_id: None,
                renewable: false,
            })
            .or_else(config::load_token);
        ControlPlane {
            http: reqwest::Client::builder()
                .user_agent(concat!("sqlflow-mcp/", env!("CARGO_PKG_VERSION")))
                .build()
                .expect("failed to build HTTP client"),
            base_url: RwLock::new(normalize_base(&base_url)),
            token: RwLock::new(token),
            pending_device_code: RwLock::new(None),
            rotate_lock: tokio::sync::Mutex::new(()),
        }
    }

    pub fn pending_device_code(&self) -> Option<String> {
        self.pending_device_code.read().unwrap().clone()
    }

    pub fn base_url(&self) -> String {
        self.base_url.read().unwrap().clone()
    }

    pub fn set_base_url(&self, url: &str) {
        *self.base_url.write().unwrap() = normalize_base(url);
        let mut cfg = config::load_config();
        cfg.control_plane_url = Some(self.base_url());
        let _ = config::save_config(&cfg);
    }

    pub fn is_authenticated(&self) -> bool {
        self.token
            .read()
            .unwrap()
            .as_ref()
            .map(|t| !t.is_expired(30))
            .unwrap_or(false)
    }

    pub fn set_token(&self, token: TokenCache) {
        let _ = config::save_token(&token);
        *self.token.write().unwrap() = Some(token);
    }

    pub fn clear_token(&self) {
        let _ = config::clear_token();
        *self.token.write().unwrap() = None;
    }

    fn bearer(&self) -> Option<String> {
        self.token
            .read()
            .unwrap()
            .as_ref()
            .map(|t| t.access_token.clone())
    }

    fn url(&self, path: &str) -> String {
        format!("{}{}", self.base_url(), path)
    }

    // --- Health & auth -----------------------------------------------------

    /// Probe `/health/ready`; returns the raw status line.
    pub async fn check_connectivity(&self) -> Result<String> {
        let resp = self
            .http
            .get(self.url("/health/ready"))
            .send()
            .await
            .context("could not reach the control plane")?;
        Ok(format!("{} ({})", resp.status(), self.base_url()))
    }

    /// Begin a device-authorization grant.
    pub async fn start_device_auth(&self) -> Result<DeviceAuth> {
        let resp = self
            .http
            .post(self.url("/api/v1/auth/device"))
            .json(&json!({ "clientId": "sqlflow-mcp", "scope": "read operate author" }))
            .send()
            .await
            .context("could not start device authorization")?;
        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            bail!("device authorization failed ({status}): {body}");
        }
        let auth = resp
            .json::<DeviceAuth>()
            .await
            .context("invalid device authorization response")?;
        *self.pending_device_code.write().unwrap() = Some(auth.device_code.clone());
        Ok(auth)
    }

    /// Poll once for the device token.
    pub async fn poll_device_token(&self, device_code: &str) -> Result<PollOutcome> {
        let resp = self
            .http
            .post(self.url("/api/v1/auth/device/token"))
            .json(&json!({ "deviceCode": device_code }))
            .send()
            .await
            .context("could not poll for the device token")?;
        if resp.status().is_success() {
            let tok: TokenResponse = resp.json().await.context("invalid token response")?;
            let expires_at = tok
                .expires_in
                .map(|s| chrono::Utc::now() + chrono::Duration::seconds(s));
            let cache = TokenCache {
                access_token: tok.access_token,
                scope: tok.scope,
                expires_at,
                // The device grant yields a short-lived session token, not a managed PAT; the caller exchanges it
                // for one right after approval.
                token_id: None,
                renewable: false,
            };
            self.set_token(cache.clone());
            return Ok(PollOutcome::Approved(cache));
        }
        // Non-success: interpret the RFC 8628 error code.
        let err: ErrorResponse = resp.json().await.unwrap_or(ErrorResponse {
            error: "unknown".to_string(),
        });
        Ok(match err.error.as_str() {
            "authorization_pending" => PollOutcome::Pending,
            "slow_down" => PollOutcome::SlowDown,
            "access_denied" => PollOutcome::Denied,
            "expired_token" => PollOutcome::Expired,
            other => bail!("device token error: {other}"),
        })
    }

    // --- Managed personal access tokens ------------------------------------

    /// Exchange the current credential for a long-lived, self-managed personal access token and store it in its
    /// place. Called right after an interactive sign-in (or after pasting a short-lived session token) so the client
    /// holds a durable credential instead of a token that lapses in an hour. Best-effort by contract: the caller
    /// ignores the error and keeps the original token if the control plane cannot mint one (an older control plane,
    /// or a session with no user behind it). Scopes are capped server-side to what the current credential holds.
    pub async fn provision_managed_token(&self, scope: &str) -> Result<()> {
        let bearer = self
            .bearer()
            .ok_or_else(|| anyhow!("not authenticated: cannot provision an access token"))?;
        let scopes: Vec<&str> = scope.split_whitespace().collect();
        let resp = self
            .http
            .post(self.url("/api/v1/me/tokens"))
            .bearer_auth(bearer)
            .json(&json!({ "name": client_token_name(), "scopes": scopes, "expiresInDays": PAT_LIFETIME_DAYS }))
            .send()
            .await
            .context("could not provision an access token")?;
        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            bail!("access-token provisioning failed ({status}): {body}");
        }
        let created: CreatedAccessToken = resp.json().await.context("invalid token response")?;
        self.set_token(TokenCache {
            access_token: created.secret,
            scope: created.token.scopes.join(" "),
            expires_at: created.token.expires_utc,
            token_id: Some(created.token.id),
            renewable: true,
        });
        Ok(())
    }

    /// Rotate the managed token if it has entered its rotation window: mint a replacement with the still-valid
    /// current token, then revoke the old one. Cheap and a no-op when no rotation is due (the common case), so it is
    /// safe to call before every request. Single-flighted so concurrent requests rotate once.
    pub async fn ensure_fresh(&self) {
        let (due, old_id, scope) = {
            let guard = self.token.read().unwrap();
            match guard.as_ref() {
                Some(t) if t.should_rotate(PAT_ROTATE_WINDOW_DAYS) => {
                    (true, t.token_id.clone(), t.scope.clone())
                }
                _ => (false, None, String::new()),
            }
        };
        if !due {
            return;
        }

        // Only one rotation at a time; a loser simply skips (the winner refreshes the shared token).
        let Ok(_guard) = self.rotate_lock.try_lock() else {
            return;
        };
        // Re-check under the guard: a rotation that finished between our snapshot and acquiring the lock leaves
        // nothing to do.
        let still_due = self
            .token
            .read()
            .unwrap()
            .as_ref()
            .map(|t| t.should_rotate(PAT_ROTATE_WINDOW_DAYS))
            .unwrap_or(false);
        if !still_due {
            return;
        }

        if self.provision_managed_token(&scope).await.is_ok() {
            if let Some(id) = old_id {
                // The freshly minted token is now current; revoke its predecessor with it. Best-effort: an orphaned
                // old token simply expires on its own.
                self.revoke_token(&id).await;
            }
        }
    }

    async fn revoke_token(&self, id: &str) {
        if let Some(bearer) = self.bearer() {
            let _ = self
                .http
                .delete(self.url(&format!("/api/v1/me/tokens/{id}")))
                .bearer_auth(bearer)
                .send()
                .await;
        }
    }

    // --- Generic verbs -----------------------------------------------------

    /// The credential for one authenticated request: the inbound HTTP caller's bearer
    /// when serving over HTTP (no local rotation applies to a token we do not own),
    /// otherwise the stored stdio-mode token, rotated first if it is due.
    async fn acquire_bearer(&self) -> Result<String> {
        if let Ok(token) = HTTP_BEARER.try_with(|t| t.clone()) {
            return Ok(token);
        }
        self.ensure_fresh().await;
        self.bearer()
            .ok_or_else(|| anyhow!("not authenticated: run the `login` tool or set an access token"))
    }

    /// Authenticated `GET` returning parsed JSON.
    pub async fn get(&self, path: &str, query: &[(&str, String)]) -> Result<Value> {
        let bearer = self.acquire_bearer().await?;
        let mut req = self.http.get(self.url(path)).bearer_auth(bearer);
        let filtered: Vec<&(&str, String)> = query.iter().filter(|(_, v)| !v.is_empty()).collect();
        if !filtered.is_empty() {
            req = req.query(&filtered);
        }
        let resp = req.send().await.with_context(|| format!("GET {path} failed"))?;
        self.read_json(resp, path).await
    }

    /// Authenticated `POST` returning parsed JSON (empty body → JSON null).
    pub async fn post(&self, path: &str, body: Value) -> Result<Value> {
        let bearer = self.acquire_bearer().await?;
        let resp = self
            .http
            .post(self.url(path))
            .bearer_auth(bearer)
            .json(&body)
            .send()
            .await
            .with_context(|| format!("POST {path} failed"))?;
        self.read_json(resp, path).await
    }

    async fn read_json(&self, resp: reqwest::Response, path: &str) -> Result<Value> {
        let status = resp.status();
        if status == reqwest::StatusCode::UNAUTHORIZED {
            bail!("control plane rejected the token (401): the credential is invalid or expired");
        }
        if status == reqwest::StatusCode::FORBIDDEN {
            bail!("insufficient scope for {path} (403); this action needs a higher-privileged token");
        }
        if !status.is_success() {
            let body = resp.text().await.unwrap_or_default();
            bail!("{path} returned {status}: {body}");
        }
        let text = resp.text().await.unwrap_or_default();
        if text.trim().is_empty() {
            return Ok(Value::Null);
        }
        serde_json::from_str(&text).with_context(|| format!("{path} returned non-JSON body"))
    }
}

/// A human label for the tokens this client mints, so they are recognizable in the GUI's token list. The host name
/// is best-effort; a nameless host still yields a usable, if generic, label.
fn client_token_name() -> String {
    let host = std::env::var("COMPUTERNAME")
        .or_else(|_| std::env::var("HOSTNAME"))
        .ok()
        .filter(|h| !h.trim().is_empty())
        .unwrap_or_else(|| "host".to_string());
    format!("sqlflow-mcp ({host})")
}

/// Strip a trailing slash so `url()` concatenation is well-formed.
fn normalize_base(url: &str) -> String {
    url.trim_end_matches('/').to_string()
}
