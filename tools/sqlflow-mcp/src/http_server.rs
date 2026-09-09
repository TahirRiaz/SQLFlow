//! Streamable HTTP transport for the MCP server (`sqlflow-mcp http`).
//!
//! Hosts rmcp's [`StreamableHttpService`] behind a small hyper front end with two
//! routes: `GET /healthz` (unauthenticated readiness probe for the container
//! platform) and `/mcp` (the MCP endpoint). Every `/mcp` request must carry
//! `Authorization: Bearer <token>`; the token is not validated here, it is forwarded
//! per tool call to the control plane (see `SqlFlowMcp::call_tool`), which enforces
//! validity and scopes exactly as it does for the CLI and GUI. Rejecting bare
//! requests at the edge keeps unauthenticated traffic away from the MCP session
//! layer entirely.
//!
//! This is the transport Azure AI Foundry's MCP tool connects to: Foundry supplies
//! the SQLFlow personal access token as a per-run `Authorization` header, so the
//! deployed server holds no ambient credentials of its own.

use std::net::SocketAddr;
use std::sync::Arc;

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

pub struct HttpServerOptions {
    pub bind: SocketAddr,
    /// `Host`-header allowlist for DNS-rebinding protection. Empty means: keep rmcp's
    /// loopback-only default when binding a loopback address, otherwise disable the
    /// check (safe because every MCP request additionally requires a bearer token,
    /// which a rebinding page cannot attach cross-origin).
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
configured); every MCP request still requires a bearer token"
        );
        StreamableHttpServerConfig::default().disable_allowed_hosts()
    };
    // Cancelling this token ends open SSE streams and in-flight sessions on shutdown.
    let cancel = config.cancellation_token.clone();

    let mcp_service = StreamableHttpService::new(
        move || Ok(SqlFlowMcp::new_http(docs.clone(), cp.clone())),
        LocalSessionManager::default().into(),
        config,
    );

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
                tokio::spawn(async move {
                    let io = TokioIo::new(stream);
                    let conn = hyper::server::conn::http1::Builder::new().serve_connection(
                        io,
                        service_fn(move |req| route(service.clone(), req)),
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
    if bearer_token(req.headers()).is_none() {
        let mut resp = text(
            StatusCode::UNAUTHORIZED,
            "missing or malformed Authorization header; expected: Bearer <SQLFlow access token>",
        );
        resp.headers_mut().insert(
            http::header::WWW_AUTHENTICATE,
            http::HeaderValue::from_static("Bearer realm=\"sqlflow-mcp\""),
        );
        return Ok(resp);
    }
    Ok(mcp.handle(req).await)
}

fn text(status: StatusCode, body: &'static str) -> Response<BoxBody<Bytes, std::convert::Infallible>> {
    Response::builder()
        .status(status)
        .header(http::header::CONTENT_TYPE, "text/plain; charset=utf-8")
        .body(Full::new(Bytes::from_static(body.as_bytes())).boxed())
        .expect("static response construction cannot fail")
}
