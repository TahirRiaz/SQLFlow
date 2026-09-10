//! SQLFlow language server: stdio LSP for `.flow.yaml`.
//!
//! Logging goes to stderr so it never corrupts the JSON-RPC channel on stdout.

mod server;

use tower_lsp::{LspService, Server};

#[tokio::main]
async fn main() {
    tracing_subscriber::fmt()
        .with_writer(std::io::stderr)
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_env("SQLFLOW_LSP_LOG")
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info")),
        )
        .init();

    let (service, socket) = LspService::build(server::SqlFlowLsp::new).finish();
    Server::new(tokio::io::stdin(), tokio::io::stdout(), socket)
        .serve(service)
        .await;
}
