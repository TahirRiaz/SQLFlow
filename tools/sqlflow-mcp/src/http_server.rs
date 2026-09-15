//! Streamable HTTP transport for the MCP server (`sqlflow-mcp http`).
//!
//! Hosts rmcp's [`StreamableHttpService`] behind a small hyper front end with two routes: `GET /healthz`
//! (unauthenticated readiness probe for the container platform) and `/mcp` (the MCP endpoint). Every `/mcp` request
//! must carry `Authorization: Bearer <token>`, and the token must be one the control plane accepts: it is verified
//! against `GET /api/v1/me` before the request reaches the MCP session layer (a verified token is remembered briefly,
//! see [`VerifiedBearers`]), then forwarded per tool call to the control plane (see `SqlFlowMcp::call_tool`), which
//! enforces what it may reach exactly as it does for the CLI and GUI. A missing, malformed, invalid, expired, or
//! revoked bearer is turned away at the edge, so no tool, not even the offline docs, runs for it.
//!
//! This is the transport the hosted assistants (Azure AI Foundry, OpenAI, Anthropic) connect to: each supplies a
//! SQLFlow token as a per-run `Authorization` header, so the deployed server holds no ambient credentials of its own.

use std::collections::hash_map::RandomState;
use std::collections::HashMap;
use std::hash::BuildHasher;
use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use anyhow::{Context, Result};
use bytes::Bytes;
use http::{Method, Request, Response, StatusCode};
use http_body_util::combinators::BoxBody;
use http_body_util::{BodyExt, Full};
use hyper::body::Incoming;
use hyper::service::service_fn;
use hyper_util::rt::TokioIo;
use rmcp::transport::streamable_http_server::session::local::LocalSessionManager;
use rmcp::transport::streamable_http_server::{StreamableHttpServerConfig, StreamableHttpService};

use crate::control_plane::ControlPlane;
use crate::docs::DocsIndex;
use crate::server::{bearer_token, SqlFlowMcp};

/// The MCP endpoint path clients are pointed at, e.g. `https://host/mcp`.
pub const MCP_PATH: &str = "/mcp";

/// How long a bearer the control plane accepted is trusted at the edge without asking again. Short on purpose: it
/// only saves a `GET /api/v1/me` per request within one assistant run, while every tool call is still authorized by
/// the control plane itself, so a token revoked inside this window is refused there regardless.
const VERIFIED_TTL: Duration = Duration::from_secs(60);

/// The most verified bearers remembered at once, so a flood of distinct valid tokens cannot grow the map without
/// bound. Reaching it evicts every stale entry, and if all are fresh, forgets them all (they are simply re-verified).
const VERIFIED_CAPACITY: usize = 4096;

pub struct HttpServerOptions {
    pub bind: SocketAddr,
    /// `Host`-header allowlist for DNS-rebinding protection. Empty means: keep rmcp's
    /// loopback-only default when binding a loopback address, otherwise disable the
    /// check (safe because every MCP request additionally requires a bearer token the
    /// control plane accepts, which a rebinding page cannot attach cross-origin).
    pub allowed_hosts: Vec<String>,
}

pub async fn serve(
    docs: Arc<DocsIndex>,
    cp: Arc<ControlPlane>,
    opts: HttpServerOptions,
) -> Result<()> {
    let config = if !opts.allowed_hosts.is_empty() {
        StreamableHttpServerConfig::default().with_allowed_hosts(opts.allowed_hosts.clone())
    } else if opts.bind.ip().is_loopback() {
        // Loopback bind: rmcp's localhost-only default already matches.
        StreamableHttpServerConfig::default()
    } else {
        tracing::info!(
            "Host-header validation disabled (no --allowed-hosts / SQLFLOW_MCP_HTTP_ALLOWED_HOSTS \
configured); every MCP request still requires a bearer token the control plane accepts"
        );
        StreamableHttpServerConfig::default().disable_allowed_hosts()
    };
    // Cancelling this token ends open SSE streams and in-flight sessions on shutdown.
    let cancel = config.cancellation_token.clone();

    let session_cp = cp.clone();
    let mcp_service = StreamableHttpService::new(
        move || Ok(SqlFlowMcp::new_http(docs.clone(), session_cp.clone())),
        LocalSessionManager::default().into(),
        config,
    );
    let verified = Arc::new(VerifiedBearers::new(VERIFIED_TTL, VERIFIED_CAPACITY));

    let listener = tokio::net::TcpListener::bind(opts.bind)
        .await
        .with_context(|| format!("could not bind {}", opts.bind))?;
    tracing::info!("MCP over HTTP listening on http://{}{}", opts.bind, MCP_PATH);

    loop {
        tokio::select! {
            signal = tokio::signal::ctrl_c() => {
                if let Err(e) = signal {
                    tracing::error!("shutdown signal listener failed: {e}; stopping the server");
                } else {
                    tracing::info!("shutdown signal received; closing sessions");
                }
                cancel.cancel();
                return Ok(());
            }
            accepted = listener.accept() => {
                let (stream, remote) = match accepted {
                    Ok(pair) => pair,
                    Err(e) => {
                        // Transient per-connection failure (reset during accept, FD
                        // pressure); the listener itself is still healthy.
                        tracing::warn!("accept failed: {e}");
                        continue;
                    }
                };
                let service = mcp_service.clone();
                let cp = cp.clone();
                let verified = verified.clone();
                tokio::spawn(async move {
                    let io = TokioIo::new(stream);
                    let conn = hyper::server::conn::http1::Builder::new().serve_connection(
                        io,
                        service_fn(move |req| route(service.clone(), cp.clone(), verified.clone(), req)),
                    );
                    if let Err(e) = conn.await {
                        // Client disconnects mid-SSE land here; not a server fault.
                        tracing::debug!("connection from {remote} ended: {e}");
                    }
                });
            }
        }
    }
}

async fn route(
    mcp: StreamableHttpService<SqlFlowMcp, LocalSessionManager>,
    cp: Arc<ControlPlane>,
    verified: Arc<VerifiedBearers>,
    req: Request<Incoming>,
) -> Result<Response<BoxBody<Bytes, std::convert::Infallible>>, std::convert::Infallible> {
    let path = req.uri().path();
    if req.method() == Method::GET && path == "/healthz" {
        return Ok(text(StatusCode::OK, "ok"));
    }
    if path != MCP_PATH {
        return Ok(text(
            StatusCode::NOT_FOUND,
            "not found; the MCP endpoint is /mcp",
        ));
    }
    let Some(token) = bearer_token(req.headers()) else {
        return Ok(unauthorized(
            "Bearer realm=\"sqlflow-mcp\"",
            "missing or malformed Authorization header; expected: Bearer <SQLFlow access token>",
        ));
    };

    let now = Instant::now();
    if !verified.is_fresh(&token, now) {
        match cp.validate_bearer(&token).await {
            Ok(true) => verified.remember(&token, now),
            Ok(false) => {
                return Ok(unauthorized(
                    "Bearer realm=\"sqlflow-mcp\", error=\"invalid_token\"",
                    "the SQLFlow control plane rejected this bearer token (invalid, expired, or revoked)",
                ));
            }
            Err(e) => {
                tracing::warn!("could not verify an inbound bearer with the control plane: {e:#}");
                return Ok(text(
                    StatusCode::SERVICE_UNAVAILABLE,
                    "could not verify the bearer token with the SQLFlow control plane; try again shortly",
                ));
            }
        }
    }
    Ok(mcp.handle(req).await)
}

/// Bearers the control plane accepted recently, keyed by a randomly seeded hash of the token so the raw secrets are
/// not retained. The seed is per process and unknown to callers, so a token cannot be crafted to collide with a
/// remembered one; and a collision would still only pass this edge check, never a control-plane authorization.
struct VerifiedBearers {
    hasher: RandomState,
    ttl: Duration,
    capacity: usize,
    entries: Mutex<HashMap<u64, Instant>>,
}

impl VerifiedBearers {
    fn new(ttl: Duration, capacity: usize) -> Self {
        VerifiedBearers {
            hasher: RandomState::new(),
            ttl,
            capacity,
            entries: Mutex::new(HashMap::new()),
        }
    }

    fn is_fresh(&self, token: &str, now: Instant) -> bool {
        let key = self.hasher.hash_one(token);
        self.entries
            .lock()
            .unwrap()
            .get(&key)
            .is_some_and(|verified_at| now.saturating_duration_since(*verified_at) < self.ttl)
    }

    fn remember(&self, token: &str, now: Instant) {
        let key = self.hasher.hash_one(token);
        let mut entries = self.entries.lock().unwrap();
        if entries.len() >= self.capacity && !entries.contains_key(&key) {
            entries.retain(|_, verified_at| now.saturating_duration_since(*verified_at) < self.ttl);
            if entries.len() >= self.capacity {
                entries.clear();
            }
        }
        entries.insert(key, now);
    }
}

fn unauthorized(
    challenge: &'static str,
    body: &'static str,
) -> Response<BoxBody<Bytes, std::convert::Infallible>> {
    let mut resp = text(StatusCode::UNAUTHORIZED, body);
    resp.headers_mut().insert(
        http::header::WWW_AUTHENTICATE,
        http::HeaderValue::from_static(challenge),
    );
    resp
}

fn text(status: StatusCode, body: &'static str) -> Response<BoxBody<Bytes, std::convert::Infallible>> {
    Response::builder()
        .status(status)
        .header(http::header::CONTENT_TYPE, "text/plain; charset=utf-8")
        .body(Full::new(Bytes::from_static(body.as_bytes())).boxed())
        .expect("static response construction cannot fail")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_verified_bearer_is_trusted_only_within_the_ttl() {
        let verified = VerifiedBearers::new(Duration::from_secs(60), 16);
        let start = Instant::now();
        assert!(!verified.is_fresh("token-a", start));

        verified.remember("token-a", start);
        assert!(verified.is_fresh("token-a", start + Duration::from_secs(59)));
        assert!(!verified.is_fresh("token-a", start + Duration::from_secs(60)));
    }

    #[test]
    fn verifying_one_bearer_never_admits_another() {
        let verified = VerifiedBearers::new(Duration::from_secs(60), 16);
        let now = Instant::now();
        verified.remember("token-a", now);
        assert!(!verified.is_fresh("token-b", now));
        assert!(!verified.is_fresh("", now));
    }

    #[test]
    fn a_full_map_evicts_stale_entries_before_forgetting_fresh_ones() {
        let verified = VerifiedBearers::new(Duration::from_secs(60), 2);
        let start = Instant::now();
        verified.remember("stale", start);
        verified.remember("fresh", start + Duration::from_secs(50));

        // Full; "stale" has aged out by now, so it alone is evicted to make room.
        let later = start + Duration::from_secs(70);
        verified.remember("new", later);
        assert!(!verified.is_fresh("stale", later));
        assert!(verified.is_fresh("fresh", later));
        assert!(verified.is_fresh("new", later));

        // Full again with nothing stale: everything is forgotten (and simply re-verified), never grown past capacity.
        verified.remember("newest", later);
        assert!(verified.is_fresh("newest", later));
        assert!(!verified.is_fresh("fresh", later));
        assert!(verified.entries.lock().unwrap().len() <= 2);
    }
}
