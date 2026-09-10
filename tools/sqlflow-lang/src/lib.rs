//! SQLFlow `.flow.yaml` analysis engine.
//!
//! This crate is protocol-free: it parses a flow document and answers editor
//! questions (completion, hover, diagnostics, symbols, code actions) against
//! SQLFlow's embedded key census. The language server (`sqlflow-lsp`) wraps it
//! in LSP; the MCP server (`sqlflow-mcp`) reuses [`features::diagnostics`] for
//! its `validate_flow` tool. Keeping the engine free of `tower-lsp`/`rmcp` lets
//! both consume it, mirroring the DeltaForge protocol/analysis/parser split.

pub mod census;
pub mod context;
pub mod document;
pub mod features;
pub mod text;

pub use document::FlowDocument;
pub use text::{LineIndex, Position, Range};

/// Analyse a document and return its diagnostics in one call (used by the MCP
/// `validate_flow` tool and by tests).
pub fn analyze(source: &str) -> Vec<features::Diagnostic> {
    let doc = FlowDocument::parse(source);
    features::diagnostics(&doc)
}

/// The crate version, surfaced by the servers on startup.
pub fn version() -> &'static str {
    env!("CARGO_PKG_VERSION")
}
