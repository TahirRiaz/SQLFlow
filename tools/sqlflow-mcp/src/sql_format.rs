//! Lays SQL out on lines for a person to read.
//!
//! A statement reaches a tool's answer as the control plane holds it, and most are one line: what a model composed for
//! a question, a report visual's query translated to T-SQL, a statement the engine ran. An editor or Slack shows that
//! line as it is, so every SQL field of an answer is laid out here first ([`format_sql_fields`], applied by
//! [`crate::server::json_str`]). A field holds SQL when its name says so: `sql`, or a name ending in `Sql` (`sourceSql`,
//! `reportSql`, `suggestedSql`, `failedStatementSql`), the naming the control plane's answers keep throughout.
//!
//! The layout comes from `sqlformat`, which knows clause structure but not every T-SQL lexeme: it splits `@@ROWCOUNT`
//! and `0x1F`, reads `#temp` as a comment, does not nest block comments, and treats a backslash in a string as an
//! escape. So every lexeme it could misread is swapped for a placeholder word first and put back after, and the result
//! is then held to the statement's own tokens: only the whitespace between tokens may differ. Anything else (a lexeme
//! this file does not know, an unterminated string, a fault in the formatter) leaves the statement exactly as it came,
//! so the SQL a person reads is always token for token the SQL that ran.

use serde_json::Value;
use sqlformat::{Dialect, FormatOptions, Indent, QueryParams};

/// Statements longer than this are left as they came. Layout is linear in the text, about a third of a second per
/// megabyte, so the bound only keeps one pathological field from holding up an answer; no statement a person reads
/// in a chat comes near it.
const MAX_FORMAT_LEN: usize = 256 * 1024;

/// The placeholder words' stem. A statement that already contains it (in any case) gets a longer one, so a
/// placeholder can never be confused with the statement's own text.
const PLACEHOLDER_STEM: &str = "sqlfmtph";

/// Lays out every SQL field in `value`, recursively: a string under a key named `sql` or ending in `Sql`, or a string
/// in an array under such a key. Every other value is left alone, and a field that cannot be laid out keeps its text.
pub fn format_sql_fields(value: &mut Value) {
    walk(value, false);
}

fn walk(value: &mut Value, holds_sql: bool) {
    match value {
        Value::String(text) if holds_sql => {
            if let Some(laid_out) = layout(text) {
                *text = laid_out;
            }
        }
        Value::Array(items) => {
            for item in items {
                walk(item, holds_sql);
            }
        }
        Value::Object(map) => {
            for (key, item) in map.iter_mut() {
                walk(item, is_sql_key(key));
            }
        }
        _ => {}
    }
}

fn is_sql_key(key: &str) -> bool {
    key == "sql" || key.ends_with("Sql")
}

/// `sql` laid out on lines, two spaces to an indent, keyword case and every token as written. Returns `sql` unchanged
/// when it cannot be laid out without changing a token (see the module documentation).
pub fn format_sql(sql: &str) -> String {
    layout(sql).unwrap_or_else(|| sql.to_string())
}

/// The laid-out text, or None when the statement is to be left as it came.
fn layout(sql: &str) -> Option<String> {
    if sql.trim().is_empty() || sql.len() > MAX_FORMAT_LEN {
        return None;
    }
    let tokens = lex(sql)?;

    let stem = placeholder_stem(sql);
    let shielded_count = tokens.iter().filter(|t| t.kind.is_shielded()).count();
    let width = digits(shielded_count.saturating_sub(1));
    let shielded = shield(sql, &tokens, &stem, width);

    // A fault in the formatter must not take the answer down with it: the statement is shown as it came instead,
    // and the fault is logged with the statement's size, never its text, which can carry literal values.
    let options = FormatOptions {
        indent: Indent::Spaces(2),
        uppercase: None,
        lines_between_queries: 2,
        dialect: Dialect::SQLServer,
        ..FormatOptions::default()
    };
    let formatted = match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        sqlformat::format(&shielded, &QueryParams::None, &options)
    })) {
        Ok(formatted) => formatted,
        Err(_) => {
            tracing::warn!(length = sql.len(), "sqlformat panicked laying out a statement; it is shown as it came");
            return None;
        }
    };

    let restored = restore(&formatted, sql, &tokens, &stem, width)?;
    let laid_out_tokens = lex(&restored)?;
    let same = laid_out_tokens.len() == tokens.len()
        && laid_out_tokens
            .iter()
            .zip(&tokens)
            .all(|(a, b)| a.kind == b.kind && a.text(&restored) == b.text(sql));
    if !same {
        tracing::debug!(length = sql.len(), "laying out a statement changed a token; it is shown as it came");
        return None;
    }

    Some(rebuild(&restored, &laid_out_tokens, &tokens))
}

/// The placeholder stem for `sql`: [`PLACEHOLDER_STEM`], lengthened until the statement does not contain it.
fn placeholder_stem(sql: &str) -> String {
    let lower = sql.to_lowercase();
    let mut stem = PLACEHOLDER_STEM.to_string();
    while lower.contains(&stem) {
        stem.push('x');
    }
    stem
}

/// How many decimal digits `n` takes. Every placeholder of one statement has the same width, so reading one back
/// never runs into a digit that follows it.
fn digits(n: usize) -> usize {
    n.to_string().len()
}

/// `sql` with every shielded token replaced by its placeholder word, numbered in order. A space separates two pieces
/// that would otherwise run together into one word (`[a]b` becomes `ph0 b`), which changes only whitespace.
fn shield(sql: &str, tokens: &[Token], stem: &str, width: usize) -> String {
    let mut out = String::with_capacity(sql.len());
    let mut previous_end = 0;
    let mut index = 0;
    for token in tokens {
        let gap = &sql[previous_end..token.start];
        out.push_str(gap);
        let piece = if token.kind.is_shielded() {
            let placeholder = format!("{stem}{index:0width$}");
            index += 1;
            placeholder
        } else {
            token.text(sql).to_string()
        };
        if gap.is_empty() && ends_in_word(&out) && starts_with_word(&piece) {
            out.push(' ');
        }
        out.push_str(&piece);
        previous_end = token.end;
    }
    out.push_str(&sql[previous_end..]);
    out
}

/// `formatted` with every placeholder replaced by the token it stands for. None when a placeholder is not where the
/// shielding put one (the formatter split or rewrote it), which leaves the statement as it came.
fn restore(formatted: &str, sql: &str, tokens: &[Token], stem: &str, width: usize) -> Option<String> {
    let originals: Vec<&str> = tokens.iter().filter(|t| t.kind.is_shielded()).map(|t| t.text(sql)).collect();
    let mut out = String::with_capacity(formatted.len() + sql.len() / 4);
    let mut rest = formatted;
    let mut seen = 0;
    while let Some(at) = rest.find(stem) {
        out.push_str(&rest[..at]);
        let after = &rest[at + stem.len()..];
        let number = after.get(..width).filter(|n| n.bytes().all(|b| b.is_ascii_digit()))?;
        let index: usize = number.parse().ok()?;
        if index != seen {
            return None;
        }
        out.push_str(originals.get(index)?);
        seen += 1;
        rest = &after[width..];
    }
    out.push_str(rest);
    (seen == originals.len()).then_some(out)
}

/// The laid-out statement from its tokens (index for index the original's, as [`layout`] has checked), with the
/// formatter's whitespace between them, less trailing blanks on a line, and none before the first token or after the
/// last. Two of the formatter's habits are corrected on the way, both in whitespace alone:
///
/// * a name the statement wrote directly against its opening parenthesis (`CONVERT(`, `REPLACE(`) keeps it there,
///   where the formatter would part a few of them;
/// * a parenthesis whose contents were broken onto lines closes on a line of its own, at the indent of the line it
///   opened on. The formatter treats a short subquery (`(SELECT NULL)`) as an inline block, breaks it at its
///   `SELECT` all the same, and leaves the `)` trailing the last line.
fn rebuild(restored: &str, laid_out: &[Token], original: &[Token]) -> String {
    struct Open {
        indent: String,
        broken: bool,
    }

    let mut out = String::with_capacity(restored.len());
    let mut open: Vec<Open> = Vec::new();
    for (i, token) in laid_out.iter().enumerate() {
        let text = token.text(restored);
        if i > 0 {
            let gap = &restored[laid_out[i - 1].end..token.start];
            if laid_out[i - 1].is_punct(restored, "(") {
                if let Some(last) = open.last_mut() {
                    last.broken = gap.contains('\n');
                }
            }
            let written_together = original[i - 1].end == original[i].start;
            let call = written_together && laid_out[i - 1].kind.is_name() && text == "(" && !gap.contains('\n');
            let close = token.is_punct(restored, ")").then(|| open.pop()).flatten();
            match close {
                Some(opened) if opened.broken && !gap.contains('\n') => {
                    out.push('\n');
                    out.push_str(&opened.indent);
                }
                _ if call => {}
                _ => push_gap(&mut out, gap),
            }
        }
        if token.is_punct(restored, "(") {
            open.push(Open { indent: current_indent(&out), broken: false });
        }
        out.push_str(text);
    }
    out
}

/// The blanks that start the last line of `text`.
fn current_indent(text: &str) -> String {
    let line = text.rsplit('\n').next().unwrap_or_default();
    line.chars().take_while(|c| *c == ' ' || *c == '\t').collect()
}

/// A run of whitespace between two tokens, with the blanks that would end a line dropped.
fn push_gap(out: &mut String, gap: &str) {
    let mut lines = gap.split('\n').peekable();
    while let Some(line) = lines.next() {
        if lines.peek().is_some() {
            out.push_str(line.trim_end_matches([' ', '\t', '\r']));
            out.push('\n');
        } else {
            out.push_str(line);
        }
    }
}

fn ends_in_word(text: &str) -> bool {
    text.chars().next_back().is_some_and(is_word_char)
}

fn starts_with_word(text: &str) -> bool {
    text.chars().next().is_some_and(is_word_char)
}

fn is_word_char(c: char) -> bool {
    c.is_alphanumeric() || matches!(c, '_' | '@' | '#' | '$')
}

fn is_operator_char(c: char) -> bool {
    matches!(c, '!' | '<' | '>' | '=' | '|' | ':' | '-' | '~' | '*' | '&' | '^' | '?' | '/' | '%' | '+')
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Kind {
    /// A keyword or a plain name.
    Word,
    /// A name `sqlformat` could misread: `@variable`, `@@ROWCOUNT`, `#temp`, `##global`, `$action`.
    MarkedName,
    /// A decimal number.
    Number,
    /// A number `sqlformat` could split: `0x1F`, `$12.50`.
    MarkedNumber,
    /// `'...'` or `N'...'`.
    String,
    /// `"..."`, `[...]` or `` `...` ``.
    QuotedName,
    LineComment,
    BlockComment,
    /// A run of operator characters, one token so that splitting `>=` or `<>` shows as a change.
    Operator,
    /// `(`, `)`, `,`, `;` or `.`.
    Punct,
    /// Any other character, kept out of the formatter's reach.
    Other,
}

impl Kind {
    fn is_shielded(self) -> bool {
        matches!(
            self,
            Kind::MarkedName | Kind::MarkedNumber | Kind::String | Kind::QuotedName | Kind::BlockComment | Kind::Other
        )
    }

    /// A token a call's opening parenthesis can follow.
    fn is_name(self) -> bool {
        matches!(self, Kind::Word | Kind::MarkedName | Kind::QuotedName)
    }
}

#[derive(Clone, Copy, Debug)]
struct Token {
    kind: Kind,
    start: usize,
    end: usize,
}

impl Token {
    fn text(self, source: &str) -> &str {
        &source[self.start..self.end]
    }

    fn is_punct(self, source: &str, punct: &str) -> bool {
        self.kind == Kind::Punct && self.text(source) == punct
    }
}

/// The statement's tokens, by T-SQL's lexical rules, whitespace skipped. None when a string, a quoted name or a block
/// comment is not closed, since nothing about the text after it can then be trusted. A line comment's token stops
/// before the line's trailing blanks, which mean nothing.
fn lex(sql: &str) -> Option<Vec<Token>> {
    let bytes = sql.as_bytes();
    let mut tokens = Vec::new();
    let mut i = 0;
    while i < sql.len() {
        let c = sql[i..].chars().next()?;
        if c.is_whitespace() {
            i += c.len_utf8();
            continue;
        }
        let next = bytes.get(i + 1).copied();
        let (kind, end) = match c {
            '-' if next == Some(b'-') => {
                let line_end = sql[i..].find(['\n', '\r']).map_or(sql.len(), |n| i + n);
                (Kind::LineComment, i + sql[i..line_end].trim_end().len())
            }
            '/' if next == Some(b'*') => (Kind::BlockComment, block_comment_end(sql, i)?),
            '\'' => (Kind::String, quoted_end(sql, i, b'\'')?),
            'N' | 'n' if next == Some(b'\'') => (Kind::String, quoted_end(sql, i + 1, b'\'')?),
            '"' => (Kind::QuotedName, quoted_end(sql, i, b'"')?),
            '`' => (Kind::QuotedName, quoted_end(sql, i, b'`')?),
            '[' => (Kind::QuotedName, quoted_end(sql, i, b']')?),
            '@' | '#' => (Kind::MarkedName, word_end(sql, i + 1)),
            '$' if next.is_some_and(|b| b.is_ascii_digit() || b == b'.') => (Kind::MarkedNumber, number_end(sql, i + 1)),
            '$' => (Kind::MarkedName, word_end(sql, i + 1)),
            '0' if matches!(next, Some(b'x' | b'X')) => {
                let digits = sql[i + 2..].find(|c: char| !c.is_ascii_hexdigit()).map_or(sql.len(), |n| i + 2 + n);
                (Kind::MarkedNumber, digits)
            }
            '0'..='9' => (Kind::Number, number_end(sql, i)),
            '.' if next.is_some_and(|b| b.is_ascii_digit()) => (Kind::Number, number_end(sql, i)),
            '(' | ')' | ',' | ';' | '.' => (Kind::Punct, i + 1),
            c if c.is_alphabetic() || c == '_' => (Kind::Word, word_end(sql, i)),
            c if is_operator_char(c) => (Kind::Operator, operator_end(sql, i)),
            c => (Kind::Other, i + c.len_utf8()),
        };
        tokens.push(Token { kind, start: i, end });
        i = end;
    }
    Some(tokens)
}

/// The end of a name's word characters from `from` on.
fn word_end(sql: &str, from: usize) -> usize {
    sql[from..].find(|c: char| !is_word_char(c)).map_or(sql.len(), |n| from + n)
}

/// The end of a decimal number from `from`: digits, an optional fraction, an optional exponent.
fn number_end(sql: &str, from: usize) -> usize {
    let bytes = sql.as_bytes();
    let mut i = from;
    while i < bytes.len() && bytes[i].is_ascii_digit() {
        i += 1;
    }
    if i < bytes.len() && bytes[i] == b'.' {
        i += 1;
        while i < bytes.len() && bytes[i].is_ascii_digit() {
            i += 1;
        }
    }
    if i < bytes.len() && matches!(bytes[i], b'e' | b'E') {
        let mut j = i + 1;
        if j < bytes.len() && matches!(bytes[j], b'+' | b'-') {
            j += 1;
        }
        if j < bytes.len() && bytes[j].is_ascii_digit() {
            i = j;
            while i < bytes.len() && bytes[i].is_ascii_digit() {
                i += 1;
            }
        }
    }
    i
}

/// The end of a run of operator characters from `from`, stopping where a comment starts.
fn operator_end(sql: &str, from: usize) -> usize {
    let bytes = sql.as_bytes();
    let mut i = from + 1;
    while i < bytes.len() && is_operator_char(bytes[i] as char) {
        if bytes[i..].starts_with(b"--") || bytes[i..].starts_with(b"/*") {
            break;
        }
        i += 1;
    }
    i
}

/// The end of a quoted lexeme opening at `open` and closed by `close`, a doubled `close` being one escaped character.
/// None when it is never closed.
fn quoted_end(sql: &str, open: usize, close: u8) -> Option<usize> {
    let bytes = sql.as_bytes();
    let mut i = open + 1;
    while i < bytes.len() {
        if bytes[i] == close {
            if bytes.get(i + 1) == Some(&close) {
                i += 2;
                continue;
            }
            return Some(i + 1);
        }
        i += 1;
    }
    None
}

/// The end of a block comment opening at `open`. T-SQL nests them, so `/* a /* b */ c */` is one comment. None when it
/// is never closed.
fn block_comment_end(sql: &str, open: usize) -> Option<usize> {
    let bytes = sql.as_bytes();
    let mut depth = 0usize;
    let mut i = open;
    while i + 1 < bytes.len() {
        match (bytes[i], bytes[i + 1]) {
            (b'/', b'*') => {
                depth += 1;
                i += 2;
            }
            (b'*', b'/') => {
                depth -= 1;
                i += 2;
                if depth == 0 {
                    return Some(i);
                }
            }
            _ => i += 1,
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// The statement's tokens as text, for holding a layout to the tokens it was given.
    fn token_texts(sql: &str) -> Vec<String> {
        lex(sql).expect("lexes").iter().map(|t| t.text(sql).to_string()).collect()
    }

    #[test]
    fn a_one_line_query_is_laid_out_by_clause() {
        let sql = "SELECT c.Name, COUNT(*) AS Orders FROM demo.Customers c INNER JOIN demo.Orders o ON o.CustomerId = c.Id \
                   WHERE o.OrderDate >= '2024-01-01' AND c.Country <> N'Norway' GROUP BY c.Name ORDER BY Orders DESC";
        assert_eq!(
            format_sql(sql),
            "SELECT\n  c.Name,\n  COUNT(*) AS Orders\nFROM\n  demo.Customers c\n  INNER JOIN demo.Orders o ON o.CustomerId = c.Id\n\
             WHERE\n  o.OrderDate >= '2024-01-01'\n  AND c.Country <> N'Norway'\nGROUP BY\n  c.Name\nORDER BY\n  Orders DESC"
        );
    }

    #[test]
    fn a_subquery_and_a_case_are_indented_inside_their_clause() {
        let sql = "WITH recent AS (SELECT w.WellId, MAX(r.LoggedAt) AS LastLog FROM dbo.Wells w LEFT JOIN dbo.Runs r ON \
                   r.WellId = w.WellId GROUP BY w.WellId) SELECT CASE WHEN LastLog IS NULL THEN 'never' ELSE 'logged' END \
                   AS Status FROM recent";
        let laid_out = format_sql(sql);
        assert!(laid_out.contains("recent AS (\n  SELECT\n    w.WellId,"), "{laid_out}");
        assert!(laid_out.contains("  CASE\n    WHEN LastLog IS NULL THEN 'never'\n    ELSE 'logged'\n  END AS Status"), "{laid_out}");
        assert_eq!(token_texts(&laid_out), token_texts(sql));
    }

    #[test]
    fn keyword_case_and_names_stay_as_written() {
        let laid_out = format_sql("select a, B from dbo.MyTable where a = 1");
        assert_eq!(laid_out, "select\n  a,\n  B\nfrom\n  dbo.MyTable\nwhere\n  a = 1");
    }

    #[test]
    fn t_sql_lexemes_the_formatter_misreads_come_back_whole() {
        let sql = "SELECT TOP (10) c.[Customer Name], c.[a]]b], @@ROWCOUNT AS rc, 0x1F AS bin, $12.50 AS price, @p AS v, \
                   'C:\\temp\\' AS path, \"Quoted Col\" FROM #staging c JOIN ##shared s ON s.id = c.id WHERE x = 1 \
                   /* nested /* inner */ still */ AND y = 2";
        let laid_out = format_sql(sql);
        assert!(laid_out.contains('\n'), "{laid_out}");
        assert_eq!(token_texts(&laid_out), token_texts(sql));
        for lexeme in [
            "c.[Customer Name]",
            "[a]]b]",
            "@@ROWCOUNT",
            "0x1F",
            "$12.50",
            "'C:\\temp\\'",
            "\"Quoted Col\"",
            "#staging c",
            "##shared s",
            "/* nested /* inner */ still */",
        ] {
            assert!(laid_out.contains(lexeme), "{lexeme} in {laid_out}");
        }
    }

    #[test]
    fn a_call_keeps_its_parenthesis_against_its_name() {
        let laid_out = format_sql("SELECT CONVERT(varchar(10), d, 120) AS a, REPLACE(n, 'x', 'y') AS b FROM t");
        assert!(laid_out.contains("CONVERT(varchar(10), d, 120) AS a"), "{laid_out}");
        assert!(laid_out.contains("REPLACE(n, 'x', 'y') AS b"), "{laid_out}");
    }

    #[test]
    fn a_subquery_broken_onto_lines_closes_on_a_line_of_its_own() {
        assert_eq!(
            format_sql("SELECT a FROM t WHERE b IN (SELECT NULL)"),
            "SELECT\n  a\nFROM\n  t\nWHERE\n  b IN (\n    SELECT\n      NULL\n  )"
        );
        let laid_out = format_sql("SELECT ROW_NUMBER() OVER (PARTITION BY a ORDER BY (SELECT NULL)) AS rn FROM t");
        assert!(laid_out.contains("    ORDER BY\n      (\n        SELECT\n          NULL\n      )\n  ) AS rn"), "{laid_out}");
        // A block the formatter kept on one line stays there.
        assert!(format_sql("SELECT a FROM t WHERE b IN (1, 2, 3)").contains("b IN (1, 2, 3)"));
    }

    #[test]
    fn a_line_comment_keeps_its_line_and_text_inside_literals_is_untouched() {
        let sql = "SELECT a, -- the key  \n b FROM t WHERE c = 'two  spaces\nand a line' AND d = 1";
        let laid_out = format_sql(sql);
        assert!(laid_out.contains("-- the key\n"), "{laid_out}");
        assert!(laid_out.contains("'two  spaces\nand a line'"), "{laid_out}");
        assert_eq!(token_texts(&laid_out), token_texts(sql));
    }

    #[test]
    fn statements_are_parted_by_a_blank_line() {
        assert_eq!(format_sql("SELECT a FROM t;SELECT b FROM u"), "SELECT\n  a\nFROM\n  t;\n\nSELECT\n  b\nFROM\n  u");
    }

    #[test]
    fn laying_out_twice_changes_nothing() {
        for sql in [
            "SELECT c.[Customer Name], CONVERT(int, x) FROM #t c WHERE c.a IN (SELECT a FROM u) -- note\n AND b = 1",
            "MERGE INTO d USING s ON d.k = s.k WHEN MATCHED THEN UPDATE SET d.a = s.a WHEN NOT MATCHED THEN INSERT (a) VALUES (s.a);",
        ] {
            let once = format_sql(sql);
            assert_eq!(format_sql(&once), once);
        }
    }

    #[test]
    fn a_statement_that_cannot_be_laid_out_safely_comes_back_as_it_came() {
        for sql in [
            "SELECT 'never closed FROM t",
            "SELECT [never closed FROM t",
            "SELECT a /* never closed FROM t",
            "   ",
            "",
        ] {
            assert_eq!(format_sql(sql), sql);
        }
        let oversized = format!("SELECT {} FROM t", vec!["a"; MAX_FORMAT_LEN / 2].join(", "));
        assert_eq!(format_sql(&oversized), oversized);
    }

    #[test]
    fn text_that_already_holds_the_placeholder_stem_gets_a_longer_one() {
        let sql = "SELECT 'x' AS sqlfmtph0, [y] AS SQLFMTPHX0 FROM t";
        let laid_out = format_sql(sql);
        assert_eq!(laid_out, "SELECT\n  'x' AS sqlfmtph0,\n  [y] AS SQLFMTPHX0\nFROM\n  t");
    }

    #[test]
    fn unicode_names_and_literals_survive() {
        let sql = "SELECT [Kundenummer ø], N'Æble' AS frukt, straße FROM dbo.Tabell WHERE navn = N'Ørjan'";
        let laid_out = format_sql(sql);
        assert!(laid_out.contains('\n'), "{laid_out}");
        assert_eq!(token_texts(&laid_out), token_texts(sql));
    }

    #[test]
    fn every_sql_field_of_an_answer_is_laid_out_and_nothing_else() {
        let one_line = "SELECT a FROM t";
        let laid_out = "SELECT\n  a\nFROM\n  t";
        let mut answer = json!({
            "sql": one_line,
            "question": one_line,
            "result": { "sql": one_line, "rows": [[one_line]] },
            "matches": [{ "sql": one_line, "reportSql": one_line, "sourceSql": null, "hasSuggestedSql": true }],
            "statements": { "failedStatementSql": [one_line, 3] },
            "sqlBlock": one_line,
        });
        format_sql_fields(&mut answer);
        assert_eq!(
            answer,
            json!({
                "sql": laid_out,
                "question": one_line,
                "result": { "sql": laid_out, "rows": [[one_line]] },
                "matches": [{ "sql": laid_out, "reportSql": laid_out, "sourceSql": null, "hasSuggestedSql": true }],
                "statements": { "failedStatementSql": [laid_out, 3] },
                "sqlBlock": one_line,
            })
        );
    }

    #[test]
    fn the_lexer_keeps_operators_whole_and_nests_block_comments() {
        assert_eq!(token_texts("a>=1 AND b<>2 OR c!=3"), ["a", ">=", "1", "AND", "b", "<>", "2", "OR", "c", "!=", "3"]);
        assert_eq!(token_texts("x/* a /* b */ c */y"), ["x", "/* a /* b */ c */", "y"]);
        assert_eq!(token_texts("a--c\nb"), ["a", "--c", "b"]);
        assert_eq!(token_texts("1.5e3 .5 0xFF $3"), ["1.5e3", ".5", "0xFF", "$3"]);
    }
}
