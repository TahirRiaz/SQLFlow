//! The two modes a conversation answers in, and which tools a session in each one lists.
//!
//! Estate mode, the default, answers questions about the estate: flows, runs, lineage, schedules, the catalog and the
//! docs, reading no rows. Data mode answers a question from warehouse data and needs only the semantic layer, the stored
//! questions, the query tools and saving an answer. A message beginning with `!` (or `!cwd`) starts data mode, its
//! follow-ups stay in it without the command, and `!exit`, or a follow-up that turns to the estate, ends it; the
//! server's instructions say so to every client.
//!
//! A client that opens a session per message (a model provider's MCP connector, as the GUI chat and the Slack bot use)
//! names the conversation's mode on the MCP address, `mode=data` or `mode=estate`, and the session lists only that
//! mode's tools: a data question chooses among a dozen tools rather than a hundred. A session that names no mode (a
//! stdio client such as VS Code or Claude Code, which keeps one session for a whole chat and does not refresh its tools
//! mid-answer) lists every tool, and the model keeps the mode from the instructions alone. A data-mode session lists
//! `end_data_mode`, which the model calls when a follow-up turns to the estate: the session switches, tells the client
//! its tools changed, and a client that opened it per message asks again in estate mode.

/// The mode a conversation answers in.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Mode {
    /// Questions about the estate, from its metadata; the default.
    Estate,
    /// A question answered from warehouse data, through the semantic layer and the query tools.
    Data,
}

impl Mode {
    /// The mode's name, as the MCP address's `mode` query parameter and `end_data_mode`'s answer spell it.
    pub fn name(self) -> &'static str {
        match self {
            Mode::Estate => "estate",
            Mode::Data => "data",
        }
    }
}

/// The tool a data-mode session lists for leaving data mode.
pub const END_DATA_MODE: &str = "end_data_mode";

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
/// what their columns mean) and the report visual a stored question came from.
pub const SHARED: &[&str] = &[
    "get_semantic_layer",
    "search_semantic_layer",
    "list_semantic_tables",
    "describe_semantic_table",
    "describe_subscriber_report",
];

/// Whether a session in `mode` lists, and answers, `tool`. A session that names no mode lists every tool but
/// `end_data_mode`, which only means something in a data-mode session. Estate mode lists every tool but the ones only
/// data mode needs; data mode lists only its own and the shared ones, so a host module's tools, which answer questions
/// about the estate, belong to estate mode.
pub fn lists(mode: Option<Mode>, tool: &str) -> bool {
    match mode {
        None => tool != END_DATA_MODE,
        Some(Mode::Estate) => !DATA_ONLY.contains(&tool),
        Some(Mode::Data) => DATA_ONLY.contains(&tool) || SHARED.contains(&tool),
    }
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_session_naming_no_mode_lists_every_tool_but_the_way_out_of_data_mode() {
        for tool in DATA_ONLY.iter().chain(SHARED).chain(&["list_runs", "describe_object", "trigger_run", "delivery_find_records"]) {
            assert_eq!(lists(None, tool), *tool != END_DATA_MODE, "{tool}");
        }
    }

    #[test]
    fn estate_mode_lists_everything_but_what_only_data_mode_needs() {
        for tool in ["list_runs", "describe_object", "trigger_run", "delivery_find_records"] {
            assert!(lists(Some(Mode::Estate), tool), "{tool}");
        }
        for tool in DATA_ONLY {
            assert!(!lists(Some(Mode::Estate), tool), "{tool}");
        }
        for tool in SHARED {
            assert!(lists(Some(Mode::Estate), tool), "{tool}");
        }
    }

    #[test]
    fn data_mode_lists_its_own_tools_and_the_shared_ones_alone() {
        for tool in DATA_ONLY.iter().chain(SHARED) {
            assert!(lists(Some(Mode::Data), tool), "{tool}");
        }
        for tool in ["list_runs", "describe_object", "search_all", "trigger_run", "delivery_find_records"] {
            assert!(!lists(Some(Mode::Data), tool), "{tool}");
        }
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
}
