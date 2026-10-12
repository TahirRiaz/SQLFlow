//! Editor features over a parsed [`FlowDocument`], returned as protocol-free
//! result types so both the LSP server and the MCP `validate_flow` tool can
//! consume them.

use crate::census::{AuthoredSeg, Census, KeyEntry, Resolution};
use crate::context::{context_at, CompletionContext};
use crate::document::{DocumentKind, FlowDocument, NodeKind};
use crate::text::{Position, Range};

// --- Result types ----------------------------------------------------------

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CompletionKind {
    Field,
    EnumMember,
    Snippet,
}

#[derive(Debug, Clone)]
pub struct CompletionItem {
    pub label: String,
    pub detail: Option<String>,
    pub documentation: Option<String>,
    pub insert_text: String,
    pub kind: CompletionKind,
    pub sort_text: String,
    /// True when `insert_text` uses `$0`/`${1:..}` LSP snippet placeholders.
    pub is_snippet: bool,
}

#[derive(Debug, Clone)]
pub struct Hover {
    pub markdown: String,
    pub range: Option<Range>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Severity {
    Error,
    Warning,
    Information,
    Hint,
}

#[derive(Debug, Clone)]
pub struct Diagnostic {
    pub range: Range,
    pub severity: Severity,
    pub message: String,
    pub code: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SymbolKind {
    Object,
    Array,
    Field,
}

#[derive(Debug, Clone)]
pub struct DocumentSymbol {
    pub name: String,
    pub detail: Option<String>,
    pub kind: SymbolKind,
    pub range: Range,
    pub selection_range: Range,
    pub children: Vec<DocumentSymbol>,
}

#[derive(Debug, Clone)]
pub struct TextEdit {
    pub range: Range,
    pub new_text: String,
}

#[derive(Debug, Clone)]
pub struct CodeAction {
    pub title: String,
    pub edits: Vec<TextEdit>,
}

/// The semantic role of a token, for editor colouring beyond what a syntactic
/// tokenizer can infer. These distinctions are census-driven: only the analysis
/// engine knows whether a key is documented for this flow type or whether a
/// value is a legal member of its key's enum.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SemanticTokenKind {
    /// A mapping key that resolves to a documented attribute or a valid
    /// container/open-dictionary member for the document's flow type.
    Property,
    /// A mapping key that resolves to nothing in the census for this flow type
    /// (the loader will ignore it).
    UnknownKey,
    /// A scalar value that is a legal member of its key's enum.
    EnumMember,
    /// A scalar value under an enum-typed key that is not one of the allowed
    /// members.
    InvalidValue,
}

#[derive(Debug, Clone)]
pub struct SemanticToken {
    pub range: Range,
    pub kind: SemanticTokenKind,
}

/// Whether `flow_type` names a flow kind this editor knows: one of SQLFlow's own, or one a module registered a
/// census for.
fn known_flow_type(flow_type: &str) -> bool {
    crate::census::BUILT_IN_FLOW_TYPES.contains(&flow_type)
        || crate::census::registered_flow_types().iter().any(|t| t == flow_type)
}

/// The flow kinds this editor knows, SQLFlow's own first, for the unknown-flowType diagnostic.
fn known_flow_types() -> String {
    crate::census::BUILT_IN_FLOW_TYPES
        .iter()
        .map(|t| t.to_string())
        .chain(crate::census::registered_flow_types())
        .collect::<Vec<_>>()
        .join(", ")
}

// --- Rendering -------------------------------------------------------------

/// Render a census entry as hover/completion markdown.
fn render_entry(entry: &KeyEntry) -> String {
    let mut md = String::new();
    md.push_str(&format!("**`{}`**: `{}`\n\n", entry.path, entry.ty));
    let mut facts = Vec::new();
    if entry.required {
        facts.push("required".to_string());
    }
    if let Some(def) = &entry.default {
        // Defaults can be long prose; keep the hover compact.
        let short = if def.chars().count() > 160 {
            format!("{}…", def.chars().take(160).collect::<String>())
        } else {
            def.clone()
        };
        facts.push(format!("default: {short}"));
    }
    if !facts.is_empty() {
        md.push_str(&format!("_{}_\n\n", facts.join(" · ")));
    }
    if let Some(values) = &entry.enum_values {
        md.push_str(&format!(
            "Allowed: {}\n\n",
            values.iter().map(|v| format!("`{v}`")).collect::<Vec<_>>().join(", ")
        ));
    }
    md.push_str(&entry.description);
    if let Some(when) = &entry.applies_when {
        md.push_str(&format!("\n\n**Applies when:** {when}"));
    }
    if let Some(v) = &entry.validation {
        md.push_str(&format!("\n\n**Validation:** {v}"));
    }
    if let Some(src) = &entry.defined_in {
        md.push_str(&format!("\n\n_defined in {src}_"));
    }
    md
}

fn first_line(text: &str) -> String {
    text.lines().next().unwrap_or("").trim().to_string()
}

// --- Completion ------------------------------------------------------------

pub fn completion(doc: &FlowDocument, pos: Position) -> Vec<CompletionItem> {
    let census = Census::for_document(doc);
    let ctx = context_at(&doc.line_index, pos);
    let mut items = Vec::new();

    match ctx {
        CompletionContext::Key { parent, partial } => {
            let already: std::collections::HashSet<String> = existing_children(doc, &parent);
            for (i, child) in census.children_of(&parent).into_iter().enumerate() {
                if !partial.is_empty()
                    && !child.name.to_lowercase().starts_with(&partial.to_lowercase())
                {
                    continue;
                }
                if already.contains(&child.name) {
                    continue;
                }
                let entry = child.entry;
                let insert = key_insert_text(&child.name, entry);
                items.push(CompletionItem {
                    label: child.name.clone(),
                    detail: Some(entry.ty.clone()),
                    documentation: Some(render_entry(entry)),
                    insert_text: insert.0,
                    kind: CompletionKind::Field,
                    sort_text: format!("{}{:04}", if entry.required { "0" } else { "1" }, i),
                    is_snippet: insert.1,
                });
            }
            if parent.is_empty() && partial.is_empty() {
                items.extend(root_snippets());
            }
        }
        CompletionContext::Value { path, partial } => {
            if let Resolution::Exact(entry) = census.resolve(&path) {
                let values = enum_candidates(entry);
                for (i, v) in values.iter().enumerate() {
                    if !partial.is_empty()
                        && !v.to_lowercase().starts_with(&partial.to_lowercase())
                    {
                        continue;
                    }
                    items.push(CompletionItem {
                        label: v.clone(),
                        detail: Some(entry.ty.clone()),
                        documentation: Some(render_entry(entry)),
                        insert_text: v.clone(),
                        kind: CompletionKind::EnumMember,
                        sort_text: format!("{i:04}"),
                        is_snippet: false,
                    });
                }
            }
        }
    }
    items
}

/// Insert text for a key completion; returns (text, is_snippet).
fn key_insert_text(name: &str, entry: &KeyEntry) -> (String, bool) {
    let ty = entry.ty.to_lowercase();
    if ty.starts_with("object") || ty.starts_with("map") || ty.starts_with("dictionary") {
        (format!("{name}:\n  "), false)
    } else if ty.starts_with("list") || ty.starts_with("array") || ty.starts_with("sequence") {
        (format!("{name}:\n  - "), false)
    } else if let Some(values) = &entry.enum_values {
        let choices = values.join(",");
        (format!("{name}: ${{1|{choices}|}}$0"), true)
    } else {
        (format!("{name}: $0"), true)
    }
}

/// Candidate values for value-position completion.
fn enum_candidates(entry: &KeyEntry) -> Vec<String> {
    if let Some(values) = &entry.enum_values {
        return values.clone();
    }
    if entry.ty.to_lowercase().starts_with("bool") {
        return vec!["true".to_string(), "false".to_string()];
    }
    Vec::new()
}

/// A minimal set of document-scaffold snippets offered on an empty file.
fn root_snippets() -> Vec<CompletionItem> {
    let snippets = [
        (
            "subscriber-library",
            "Consumer registration skeleton (subscribers.yaml)",
            "connections:
  ${1:dwh}: ${env:SQLFLOW_CONN_${2:DWH}}

subscribers:
  ${3:Report_Name}:
    type: ${4:PowerBI}
    description: ${5:what the report is for}
    server: ${1}
    queries:
      - name: ${6:Dataset}
        sql: |
          SELECT * FROM ${7:arc.SomeTable}
$0",
        ),
        (
            "file-flow",
            "File flow skeleton (CSV/JSON/… → SQL Server)",
            "name: ${1:flowName}\nsource:\n  type: ${2:csv}\n  path: ${3:./data/*.csv}\ntarget:\n  server: ${4:dwh}\n  table: ${5:stg.${1}}\n$0",
        ),
        (
            "ingestion-flow",
            "Table-to-table ingestion skeleton (flowType: ing)",
            "flowType: ing\nname: ${1:flowName}\nsource:\n  server: ${2:src}\n  table: ${3:dbo.source}\ntarget:\n  server: ${4:dwh}\n  table: ${5:stg.target}\nload:\n  mode: ${6:append}\n$0",
        ),
        (
            "batch-flow",
            "Ordered multi-flow batch skeleton (flowType: batch)",
            "flowType: batch\nname: ${1:batchName}\nbatch: ${2:sourceSystem}\nmaxParallel: ${3:4}\n$0",
        ),
        (
            "ai-context",
            "AI context skeleton (*.ai-context.yaml), approved by an admin before an assistant uses it",
            "aiContext: ${1:sales}\ndescription: ${2:What the context covers}\ninstructions: |\n  ${3:Amounts are in the reporting currency, excluding tax.}\ntables:\n  - table: ${4:dw.sales.Orders}\n    name: ${5:Order lines}\n    description: ${6:One row per order line.}\n    key: [${7:OrderId}]\n    columns:\n      - column: ${8:Amount}\n        description: ${9:Line amount excluding tax.}\n    deny: [${10:CostPrice}]\n$0",
        ),
    ];
    snippets
        .iter()
        .enumerate()
        .map(|(i, (label, detail, body))| CompletionItem {
            label: label.to_string(),
            detail: Some(detail.to_string()),
            documentation: Some(format!("```yaml\n{}\n```", body.replace("$0", "").replace(['$'], ""))),
            insert_text: body.to_string(),
            kind: CompletionKind::Snippet,
            sort_text: format!("2{i:04}"),
            is_snippet: true,
        })
        .collect()
}

/// Names of keys already present under `parent` in the parsed document.
fn existing_children(doc: &FlowDocument, parent: &[AuthoredSeg]) -> std::collections::HashSet<String> {
    let depth = parent.len();
    doc.locations
        .iter()
        .filter_map(|loc| {
            if loc.path.len() == depth + 1 && loc.path[..depth] == *parent {
                if let Some(AuthoredSeg::Key(k)) = loc.path.last() {
                    return Some(k.clone());
                }
            }
            None
        })
        .collect()
}

// --- Hover -----------------------------------------------------------------

pub fn hover(doc: &FlowDocument, pos: Position) -> Option<Hover> {
    let offset = doc.line_index.offset_of(pos);
    let loc = doc.locate(offset)?;
    let census = Census::for_document(doc);
    match census.resolve(&loc.path) {
        Resolution::Exact(entry) => Some(Hover {
            markdown: render_entry(entry),
            range: loc.key_range.or(Some(loc.value_range)),
        }),
        Resolution::OpenDictMember => {
            // Show the documentation of the open-dictionary container itself.
            if loc.path.len() >= 2 {
                let parent = &loc.path[..loc.path.len() - 1];
                let mut wild_path = parent.to_vec();
                // A representative wildcard entry documents the members.
                for entry in &census.entries {
                    if entry.segs.len() == parent.len() + 1
                        && crate::census::Seg::Wild == entry.segs[parent.len()]
                    {
                        wild_path.clear();
                        return Some(Hover {
                            markdown: render_entry(entry),
                            range: loc.key_range.or(Some(loc.value_range)),
                        });
                    }
                }
            }
            None
        }
        _ => None,
    }
}

// --- Diagnostics -----------------------------------------------------------

pub fn diagnostics(doc: &FlowDocument) -> Vec<Diagnostic> {
    if let Some(err) = &doc.parse_error {
        return vec![Diagnostic {
            range: err.range,
            severity: Severity::Error,
            message: err.message.clone(),
            code: Some("flow-parse".to_string()),
        }];
    }

    let mut out = Vec::new();
    let census = Census::for_document(doc);

    // A document whose kind no module registered a census for is not a flow, and nothing here describes it:
    // say so once rather than flag every key it has.
    if doc.kind == DocumentKind::Document && census.entries.is_empty() {
        let dt = doc.document_type.as_deref().unwrap_or_default();
        if let Some(loc) = doc
            .locations
            .iter()
            .find(|l| l.path == vec![AuthoredSeg::Key("documentType".into())])
        {
            out.push(Diagnostic {
                range: loc.value_range,
                severity: Severity::Information,
                message: format!(
                    "no key census is registered for documentType '{dt}' in this editor, so its keys are not checked"
                ),
                code: Some("flow-unregistered-document".to_string()),
            });
        }
        return out;
    }

    // Unknown flowType discriminator.
    if let Some(ft) = &doc.flow_type {
        if !known_flow_type(ft) {
            if let Some(loc) = doc
                .locations
                .iter()
                .find(|l| l.path == vec![AuthoredSeg::Key("flowType".into())])
            {
                out.push(Diagnostic {
                    range: loc.value_range,
                    severity: Severity::Error,
                    message: format!(
                        "unknown flowType '{ft}'. Use one of: {}, or omit flowType for a file flow.",
                        known_flow_types()
                    ),
                    code: Some("flow-unknown-flowtype".to_string()),
                });
            }
        }
    }

    // Per-key checks.
    for loc in &doc.locations {
        let Some(key_range) = loc.key_range else {
            continue;
        };
        match census.resolve(&loc.path) {
            Resolution::Unknown => {
                let name = match loc.path.last() {
                    Some(AuthoredSeg::Key(k)) => k.clone(),
                    _ => String::new(),
                };

                // The classic off-canon authoring: a hand-written source query on a relational
                // flow, whose canonical form is source.object + source.filter + the incremental
                // block. Called out specifically, because the generic unknown-key warning does not
                // say what to write instead.
                if let Some(guidance) = relational_query_guidance(doc.flow_type.as_deref(), &loc.path) {
                    out.push(Diagnostic {
                        range: key_range,
                        severity: Severity::Warning,
                        message: guidance,
                        code: Some("flow-no-source-query".to_string()),
                    });
                    continue;
                }

                // A key documented under a DIFFERENT parent is almost always a misplacement the
                // loader silently drops (IgnoreUnmatchedProperties), which reads as "the option
                // did nothing" at run time. Name the documented home(s) so the fix is obvious.
                let homes = documented_homes(&census, &name);
                let subject = match doc.kind {
                    DocumentKind::Subscribers => "a subscriber library",
                    DocumentKind::Schedules => "a schedule library",
                    DocumentKind::AiContext => "an AI context file",
                    DocumentKind::Flow => "this flow type",
                    DocumentKind::Document => "this document type",
                };
                // A strict loader refuses the whole document over an undocumented key; SQLFlow's own loaders
                // drop the key and carry on, which reads as "the option did nothing" at run time.
                let (severity, consequence) = if census.strict {
                    (Severity::Error, "the loader refuses the document")
                } else {
                    (Severity::Warning, "it will be ignored by the loader")
                };
                if homes.is_empty() {
                    out.push(Diagnostic {
                        range: key_range,
                        severity,
                        message: format!("unknown key '{name}' for {subject}; {consequence}"),
                        code: Some("flow-unknown-key".to_string()),
                    });
                } else {
                    out.push(Diagnostic {
                        range: key_range,
                        severity,
                        message: format!(
                            "key '{name}' is not documented here for {subject}, and {consequence}; a key of that \
                             name is documented at: {}. If that is what you meant, move it there.",
                            homes.join(", ")
                        ),
                        code: Some("flow-misplaced-key".to_string()),
                    });
                }
            }
            Resolution::Exact(entry) => {
                if let (Some(values), Some(value), NodeKind::Scalar) =
                    (&entry.enum_values, &loc.value, loc.value_kind)
                {
                    if !enum_ok(&entry.path, value, values) {
                        out.push(Diagnostic {
                            range: loc.value_range,
                            severity: Severity::Warning,
                            message: format!(
                                "'{value}' is not a valid value for {}; allowed: {}",
                                entry.path,
                                values.join(", ")
                            ),
                            code: Some("flow-invalid-enum".to_string()),
                        });
                    }
                }
            }
            _ => {}
        }
    }

    // Missing required top-level keys (only those without conditional prose, to
    // avoid false positives on context-dependent requirements).
    let present: std::collections::HashSet<String> = doc
        .locations
        .iter()
        .filter_map(|l| match (l.path.len(), l.path.first()) {
            (1, Some(AuthoredSeg::Key(k))) => Some(k.clone()),
            _ => None,
        })
        .collect();
    for entry in census.required_top_level() {
        if entry.applies_when.is_some() {
            continue;
        }
        if let Some(crate::census::Seg::Key(name)) = entry.segs.first() {
            if !present.contains(name) {
                out.push(Diagnostic {
                    range: Range::new(
                        Position { line: 0, character: 0 },
                        Position { line: 0, character: 0 },
                    ),
                    severity: Severity::Warning,
                    message: format!("required key '{name}' is missing"),
                    code: Some("flow-missing-required".to_string()),
                });
            }
        }
    }

    canon_diagnostics(doc, &census, &mut out);

    out
}

// --- Canonical-pattern lints ------------------------------------------------
//
// Structural validity is necessary but not sufficient: a flow can parse cleanly
// while re-implementing, in hand-written SQL, a mechanism the engine already
// owns (incremental watermarks), or while invoking a mechanism that does not
// exist at all (macro tokens in query text). These lints keep authored flows on
// the canonical path: declare intent in YAML, let the engine compose the SQL.

/// Census paths whose scalar values are raw SQL that SQLFlow sends or splices
/// VERBATIM: full statements (hooks, declared queries) and fragments (filters,
/// expressions). The engine performs no macro or parameter expansion on any of
/// them, which is what the invented-macro lint enforces.
const SQL_BEARING_PATHS: &[&str] = &[
    // Full statements.
    "source.query",
    "datasets[].query",
    "source.options.query",
    "preProcess",
    "preProcess[]",
    "postProcess",
    "postProcess[]",
    "source.options.preProcessOnTrg",
    "source.options.postProcessOnTrg",
    "surrogateKeys[].preProcess",
    "surrogateKeys[].postProcess",
    // Fragments spliced into engine-composed SQL.
    "source.filter",
    "source.incrementalClause",
    "source.options.filter",
    "filter",
    "virtualColumns[].expression",
    "assertions[].expression",
    "transform.columns[].expr",
];

/// T-SQL keywords that start a new statement, ending a DECLARE's variable list.
const STATEMENT_STARTERS: &[&str] = &[
    "select", "set", "insert", "update", "delete", "merge", "with", "if", "while", "begin", "end",
    "exec", "execute", "print", "create", "alter", "drop", "truncate", "from", "where", "declare",
];

fn canon_diagnostics(doc: &FlowDocument, census: &Census, out: &mut Vec<Diagnostic>) {
    for loc in &doc.locations {
        let (Some(value), NodeKind::Scalar) = (&loc.value, loc.value_kind) else {
            continue;
        };
        let Resolution::Exact(entry) = census.resolve(&loc.path) else {
            continue;
        };
        if !SQL_BEARING_PATHS.contains(&entry.path.as_str()) {
            continue;
        }

        for var in undeclared_sf_variables(value) {
            out.push(Diagnostic {
                range: loc.value_range,
                severity: Severity::Error,
                message: format!(
                    "'@{var}' looks like an invented SQLFlow macro. SQLFlow has no macro or \
                     parameter expansion in SQL it executes: this token reaches the database \
                     verbatim as an undefined variable and the statement fails. Declare the intent \
                     in YAML instead (the 'incremental' block for watermarks, 'source.filter' for \
                     a static narrowing predicate)."
                ),
                code: Some("flow-invented-macro".to_string()),
            });
        }

        if let Some(alternative) = watermark_alternative(doc.flow_type.as_deref(), &entry.path) {
            if let Some(shape) = handwritten_watermark(value) {
                out.push(Diagnostic {
                    range: loc.value_range,
                    severity: Severity::Warning,
                    message: format!(
                        "'{}' hand-codes an incremental watermark ({shape}). The canonical \
                         mechanism is {alternative}. Declare the watermark there and keep '{}' \
                         free of watermark SQL (static narrowing is fine).",
                        entry.path, entry.path
                    ),
                    code: Some("flow-handwritten-watermark".to_string()),
                });
            }
        }
    }
}

/// Guidance for the specific misauthoring of a hand-written source query on a relational flow.
/// Returns `Some(message)` when the authored path is `source.query` / `source.sql` on an
/// ing or exp document, whose census has no such key on purpose.
fn relational_query_guidance(flow_type: Option<&str>, path: &[AuthoredSeg]) -> Option<String> {
    let ft = flow_type?;
    if !matches!(ft, "ing" | "exp") {
        return None;
    }
    let [AuthoredSeg::Key(first), AuthoredSeg::Key(second)] = path else {
        return None;
    };
    if first.as_str() != "source" || !matches!(second.as_str(), "query" | "sql") {
        return None;
    }
    Some(format!(
        "'source.{second}' does not exist for flowType: {ft} and will be ignored by the loader; \
         the flow would silently read the whole object instead of your SQL. A relational flow \
         reads 'source.object', narrowed by 'source.filter' when needed. Incremental loading is \
         declared in the 'incremental' block (columns, or dateColumn + overlapDays, or lookback \
         for numeric keys) and the engine composes the WHERE clause itself; hand-written watermark \
         SQL is never part of a flow."
    ))
}

/// The documented full paths of census keys whose LEAF name matches `name` (case-insensitive),
/// for the misplaced-key suggestion. Capped so a common leaf name stays readable.
fn documented_homes(census: &Census, name: &str) -> Vec<String> {
    if name.is_empty() {
        return Vec::new();
    }
    let mut homes: Vec<String> = Vec::new();
    for entry in &census.entries {
        if let Some(crate::census::Seg::Key(leaf)) = entry.segs.last() {
            if leaf.eq_ignore_ascii_case(name) && !homes.contains(&entry.path) {
                homes.push(entry.path.clone());
            }
        }
    }
    homes.truncate(3);
    homes
}

/// The canonical alternative to a hand-written watermark for this (flow kind, key), or `None`
/// for keys/kinds where a MAX() subquery can be legitimate business SQL (trl queries, hooks).
fn watermark_alternative(flow_type: Option<&str>, entry_path: &str) -> Option<&'static str> {
    match (flow_type, entry_path) {
        (None, "source.options.query" | "source.options.filter") => Some(
            "the 'incremental' block on this file flow ('watermarkColumn' for row-level bounds, \
             'dateColumn' + 'overlapDays' for file dates); the engine pushes the bound into the \
             scan and filters every reader identically",
        ),
        (Some("ing"), "source.filter" | "source.incrementalClause") => Some(
            "the 'incremental' block ('columns', or 'dateColumn' + 'overlapDays', or 'lookback' \
             for numeric keys); the engine probes the target's MAX and combines the bound with \
             'source.filter' automatically",
        ),
        _ => None,
    }
}

/// Replaces single-quoted string literals, `--` line comments, and `/* */` block comments
/// (nesting included) with spaces, so tokens inside them are never linted and offsets keep
/// their line structure.
fn strip_sql_noise(sql: &str) -> String {
    let chars: Vec<char> = sql.chars().collect();
    let mut out = String::with_capacity(sql.len());
    let mut i = 0;
    let blank = |c: char| if c == '\n' { '\n' } else { ' ' };
    while i < chars.len() {
        match chars[i] {
            '\'' => {
                out.push(' ');
                i += 1;
                while i < chars.len() {
                    if chars[i] == '\'' {
                        if i + 1 < chars.len() && chars[i + 1] == '\'' {
                            out.push(' ');
                            out.push(' ');
                            i += 2;
                            continue;
                        }
                        out.push(' ');
                        i += 1;
                        break;
                    }
                    out.push(blank(chars[i]));
                    i += 1;
                }
            }
            '-' if i + 1 < chars.len() && chars[i + 1] == '-' => {
                while i < chars.len() && chars[i] != '\n' {
                    out.push(' ');
                    i += 1;
                }
            }
            '/' if i + 1 < chars.len() && chars[i + 1] == '*' => {
                let mut depth = 1usize;
                out.push(' ');
                out.push(' ');
                i += 2;
                while i < chars.len() && depth > 0 {
                    if chars[i] == '/' && i + 1 < chars.len() && chars[i + 1] == '*' {
                        depth += 1;
                        out.push(' ');
                        out.push(' ');
                        i += 2;
                    } else if chars[i] == '*' && i + 1 < chars.len() && chars[i + 1] == '/' {
                        depth -= 1;
                        out.push(' ');
                        out.push(' ');
                        i += 2;
                    } else {
                        out.push(blank(chars[i]));
                        i += 1;
                    }
                }
            }
            c => {
                out.push(c);
                i += 1;
            }
        }
    }
    out
}

/// One token of the noise-stripped SQL, for the small amount of structure the lints need.
enum SqlToken {
    Word(String),
    Variable(String),
}

/// Words and `@variables` of the noise-stripped SQL, in order. `@@functions` (system
/// functions like `@@ROWCOUNT`) are skipped; punctuation carries no information here.
fn sql_tokens(stripped: &str) -> Vec<SqlToken> {
    let chars: Vec<char> = stripped.chars().collect();
    let mut tokens = Vec::new();
    let mut i = 0;
    while i < chars.len() {
        let c = chars[i];
        if c == '@' {
            if i + 1 < chars.len() && chars[i + 1] == '@' {
                i += 2;
                while i < chars.len() && (chars[i].is_alphanumeric() || chars[i] == '_') {
                    i += 1;
                }
                continue;
            }
            let start = i + 1;
            let mut end = start;
            while end < chars.len() && (chars[end].is_alphanumeric() || chars[end] == '_') {
                end += 1;
            }
            if end > start {
                tokens.push(SqlToken::Variable(chars[start..end].iter().collect()));
            }
            i = end.max(i + 1);
        } else if c.is_alphabetic() || c == '_' {
            let start = i;
            let mut end = i;
            while end < chars.len() && (chars[end].is_alphanumeric() || chars[end] == '_') {
                end += 1;
            }
            tokens.push(SqlToken::Word(chars[start..end].iter().collect()));
            i = end;
        } else {
            i += 1;
        }
    }
    tokens
}

/// The variable names a `DECLARE` list in this SQL declares (lowercased). A DECLARE's list
/// runs until the next statement-starting keyword, so `DECLARE @a INT, @b INT` declares both.
fn declared_variables(tokens: &[SqlToken]) -> std::collections::HashSet<String> {
    let mut declared = std::collections::HashSet::new();
    let mut in_declare = false;
    for token in tokens {
        match token {
            SqlToken::Word(word) => {
                let lower = word.to_lowercase();
                if lower == "declare" {
                    in_declare = true;
                } else if STATEMENT_STARTERS.contains(&lower.as_str()) {
                    in_declare = false;
                }
            }
            SqlToken::Variable(name) => {
                if in_declare {
                    declared.insert(name.to_lowercase());
                }
            }
        }
    }
    declared
}

/// Distinct `@sf_*` variables the SQL uses without declaring, in first-use order and original
/// casing. These are the signature of an invented macro: no engine path defines them.
fn undeclared_sf_variables(sql: &str) -> Vec<String> {
    let stripped = strip_sql_noise(sql);
    let tokens = sql_tokens(&stripped);
    let declared = declared_variables(&tokens);
    let mut seen = std::collections::HashSet::new();
    let mut out = Vec::new();
    for token in &tokens {
        if let SqlToken::Variable(name) = token {
            let lower = name.to_lowercase();
            if lower.starts_with("sf_") && !declared.contains(&lower) && seen.insert(lower) {
                out.push(name.clone());
            }
        }
    }
    out
}

/// Whether this SQL hand-codes an incremental watermark, and in what shape: a
/// `SELECT MAX(...)` probe subquery, or a comparison against an undeclared variable whose
/// name says watermark. Returns a short description of the detected shape.
fn handwritten_watermark(sql: &str) -> Option<String> {
    let stripped = strip_sql_noise(sql);
    let collapsed = stripped
        .to_lowercase()
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
        .replace("max (", "max(");
    if collapsed.contains("select max(") {
        return Some("a 'SELECT MAX(...)' probe subquery".to_string());
    }

    let tokens = sql_tokens(&stripped);
    let declared = declared_variables(&tokens);
    const WATERMARK_NAMES: &[&str] =
        &["watermark", "highwater", "high_water", "lastload", "last_load", "lastrun", "last_run"];
    for token in &tokens {
        if let SqlToken::Variable(name) = token {
            let lower = name.to_lowercase();
            if !declared.contains(&lower) && WATERMARK_NAMES.iter().any(|m| lower.contains(m)) {
                return Some(format!("a comparison against the undeclared variable '@{name}'"));
            }
        }
    }
    None
}

/// Normalise a token for the two carve-out keys that strip separators/case.
fn normalize_token(value: &str) -> String {
    value.chars().filter(|c| *c != '-' && *c != '_').collect::<String>().to_lowercase()
}

fn enum_ok(path: &str, value: &str, allowed: &[String]) -> bool {
    let carve_out = path == "load.mode" || path == "transform.onConvertError";
    allowed.iter().any(|a| {
        if carve_out {
            normalize_token(a) == normalize_token(value)
        } else {
            a.eq_ignore_ascii_case(value)
        }
    })
}

// --- Semantic tokens -------------------------------------------------------

/// Classify each authored key and enum value into a [`SemanticTokenKind`] so an
/// editor can colour them by census knowledge: documented vs unknown keys, and
/// valid vs invalid enum members. Only tokens carrying information the syntactic
/// tokenizer lacks are emitted (keys, and scalar values under enum-typed keys);
/// strings, numbers, and structural punctuation are left to the base grammar.
///
/// The result is sorted by start position and free of overlaps (a key token and
/// its value token never share a range), which is what the LSP delta encoding
/// and Monaco's provider both require.
pub fn semantic_tokens(doc: &FlowDocument) -> Vec<SemanticToken> {
    // A document that failed to parse has no reliable node index; colouring a
    // half-parsed tree would flag correct keys as unknown, so emit nothing.
    if doc.parse_error.is_some() {
        return Vec::new();
    }

    let census = Census::for_document(doc);
    let mut out = Vec::new();

    for loc in &doc.locations {
        if let Some(key_range) = loc.key_range {
            let kind = match census.resolve(&loc.path) {
                Resolution::Unknown => SemanticTokenKind::UnknownKey,
                _ => SemanticTokenKind::Property,
            };
            out.push(SemanticToken { range: key_range, kind });
        }

        // Colour a scalar value only when its key declares an enum, so the
        // colour tells the author whether the value is one of the allowed set.
        if let (Resolution::Exact(entry), Some(value), NodeKind::Scalar) =
            (census.resolve(&loc.path), &loc.value, loc.value_kind)
        {
            if let Some(values) = &entry.enum_values {
                let kind = if enum_ok(&entry.path, value, values) {
                    SemanticTokenKind::EnumMember
                } else {
                    SemanticTokenKind::InvalidValue
                };
                out.push(SemanticToken { range: loc.value_range, kind });
            }
        }
    }

    out.sort_by_key(|t| (t.range.start.line, t.range.start.character));
    out
}

// --- Document symbols ------------------------------------------------------

pub fn document_symbols(doc: &FlowDocument) -> Vec<DocumentSymbol> {
    // Rebuild a tree from the flat, document-ordered location list.
    let mut roots: Vec<DocumentSymbol> = Vec::new();
    for loc in &doc.locations {
        let Some(key_range) = loc.key_range else {
            continue; // list scalar elements are not shown as symbols
        };
        let name = match loc.path.last() {
            Some(AuthoredSeg::Key(k)) => k.clone(),
            _ => continue,
        };
        let kind = match loc.value_kind {
            NodeKind::Mapping => SymbolKind::Object,
            NodeKind::Sequence => SymbolKind::Array,
            NodeKind::Scalar => SymbolKind::Field,
        };
        let detail = loc.value.as_ref().map(|v| {
            let v = first_line(v);
            if v.chars().count() > 60 {
                format!("{}…", v.chars().take(60).collect::<String>())
            } else {
                v
            }
        });
        let symbol = DocumentSymbol {
            name,
            detail,
            kind,
            range: full_range(loc.key_range, loc.value_range),
            selection_range: key_range,
            children: Vec::new(),
        };
        insert_symbol(&mut roots, &loc.path, symbol);
    }
    roots
}

fn full_range(key: Option<Range>, value: Range) -> Range {
    match key {
        Some(k) => Range::new(k.start, value.end),
        None => value,
    }
}

/// Insert a symbol at the position implied by its path, nesting under the
/// deepest existing ancestor.
fn insert_symbol(roots: &mut Vec<DocumentSymbol>, path: &[AuthoredSeg], symbol: DocumentSymbol) {
    // Descend through the key segments (skipping List markers) to the parent.
    let key_segs: Vec<&String> = path
        .iter()
        .filter_map(|s| match s {
            AuthoredSeg::Key(k) => Some(k),
            AuthoredSeg::List => None,
        })
        .collect();
    if key_segs.len() <= 1 {
        roots.push(symbol);
        return;
    }
    let mut level = roots;
    for seg in &key_segs[..key_segs.len() - 1] {
        // Find or fall back to pushing at this level.
        let idx = level.iter().position(|s| &&s.name == seg);
        match idx {
            Some(i) => level = &mut level[i].children,
            None => {
                // Parent not recorded (e.g. a list element); attach at this level.
                level.push(symbol);
                return;
            }
        }
    }
    level.push(symbol);
}

// --- Code actions ----------------------------------------------------------

pub fn code_actions(doc: &FlowDocument, range: Range) -> Vec<CodeAction> {
    let census = Census::for_document(doc);
    let ctx = context_at(&doc.line_index, range.start);
    let CompletionContext::Key { parent, .. } = ctx else {
        return Vec::new();
    };
    let existing = existing_children(doc, &parent);
    let defaults: Vec<&KeyEntry> = census
        .defaults_at(&parent)
        .into_iter()
        .filter(|e| match e.segs.last() {
            Some(crate::census::Seg::Key(k)) => !existing.contains(k),
            _ => false,
        })
        .collect();
    if defaults.is_empty() {
        return Vec::new();
    }
    let indent = " ".repeat(range.start.character as usize);
    let mut text = String::new();
    for e in &defaults {
        if let Some(crate::census::Seg::Key(k)) = e.segs.last() {
            let default = e.default.as_deref().unwrap_or("");
            let value = first_line(default);
            text.push_str(&format!("{indent}{k}: {value}\n"));
        }
    }
    vec![CodeAction {
        title: format!("Insert {} default value(s)", defaults.len()),
        edits: vec![TextEdit {
            range: Range::new(range.start, range.start),
            new_text: text,
        }],
    }]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn subscriber_library_is_clean_and_still_catches_typos() {
        let src = concat!(
            "connections:
  dwh: ${env:SQLFLOW_CONN_DWDWHPROD}

",
            "subscribers:
  Dashboard_Salg:
    type: PowerBI
",
            "    description: Sales dashboard
",
            "    notes: |
      Inaktivitet.
      Incomplete dataset.
",
            "    server: dwh
    queries:
      - name: Fara
        sql: |
",
            "          SELECT * FROM [dw-dwh-prod].[arc].[Fara_Stattrafficincome]
",
        );
        let doc = FlowDocument::parse(src);
        assert_eq!(doc.kind, DocumentKind::Subscribers);
        let diags = diagnostics(&doc);
        assert!(diags.is_empty(), "a valid subscriber library should be clean, got: {diags:?}");

        // A misspelt key is still reported, so the new census is real validation and not a blanket pass.
        let typo = src.replace("    notes: |", "    notez: |");
        let diags = diagnostics(&FlowDocument::parse(&typo));
        assert!(
            diags.iter().any(|d| d.message.contains("'notez'") && d.message.contains("subscriber library")),
            "expected an unknown-key warning for 'notez', got: {diags:?}"
        );
    }

    #[test]
    fn schedule_library_is_clean_and_still_catches_typos() {
        let src = concat!(
            "schedules:\n",
            "  nightly:\n    cron: \"0 2 * * *\"\n    timezone: Europe/Oslo\n    maxConcurrency: 2\n",
            "    catchup: true\n    enabled: true\n    operation: refresh\n    values:\n      region: north\n",
            "  hourly: { intervalSeconds: 3600 }\n",
            "  after_nightly:\n    after: [nightly, hourly]\n    parentFreshnessHours: 30\n",
        );
        let doc = FlowDocument::parse(src);
        assert_eq!(doc.kind, DocumentKind::Schedules);
        let diags = diagnostics(&doc);
        assert!(diags.is_empty(), "a valid schedule library should be clean, got: {diags:?}");

        // A key no library entry takes is still reported, under the library's own name.
        let typo = src.replace("    catchup: true", "    scope: batch");
        let diags = diagnostics(&FlowDocument::parse(&typo));
        assert!(
            diags.iter().any(|d| d.message.contains("'scope'") && d.message.contains("schedule library")),
            "expected an unknown-key warning for 'scope', got: {diags:?}"
        );
    }

    #[test]
    fn ai_context_file_is_clean_and_its_strict_loader_refuses_a_typo() {
        let src = concat!(
            "aiContext: sales\n",
            "description: Orders and revenue.\n",
            "instructions: |\n  Amounts exclude tax.\n",
            "tables:\n",
            "  - table: dw.sales.Orders\n    datasource: ${env:SQLFLOW_DW}\n    name: Order lines\n",
            "    description: One row per order line.\n    synonyms: [sales]\n    key: [OrderId]\n",
            "    columns:\n      - column: Amount\n        description: Line amount.\n",
            "        synonyms: [revenue]\n",
            "    deny: [CostPrice, InternalNote]\n",
            "    measures:\n      - name: net_revenue\n        expression: SUM(Amount)\n        description: Revenue.\n",
            "    joins:\n      - to: dw.sales.Customers\n        columns: [CustomerId]\n        toColumns: [Id]\n",
            "        type: left\n        description: Every order has a customer.\n",
            "examples:\n  - question: What was revenue last month?\n    sql: SELECT SUM(Amount) FROM sales.Orders\n",
        );
        let doc = FlowDocument::parse(src);
        assert_eq!(doc.kind, DocumentKind::AiContext);
        let diags = diagnostics(&doc);
        assert!(diags.is_empty(), "a valid AI context file should be clean, got: {diags:?}");

        // Its loader refuses an unknown key, so the editor says so as an error, under the file's own name.
        let typo = src.replace("    name: Order lines", "    nmae: Order lines");
        let diags = diagnostics(&FlowDocument::parse(&typo));
        assert!(
            diags.iter().any(|d| d.message.contains("'nmae'")
                && d.message.contains("an AI context file")
                && d.severity == Severity::Error),
            "expected an unknown-key error for 'nmae', got: {diags:?}"
        );

        // A join type outside the two the loader takes is caught as a value, not just as a key.
        let bad_type = src.replace("        type: left", "        type: full");
        let diags = diagnostics(&FlowDocument::parse(&bad_type));
        assert!(!diags.is_empty(), "expected a diagnostic for join type 'full', got none");
    }

    #[test]
    fn every_built_in_kind_but_batch_takes_the_schedule_and_mode_the_loader_reads_for_it() {
        let shapes = [
            "schedule: nightly\n",
            "schedule: [nightly, hourly]\n",
            "schedule:\n  name: nightly\n  cron: \"0 2 * * *\"\n  timezone: UTC\n  enabled: true\n  catchup: false\n  maxConcurrency: 2\n",
        ];
        for kind in ["ing", "exp", "sp", "inv", "hc", "scm", "api", "cpy", "sftp", "cal", "trl"] {
            for shape in shapes {
                let src = format!("flowType: {kind}\nname: orders\nmode: manual\n{shape}");
                let diags = diagnostics(&FlowDocument::parse(&src));
                let flagged: Vec<_> = diags
                    .iter()
                    .filter(|d| matches!(d.code.as_deref(), Some("flow-unknown-key" | "flow-misplaced-key")))
                    .collect();
                assert!(flagged.is_empty(), "flowType {kind} flagged its envelope ({shape:?}): {flagged:?}");
            }
        }

        // A batch projects no pipeline, so a schedule on it is parsed and then has no effect: it is still flagged.
        let diags = diagnostics(&FlowDocument::parse("flowType: batch\nname: nightly-set\nschedule: nightly\n"));
        assert!(
            diags.iter().any(|d| d.code.as_deref() == Some("flow-unknown-key") && d.message.contains("'schedule'")),
            "a batch's schedule should be flagged, got: {diags:?}"
        );
    }

    #[test]
    fn diagnoses_unknown_key_and_bad_enum() {
        // File flow (no flowType): load.mode is an enum here.
        let src = "name: demo\nload:\n  mode: nonsense\nbogusKey: 1\n";
        let doc = FlowDocument::parse(src);
        let diags = diagnostics(&doc);
        assert!(diags.iter().any(|d| d.code.as_deref() == Some("flow-invalid-enum")));
        assert!(diags.iter().any(|d| d.code.as_deref() == Some("flow-unknown-key")));
    }

    #[test]
    fn accepts_load_mode_separator_variants() {
        // truncate_load normalises to the canonical truncateLoad.
        let src = "name: demo\nload:\n  mode: truncate_load\n";
        let doc = FlowDocument::parse(src);
        let diags = diagnostics(&doc);
        assert!(!diags.iter().any(|d| d.code.as_deref() == Some("flow-invalid-enum")));
    }

    #[test]
    fn hover_on_known_key() {
        let src = "name: demo\nsource:\n  type: csv\n";
        let doc = FlowDocument::parse(src);
        let off = src.find("type").unwrap();
        let pos = doc.line_index.position_of(off);
        let h = hover(&doc, pos).expect("a known key has a hover");
        // The key and its type head the hover, written without the em dash the writing style forbids.
        assert!(h.markdown.starts_with("**`source.type`**: `"), "{}", h.markdown);
        assert!(!h.markdown.contains('\u{2014}'));
    }

    #[test]
    fn completes_keys_under_source() {
        let src = "name: demo\nsource:\n  \n";
        let doc = FlowDocument::parse(src);
        let items = completion(&doc, Position { line: 2, character: 2 });
        assert!(items.iter().any(|i| i.label == "type"));
    }

    #[test]
    fn semantic_tokens_classify_keys_and_enum_values() {
        // `load.mode: append` is a valid enum member; `bogusKey` is unknown.
        let src = "name: demo\nload:\n  mode: append\nbogusKey: 1\n";
        let doc = FlowDocument::parse(src);
        let toks = semantic_tokens(&doc);

        // The unknown root key is flagged.
        let bogus = toks
            .iter()
            .find(|t| t.range.start.line == 3)
            .expect("token on the bogusKey line");
        assert_eq!(bogus.kind, SemanticTokenKind::UnknownKey);

        // `append` on the mode line is a valid enum member.
        let mode_value = toks
            .iter()
            .find(|t| t.kind == SemanticTokenKind::EnumMember)
            .expect("an enum-member value token");
        assert_eq!(mode_value.range.start.line, 2);

        // Every documented key (name, load, mode) is a Property.
        assert!(toks.iter().any(|t| t.kind == SemanticTokenKind::Property));
        // Tokens are sorted by position for delta encoding.
        assert!(toks.windows(2).all(|w| {
            (w[0].range.start.line, w[0].range.start.character)
                <= (w[1].range.start.line, w[1].range.start.character)
        }));
    }

    #[test]
    fn semantic_tokens_flag_invalid_enum_value() {
        let src = "name: demo\nload:\n  mode: nonsense\n";
        let doc = FlowDocument::parse(src);
        let toks = semantic_tokens(&doc);
        assert!(toks.iter().any(|t| t.kind == SemanticTokenKind::InvalidValue));
    }

    #[test]
    fn semantic_tokens_empty_on_parse_error() {
        let doc = FlowDocument::parse("name: demo\n  bad: : :\n:\n");
        assert!(doc.parse_error.is_some());
        assert!(semantic_tokens(&doc).is_empty());
    }

    #[test]
    fn ing_source_query_gets_canonical_guidance_not_a_generic_unknown() {
        let src = "flowType: ing\nname: demo\nsource:\n  server: src\n  query: SELECT * FROM t WHERE x > 1\n";
        let diags = diagnostics(&FlowDocument::parse(src));
        let hit = diags
            .iter()
            .find(|d| d.code.as_deref() == Some("flow-no-source-query"))
            .expect("expected the source.query guidance");
        assert!(hit.message.contains("source.object"));
        assert!(hit.message.contains("'incremental' block"));
        // The specific guidance replaces the generic unknown-key warning for this key.
        assert!(!diags.iter().any(|d| {
            d.code.as_deref() == Some("flow-unknown-key") && d.message.contains("'query'")
        }));
    }

    #[test]
    fn invented_sf_macro_in_query_is_an_error() {
        let src = concat!(
            "flowType: trl\nname: demo\nsource:\n  server: src\n",
            "  query: SELECT * FROM dbo.Orders WHERE Id > @sf_incremental_watermark\n",
        );
        let diags = diagnostics(&FlowDocument::parse(src));
        let hit = diags
            .iter()
            .find(|d| d.code.as_deref() == Some("flow-invented-macro"))
            .expect("expected an invented-macro error");
        assert_eq!(hit.severity, Severity::Error);
        assert!(hit.message.contains("@sf_incremental_watermark"));
    }

    #[test]
    fn declared_commented_and_quoted_sf_tokens_are_not_macros() {
        // Declared in the same batch, inside a string literal, and inside comments: all legitimate.
        let src = concat!(
            "flowType: trl\nname: demo\nsource:\n  server: src\n",
            "  query: |\n",
            "    DECLARE @sf_cutoff INT = 5, @sf_other INT\n",
            "    -- @sf_ghost is only a comment\n",
            "    /* @sf_ghost2 */\n",
            "    SELECT '@sf_literal' AS tag FROM t WHERE Id > @sf_cutoff AND x < @sf_other\n",
        );
        let diags = diagnostics(&FlowDocument::parse(src));
        assert!(
            !diags.iter().any(|d| d.code.as_deref() == Some("flow-invented-macro")),
            "no macro error expected, got: {diags:?}"
        );
    }

    #[test]
    fn handwritten_watermark_in_ing_filter_is_flagged() {
        let src = concat!(
            "flowType: ing\nname: demo\nsource:\n  server: src\n",
            "  filter: AND ModifiedDate > (SELECT MAX(ModifiedDate) FROM dw.raw.Orders)\n",
        );
        let diags = diagnostics(&FlowDocument::parse(src));
        let hit = diags
            .iter()
            .find(|d| d.code.as_deref() == Some("flow-handwritten-watermark"))
            .expect("expected a handwritten-watermark warning");
        assert_eq!(hit.severity, Severity::Warning);
        assert!(hit.message.contains("'incremental' block"));
    }

    #[test]
    fn handwritten_watermark_in_duckdb_query_is_flagged() {
        let src = concat!(
            "name: demo\nsource:\n  type: duckdb\n  options:\n",
            "    query: SELECT * FROM read_parquet('x') WHERE v > (SELECT max(v) FROM t)\n",
        );
        let diags = diagnostics(&FlowDocument::parse(src));
        assert!(diags.iter().any(|d| d.code.as_deref() == Some("flow-handwritten-watermark")));
    }

    #[test]
    fn plain_static_filter_is_clean() {
        let src = "flowType: ing\nname: demo\nsource:\n  server: src\n  filter: AND SystemID = 13\n";
        let diags = diagnostics(&FlowDocument::parse(src));
        assert!(!diags.iter().any(|d| {
            matches!(
                d.code.as_deref(),
                Some("flow-handwritten-watermark") | Some("flow-invented-macro")
            )
        }));
    }

    #[test]
    fn misplaced_key_names_its_documented_home() {
        // truncateBeforeLoad belongs under target:, not load: (the classic silently-dropped key).
        let src = "flowType: ing\nname: demo\nload:\n  truncateBeforeLoad: true\n";
        let diags = diagnostics(&FlowDocument::parse(src));
        let hit = diags
            .iter()
            .find(|d| d.code.as_deref() == Some("flow-misplaced-key"))
            .expect("expected a misplaced-key warning");
        assert!(hit.message.contains("target.truncateBeforeLoad"));
    }

    // --- Module census registration --------------------------------------------------------------------------
    // Each test registers a kind no other test uses, since the registry is shared by the whole process.

    const WIDGET_CENSUS: &str = r#"{
        "flowType": "tests-widget",
        "strictKeys": true,
        "includeEnvelope": true,
        "keys": [
            { "path": "name", "type": "string", "required": true, "description": "The widget's name." },
            { "path": "source.colour", "type": "string", "enumValues": ["red", "blue"], "description": "The widget's colour." },
            { "path": "source.size", "type": "integer", "description": "How big it is." },
            { "path": "body", "type": "object", "freeForm": true, "description": "Anything the author writes." }
        ]
    }"#;

    #[test]
    fn a_registered_flow_kind_is_known_completed_hovered_and_checked() {
        let kind = crate::census::register(WIDGET_CENSUS).expect("the widget census registers");
        assert_eq!(kind, crate::census::CensusKind::FlowType("tests-widget".into()));
        assert!(crate::census::registered_flow_types().iter().any(|t| t == "tests-widget"));

        let src = "flowType: tests-widget\nname: w\nsource:\n  colour: green\n  shade: dark\n  \nbody:\n  any:\n    deep: 1\nschedule:\n  cron: \"0 2 * * *\"\n";
        let doc = FlowDocument::parse(src);
        let diags = diagnostics(&doc);

        // The kind is known, its enum is checked, and an undocumented key is an error: its loader refuses it.
        assert!(!diags.iter().any(|d| d.code.as_deref() == Some("flow-unknown-flowtype")), "{diags:?}");
        assert!(diags.iter().any(|d| d.code.as_deref() == Some("flow-invalid-enum") && d.message.contains("'green'")));
        let shade = diags
            .iter()
            .find(|d| d.code.as_deref() == Some("flow-unknown-key") && d.message.contains("'shade'"))
            .expect("an undocumented key is flagged");
        assert_eq!(shade.severity, Severity::Error);
        assert!(shade.message.contains("the loader refuses the document"));

        // The platform envelope is the kind's too, documented as SQLFlow documents it for the file flow.
        assert!(!diags.iter().any(|d| d.message.contains("'schedule'") || d.message.contains("'cron'")), "{diags:?}");

        // What the author writes under a free-form attribute is theirs.
        assert!(!diags.iter().any(|d| d.message.contains("'any'") || d.message.contains("'deep'")), "{diags:?}");

        // Hover documents a key, and completion offers the keys of a block.
        let hovered = hover(&doc, doc.line_index.position_of(src.find("colour").unwrap())).expect("a hover on colour");
        assert!(hovered.markdown.contains("The widget's colour."));
        assert!(hovered.markdown.contains("`red`"));
        let items = completion(&doc, Position { line: 5, character: 2 });
        assert!(items.iter().any(|i| i.label == "size"), "{:?}", items.iter().map(|i| &i.label).collect::<Vec<_>>());
    }

    #[test]
    fn an_unregistered_flow_kind_names_every_kind_this_editor_knows() {
        crate::census::register(&WIDGET_CENSUS.replace("tests-widget", "tests-listed")).expect("registers");
        let doc = FlowDocument::parse("flowType: tests-nothing\nname: w\n");
        let unknown = diagnostics(&doc)
            .into_iter()
            .find(|d| d.code.as_deref() == Some("flow-unknown-flowtype"))
            .expect("an unregistered kind is unknown");
        assert!(unknown.message.contains("ing, exp"), "{}", unknown.message);
        assert!(unknown.message.contains("tests-listed"), "{}", unknown.message);
    }

    #[test]
    fn a_schema_registration_is_a_built_in_kind() {
        let doc = FlowDocument::parse("flowType: sch
name: w
");
        assert!(
            !diagnostics(&doc).iter().any(|d| d.code.as_deref() == Some("flow-unknown-flowtype")),
            "sch is one of SQLFlow's own kinds"
        );
        assert!(crate::census::register(&WIDGET_CENSUS.replace("tests-widget", "sch")).is_err(), "a module cannot claim sch");
    }

    #[test]
    fn a_registered_document_type_is_checked_by_its_census_and_an_unregistered_one_is_left_alone() {
        crate::census::register(r#"{
            "documentType": "tests-glossary",
            "keys": [
                { "path": "documentType", "type": "string", "required": true, "description": "The document kind." },
                { "path": "terms[].word", "type": "string", "description": "A word." },
                { "path": "terms[].means", "type": "string", "description": "What it means." }
            ]
        }"#)
        .expect("the glossary census registers");

        let glossary = FlowDocument::parse("documentType: tests-glossary\nterms:\n  - word: a\n    means: b\n    colour: c\n");
        let diags = diagnostics(&glossary);
        let colour = diags
            .iter()
            .find(|d| d.code.as_deref() == Some("flow-unknown-key"))
            .expect("an undocumented key of a document is flagged");
        assert!(colour.message.contains("'colour' for this document type"));
        // A census that does not say its loader is strict leaves an undocumented key a warning.
        assert_eq!(colour.severity, Severity::Warning);
        assert_eq!(diags.len(), 1, "{diags:?}");

        // A document nothing describes is not a file flow: one note, and none of its keys is flagged.
        let other = FlowDocument::parse("documentType: tests-unregistered\nanything: 1\nnested:\n  deep: 2\n");
        let notes = diagnostics(&other);
        assert_eq!(notes.len(), 1, "{notes:?}");
        assert_eq!(notes[0].code.as_deref(), Some("flow-unregistered-document"));
        assert_eq!(notes[0].severity, Severity::Information);
    }

    #[test]
    fn a_census_file_that_cannot_describe_a_kind_is_refused() {
        let refused = |json: &str| crate::census::register(json).expect_err("refused");
        assert!(refused("not json").contains("not a census file"));
        assert!(refused(r#"{ "keys": [{ "path": "a", "type": "string" }] }"#).contains("names the flowType or the documentType"));
        assert!(refused(r#"{ "flowType": "x", "documentType": "y", "keys": [{ "path": "a", "type": "string" }] }"#).contains("not both"));
        assert!(refused(r#"{ "flowType": "ing", "keys": [{ "path": "a", "type": "string" }] }"#).contains("SQLFlow's own flow kinds"));
        assert!(refused(r#"{ "documentType": "tests-empty", "keys": [] }"#).contains("documents no key"));

        // Registering a kind again replaces it, and a kind can be withdrawn.
        let first = r#"{ "documentType": "tests-replaced", "keys": [{ "path": "one", "type": "string" }] }"#;
        let second = r#"{ "documentType": "tests-replaced", "keys": [{ "path": "two", "type": "string" }] }"#;
        let kind = crate::census::register(first).expect("registers");
        crate::census::register(second).expect("registers again");
        let census = Census::for_document_type("tests-replaced").expect("registered");
        assert!(census.entries.iter().any(|e| e.path == "two") && !census.entries.iter().any(|e| e.path == "one"));
        assert!(crate::census::unregister(&kind));
        assert!(Census::for_document_type("tests-replaced").is_none());
    }
}
