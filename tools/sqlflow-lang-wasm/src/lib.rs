//! WebAssembly bindings for `sqlflow-lang`.
//!
//! The browser GUI cannot spawn the stdio `sqlflow-lsp` process, so it runs the
//! same analysis engine directly in a Monaco web worker via this crate. Every
//! function parses the document and delegates to a `sqlflow_lang::features`
//! call, returning the result as a JSON string the worker parses. Positions are
//! the engine's LSP form (zero-based line, UTF-16 character); the GUI converts
//! them to Monaco's one-based positions.
//!
//! This is a third binding onto the one engine, alongside `sqlflow-lsp` (LSP)
//! and `sqlflow-mcp` (validate_flow). It adds no analysis logic of its own.

use serde::Serialize;
use sqlflow_lang::features::{self, SemanticTokenKind, Severity};
use sqlflow_lang::text::{Position, Range};
use sqlflow_lang::FlowDocument;
use wasm_bindgen::prelude::*;

#[derive(Serialize)]
struct HoverDto {
    markdown: String,
    range: Option<Range>,
}

#[derive(Serialize)]
struct DiagnosticDto {
    range: Range,
    severity: &'static str,
    message: String,
    code: Option<String>,
}

#[derive(Serialize)]
struct SemanticTokenDto {
    range: Range,
    kind: &'static str,
}

fn severity_str(s: Severity) -> &'static str {
    match s {
        Severity::Error => "error",
        Severity::Warning => "warning",
        Severity::Information => "information",
        Severity::Hint => "hint",
    }
}

fn kind_str(k: SemanticTokenKind) -> &'static str {
    match k {
        SemanticTokenKind::Property => "property",
        SemanticTokenKind::UnknownKey => "unknownKey",
        SemanticTokenKind::EnumMember => "enumMember",
        SemanticTokenKind::InvalidValue => "invalidValue",
    }
}

/// Hover documentation for the attribute at `(line, character)`, as a JSON
/// `{ markdown, range }` object, or `null` when nothing is documented there.
#[wasm_bindgen]
pub fn hover(source: &str, line: u32, character: u32) -> Option<String> {
    let doc = FlowDocument::parse(source);
    features::hover(&doc, Position { line, character }).map(|h| {
        let dto = HoverDto {
            markdown: h.markdown,
            range: h.range,
        };
        // Serializing a struct of owned strings and plain integers cannot fail.
        serde_json::to_string(&dto).unwrap_or_default()
    })
}

/// All diagnostics for the document, as a JSON array of
/// `{ range, severity, message, code }`.
#[wasm_bindgen]
pub fn diagnostics(source: &str) -> String {
    let doc = FlowDocument::parse(source);
    let dtos: Vec<DiagnosticDto> = features::diagnostics(&doc)
        .into_iter()
        .map(|d| DiagnosticDto {
            range: d.range,
            severity: severity_str(d.severity),
            message: d.message,
            code: d.code,
        })
        .collect();
    serde_json::to_string(&dtos).unwrap_or_else(|_| "[]".to_string())
}

/// Census-driven semantic tokens, as a JSON array of `{ range, kind }`, sorted
/// by position. The GUI builds Monaco's delta-encoded token array from this.
#[wasm_bindgen]
pub fn semantic_tokens(source: &str) -> String {
    let doc = FlowDocument::parse(source);
    let dtos: Vec<SemanticTokenDto> = features::semantic_tokens(&doc)
        .into_iter()
        .map(|t| SemanticTokenDto {
            range: t.range,
            kind: kind_str(t.kind),
        })
        .collect();
    serde_json::to_string(&dtos).unwrap_or_else(|_| "[]".to_string())
}

/// The engine version, so the GUI can surface which analysis build is loaded.
#[wasm_bindgen]
pub fn version() -> String {
    sqlflow_lang::version().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    // These lock the JSON contract the GUI worker parses. The bindings compile
    // as ordinary functions on the host target, so they are testable directly.

    #[test]
    fn hover_returns_markdown_json_for_a_known_key() {
        let src = "name: demo\nsource:\n  type: csv\n";
        // Cursor on the 't' of "type" (line 2, character 2).
        let json = hover(src, 2, 2).expect("hover over a documented key");
        let parsed: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert!(parsed["markdown"].as_str().unwrap().contains("source.type"));
        assert!(parsed["range"]["start"]["line"].is_number());
    }

    #[test]
    fn hover_is_none_off_any_key() {
        // Column far past the content resolves to nothing documented.
        assert!(hover("name: demo\n", 0, 40).is_none());
    }

    #[test]
    fn diagnostics_flag_unknown_key() {
        let json = diagnostics("name: demo\nbogusKey: 1\n");
        let arr: Vec<serde_json::Value> = serde_json::from_str(&json).unwrap();
        assert!(arr.iter().any(|d| d["code"] == "flow-unknown-key"));
        assert!(arr.iter().all(|d| d["severity"].is_string()));
    }

    #[test]
    fn semantic_tokens_expose_kind_and_range() {
        let json = semantic_tokens("name: demo\nload:\n  mode: append\n");
        let arr: Vec<serde_json::Value> = serde_json::from_str(&json).unwrap();
        assert!(arr.iter().any(|t| t["kind"] == "enumMember"));
        assert!(arr.iter().all(|t| t["range"]["start"]["character"].is_number()));
    }

    #[test]
    fn version_is_non_empty() {
        assert!(!version().is_empty());
    }
}
