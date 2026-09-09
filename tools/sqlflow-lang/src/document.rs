//! Parsing a `.flow.yaml` document into a position-indexed model.
//!
//! We parse with `marked-yaml` (which preserves source spans), detect the
//! document's `flowType`, and flatten the tree into a list of [`Located`]
//! nodes: every mapping key and list element with its authored census path and
//! its source range. Hover, diagnostics, and symbols all read from this index;
//! only completion (which must work on half-typed, unparseable lines) falls
//! back to the text heuristics in `context.rs`.

use crate::census::AuthoredSeg;
use crate::text::{LineIndex, Range};
use marked_yaml::types::{MarkedScalarNode, Node};
use marked_yaml::Span;

/// The kind of value sitting under a key (or the kind of a list element).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NodeKind {
    Scalar,
    Mapping,
    Sequence,
}

/// One located node: a mapping key or a list element, with its path and ranges.
#[derive(Debug, Clone)]
pub struct Located {
    /// Authored census path to this node.
    pub path: Vec<AuthoredSeg>,
    /// Range of the key token, when this node is a mapping entry.
    pub key_range: Option<Range>,
    /// Range of the value (for a mapping entry) or of the element (for a list).
    pub value_range: Range,
    /// The scalar text of the value/element, when it is a scalar.
    pub value: Option<String>,
    /// The kind of the value/element.
    pub value_kind: NodeKind,
}

/// A parse failure with a location, surfaced as a diagnostic.
#[derive(Debug, Clone)]
pub struct ParseError {
    pub message: String,
    pub range: Range,
}

/// Which SQLFlow document kind a file is. A flow document is discriminated by its `flowType` (absent means
/// the file flow), but a subscriber library has no `flowType` at all: it is a library of consumers, not a
/// pipeline. Without this distinction a `subscribers.yaml` would be analysed against the file-flow census and
/// every one of its keys reported as unknown.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DocumentKind {
    /// A flow document: `flowType` selects the census, `None` meaning the file flow.
    Flow,
    /// A subscriber library: `subscribers.yaml` or `*.subscribers.yaml`, detected by its root key.
    Subscribers,
}

/// A fully analysed flow document.
pub struct FlowDocument {
    pub source: String,
    pub line_index: LineIndex,
    pub kind: DocumentKind,
    pub flow_type: Option<String>,
    pub locations: Vec<Located>,
    pub parse_error: Option<ParseError>,
}

impl FlowDocument {
    pub fn parse(source: &str) -> FlowDocument {
        let line_index = LineIndex::new(source);
        let mut locations = Vec::new();
        let mut flow_type = None;
        let mut kind = DocumentKind::Flow;
        let mut parse_error = None;

        match marked_yaml::parse_yaml(0, source) {
            Ok(node) => {
                if let Some(map) = node.as_mapping() {
                    if let Some(ft) = map.get_scalar("flowType") {
                        let t = ft.as_str().trim();
                        if !t.is_empty() {
                            flow_type = Some(t.to_string());
                        }
                    }
                    // The root `subscribers:` key is the discriminator, not the file name: the engine analyses
                    // buffers whose name it may not know, and a library is a library wherever it is saved.
                    // A document carrying a flowType stays a flow even if something is called `subscribers`,
                    // so a flow with a genuine `subscribers` attribute could never be misread as a library.
                    if flow_type.is_none() && map.get_node("subscribers").is_some() {
                        kind = DocumentKind::Subscribers;
                    }
                    walk_mapping(&node, &[], &line_index, &mut locations);
                }
            }
            Err(err) => {
                parse_error = Some(load_error(&err, &line_index));
            }
        }

        FlowDocument {
            source: source.to_string(),
            line_index,
            kind,
            flow_type,
            locations,
            parse_error,
        }
    }

    /// The located node whose key or value range contains `offset`, if any.
    /// A containing key wins over a containing value so hover shows the key doc.
    pub fn locate(&self, char_offset: usize) -> Option<&Located> {
        let mut best: Option<&Located> = None;
        for loc in &self.locations {
            if let Some(kr) = loc.key_range {
                if contains(kr, char_offset, &self.line_index) {
                    return Some(loc);
                }
            }
            if contains(loc.value_range, char_offset, &self.line_index) {
                // Prefer the deepest (most specific) containing value.
                best = Some(match best {
                    Some(prev) if prev.path.len() >= loc.path.len() => prev,
                    _ => loc,
                });
            }
        }
        best
    }
}

fn contains(range: Range, offset: usize, idx: &LineIndex) -> bool {
    let start = idx.offset_of(range.start);
    let end = idx.offset_of(range.end);
    offset >= start && offset < end.max(start + 1)
}

/// Character offset of a node's span start (0 if the span is blank).
fn start_offset(span: &Span) -> usize {
    span.start().map(|m| m.character()).unwrap_or(0)
}

/// Range of a scalar token, whose span carries only a start marker: the end is
/// computed from the token's character length.
fn scalar_range(node: &MarkedScalarNode, idx: &LineIndex) -> Range {
    let start = start_offset(node.span());
    let len = node.as_str().chars().count();
    idx.range_of(start, (start + len).min(idx.char_len()))
}

/// Range of any node: scalars use their token length, collections use their
/// span (which carries both markers).
fn node_range(node: &Node, idx: &LineIndex) -> Range {
    match node {
        Node::Scalar(s) => scalar_range(s, idx),
        other => {
            let span = other.span();
            let start = start_offset(span);
            let end = span.end().map(|m| m.character()).unwrap_or(start + 1);
            idx.range_of(start, end.min(idx.char_len()))
        }
    }
}

fn node_kind(node: &Node) -> NodeKind {
    match node {
        Node::Scalar(_) => NodeKind::Scalar,
        Node::Mapping(_) => NodeKind::Mapping,
        Node::Sequence(_) => NodeKind::Sequence,
    }
}

/// Walk a mapping node, recording a `Located` per entry and recursing.
fn walk_mapping(node: &Node, prefix: &[AuthoredSeg], idx: &LineIndex, out: &mut Vec<Located>) {
    let Some(map) = node.as_mapping() else {
        return;
    };
    for (key, value) in map.iter() {
        let key_str = key.as_str().to_string();
        let mut path = prefix.to_vec();
        path.push(AuthoredSeg::Key(key_str));

        let key_start = start_offset(key.span());
        let key_len = key.as_str().chars().count();
        let key_range = idx.range_of(key_start, (key_start + key_len).min(idx.char_len()));

        out.push(Located {
            path: path.clone(),
            key_range: Some(key_range),
            value_range: node_range(value, idx),
            value: value.as_scalar().map(|s| s.as_str().to_string()),
            value_kind: node_kind(value),
        });

        match value {
            Node::Mapping(_) => walk_mapping(value, &path, idx, out),
            Node::Sequence(seq) => walk_sequence(seq, &path, idx, out),
            Node::Scalar(_) => {}
        }
    }
}

/// Walk a sequence, appending a `List` segment for its elements.
fn walk_sequence(
    seq: &marked_yaml::types::MarkedSequenceNode,
    prefix: &[AuthoredSeg],
    idx: &LineIndex,
    out: &mut Vec<Located>,
) {
    let mut path = prefix.to_vec();
    path.push(AuthoredSeg::List);
    for element in seq.iter() {
        match element {
            Node::Mapping(_) => walk_mapping(element, &path, idx, out),
            Node::Sequence(inner) => walk_sequence(inner, &path, idx, out),
            Node::Scalar(s) => {
                out.push(Located {
                    path: path.clone(),
                    key_range: None,
                    value_range: scalar_range(s, idx),
                    value: Some(s.as_str().to_string()),
                    value_kind: NodeKind::Scalar,
                });
            }
        }
    }
}

/// Map a marked-yaml load error to a positioned diagnostic.
fn load_error(err: &marked_yaml::LoadError, idx: &LineIndex) -> ParseError {
    use marked_yaml::LoadError::*;
    let marker = match err {
        TopLevelMustBeMapping(m)
        | TopLevelMustBeSequence(m)
        | UnexpectedAnchor(m)
        | MappingKeyMustBeScalar(m)
        | UnexpectedTag(m)
        | ScanError(m, _) => Some(*m),
        DuplicateKey(inner) => inner.key.span().start().copied(),
    };
    let range = match marker {
        Some(m) => {
            let start = m.character();
            idx.range_of(start, (start + 1).min(idx.char_len().max(start + 1)))
        }
        None => idx.range_of(0, 1),
    };
    ParseError {
        message: err.to_string(),
        range,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_flow_type_and_indexes_keys() {
        let src = "flowType: ing\nname: demo\nsource:\n  type: mssql\n";
        let doc = FlowDocument::parse(src);
        assert_eq!(doc.flow_type.as_deref(), Some("ing"));
        assert!(doc.parse_error.is_none());
        // source.type must be indexed as a nested key.
        let has_source_type = doc.locations.iter().any(|l| {
            l.path == vec![AuthoredSeg::Key("source".into()), AuthoredSeg::Key("type".into())]
        });
        assert!(has_source_type, "expected source.type in the index");
    }

    #[test]
    fn file_flow_has_no_flow_type() {
        let doc = FlowDocument::parse("name: demo\nsource:\n  type: csv\n");
        assert_eq!(doc.flow_type, None);
    }

    #[test]
    fn reports_parse_error() {
        let doc = FlowDocument::parse("name: demo\n  bad: : :\n:\n");
        assert!(doc.parse_error.is_some());
    }

    #[test]
    fn locates_key_under_cursor() {
        let src = "name: demo\nsource:\n  type: csv\n";
        let doc = FlowDocument::parse(src);
        let off = src.find("type").unwrap();
        let loc = doc.locate(off).expect("should locate a node at 'type'");
        assert_eq!(loc.path.last(), Some(&AuthoredSeg::Key("type".into())));
    }
}
