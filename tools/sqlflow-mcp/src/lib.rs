//! The SQLFlow MCP server.
//!
//! Serves the Model Context Protocol over stdio by default, or over streamable
//! HTTP with the `http` subcommand (for remote clients such as Azure AI Foundry's
//! MCP tool). Logging goes to stderr so it never corrupts the stdio protocol
//! channel. A small `install` subcommand prints ready-to-paste registration for
//! common MCP clients.
//!
//! It is a library so that a host can compose it, as a host composes the control plane and the CLI:
//! `sqlflow-mcp` is [`run`] with [`McpHost::sqlflow`], and a product built on SQLFlow runs the same
//! server under its own name with the modules it adds ([`McpModule`]): tools, instructions, reference
//! pages, GUI link rules and key census files of its own, beside everything defined here.

pub mod config;
pub mod control_plane;
pub mod docs;
pub mod host;
pub mod http_server;
pub mod links;
pub mod module;
pub mod server;

pub use docs::{DocMeta, DocPage};
pub use host::{run, McpHost};
pub use links::{encode, GuiLinks, LinkRule};
pub use module::{McpContext, McpModule};
pub use server::{done, json_str, EmptyInput};

/// The MCP SDK this server is built on, for a host module to define its tools with: the `#[tool]` and
/// `#[tool_router]` macros, `Parameters`, and `schemars` for its input types. A module names the same
/// version by depending on this crate alone.
pub use rmcp;
