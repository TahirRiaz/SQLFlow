//! Completion context reconstruction.
//!
//! Completion must work on a line the user is halfway through typing, which by
//! definition does not parse as YAML. So instead of the parsed tree we rebuild
//! the enclosing path from indentation: scan every line above the cursor,
//! maintaining a stack of `(indent, segment)` that mirrors YAML block nesting,
//! including sequence (`- `) elements which contribute a `List` segment.

use crate::census::AuthoredSeg;
use crate::text::{LineIndex, Position};

/// What the cursor is positioned to complete.
pub enum CompletionContext {
    /// Typing a mapping key under `parent`; `partial` is the text so far.
    Key {
        parent: Vec<AuthoredSeg>,
        partial: String,
    },
    /// Typing a value for `path` (an existing `key:`); `partial` is the text so far.
    Value {
        path: Vec<AuthoredSeg>,
        partial: String,
    },
}

/// Count leading spaces (YAML indentation is spaces; a leading tab counts as one).
fn indent_of(line: &str) -> usize {
    line.chars().take_while(|c| *c == ' ' || *c == '\t').count()
}

fn is_blank_or_comment(line: &str) -> bool {
    let t = line.trim_start();
    t.is_empty() || t.starts_with('#')
}

/// Split a line's content (leading indent already removed) into a key and a
/// flag for whether a value follows on the same line.
fn split_key(content: &str) -> Option<(String, bool)> {
    let content = content.trim_end();
    if content.is_empty() || content.starts_with('#') {
        return None;
    }
    // Find the first ":" that terminates a key (followed by space or EOL).
    let bytes: Vec<char> = content.chars().collect();
    let mut colon = None;
    for (i, c) in bytes.iter().enumerate() {
        if *c == ':' {
            let next = bytes.get(i + 1);
            if next.is_none() || matches!(next, Some(' ')) {
                colon = Some(i);
                break;
            }
        }
    }
    let idx = colon?;
    let key: String = bytes[..idx].iter().collect();
    let key = key.trim().trim_matches('"').trim_matches('\'').to_string();
    if key.is_empty() {
        return None;
    }
    let has_value = bytes[idx + 1..].iter().any(|c| !c.is_whitespace());
    Some((key, has_value))
}

/// The character column (in chars, for slicing the line text) of an LSP
/// position on its line.
fn char_col(line: &str, character: u32) -> usize {
    let mut utf16 = 0u32;
    for (i, c) in line.chars().enumerate() {
        if utf16 >= character {
            return i;
        }
        utf16 += c.len_utf16() as u32;
    }
    line.chars().count()
}

/// Reconstruct the completion context at `pos`.
pub fn context_at(idx: &LineIndex, pos: Position) -> CompletionContext {
    // Build the block stack from all lines strictly above the cursor line.
    let mut stack: Vec<(usize, AuthoredSeg)> = Vec::new();
    for l in 0..(pos.line as usize) {
        let line = idx.line_text(l);
        if is_blank_or_comment(&line) {
            continue;
        }
        let ind = indent_of(&line);
        while matches!(stack.last(), Some(&(top, _)) if top >= ind) {
            stack.pop();
        }
        let content = &line[ind..];
        if let Some(rest) = content.strip_prefix("- ") {
            // A sequence element: the list is a child of the current top key.
            stack.push((ind, AuthoredSeg::List));
            let extra = rest.chars().take_while(|c| *c == ' ').count();
            if let Some((k, _)) = split_key(rest.trim_start()) {
                stack.push((ind + 2 + extra, AuthoredSeg::Key(k)));
            }
        } else if content == "-" {
            stack.push((ind, AuthoredSeg::List));
        } else if let Some((k, _)) = split_key(content) {
            stack.push((ind, AuthoredSeg::Key(k)));
        }
    }

    // Now interpret the current line.
    let line = idx.line_text(pos.line as usize);
    let cut = char_col(&line, pos.character);
    let before: String = line.chars().take(cut).collect();
    let cur_indent = indent_of(&before);
    let content_before = before[cur_indent.min(before.len())..].to_string();

    // Pop stack entries that are siblings-or-deeper than the current line.
    while matches!(stack.last(), Some(&(top, _)) if top >= cur_indent) {
        stack.pop();
    }
    let parent: Vec<AuthoredSeg> = stack.into_iter().map(|(_, s)| s).collect();

    // Strip a leading "- " so a fresh sequence element completes element keys.
    let (parent, content_before) = if let Some(rest) = content_before.strip_prefix("- ") {
        let mut p = parent.clone();
        p.push(AuthoredSeg::List);
        (p, rest.to_string())
    } else {
        (parent, content_before)
    };

    // Value position: the current line already has `key:` before the cursor.
    if let Some((key, _)) = split_key(&content_before) {
        // Everything after the first ": " is the value being typed.
        if let Some((_, rest)) = content_before.split_once(':') {
            let mut path = parent;
            path.push(AuthoredSeg::Key(key));
            return CompletionContext::Value {
                path,
                partial: rest.trim().to_string(),
            };
        }
    }

    CompletionContext::Key {
        parent,
        partial: content_before.trim().to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ctx(src: &str, line: u32, character: u32) -> CompletionContext {
        let idx = LineIndex::new(src);
        context_at(&idx, Position { line, character })
    }

    #[test]
    fn key_under_nested_mapping() {
        let src = "name: demo\nsource:\n  ty\n";
        match ctx(src, 2, 4) {
            CompletionContext::Key { parent, partial } => {
                assert_eq!(parent, vec![AuthoredSeg::Key("source".into())]);
                assert_eq!(partial, "ty");
            }
            _ => panic!("expected key context"),
        }
    }

    #[test]
    fn key_inside_list_of_mappings() {
        let src = "transform:\n  columns:\n    - name: foo\n      ex\n";
        match ctx(src, 3, 8) {
            CompletionContext::Key { parent, .. } => {
                assert_eq!(
                    parent,
                    vec![
                        AuthoredSeg::Key("transform".into()),
                        AuthoredSeg::Key("columns".into()),
                        AuthoredSeg::List,
                    ]
                );
            }
            _ => panic!("expected key context inside list"),
        }
    }

    #[test]
    fn value_position_after_colon() {
        let src = "load:\n  mode: \n";
        match ctx(src, 1, 8) {
            CompletionContext::Value { path, .. } => {
                assert_eq!(
                    path,
                    vec![AuthoredSeg::Key("load".into()), AuthoredSeg::Key("mode".into())]
                );
            }
            _ => panic!("expected value context"),
        }
    }
}
