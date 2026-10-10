//! HTTP client for the SQLFlow control plane (`/api/v1`).
//!
//! Read tools (catalog, lineage, runs, schedules, search, summary) proxy the control plane's authenticated read
//! surface; the trigger/cancel tools use the operate surface. Over stdio, authentication is an OAuth 2.0
//! device-authorization grant (RFC 8628) against the endpoints in `AuthEndpoints.cs`, with a token paste
//! (`set_access_token` / `SQLFLOW_CONTROL_PLANE_TOKEN`) as a fallback, and the resulting interactive session is kept
//! alive by [`ControlPlane::spawn_session_keeper`], which rolls it at `POST /api/v1/auth/renew` on the GUI's own
//! schedule. Over HTTP this process holds no credential at all: every call runs as the bearer on the inbound request.

use crate::config::{self, TokenCache};
use anyhow::{anyhow, bail, Context, Result};
use serde::Deserialize;
use serde_json::{json, Value};
use std::sync::{Arc, RwLock};
use std::time::Duration;

tokio::task_local! {
    /// The bearer token of the inbound request when the MCP server runs over HTTP. The HTTP dispatch
    /// (`SqlFlowMcp::call_tool`) scopes it around each tool call, so every control-plane request runs as the
    /// connecting caller. It is never set over stdio, and over HTTP it is the only credential there is: the server
    /// loads no token of its own, so a call without it fails rather than falling back to one.
    pub static HTTP_BEARER: String;

    /// The client surface the inbound HTTP request declared through the MCP URL's query
    /// (`?surface=assistant`), scoped by the HTTP dispatch beside `HTTP_BEARER`. While present it is
    /// sent to the control plane as `SURFACE_HEADER`, which then serves only the semantic layer. It
    /// narrows what is shown and never widens it, so honouring the query is safe without authentication.
    pub static HTTP_SURFACE: String;
}

/// The control-plane request header carrying the client surface (`AssistantScope.HeaderName`).
pub const SURFACE_HEADER: &str = "X-SqlFlow-Surface";

/// The assistant surface's value, in both the MCP URL query and `SURFACE_HEADER`
/// (`AssistantScope.AssistantValue`, `McpOptions.AssistantSurfaceQuery`).
pub const ASSISTANT_SURFACE: &str = "assistant";

/// Adds the surface header to a control-plane request when the current tool call has one.
fn with_surface(req: reqwest::RequestBuilder) -> reqwest::RequestBuilder {
    match HTTP_SURFACE.try_with(|surface| surface.clone()) {
        Ok(surface) => req.header(SURFACE_HEADER, surface),
        Err(_) => req,
    }
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

/// Outcome of asking the control plane who the stored credential authenticates as.
pub enum WhoAmI {
    Authenticated {
        subject: String,
        role: Option<String>,
        scopes: Vec<String>,
    },
    Unauthenticated,
}

/// `GET /api/v1/me`: the caller's own identity, the cheapest authoritative "whoami" the control plane offers.
#[derive(Deserialize)]
struct IdentityResponse {
    subject: String,
    role: Option<String>,
    #[serde(default)]
    scopes: Vec<String>,
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

/// The `POST /api/v1/auth/renew` response: the control plane's `SessionResponse`, camelCase on the wire.
#[derive(Deserialize)]
struct RenewedSession {
    #[serde(rename = "accessToken")]
    access_token: String,
    #[serde(rename = "expiresIn")]
    expires_in: i64,
    #[serde(default)]
    scopes: Vec<String>,
}

/// What one pass of [`ControlPlane::renew_if_due`] did.
pub enum RenewOutcome {
    /// Nothing to do: no renewable session, or it is not yet inside the renewal lead.
    NotDue,
    /// The session was rolled onto a fresh token.
    Renewed,
    /// Another process sharing the token file had already rolled the session; its newer token was adopted.
    AdoptedNewer,
    /// The control plane refused to renew (the absolute session cap was reached, or the account was deactivated or
    /// removed). The session was cleared and needs a fresh sign-in; carries the control plane's reason.
    Ended(String),
    /// The control plane says this credential does not renew. It is kept and used until it expires.
    NotRenewable,
    /// The renewal could not complete (network, a server error). The token in hand still works until it expires, so
    /// the next pass retries.
    Failed(anyhow::Error),
}

/// How often the background session keeper checks whether the stored session needs rolling. Matches the GUI's
/// `RENEW_RETRY_MS`: a failed renewal is retried several times inside the renewal lead, and because each pass reads
/// the wall clock afresh, a laptop waking from sleep catches up on its next tick instead of trusting a stale timer.
const SESSION_KEEPER_TICK: Duration = Duration::from_secs(60);

pub struct ControlPlane {
    http: reqwest::Client,
    base_url: RwLock<String>,
    token: RwLock<Option<TokenCache>>,
    /// The device code of an in-flight `login`, so `check_auth_status` can poll without the caller re-supplying it.
    pending_device_code: RwLock<Option<String>>,
    /// Whether this process may hold and present a credential of its own (stdio). False over HTTP, where the only
    /// credential is the one on each inbound request, so a missing bearer can never fall back to a stored one.
    ambient_credentials: bool,
    /// Why the stored session ended, when the control plane refused to renew it, so the next status check or tool
    /// call can say so instead of a bare "not authenticated".
    session_ended: RwLock<Option<String>>,
    /// Single-flights session renewal, so concurrent requests entering the renewal lead roll the session once.
    renew_lock: tokio::sync::Mutex<()>,
}

impl ControlPlane {
    /// Build the client from the persisted config (overridden by `SQLFLOW_CONTROL_PLANE_URL`). With
    /// `ambient_credentials` (stdio) the persisted token is loaded too, overridden by a live
    /// `SQLFLOW_CONTROL_PLANE_TOKEN`; without it (HTTP) no token is loaded from anywhere.
    pub fn from_env(ambient_credentials: bool) -> Self {
        let cfg = config::load_config();
        let base_url = std::env::var("SQLFLOW_CONTROL_PLANE_URL")
            .ok()
            .or(cfg.control_plane_url)
            .unwrap_or_else(|| config::DEFAULT_URL.to_string());
        let token = if ambient_credentials {
            std::env::var("SQLFLOW_CONTROL_PLANE_TOKEN")
                .ok()
                .map(|t| TokenCache {
                    // A session JWT states its own lifetime; read it rather than assuming the credential never
                    // lapses. Never renewed: the environment cannot be rewritten, and persisting a rolled copy would
                    // overwrite whatever sign-in the token file holds.
                    expires_at: config::jwt_expiry(&t),
                    access_token: t,
                    scope: "read operate author".to_string(),
                    renews: false,
                })
                // A lapsed environment credential must not shadow the stored one, or every restart would send the
                // user back to the sign-in they just completed, with no way out but editing the environment.
                .filter(|t| !t.is_expired(config::EXPIRY_FLOOR_SECS))
                .or_else(config::load_token)
        } else {
            None
        };
        ControlPlane {
            http: reqwest::Client::builder()
                .user_agent(concat!("sqlflow-mcp/", env!("CARGO_PKG_VERSION")))
                .build()
                .expect("failed to build HTTP client"),
            base_url: RwLock::new(normalize_base(&base_url)),
            token: RwLock::new(token),
            pending_device_code: RwLock::new(None),
            ambient_credentials,
            session_ended: RwLock::new(None),
            renew_lock: tokio::sync::Mutex::new(()),
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
            .map(|t| !t.is_expired(config::EXPIRY_FLOOR_SECS))
            .unwrap_or(false)
    }

    /// Whether a credential is stored at all, regardless of its cached expiry. Used to word `check_auth_status`
    /// accurately when [`whoami`](Self::whoami) reports the control plane rejected it.
    pub fn has_token(&self) -> bool {
        self.token.read().unwrap().is_some()
    }

    /// Why the stored session ended, when the control plane refused to renew it.
    pub fn session_ended_reason(&self) -> Option<String> {
        self.session_ended.read().unwrap().clone()
    }

    pub fn set_token(&self, token: TokenCache) {
        let _ = config::save_token(&token);
        *self.token.write().unwrap() = Some(token);
        *self.session_ended.write().unwrap() = None;
    }

    pub fn clear_token(&self) {
        let _ = config::clear_token();
        *self.token.write().unwrap() = None;
        *self.session_ended.write().unwrap() = None;
    }

    fn current_token(&self) -> Option<TokenCache> {
        self.token.read().unwrap().clone()
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

    /// Ask the control plane whether the stored credential is actually valid, rather than trusting the locally
    /// cached expiry: a token can be revoked, or the catalog row backing it can disappear (a local-dev database
    /// reset), while the cached `expires_at` is still comfortably in the future. Renews first if the session is due,
    /// then calls `GET /api/v1/me`. A 401 is reported as `Unauthenticated`, never as an error, so a stale credential
    /// surfaces as truthful status rather than a failed request.
    pub async fn whoami(&self) -> Result<WhoAmI> {
        if let RenewOutcome::Failed(e) = self.renew_if_due().await {
            tracing::warn!("session renewal before whoami failed; checking the current token: {e:#}");
        }
        let Some(token) = self.current_token() else {
            return Ok(WhoAmI::Unauthenticated);
        };
        let resp = self
            .http
            .get(self.url("/api/v1/me"))
            .bearer_auth(&token.access_token)
            .send()
            .await
            .context("could not reach the control plane")?;
        if resp.status() == reqwest::StatusCode::UNAUTHORIZED {
            return Ok(WhoAmI::Unauthenticated);
        }
        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            bail!("GET /api/v1/me returned {status}: {body}");
        }
        let identity: IdentityResponse = resp.json().await.context("invalid identity response")?;
        Ok(WhoAmI::Authenticated {
            subject: identity.subject,
            role: identity.role,
            scopes: identity.scopes,
        })
    }

    /// Whether the control plane accepts `token` as a bearer (`GET /api/v1/me`). Used by the HTTP transport to turn
    /// away a request whose bearer is invalid, expired, or revoked before any tool runs. A 401 or 403 is a definite
    /// "no"; anything else that is not a success is an error, since it says nothing about the token.
    pub async fn validate_bearer(&self, token: &str) -> Result<bool> {
        let resp = self
            .http
            .get(self.url("/api/v1/me"))
            .bearer_auth(token)
            .send()
            .await
            .context("could not reach the control plane to verify the bearer")?;
        match resp.status() {
            status if status.is_success() => Ok(true),
            reqwest::StatusCode::UNAUTHORIZED | reqwest::StatusCode::FORBIDDEN => Ok(false),
            other => bail!("GET /api/v1/me returned {other} while verifying a bearer"),
        }
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

    /// Poll once for the device token. An approved token is stored as is: when the approving browser session was an
    /// interactive one, the token carries its `auth_time` and the session keeper rolls it from then on.
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
            let mut cache = TokenCache::from_bearer(tok.access_token, tok.scope);
            if cache.expires_at.is_none() {
                cache.expires_at = tok
                    .expires_in
                    .map(|s| chrono::Utc::now() + chrono::Duration::seconds(s));
            }
            self.set_token(cache.clone());
            *self.pending_device_code.write().unwrap() = None;
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

    // --- Session renewal ---------------------------------------------------

    /// Roll the stored session onto a fresh token if it has entered the renewal lead, exactly as the GUI rolls its
    /// own. Cheap and a no-op when nothing is due (the common case), so it is safe to call before every request.
    /// Single-flighted: a caller that waits on an in-flight renewal re-reads the result instead of renewing again.
    pub async fn renew_if_due(&self) -> RenewOutcome {
        if !self.ambient_credentials {
            return RenewOutcome::NotDue;
        }
        let _guard = self.renew_lock.lock().await;
        let now = chrono::Utc::now();
        let Some(current) = self.current_token() else {
            return RenewOutcome::NotDue;
        };
        if !current.renewal_due(now) {
            return RenewOutcome::NotDue;
        }

        // Another sqlflow-mcp process (a second editor window) shares the token file and may already have rolled the
        // session. Renewing the older token again would work, but adopting the newer one keeps the processes on one
        // token instead of each holding its own.
        if let Some(disk) = config::load_token() {
            if disk.renews && disk.access_token != current.access_token && disk.expires_at > current.expires_at {
                let still_due = disk.renewal_due(now);
                *self.token.write().unwrap() = Some(disk);
                if !still_due {
                    return RenewOutcome::AdoptedNewer;
                }
            }
        }
        let Some(current) = self.current_token() else {
            return RenewOutcome::NotDue;
        };

        let resp = match self
            .http
            .post(self.url("/api/v1/auth/renew"))
            .bearer_auth(&current.access_token)
            .send()
            .await
        {
            Ok(resp) => resp,
            Err(e) => {
                return RenewOutcome::Failed(
                    anyhow!(e).context("could not reach the control plane to renew the session"),
                )
            }
        };

        let status = resp.status();
        if status.is_success() {
            return match resp.json::<RenewedSession>().await {
                Ok(session) => {
                    let scope = if session.scopes.is_empty() {
                        current.scope
                    } else {
                        session.scopes.join(" ")
                    };
                    self.set_token(TokenCache {
                        expires_at: Some(chrono::Utc::now() + chrono::Duration::seconds(session.expires_in)),
                        access_token: session.access_token,
                        scope,
                        renews: true,
                    });
                    RenewOutcome::Renewed
                }
                Err(e) => RenewOutcome::Failed(anyhow!(e).context("invalid session renewal response")),
            };
        }

        let body = resp.text().await.unwrap_or_default();
        match status {
            reqwest::StatusCode::UNAUTHORIZED => {
                let reason =
                    problem_detail(&body).unwrap_or_else(|| "the control plane refused to renew the session".to_string());
                self.clear_token();
                *self.session_ended.write().unwrap() = Some(reason.clone());
                RenewOutcome::Ended(reason)
            }
            reqwest::StatusCode::FORBIDDEN => {
                self.set_token(TokenCache { renews: false, ..current });
                RenewOutcome::NotRenewable
            }
            other => RenewOutcome::Failed(anyhow!("session renewal returned {other}: {body}")),
        }
    }

    /// Start the background task that keeps the stored session alive for as long as this process runs: every
    /// [`SESSION_KEEPER_TICK`] it renews the session if it is due and logs what happened. Deliberately detached, not
    /// awaited: it has no end state of its own and lives exactly as long as the stdio server, whose runtime shutdown
    /// cancels it. Requests renew on demand as well, so a tick that fires late never lets a token lapse mid-call.
    pub fn spawn_session_keeper(self: Arc<Self>) {
        tokio::spawn(async move {
            loop {
                match self.renew_if_due().await {
                    RenewOutcome::NotDue => {}
                    RenewOutcome::Renewed => tracing::info!("renewed the stored control-plane session"),
                    RenewOutcome::AdoptedNewer => {
                        tracing::info!("adopted the session another sqlflow-mcp process had already renewed")
                    }
                    RenewOutcome::Ended(reason) => tracing::warn!(
                        "the stored control-plane session ended ({reason}); sign in again with the login tool"
                    ),
                    RenewOutcome::NotRenewable => tracing::warn!(
                        "the control plane does not renew the stored credential; it is used until it expires"
                    ),
                    RenewOutcome::Failed(e) => tracing::warn!(
                        "session renewal failed, retrying in {}s: {e:#}",
                        SESSION_KEEPER_TICK.as_secs()
                    ),
                }
                tokio::time::sleep(SESSION_KEEPER_TICK).await;
            }
        });
    }

    // --- Generic verbs -----------------------------------------------------

    /// The credential for one authenticated request: the inbound HTTP caller's bearer when serving over HTTP (the
    /// only credential there is in that mode), otherwise the stored stdio-mode session, renewed first if it is due.
    async fn acquire_bearer(&self) -> Result<String> {
        if let Ok(token) = HTTP_BEARER.try_with(|t| t.clone()) {
            return Ok(token);
        }
        if !self.ambient_credentials {
            bail!("no bearer on this request: over HTTP every call must carry the caller's own Authorization header");
        }
        if let RenewOutcome::Failed(e) = self.renew_if_due().await {
            tracing::warn!("session renewal before a request failed; using the current token: {e:#}");
        }
        let Some(token) = self.current_token() else {
            return Err(match self.session_ended_reason() {
                Some(reason) => anyhow!("the stored session ended ({reason}); run the `login` tool to sign in again"),
                None => anyhow!("not authenticated: run the `login` tool or set an access token"),
            });
        };
        if token.is_expired(0) {
            let when = token.expires_at.map(|e| format!(" at {e}")).unwrap_or_default();
            bail!("the stored credential expired{when}; run the `login` tool to sign in again");
        }
        Ok(token.access_token)
    }

    /// Authenticated `GET` returning parsed JSON.
    pub async fn get(&self, path: &str, query: &[(&str, String)]) -> Result<Value> {
        let bearer = self.acquire_bearer().await?;
        let mut req = with_surface(self.http.get(self.url(path)).bearer_auth(bearer));
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
        let resp = with_surface(self.http.post(self.url(path)).bearer_auth(bearer))
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
            let body = resp.text().await.unwrap_or_default();
            let detail = problem_detail(&body).unwrap_or_else(|| "this credential is not permitted to use it".to_string());
            bail!("the control plane refused {path} (403): {detail}");
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

/// The human-readable `detail` (or, failing that, `title`) of an RFC 7807 problem-details body, when the body is one.
fn problem_detail(body: &str) -> Option<String> {
    let problem: Value = serde_json::from_str(body).ok()?;
    ["detail", "title"]
        .iter()
        .find_map(|field| problem.get(field)?.as_str().map(str::trim).filter(|s| !s.is_empty()))
        .map(str::to_string)
}

/// Strip a trailing slash so `url()` concatenation is well-formed.
fn normalize_base(url: &str) -> String {
    url.trim_end_matches('/').to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_the_reason_a_problem_details_body_gives() {
        let body = r#"{"type":"about:blank","title":"Session has reached its maximum age","status":401,
            "detail":"A session renews for at most 30 days after signing in; sign in again to continue."}"#;
        assert_eq!(
            problem_detail(body).as_deref(),
            Some("A session renews for at most 30 days after signing in; sign in again to continue.")
        );
    }

    #[test]
    fn falls_back_to_the_title_and_ignores_non_problem_bodies() {
        assert_eq!(problem_detail(r#"{"title":"Account is no longer active","detail":"  "}"#).as_deref(),
            Some("Account is no longer active"));
        assert!(problem_detail("").is_none());
        assert!(problem_detail("<html>bad gateway</html>").is_none());
        assert!(problem_detail(r#"{"status":401}"#).is_none());
    }

    #[tokio::test]
    async fn an_http_mode_client_holds_no_credential_and_refuses_a_call_without_a_bearer() {
        let cp = ControlPlane::from_env(false);
        assert!(!cp.has_token());
        let err = cp.get("/api/v1/repos", &[]).await.unwrap_err();
        assert!(err.to_string().contains("no bearer on this request"), "{err:#}");
    }
}
