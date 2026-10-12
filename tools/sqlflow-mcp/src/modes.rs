//! The two modes a conversation answers in, and which tools a session in each one lists.
//!
//! Estate mode, the default, answers questions about the estate: flows, runs, lineage, schedules, the catalog and the
//! docs, reading no rows. Data mode answers a question from warehouse data and needs only the semantic layer, the stored
//! questions, the query tools and saving an answer. A message beginning with `!data` (or a bare `!`) starts data mode,
//! its follow-ups stay in it without the command, and `!exit`, or a follow-up that turns to the estate, ends it; the
//! server's instructions say so to every client. Where a leading `!` does not reach the model (Claude Code runs it as a
//! shell command), the person switches in words, and the model calls `start_data_mode` or `end_data_mode`; `get_mode`
//! says which mode the conversation is in.
//!
//! A client that opens a session per message (a model provider's MCP connector, as the GUI chat and the Slack bot use)
//! names the conversation's mode on the MCP address, `mode=data` or `mode=estate`, and the session lists only that
//! mode's tools: a data question chooses among a dozen tools rather than a hundred. A switch moves such a session to
//! the other mode's tools, and its client asks the question again there. A session that names no mode (a stdio client
//! such as VS Code or Claude Code, which keeps one session across chats and does not reliably refresh its tools
//! mid-session) lists every tool; a switch there records the mode, which `get_mode` reports.
//!
//! An editor that wants a small list registers the server in one mode (`--mode data` or `--mode estate`, or
//! `SQLFLOW_MCP_MODE`), once per mode it uses, and turns on the registration a chat needs. A registered session stays in
//! its mode, since a client that does not refresh its tools would otherwise hold the other mode's list: it lists that
//! mode's tools and the setup tools (it signs in on its own), never a switch.

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
}
