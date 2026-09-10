//! The LSP protocol layer over `sqlflow-lang`.
//!
//! Thin by design: it holds a per-document source cache, and every handler
//! delegates to a `sqlflow_lang::features` function, translating the engine's
//! protocol-free result types into `lsp_types`. All position math lives in the
//! engine; this file only reshapes data.

use std::collections::HashMap;
use std::sync::Arc;

use sqlflow_lang::features::{
    self, CompletionKind, SemanticTokenKind, Severity, SymbolKind as EngineSymbolKind,
};
use sqlflow_lang::text::{Position as EPos, Range as ERange};
use sqlflow_lang::FlowDocument;
use tokio::sync::Mutex;
use tower_lsp::jsonrpc::Result as RpcResult;
use tower_lsp::lsp_types::*;
use tower_lsp::{Client, LanguageServer};

pub struct SqlFlowLsp {
    client: Client,
    /// URI → current full source text.
    documents: Arc<Mutex<HashMap<Url, String>>>,
}

impl SqlFlowLsp {
    pub fn new(client: Client) -> Self {
        SqlFlowLsp {
            client,
            documents: Arc::new(Mutex::new(HashMap::new())),
        }
    }

    async fn source_of(&self, uri: &Url) -> Option<String> {
        self.documents.lock().await.get(uri).cloned()
    }

    /// Parse the document and publish its diagnostics.
    async fn refresh_diagnostics(&self, uri: Url, version: Option<i32>) {
        let source = match self.source_of(&uri).await {
            Some(s) => s,
            None => return,
        };
        let doc = FlowDocument::parse(&source);
        let diags = features::diagnostics(&doc)
            .into_iter()
            .map(to_lsp_diagnostic)
            .collect::<Vec<_>>();
        self.client
            .publish_diagnostics(uri, diags, version)
            .await;
    }
}

#[tower_lsp::async_trait]
impl LanguageServer for SqlFlowLsp {
    async fn initialize(&self, _: InitializeParams) -> RpcResult<InitializeResult> {
        Ok(InitializeResult {
            server_info: Some(ServerInfo {
                name: "sqlflow-lsp".to_string(),
                version: Some(sqlflow_lang::version().to_string()),
            }),
            capabilities: ServerCapabilities {
                text_document_sync: Some(TextDocumentSyncCapability::Kind(
                    TextDocumentSyncKind::FULL,
                )),
                completion_provider: Some(CompletionOptions {
                    trigger_characters: Some(vec![
                        ":".to_string(),
                        " ".to_string(),
                        "-".to_string(),
                    ]),
                    resolve_provider: Some(false),
                    ..Default::default()
                }),
                hover_provider: Some(HoverProviderCapability::Simple(true)),
                document_symbol_provider: Some(OneOf::Left(true)),
                code_action_provider: Some(CodeActionProviderCapability::Simple(true)),
                semantic_tokens_provider: Some(
                    SemanticTokensServerCapabilities::SemanticTokensOptions(SemanticTokensOptions {
                        legend: SemanticTokensLegend {
                            token_types: semantic_token_legend(),
                            token_modifiers: Vec::new(),
                        },
                        full: Some(SemanticTokensFullOptions::Bool(true)),
                        range: Some(false),
                        ..Default::default()
                    }),
                ),
                ..Default::default()
            },
        })
    }

    async fn initialized(&self, _: InitializedParams) {
        self.client
            .log_message(MessageType::INFO, "SQLFlow language server ready")
            .await;
    }

    async fn shutdown(&self) -> RpcResult<()> {
        Ok(())
    }

    async fn did_open(&self, params: DidOpenTextDocumentParams) {
        let uri = params.text_document.uri.clone();
        self.documents
            .lock()
            .await
            .insert(uri.clone(), params.text_document.text);
        self.refresh_diagnostics(uri, Some(params.text_document.version))
            .await;
    }

    async fn did_change(&self, mut params: DidChangeTextDocumentParams) {
        // FULL sync: the last change contains the whole document.
        if let Some(change) = params.content_changes.pop() {
            let uri = params.text_document.uri.clone();
            self.documents
                .lock()
                .await
                .insert(uri.clone(), change.text);
            self.refresh_diagnostics(uri, Some(params.text_document.version))
                .await;
        }
    }

    async fn did_close(&self, params: DidCloseTextDocumentParams) {
        let uri = params.text_document.uri;
        self.documents.lock().await.remove(&uri);
        self.client.publish_diagnostics(uri, Vec::new(), None).await;
    }

    async fn completion(&self, params: CompletionParams) -> RpcResult<Option<CompletionResponse>> {
        let uri = params.text_document_position.text_document.uri;
        let Some(source) = self.source_of(&uri).await else {
            return Ok(None);
        };
        let doc = FlowDocument::parse(&source);
        let pos = from_lsp_pos(params.text_document_position.position);
        let items = features::completion(&doc, pos)
            .into_iter()
            .map(to_lsp_completion)
            .collect::<Vec<_>>();
        Ok(Some(CompletionResponse::Array(items)))
    }

    async fn hover(&self, params: HoverParams) -> RpcResult<Option<Hover>> {
        let uri = params.text_document_position_params.text_document.uri;
        let Some(source) = self.source_of(&uri).await else {
            return Ok(None);
        };
        let doc = FlowDocument::parse(&source);
        let pos = from_lsp_pos(params.text_document_position_params.position);
        Ok(features::hover(&doc, pos).map(|h| Hover {
            contents: HoverContents::Markup(MarkupContent {
                kind: MarkupKind::Markdown,
                value: h.markdown,
            }),
            range: h.range.map(to_lsp_range),
        }))
    }

    async fn document_symbol(
        &self,
        params: DocumentSymbolParams,
    ) -> RpcResult<Option<DocumentSymbolResponse>> {
        let uri = params.text_document.uri;
        let Some(source) = self.source_of(&uri).await else {
            return Ok(None);
        };
        let doc = FlowDocument::parse(&source);
        let symbols = features::document_symbols(&doc)
            .into_iter()
            .map(to_lsp_symbol)
            .collect::<Vec<_>>();
        Ok(Some(DocumentSymbolResponse::Nested(symbols)))
    }

    async fn code_action(
        &self,
        params: CodeActionParams,
    ) -> RpcResult<Option<CodeActionResponse>> {
        let uri = params.text_document.uri.clone();
        let Some(source) = self.source_of(&uri).await else {
            return Ok(None);
        };
        let doc = FlowDocument::parse(&source);
        let range = from_lsp_range(params.range);
        let actions = features::code_actions(&doc, range)
            .into_iter()
            .map(|a| {
                let mut changes = HashMap::new();
                changes.insert(
                    uri.clone(),
                    a.edits
                        .into_iter()
                        .map(|e| TextEdit {
                            range: to_lsp_range(e.range),
                            new_text: e.new_text,
                        })
                        .collect(),
                );
                CodeActionOrCommand::CodeAction(CodeAction {
                    title: a.title,
                    kind: Some(CodeActionKind::REFACTOR),
                    edit: Some(WorkspaceEdit {
                        changes: Some(changes),
                        ..Default::default()
                    }),
                    ..Default::default()
                })
            })
            .collect::<Vec<_>>();
        Ok(Some(actions))
    }

    async fn semantic_tokens_full(
        &self,
        params: SemanticTokensParams,
    ) -> RpcResult<Option<SemanticTokensResult>> {
        let uri = params.text_document.uri;
        let Some(source) = self.source_of(&uri).await else {
            return Ok(None);
        };
        let doc = FlowDocument::parse(&source);
        let data = encode_semantic_tokens(features::semantic_tokens(&doc));
        Ok(Some(SemanticTokensResult::Tokens(SemanticTokens {
            result_id: None,
            data,
        })))
    }
}

// --- Conversions -----------------------------------------------------------

fn from_lsp_pos(p: Position) -> EPos {
    EPos {
        line: p.line,
        character: p.character,
    }
}

fn to_lsp_pos(p: EPos) -> Position {
    Position {
        line: p.line,
        character: p.character,
    }
}

fn from_lsp_range(r: Range) -> ERange {
    ERange::new(from_lsp_pos(r.start), from_lsp_pos(r.end))
}

fn to_lsp_range(r: ERange) -> Range {
    Range {
        start: to_lsp_pos(r.start),
        end: to_lsp_pos(r.end),
    }
}

fn to_lsp_diagnostic(d: features::Diagnostic) -> Diagnostic {
    Diagnostic {
        range: to_lsp_range(d.range),
        severity: Some(match d.severity {
            Severity::Error => DiagnosticSeverity::ERROR,
            Severity::Warning => DiagnosticSeverity::WARNING,
            Severity::Information => DiagnosticSeverity::INFORMATION,
            Severity::Hint => DiagnosticSeverity::HINT,
        }),
        code: d.code.map(NumberOrString::String),
        source: Some("sqlflow".to_string()),
        message: d.message,
        ..Default::default()
    }
}

fn to_lsp_completion(c: features::CompletionItem) -> CompletionItem {
    CompletionItem {
        label: c.label,
        kind: Some(match c.kind {
            CompletionKind::Field => CompletionItemKind::FIELD,
            CompletionKind::EnumMember => CompletionItemKind::ENUM_MEMBER,
            CompletionKind::Snippet => CompletionItemKind::SNIPPET,
        }),
        detail: c.detail,
        documentation: c.documentation.map(|d| {
            Documentation::MarkupContent(MarkupContent {
                kind: MarkupKind::Markdown,
                value: d,
            })
        }),
        insert_text: Some(c.insert_text),
        insert_text_format: Some(if c.is_snippet {
            InsertTextFormat::SNIPPET
        } else {
            InsertTextFormat::PLAIN_TEXT
        }),
        sort_text: Some(c.sort_text),
        ..Default::default()
    }
}

/// The semantic-token legend, in the index order the encoder relies on. The two
/// SQLFlow-specific roles (unknown key, invalid value) are custom types the
/// VSCode extension themes; the other two are standard.
fn semantic_token_legend() -> Vec<SemanticTokenType> {
    vec![
        SemanticTokenType::PROPERTY,          // 0: Property
        SemanticTokenType::new("unknownKey"), // 1: UnknownKey
        SemanticTokenType::ENUM_MEMBER,       // 2: EnumMember
        SemanticTokenType::new("invalidValue"), // 3: InvalidValue
    ]
}

/// Index of a token kind into [`semantic_token_legend`].
fn semantic_token_type(kind: SemanticTokenKind) -> u32 {
    match kind {
        SemanticTokenKind::Property => 0,
        SemanticTokenKind::UnknownKey => 1,
        SemanticTokenKind::EnumMember => 2,
        SemanticTokenKind::InvalidValue => 3,
    }
}

/// Encode engine tokens (already sorted, single-line, non-overlapping) into the
/// LSP delta form: each token is relative to the previous one.
fn encode_semantic_tokens(tokens: Vec<features::SemanticToken>) -> Vec<SemanticToken> {
    let mut data = Vec::with_capacity(tokens.len());
    let mut prev_line = 0u32;
    let mut prev_start = 0u32;
    for tok in tokens {
        let line = tok.range.start.line;
        let start = tok.range.start.character;
        // Engine tokens never span lines, so end.character is on the same line.
        let length = tok.range.end.character.saturating_sub(start);
        let delta_line = line - prev_line;
        let delta_start = if delta_line == 0 { start - prev_start } else { start };
        data.push(SemanticToken {
            delta_line,
            delta_start,
            length,
            token_type: semantic_token_type(tok.kind),
            token_modifiers_bitset: 0,
        });
        prev_line = line;
        prev_start = start;
    }
    data
}

fn to_lsp_symbol(s: features::DocumentSymbol) -> DocumentSymbol {
    #[allow(deprecated)]
    DocumentSymbol {
        name: s.name,
        detail: s.detail,
        kind: match s.kind {
            EngineSymbolKind::Object => SymbolKind::OBJECT,
            EngineSymbolKind::Array => SymbolKind::ARRAY,
            EngineSymbolKind::Field => SymbolKind::FIELD,
        },
        tags: None,
        deprecated: None,
        range: to_lsp_range(s.range),
        selection_range: to_lsp_range(s.selection_range),
        children: Some(s.children.into_iter().map(to_lsp_symbol).collect()),
    }
}
