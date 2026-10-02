//! SQLFlow MCP server entry point.
//!
//! Serves the Model Context Protocol over stdio by default, or over streamable
//! HTTP with the `http` subcommand (for remote clients such as Azure AI Foundry's
//! MCP tool). Logging goes to stderr so it never corrupts the stdio protocol
//! channel. A small `install` subcommand prints ready-to-paste registration for
//! common MCP clients.
//!
//! The server itself is the `sqlflow_mcp` library; this program is the host with no modules.

use sqlflow_mcp::McpHost;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    sqlflow_mcp::run(McpHost::sqlflow()).await
}
