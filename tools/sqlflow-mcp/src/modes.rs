//! The two modes a conversation answers in, the commands that move it between them, and which tools a session in each
//! one lists and answers.
//!
//! Estate mode, the default, answers questions about the estate: flows, runs, lineage, schedules, the catalog and the
//! docs, reading no rows. Data mode answers a question from warehouse data and needs only the semantic layer, the stored
//! questions, the query tools and saving an answer. Only the person's command moves a conversation between them, read
//! from the first characters of their message by one grammar ([`command_of`]): `!data` or a bare `!` starts data mode,
//! `!exit` or `!estate` ends it, and a message without a command stays in the mode the conversation is in. A model's
//! judgement of what a question is about never moves it, and data and estate questions are not mixed in one mode.
//!
//! Every session answers a call only in the mode it is in, whatever it lists: a tool of the other mode is answered with
//! the command that moves the conversation, never run. How a session comes to be in its mode is one of three ways.
//!
//! - Registered: an editor registers the server in one mode (`--mode data` or `--mode estate`, or `SQLFLOW_MCP_MODE`),
//!   once per mode it uses, and turns on the registration a chat needs. The session lists that mode's tools and the setup
//!   tools (it signs in on its own), and stays in the mode; a switch is answered, never made.
//! - Named: a client that opens a session per message (a model provider's MCP connector, as the GUI chat and the Slack
//!   bot use) reads the person's command itself and names the mode on the MCP address, `mode=data` or `mode=estate`.
//!   The session lists only that mode's tools, never a switch, and stays in the mode for the message.
//! - Switched: a session that names no mode (an editor's single registration, which keeps one session across chats and
//!   may not refresh its tools mid-session) lists every tool and starts in estate mode. The model relays the person's
//!   message to `start_data_mode` or `end_data_mode`, and the server switches only when that message carries the matching
//!   command; `get_mode` says which mode the session is in.

/// The mode a conversation answers in.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Mode {
    /// Questions about the estate, from its metadata; the default.
    Estate,
    /// A question answered from warehouse data, through the semantic layer and the query tools.
    Data,
}

impl Mode {
    /// The mode's name, as the MCP address's `mode` query parameter, `--mode` and the mode tools' answers spell it.
    pub fn name(self) -> &'static str {
        match self {
            Mode::Estate => "estate",
            Mode::Data => "data",
        }
    }

    /// The mode `name` spells (`data` or `estate`, as [`name`](Self::name) writes them), if it is one.
    pub fn named(name: &str) -> Option<Mode> {
        match name {
            "data" => Some(Mode::Data),
            "estate" => Some(Mode::Estate),
            _ => None,
        }
    }

    /// The other mode.
    pub fn other(self) -> Mode {
        match self {
            Mode::Estate => Mode::Data,
            Mode::Data => Mode::Estate,
        }
    }
}

/// The tool that switches a conversation to data mode.
pub const START_DATA_MODE: &str = "start_data_mode";

/// The tool that switches a conversation back to estate mode.
pub const END_DATA_MODE: &str = "end_data_mode";

/// The tool that reports which mode a conversation is in.
pub const GET_MODE: &str = "get_mode";

/// The tools only data mode needs: the stored questions, the query tools, saving an answer, and the way out.
pub const DATA_ONLY: &[&str] = &[
    "find_similar_questions",
    "prepare_query",
    "run_query",
    "auto_run_trusted_match",
    "confirm_question",
    END_DATA_MODE,
];

/// The tools a session in either mode lists: the semantic layer read on its own (which tables a question may use,
/// what their columns mean), the report visual a stored question came from, and which mode the conversation is in.
pub const SHARED: &[&str] = &[
    "get_semantic_layer",
    "search_semantic_layer",
    "list_semantic_tables",
    "describe_semantic_table",
    "describe_subscriber_report",
    GET_MODE,
];

/// The tools that point a session at the control plane and sign it in. A stdio session holds its own sign-in, so it
/// needs them whatever its mode; over HTTP the ones that sign in are not offered at all.
pub const SETUP: &[&str] = &[
    "get_control_plane_url",
    "set_control_plane_url",
    "check_connectivity",
    "login",
    "check_auth_status",
    "set_access_token",
    "logout",
];

/// Whether a session listing `mode`'s tools lists `tool`; `None` is a session that names no mode, which lists every
/// tool. Estate mode lists every tool but the ones only data mode needs, the way into data mode included; data mode
/// lists only its own and the shared ones, so a host module's tools, which answer questions about the estate, belong to
/// estate mode.
pub fn lists(mode: Option<Mode>, tool: &str) -> bool {
    match mode {
        None => true,
        Some(Mode::Estate) => !DATA_ONLY.contains(&tool),
        Some(Mode::Data) => DATA_ONLY.contains(&tool) || SHARED.contains(&tool),
    }
}

/// Whether a session registered in `mode` lists `tool`: that mode's tools and the setup tools, and no switch, since the
/// session stays in the mode it was registered in.
pub fn lists_registered(mode: Mode, tool: &str) -> bool {
    switches_to(tool).is_none() && (lists(Some(mode), tool) || SETUP.contains(&tool))
}

/// Whether a session its client opened in `mode` (`mode=` on the MCP address) lists `tool`: that mode's tools, never a
/// switch, since the client read the person's command and the session stays in the mode for the message.
pub fn named_lists(mode: Mode, tool: &str) -> bool {
    switches_to(tool).is_none() && lists(Some(mode), tool)
}

/// Whether a session that switches (one that names no mode) answers a call of `tool` while it is in `mode`: the mode
/// tools and the setup tools in either mode, and otherwise the tools `mode` lists. It lists every tool, since its client
/// may not refresh its list after a switch, so this, not its list, keeps data and estate questions apart.
pub fn answers_in(mode: Mode, tool: &str) -> bool {
    is_mode_tool(tool) || SETUP.contains(&tool) || lists(Some(mode), tool)
}

/// The mode a switch tool moves a conversation to, when `tool` is one.
pub fn switches_to(tool: &str) -> Option<Mode> {
    match tool {
        START_DATA_MODE => Some(Mode::Data),
        END_DATA_MODE => Some(Mode::Estate),
        _ => None,
    }
}

/// Whether `tool` is one of the mode tools, which any session answers whatever its list: a client holding a stale
/// list still switches, and still learns which mode it is in.
pub fn is_mode_tool(tool: &str) -> bool {
    tool == GET_MODE || switches_to(tool).is_some()
}

/// The mode an inbound HTTP request's session is in, from the MCP address's query (`mode=data` or `mode=estate`),
/// when it names one. Any other value names none, and the session lists every tool.
pub fn requested(query: Option<&str>) -> Option<Mode> {
    query?.split('&').find_map(|pair| match pair.split_once('=')? {
        ("mode", "data") => Some(Mode::Data),
        ("mode", "estate") => Some(Mode::Estate),
        _ => None,
    })
}

/// The character a command starts with.
pub const COMMAND: char = '!';

/// The word that may follow the `!` directly for data mode (`!data which regions`). It counts only touching the `!`, since
/// it is an ordinary word too: `! data quality by region` is the bare command and a question about data quality.
const DATA_WORD: &str = "data";

/// The words that end data mode after the `!`, directly or after white space (`!exit`, `! exit`, `!estate`).
const EXIT_WORDS: &[&str] = &["exit", "estate"];

/// What may follow a command word besides white space and the end of the message: sentence punctuation, so `!exit.` is
/// the command and `!exit-interview` or `!data_quality` a question for the data.
const WORD_ENDS: &[char] = &['.', ',', ':', ';', '!', '?'];

/// Characters that show nothing and that a message may carry before its `!` or after it (pasted text, an editor's
/// byte order mark), skipped as white space is.
const INVISIBLE: &[char] = &['\u{180E}', '\u{200B}', '\u{200C}', '\u{200D}', '\u{2060}', '\u{FEFF}'];

/// What a person's message asks of the conversation: the mode its command sets, and the question after the command.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Command {
    /// The mode the command sets.
    pub mode: Mode,
    /// The rest of the message, without the command, its separator or white space at either end; empty when the
    /// message is the command alone.
    pub question: String,
}

/// The command `message` carries, when it starts with one. White space (Unicode White_Space) and the invisible
/// characters are skipped, then one or more `!` (invisible characters among them) start the command. `data` directly
/// after them is the data command's word; otherwise, after any white space, `exit` or `estate` is the command that ends
/// data mode. A word counts in any case of its ASCII letters, ending the message or followed by white space, an
/// invisible character or sentence punctuation. Anything else after the `!` is the question of a bare `!`, which is data
/// mode. None for a message that does not start with a `!`, whatever it asks. The control plane reads a message by the
/// same grammar (`AssistantQuestions`), and one table of cases (`tests/assistant-commands.json`) holds both to it.
pub fn command_of(message: &str) -> Option<Command> {
    let rest = message.trim_start_matches(skippable).strip_prefix(COMMAND)?;
    let rest = rest.trim_start_matches(|c| c == COMMAND || INVISIBLE.contains(&c));
    if let Some(after) = after_word(rest, DATA_WORD) {
        return Some(Command { mode: Mode::Data, question: question_after(after) });
    }
    let rest = rest.trim_start_matches(skippable);
    for word in EXIT_WORDS {
        if let Some(after) = after_word(rest, word) {
            return Some(Command { mode: Mode::Estate, question: question_after(after) });
        }
    }
    Some(Command { mode: Mode::Data, question: rest.trim_end_matches(skippable).to_string() })
}

/// The question after a command word: its separator (white space and sentence punctuation) and trailing white space off.
fn question_after(after: &str) -> String {
    after.trim_start_matches(|c| skippable(c) || WORD_ENDS.contains(&c)).trim_end_matches(skippable).to_string()
}

/// White space or a character that shows nothing.
fn skippable(c: char) -> bool {
    c.is_whitespace() || INVISIBLE.contains(&c)
}

/// What follows `word` at the start of `text` when `text` starts with it as a word of its own: the letters matched
/// without regard to ASCII case (never Unicode case folding, which would read a dotless `ı` as `i`), and followed by the
/// end, white space, an invisible character or sentence punctuation.
fn after_word<'a>(text: &'a str, word: &str) -> Option<&'a str> {
    let head = text.get(..word.len())?;
    if !head.eq_ignore_ascii_case(word) {
        return None;
    }
    let after = &text[word.len()..];
    match after.chars().next() {
        None => Some(after),
        Some(c) if skippable(c) || WORD_ENDS.contains(&c) => Some(after),
        Some(_) => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_session_naming_no_mode_lists_every_tool() {
        for tool in DATA_ONLY.iter().chain(SHARED).chain(&[START_DATA_MODE, "list_runs", "trigger_run", "delivery_find_records"]) {
            assert!(lists(None, tool), "{tool}");
        }
    }

    #[test]
    fn estate_mode_lists_everything_but_what_only_data_mode_needs() {
        for tool in ["list_runs", "describe_object", "trigger_run", "delivery_find_records", START_DATA_MODE, GET_MODE] {
            assert!(lists(Some(Mode::Estate), tool), "{tool}");
        }
        for tool in DATA_ONLY {
            assert!(!lists(Some(Mode::Estate), tool), "{tool}");
        }
    }

    #[test]
    fn data_mode_lists_its_own_tools_and_the_shared_ones_alone() {
        for tool in DATA_ONLY.iter().chain(SHARED) {
            assert!(lists(Some(Mode::Data), tool), "{tool}");
        }
        for tool in ["list_runs", "describe_object", "search_all", "trigger_run", "delivery_find_records", START_DATA_MODE] {
            assert!(!lists(Some(Mode::Data), tool), "{tool}");
        }
    }

    #[test]
    fn the_switches_name_where_they_go_and_every_mode_tool_is_answered_anywhere() {
        assert_eq!(switches_to(START_DATA_MODE), Some(Mode::Data));
        assert_eq!(switches_to(END_DATA_MODE), Some(Mode::Estate));
        assert_eq!(switches_to(GET_MODE), None);
        assert!(is_mode_tool(START_DATA_MODE) && is_mode_tool(END_DATA_MODE) && is_mode_tool(GET_MODE));
        assert!(!is_mode_tool("find_similar_questions"));
    }

    #[test]
    fn the_mode_a_session_is_in_is_read_from_the_query() {
        assert_eq!(requested(Some("mode=data")), Some(Mode::Data));
        assert_eq!(requested(Some("surface=assistant&mode=data")), Some(Mode::Data));
        assert_eq!(requested(Some("mode=estate&surface=assistant")), Some(Mode::Estate));
        assert_eq!(requested(None), None);
        assert_eq!(requested(Some("")), None);
        assert_eq!(requested(Some("mode=Data")), None);
        assert_eq!(requested(Some("mode=business")), None);
        assert_eq!(requested(Some("modes=data")), None);
        assert_eq!(requested(Some("mode")), None);
        assert_eq!(requested(Some("flag&mode=data")), Some(Mode::Data));
    }

    #[test]
    fn a_mode_is_named_as_the_query_spells_it() {
        assert_eq!(requested(Some(&format!("mode={}", Mode::Data.name()))), Some(Mode::Data));
        assert_eq!(requested(Some(&format!("mode={}", Mode::Estate.name()))), Some(Mode::Estate));
    }

    #[test]
    fn a_registration_names_its_mode_as_the_address_does() {
        for mode in [Mode::Data, Mode::Estate] {
            assert_eq!(Mode::named(mode.name()), Some(mode));
            assert_eq!(mode.other().other(), mode);
            assert_ne!(mode.other(), mode);
        }
        for name in ["", "Data", "ESTATE", "business", " data", "data "] {
            assert_eq!(Mode::named(name), None, "{name:?}");
        }
    }

    #[test]
    fn a_registered_session_lists_its_modes_tools_and_setup_and_never_a_switch() {
        for tool in DATA_ONLY.iter().chain(SHARED).chain(SETUP).filter(|tool| switches_to(tool).is_none()) {
            assert!(lists_registered(Mode::Data, tool), "{tool}");
        }
        for tool in [START_DATA_MODE, END_DATA_MODE, "list_runs", "describe_object", "delivery_find_records"] {
            assert!(!lists_registered(Mode::Data, tool), "{tool}");
        }
        for tool in ["list_runs", "describe_object", "trigger_run", "delivery_find_records", GET_MODE, "login"] {
            assert!(lists_registered(Mode::Estate, tool), "{tool}");
        }
        for tool in [START_DATA_MODE, END_DATA_MODE, "find_similar_questions", "run_query"] {
            assert!(!lists_registered(Mode::Estate, tool), "{tool}");
        }
    }

    /// One table holds the server and the control plane to one grammar: each case's mode and question as this reader
    /// reads them, and the control plane's suite reads the same file.
    #[test]
    fn every_message_in_the_shared_table_is_read_as_the_table_says() {
        let table: serde_json::Value =
            serde_json::from_str(include_str!("../tests/assistant-commands.json")).expect("the table is JSON");
        let cases = table["cases"].as_array().expect("the table has cases");
        assert!(cases.len() >= 50, "the table covers the grammar, {} cases", cases.len());
        for case in cases {
            let message = case["message"].as_str().expect("each case has a message");
            let read = command_of(message);
            let mode = case["mode"].as_str().map(|name| Mode::named(name).expect("the table names a mode"));
            assert_eq!(read.as_ref().map(|c| c.mode), mode, "{message:?}");
            assert_eq!(read.as_ref().map(|c| c.question.as_str()), case["question"].as_str(), "{message:?}");
        }
    }

    /// Every character there is, before a command: white space and the invisible characters are skipped, a second `!`
    /// is the question, and anything else means the message carries no command.
    #[test]
    fn only_white_space_and_invisible_characters_may_come_before_the_command() {
        for c in (0..=0x10FFFF_u32).filter_map(char::from_u32) {
            let read = command_of(&format!("{c}!exit"));
            if c.is_whitespace() || INVISIBLE.contains(&c) {
                assert_eq!(read.map(|r| r.mode), Some(Mode::Estate), "U+{:04X}", c as u32);
            } else if c == COMMAND {
                assert_eq!(read, Some(Command { mode: Mode::Estate, question: String::new() }), "a repeated `!` is one command");
            } else {
                assert_eq!(read, None, "U+{:04X}", c as u32);
            }
        }
    }

    /// Every character there is, after each command word: the word is the command only when white space, an invisible
    /// character or sentence punctuation follows it; anything else makes it a word of the question.
    #[test]
    fn a_command_word_ends_only_at_white_space_or_sentence_punctuation() {
        let words = std::iter::once((DATA_WORD, Mode::Data)).chain(EXIT_WORDS.iter().map(|word| (*word, Mode::Estate)));
        for (word, mode) in words {
            for c in (0..=0x10FFFF_u32).filter_map(char::from_u32) {
                let message = format!("!{word}{c}x");
                let read = command_of(&message).expect("a `!` message carries a command");
                if c.is_whitespace() || INVISIBLE.contains(&c) || WORD_ENDS.contains(&c) {
                    assert_eq!(read, Command { mode, question: "x".to_string() }, "{message:?}");
                } else {
                    assert_eq!(read, Command { mode: Mode::Data, question: format!("{word}{c}x") }, "{message:?}");
                }
            }
        }
    }

    /// The words are matched in ASCII case only: every spelling of each in upper and lower case is the command, and a
    /// letter whose Unicode case folds to an ASCII one (dotless ı, Kelvin K) is not.
    #[test]
    fn a_command_word_matches_in_any_ascii_case_and_no_other() {
        let words = std::iter::once((DATA_WORD, Mode::Data)).chain(EXIT_WORDS.iter().map(|word| (*word, Mode::Estate)));
        for (word, mode) in words {
            for mask in 0..(1_u32 << word.len()) {
                let spelled: String = word
                    .chars()
                    .enumerate()
                    .map(|(i, c)| if mask & (1 << i) != 0 { c.to_ascii_uppercase() } else { c })
                    .collect();
                assert_eq!(command_of(&format!("!{spelled} q")).map(|c| c.mode), Some(mode), "{spelled}");
            }
        }
        for lookalike in ["!exıt", "!EXİT", "!estate\u{212A}", "!dat\u{0430}"] {
            assert_eq!(command_of(lookalike).map(|c| c.mode), Some(Mode::Data), "{lookalike}");
        }
    }

    /// `data` is the command's word only touching the `!` (it is an ordinary word too), while `exit` and `estate` end data
    /// mode after white space as well; any number of `!` is one command.
    #[test]
    fn the_data_word_touches_the_command_and_the_exit_words_may_stand_apart() {
        let read = |message: &str| command_of(message).map(|c| (c.mode, c.question));
        assert_eq!(read("!data quality by region"), Some((Mode::Data, "quality by region".to_string())));
        assert_eq!(read("! data quality by region"), Some((Mode::Data, "data quality by region".to_string())));
        assert_eq!(read("!\u{200B}data quality"), Some((Mode::Data, "quality".to_string())));
        for message in ["!exit", "! exit", "!\t\nexit", "!!!exit", "!\u{FEFF}!  estate"] {
            assert_eq!(read(message), Some((Mode::Estate, String::new())), "{message:?}");
        }
        assert_eq!(read("!!!data which regions"), Some((Mode::Data, "which regions".to_string())));
        assert_eq!(read("! !data which regions"), Some((Mode::Data, "!data which regions".to_string())));
    }

    #[test]
    fn a_switched_session_answers_only_the_tools_of_the_mode_it_is_in() {
        for tool in DATA_ONLY.iter().chain(SHARED).chain(SETUP).chain(&[START_DATA_MODE]) {
            assert!(answers_in(Mode::Data, tool), "{tool}");
        }
        for tool in ["list_runs", "describe_object", "search_all", "trigger_run", "list_schedules", "list_probes"] {
            assert!(!answers_in(Mode::Data, tool), "{tool}");
            assert!(answers_in(Mode::Estate, tool), "{tool}");
        }
        for tool in DATA_ONLY.iter().filter(|tool| !is_mode_tool(tool)) {
            assert!(!answers_in(Mode::Estate, tool), "{tool}");
        }
        for tool in SHARED.iter().chain(SETUP).chain(&[START_DATA_MODE, END_DATA_MODE, GET_MODE]) {
            assert!(answers_in(Mode::Estate, tool), "{tool}");
        }
    }

    #[test]
    fn a_named_session_lists_its_modes_tools_and_never_a_switch() {
        for mode in [Mode::Data, Mode::Estate] {
            for tool in [START_DATA_MODE, END_DATA_MODE] {
                assert!(!named_lists(mode, tool), "{mode:?} {tool}");
            }
            assert!(named_lists(mode, GET_MODE), "{mode:?}");
        }
        for tool in DATA_ONLY.iter().chain(SHARED).filter(|tool| switches_to(tool).is_none()) {
            assert!(named_lists(Mode::Data, tool), "{tool}");
        }
        assert!(!named_lists(Mode::Data, "list_runs") && !named_lists(Mode::Data, "login"));
        assert!(named_lists(Mode::Estate, "list_runs") && !named_lists(Mode::Estate, "run_query"));
    }
}
