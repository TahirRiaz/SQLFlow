//! SQLFlow MCP server entry point.
//!
//! Serves the Model Context Protocol over stdio by default, or over streamable
//! HTTP with the `http` subcommand (for remote clients such as Azure AI Foundry's
//! MCP tool). Logging goes to stderr so it never corrupts the stdio protocol
//! channel. A small `install` subcommand prints ready-to-paste registration for
//! common MCP clients.

mod config;
mod control_plane;
mod docs;
mod http_server;
mod links;
mod server;

use std::sync::Arc;

use anyhow::Context;
use control_plane::ControlPlane;
use docs::DocsIndex;
use rmcp::transport::stdio;
use rmcp::ServiceExt;
use server::SqlFlowMcp;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let args: Vec<String> = std::env::args().collect();
    match args.get(1).map(String::as_str) {
        Some("--version") | Some("-V") => {
            println!("sqlflow-mcp {}", env!("CARGO_PKG_VERSION"));
            return Ok(());
        }
        Some("install") => {
            print_install(args.get(2).map(String::as_str));
            return Ok(());
        }
        _ => {}
    }

    tracing_subscriber::fmt()
        .with_writer(std::io::stderr)
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_env("SQLFLOW_MCP_LOG")
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info")),
        )
        .init();

    let docs = Arc::new(DocsIndex::load());
    tracing::info!("loaded {} reference pages for {}", docs.len(), docs.product());
    let cp = Arc::new(ControlPlane::from_env());
    tracing::info!("control plane: {}", cp.base_url());
    let gui = links::GuiLinks::from_env();
    match gui.base() {
        "" => tracing::info!(
            "GUI links: root-relative (set SQLFLOW_GUI_URL to make them absolute for clients outside the GUI)"
        ),
        base => tracing::info!("GUI links: {base}"),
    }

    if args.get(1).map(String::as_str) == Some("http") {
        let opts = parse_http_options(&args[2..])?;
        return http_server::serve(docs, cp, opts).await;
    }

    let service = SqlFlowMcp::new(docs, cp).serve(stdio()).await?;
    service.waiting().await?;
    Ok(())
}

/// Options for `sqlflow-mcp http`: flags win over their environment fallbacks
/// (`SQLFLOW_MCP_HTTP_BIND`, `SQLFLOW_MCP_HTTP_ALLOWED_HOSTS`), and the default bind
/// is loopback so a bare local start is never accidentally network-exposed.
fn parse_http_options(args: &[String]) -> anyhow::Result<http_server::HttpServerOptions> {
    let mut bind: Option<String> = None;
    let mut allowed_hosts: Option<String> = None;
    let mut iter = args.iter();
    while let Some(arg) = iter.next() {
        match arg.as_str() {
            "--bind" => {
                bind = Some(
                    iter.next()
                        .context("--bind requires an address, e.g. --bind 0.0.0.0:8080")?
                        .clone(),
                );
            }
            "--allowed-hosts" => {
                allowed_hosts = Some(
                    iter.next()
                        .context("--allowed-hosts requires a comma-separated list, e.g. --allowed-hosts mcp.example.com")?
                        .clone(),
                );
            }
            other => anyhow::bail!(
                "unknown argument '{other}' for `sqlflow-mcp http`; expected --bind <addr> and/or --allowed-hosts <h1,h2>"
            ),
        }
    }
    let bind = bind
        .or_else(|| std::env::var("SQLFLOW_MCP_HTTP_BIND").ok())
        .unwrap_or_else(|| "127.0.0.1:8787".to_string());
    let bind: std::net::SocketAddr = bind
        .parse()
        .with_context(|| format!("invalid bind address '{bind}'; expected host:port, e.g. 0.0.0.0:8080"))?;
    let allowed_hosts = allowed_hosts
        .or_else(|| std::env::var("SQLFLOW_MCP_HTTP_ALLOWED_HOSTS").ok())
        .unwrap_or_default()
        .split(',')
        .map(str::trim)
        .filter(|h| !h.is_empty())
        .map(String::from)
        .collect();
    Ok(http_server::HttpServerOptions { bind, allowed_hosts })
}

fn print_install(client: Option<&str>) {
    let exe = std::env::current_exe()
        .map(|p| p.display().to_string())
        .unwrap_or_else(|_| "sqlflow-mcp".to_string());
    match client {
        Some("claude") => {
            println!("claude mcp add sqlflow -- {exe}");
        }
        Some("codex") => {
            println!("[mcp_servers.sqlflow]\ncommand = \"{exe}\"");
        }
        Some("cursor") | Some("vscode") | None => {
            println!(
                "{{\n  \"mcpServers\": {{\n    \"sqlflow\": {{\n      \"command\": \"{}\",\n      \"env\": {{ \"SQLFLOW_CONTROL_PLANE_URL\": \"http://localhost:8080\" }}\n    }}\n  }}\n}}",
                exe.replace('\\', "\\\\")
            );
        }
        Some(other) => {
            eprintln!("Unknown client '{other}'. Try: claude, cursor, codex, vscode.");
        }
    }
}
