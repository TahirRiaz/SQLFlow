//! Protocol-free position types and the char-offset ↔ LSP-position converter.
//!
//! marked-yaml reports positions as a character offset from the start of the
//! document (the parser consumes a `chars()` iterator). LSP, by contrast, uses
//! `(line, UTF-16 code unit)` positions. [`LineIndex`] bridges the two exactly,
//! so a document containing astral-plane characters still gets correct ranges.
//!
//! Keeping these types free of `lsp_types`/`tower-lsp` lets the same analysis
//! engine back both the language server and the MCP `validate_flow` tool.

use serde::{Deserialize, Serialize};

/// A zero-based `(line, UTF-16 character)` position, matching LSP semantics.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Position {
    pub line: u32,
    pub character: u32,
}

/// A half-open `[start, end)` range.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Range {
    pub start: Position,
    pub end: Position,
}

impl Range {
    pub fn new(start: Position, end: Position) -> Self {
        Range { start, end }
    }
}

/// Converts between marked-yaml character offsets and LSP positions.
pub struct LineIndex {
    /// The document as a flat vector of chars (for O(1) slicing).
    chars: Vec<char>,
    /// Char offset at which each line begins. `line_starts[0] == 0`.
    line_starts: Vec<usize>,
}

impl LineIndex {
    pub fn new(source: &str) -> Self {
        let chars: Vec<char> = source.chars().collect();
        let mut line_starts = vec![0usize];
        for (i, &c) in chars.iter().enumerate() {
            if c == '\n' {
                line_starts.push(i + 1);
            }
        }
        LineIndex { chars, line_starts }
    }

    /// Total number of characters in the document.
    pub fn char_len(&self) -> usize {
        self.chars.len()
    }

    /// Convert a marked-yaml character offset to an LSP position.
    pub fn position_of(&self, char_offset: usize) -> Position {
        let offset = char_offset.min(self.chars.len());
        // Largest line whose start is <= offset.
        let line = match self.line_starts.binary_search(&offset) {
            Ok(l) => l,
            Err(l) => l - 1,
        };
        let line_start = self.line_starts[line];
        let mut utf16 = 0u32;
        for &c in &self.chars[line_start..offset] {
            utf16 += c.len_utf16() as u32;
        }
        Position {
            line: line as u32,
            character: utf16,
        }
    }

    /// Convert an LSP position to a marked-yaml character offset.
    pub fn offset_of(&self, pos: Position) -> usize {
        let line = pos.line as usize;
        if line >= self.line_starts.len() {
            return self.chars.len();
        }
        let line_start = self.line_starts[line];
        let line_end = self
            .line_starts
            .get(line + 1)
            .map(|&s| s.saturating_sub(1)) // drop the '\n'
            .unwrap_or(self.chars.len());
        let mut utf16 = 0u32;
        let mut offset = line_start;
        for &c in &self.chars[line_start..line_end] {
            if utf16 >= pos.character {
                break;
            }
            utf16 += c.len_utf16() as u32;
            offset += 1;
        }
        offset
    }

    /// A range spanning a single character offset run `[start, end)`.
    pub fn range_of(&self, start: usize, end: usize) -> Range {
        Range::new(self.position_of(start), self.position_of(end))
    }

    /// The raw text of a zero-based line (without its line terminator).
    pub fn line_text(&self, line: usize) -> String {
        let Some(&start) = self.line_starts.get(line) else {
            return String::new();
        };
        let end = self
            .line_starts
            .get(line + 1)
            .map(|&s| s.saturating_sub(1))
            .unwrap_or(self.chars.len());
        let mut s: String = self.chars[start..end.min(self.chars.len())].iter().collect();
        // Trailing '\r' from CRLF documents is not part of the line.
        if s.ends_with('\r') {
            s.pop();
        }
        s
    }

    /// Total number of lines.
    pub fn line_count(&self) -> usize {
        self.line_starts.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ascii_positions_round_trip() {
        let src = "name: demo\nsource:\n  type: csv\n";
        let idx = LineIndex::new(src);
        // Offset of the 't' in "type" on line 2.
        let type_off = src.char_indices().find(|&(_, c)| c == 't').map(|(i, _)| i).unwrap();
        // char offset == byte offset for ASCII; the first 't' is in "type" on line 2.
        let pos = idx.position_of(type_off);
        assert_eq!(pos.line, 2);
        // Round trip an arbitrary position.
        let p = Position { line: 2, character: 2 };
        let off = idx.offset_of(p);
        assert_eq!(idx.position_of(off), p);
    }

    #[test]
    fn utf16_columns_account_for_astral_chars() {
        // "a😀b" — the emoji is 2 UTF-16 code units but 1 char.
        let src = "k: a😀b\n";
        let idx = LineIndex::new(src);
        // The 'b' is char offset 5 (k,:,space,a,😀,b -> indices 0..5 => b at 5).
        let chars: Vec<char> = src.chars().collect();
        let b_off = chars.iter().position(|&c| c == 'b').unwrap();
        let pos = idx.position_of(b_off);
        // column: "k: a😀" -> k(1) :(1) space(1) a(1) emoji(2) = 6 utf16 units
        assert_eq!(pos.character, 6);
        assert_eq!(idx.offset_of(pos), b_off);
    }
}
