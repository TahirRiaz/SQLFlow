//! The SQLFlow MCP server: tool definitions and dispatch.
//!
//! Two capability tiers, mirroring DeltaForge's design:
//!   * **Offline**: the embedded reference corpus (doc tools) and the
//!     `.flow.yaml` analysis engine (`validate_flow`, key lookup). No network.
//!   * **Online**: a proxy over the control plane's `/api/v1` read surface
//!     (catalog, lineage, runs, schedules, search, summary) plus the operate
//!     surface (trigger/cancel), gated by device-flow authentication.

use std::sync::Arc;

use rmcp::handler::server::router::tool::ToolRouter;
use rmcp::handler::server::wrapper::Parameters;
use rmcp::model::{ServerCapabilities, ServerInfo};
use rmcp::{schemars, tool, tool_handler, tool_router, ServerHandler};
use schemars::JsonSchema;
use serde::Deserialize;
use serde_json::{json, Value};

use crate::control_plane::{ControlPlane, PollOutcome, WhoAmI};
use crate::docs::DocsIndex;
use crate::links::GuiLinks;
use sqlflow_lang::census::Census;

#[derive(Clone)]
pub struct SqlFlowMcp {
    docs: Arc<DocsIndex>,
    cp: Arc<ControlPlane>,
    /// The GUI routes every online result is decorated with, so an answer can hand the reader a way
    /// to open the thing it is about.
    links: GuiLinks,
    /// True when serving over HTTP: every request carries the caller's own bearer
    /// (scoped around dispatch in `call_tool`), so the server-side sign-in tools are
    /// inert, the control-plane URL is operator-fixed, and tools that read files on
    /// the server host are disabled.
    http_mode: bool,
    tool_router: ToolRouter<Self>,
}

impl SqlFlowMcp {
    /// A stdio-mode server: credentials are managed locally (device flow / pasted
    /// token) and host-local tools are available.
    pub fn new(docs: Arc<DocsIndex>, cp: Arc<ControlPlane>) -> Self {
        Self::with_mode(docs, cp, false)
    }

    /// An HTTP-mode server instance, created per session by the HTTP transport.
    pub fn new_http(docs: Arc<DocsIndex>, cp: Arc<ControlPlane>) -> Self {
        Self::with_mode(docs, cp, true)
    }

    fn with_mode(docs: Arc<DocsIndex>, cp: Arc<ControlPlane>, http_mode: bool) -> Self {
        SqlFlowMcp {
            docs,
            cp,
            links: GuiLinks::from_env(),
            http_mode,
            tool_router: Self::grounded_router(),
        }
    }

    /// The generated router with [`GROUNDING_RULE`] appended to every tool description. The description is
    /// the one text every consumer of this server sees: the built-in chatbot and the Slack bot reach the tools
    /// through their provider's MCP connector, which forwards descriptions but not the server's
    /// `instructions`, and an external client may ignore `instructions` too. Appending the rule here, once,
    /// makes it hold on every surface without 85 hand-copies that would drift.
    fn grounded_router() -> ToolRouter<Self> {
        let mut router = Self::tool_router();
        for route in router.map.values_mut() {
            let description = route.attr.description.take().unwrap_or_default();
            route.attr.description = Some(format!("{description}\n\n{GROUNDING_RULE}").into());
        }
        router
    }
}

// How a tool description uses CAPITALS, enforced by `capitals_are_rationed_to_routing_and_safety`.
//
// Capitals are a routing signal, and a signal only works while it is scarce. The descriptions had drifted
// to 222 capitalised words across the surface, at which point START HERE appeared on two tools answering
// different questions, USE THIS on three that overlap, and the emphasis had stopped carrying information:
// a model reading everything shouted reads nothing as urgent.
//
// So capitals are rationed to two jobs:
//
// 1. Routing, in the opening words, naming the one question this tool is the right answer to when a
//    neighbouring tool would otherwise be picked. `START HERE` marks the entry point of a family and
//    belongs to exactly one tool (asserted).
// 2. Safety, for a constraint whose breach causes real harm: an approval that must precede execution, a
//    surface that bypasses the column allow-list, a call that must not be made on the model's own
//    judgement.
//
// Everything else is lowercase prose. A contrast between two tools ("columns sees only synced objects,
// flowColumns sees pipeline-only columns") is carried by the sentence, not by shouting one word of it.
// Acronyms (SQL, YAML, DDL) are not emphasis and do not count against the budget.
/// The rule appended to every tool description: the model may state only what the tool returned. A missing
/// fact is reported as missing, never filled in from what a flow or a run of that kind usually looks like.
const GROUNDING_RULE: &str = "Grounding: state only values this result contains. A null or an empty list is \
the answer (nothing declared, never happened), not a gap to fill. If a fact is not in the result, say so and \
name the tool that holds it. Never invent rows, counts, SQL, or watermarks.";

#[cfg(test)]
mod grounding_tests {
    use super::*;

    /// Reads the router off a CONSTRUCTED server, not `grounded_router()` directly: `list_tools` serves
    /// `self.tool_router`, and a `#[tool_handler]` left on its default would serve the generated router
    /// instead, shipping ungrounded descriptions while a test calling `grounded_router()` still passed.
    #[test]
    fn every_tool_description_ends_with_the_grounding_rule() {
        let server = SqlFlowMcp::new(Arc::new(DocsIndex::load()), Arc::new(ControlPlane::from_env(true)));

        // Through ServerHandler::get_tool, which the #[tool_handler] macro generates from the SAME router
        // expression as list_tools. Reading server.tool_router directly would pass even with the macro left on
        // its default, which serves the ungrounded generated router: that is the bug this guards.
        let served = ServerHandler::get_tool(&server, "find_similar_questions")
            .expect("find_similar_questions is served");
        assert!(
            served.description.as_deref().unwrap_or("").ends_with(GROUNDING_RULE),
            "the SERVED description lacks the grounding rule; is #[tool_handler] missing `router = self.tool_router`?"
        );

        let tools = server.tool_router.list_all();
        assert!(tools.len() > 50, "expected the full tool set, got {}", tools.len());
        for tool in tools {
            let description = tool.description.as_deref().unwrap_or("");
            assert!(
                description.ends_with(GROUNDING_RULE),
                "tool '{}' lacks the grounding rule: {description}",
                tool.name
            );
            assert!(
                description.len() > GROUNDING_RULE.len() + 10,
                "tool '{}' has no description of its own",
                tool.name
            );
        }
    }
}

/// Replies for tools that do not apply when serving over HTTP.
const HTTP_MODE_AUTH_NOTE: &str = "Not applicable over HTTP: this server authenticates every \
request with the Authorization bearer supplied by the connecting client, so there is no \
server-side sign-in to start, poll, store, or clear.";
const HTTP_MODE_URL_NOTE: &str = "Not applicable over HTTP: the control-plane URL is fixed by \
the server operator (SQLFLOW_CONTROL_PLANE_URL) and cannot be changed by a client.";
const HTTP_MODE_DISCOVER_NOTE: &str = "discover_source is disabled over HTTP because it reads \
sample files on the server host, not on yours. Run sqlflow-mcp locally over stdio to use it.";
const HTTP_MODE_SCAFFOLD_NOTE: &str = "scaffold_ingestion_flow is disabled over HTTP because it \
runs the local `sqlflow` CLI and resolves the connection reference on the server host, not on \
yours. Run sqlflow-mcp locally over stdio to use it, or run `sqlflow catalog scaffold` yourself.";

// --- Parameter structs -----------------------------------------------------

#[derive(Debug, Deserialize, JsonSchema)]
pub struct SearchDocsInput {
    /// Keywords to match across doc titles, keywords, and summaries. Prime the
    /// query with SQLFlow terms: "job/trigger" → schedule; "folder" → repo;
    /// "upsert" → ingestion load; "wave" → lineage execution plan.
    pub query: String,
    /// Optional type filter: cli-command, flow-reference, source-type, concept, guide.
    #[serde(rename = "type")]
    pub doc_type: Option<String>,
    /// Maximum results (default 15, max 50).
    pub limit: Option<usize>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct IdInput {
    /// The doc id from the manifest (e.g. "cli-run", "flow-source", "concept-lineage").
    pub id: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct YamlPathInput {
    /// A `.flow.yaml` dot-path, e.g. "source.options.header" or "load.mode".
    #[serde(rename = "yamlPath")]
    pub yaml_path: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct CliCommandInput {
    /// A top-level CLI command, e.g. "run", "validate", "lineage", "catalog".
    pub command: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ListDocsInput {
    /// Optional type filter: cli-command, flow-reference, source-type, concept, guide.
    #[serde(rename = "type")]
    pub doc_type: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ValidateFlowInput {
    /// The full `.flow.yaml` document text to validate.
    pub yaml: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct FlowTypeInput {
    /// The flow kind: omit for the file flow, or one of ing, exp, sp, inv, hc, scm, sch, batch, api, cpy, sftp, cal, trl.
    #[serde(rename = "flowType")]
    pub flow_type: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct DescribeKeyInput {
    /// The census path, e.g. "source.type" or "load.mode".
    pub path: String,
    /// The flow kind selecting which census applies (see list_flow_keys).
    #[serde(rename = "flowType")]
    pub flow_type: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct UrlInput {
    /// The control-plane base URL, e.g. "https://sqlflow.example.com".
    pub url: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct TokenInput {
    /// A bearer access token issued by the control plane.
    pub token: String,
    /// Space-delimited scopes the token carries (default "read operate").
    pub scope: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct CheckAuthInput {
    /// The device code from `login`; omit to use the in-flight login.
    #[serde(rename = "deviceCode")]
    pub device_code: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct GuidInput {
    /// A catalog entity id (GUID).
    pub id: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct RepoIdInput {
    /// A repository id (GUID).
    #[serde(rename = "repoId")]
    pub repo_id: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct RunIdInput {
    /// A run id (GUID).
    #[serde(rename = "runId")]
    pub run_id: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ListPipelinesInput {
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// Flow kind filter (e.g. ing, exp, sp).
    pub kind: Option<String>,
    /// Filter by active state.
    pub active: Option<bool>,
    /// Substring name filter.
    pub name: Option<String>,
    /// Batch (source-system grouping) filter; "default" also matches flows that declare no batch.
    pub batch: Option<String>,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ListBatchesInput {
    /// Repository id (GUID) filter (optional).
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// Filter by active state (optional).
    pub active: Option<bool>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct DependenciesInput {
    /// A repository id (GUID).
    #[serde(rename = "repoId")]
    pub repo_id: String,
    /// Keep only the dependency edges touching this flow, in either direction (optional).
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct CatalogTreeInput {
    /// Server reference: give it to browse below one server.
    #[serde(rename = "serverRef")]
    pub server_ref: Option<String>,
    /// Database: give it (with serverRef) to browse below one database.
    pub database: Option<String>,
    /// Schema: give it to get the schema's object-kind groups with counts.
    pub schema: Option<String>,
    /// Object kind (Table, View, Procedure, Function, Trigger, Synonym, File): give it to list that
    /// kind's objects in the schema, paged.
    pub kind: Option<String>,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct PipelineColumnsInput {
    /// The pipeline id (GUID).
    pub id: String,
    /// Column set: "declared" or "detected".
    pub kind: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ListRunsInput {
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// Run status filter (queued, running, succeeded, failed, cancelled).
    pub status: Option<String>,
    #[serde(rename = "flowName")]
    pub flow_name: Option<String>,
    /// Grouping-label (source system) filter.
    pub batch: Option<String>,
    /// Only the latest run per pipeline.
    pub latest: Option<bool>,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct LineageObjectsInput {
    /// Substring name filter.
    pub name: Option<String>,
    /// Server reference filter.
    #[serde(rename = "serverRef")]
    pub server_ref: Option<String>,
    /// Database filter (exact).
    pub database: Option<String>,
    /// Schema filter (exact, e.g. "dbo").
    pub schema: Option<String>,
    /// Object kind filter (table, view, proc, function, file).
    pub kind: Option<String>,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ListSchemasInput {
    /// Server reference filter (optional).
    #[serde(rename = "serverRef")]
    pub server_ref: Option<String>,
    /// Database filter (optional).
    pub database: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct KeyInput {
    /// The catalog object key (serverRef+db+schema+name composite).
    pub key: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct SemanticSearchInput {
    /// One or more words: a table or column name, or a business term (a synonym such as "turnover" finds
    /// the column an admin tagged with it). Every word must match somewhere on the same table.
    pub q: String,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct SemanticTablesInput {
    /// Database filter (exact), as get_semantic_layer lists it.
    pub database: Option<String>,
    /// Schema filter (exact, e.g. "dbo"), as get_semantic_layer lists it.
    pub schema: Option<String>,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct SimilarQuestionsInput {
    /// The question to answer from warehouse data, in the user's own words, with the `!cwd` command prefix
    /// stripped. It need not be about business: an operational or data-quality question asked with `!cwd`
    /// belongs here too. Pass the
    /// rest exactly as they typed it: the match is on meaning, so rewording it into schema terms first
    /// throws away the signal this searches on.
    pub question: String,
    /// The business vocabulary to search for, expanded by YOU from the question: its own meaningful words
    /// first, then the synonyms and closely related business terms a dashboard might have used for the
    /// same thing, each also in its common grammatical forms (e.g. for "what drives our turnover":
    /// turnover, revenue, sales, income, drive, drives, driver, drivers). Single words or short phrases,
    /// lowercase, no stop words; at most 48 are used. Stored questions are ranked by how many of these they
    /// contain, so a term that is only loosely related adds noise. Omit it and the server searches the
    /// typed words alone (or expands them itself, where it is configured to).
    pub expanded_terms: Option<Vec<String>>,
    /// How many matches to return (default 3, max 20).
    pub top_k: Option<u32>,
    /// Restrict to one repo by id. Omit to search the whole estate, which is usually right: a question
    /// about revenue is worth answering from whichever repo's report first asked it.
    pub repo_id: Option<String>,
}

/// The query string for `GET /lineage/subscribers/similar-questions`: every expanded term travels as its own
/// repeated `expandedTerms` key, which the control plane binds to its term list. Blank terms are dropped here
/// so an empty list sends none and the server falls back to its own term source.
fn similar_questions_query(input: SimilarQuestionsInput) -> Vec<(&'static str, String)> {
    let mut query: Vec<(&'static str, String)> = vec![("question", input.question)];
    for term in input.expanded_terms.unwrap_or_default() {
        let term = term.trim();
        if !term.is_empty() {
            query.push(("expandedTerms", term.to_string()));
        }
    }
    if let Some(top_k) = input.top_k {
        query.push(("topK", top_k.to_string()));
    }
    if let Some(repo_id) = input.repo_id {
        query.push(("repoId", repo_id));
    }
    query
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ConfirmQuestionInput {
    /// The question that was asked, in the user's own words, exactly as they typed it, or the wording they
    /// gave you when you showed them what would be saved. This is the text later questions are matched
    /// against, so rewriting it into schema terms would store something nobody will ever ask again. Do not
    /// tidy it on your own judgement either, not even an obvious typo: show the person what you are about to
    /// save and change it only if they ask, since the stored question is theirs, not yours.
    pub question: String,
    /// The query that answers it. On outcome="corrected" this is the CORRECTED SQL, not what was first
    /// proposed: the store holds what worked, never what was fixed. Must be a single read-only SELECT; it is
    /// parsed and refused otherwise.
    pub sql: String,
    /// What the person decided about the RESULT: "accepted" (they explicitly said the answer is right, or asked
    /// to save it), "corrected" (it answered it after being edited), or "rejected" (it did not). Approving a
    /// run is none of these. A rejection stores nothing and says so.
    pub outcome: String,
    /// The warehouse object keys the query reads, when you resolved them (from describe_object or the match
    /// you adapted). Omit rather than guessing: an example is still worth keeping without them.
    pub object_keys: Option<Vec<String>>,
    /// The find_similar_questions `score` this answer was built from, when it was built from a match. Omit
    /// when the query was composed from the schema instead. Never your own estimate of your correctness.
    pub confidence: Option<i32>,
    /// The repo to attribute the example to. Omit to keep it estate-wide, which is usually right.
    pub repo_id: Option<String>,
    /// The datasource this sql runs against: a whole ${env:...} / ${keyvault:...} reference or an @alias,
    /// matching a datasource the catalog already declares (the same reference prepare_query takes). Pass it
    /// when the person told you, or picked, which datasource this answer is for; omit it when none was
    /// established. Without it a later trusted match to this example still stands as precedent but cannot be
    /// auto-run, since nothing would tell prepare_query which connection to use. Ignored for outcome="rejected",
    /// which is never stored at all.
    #[serde(rename = "sourceRef")]
    pub source_ref: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct AutoRunQuestionInput {
    /// The `exampleId` a TRUSTED find_similar_questions match carried. Only present on a user-confirmed match
    /// that also carries a `sourceRef`; a match with no exampleId (a PowerBI-derived question) or no sourceRef
    /// cannot be auto-run, and must go through prepare_query/run_query instead.
    #[serde(rename = "exampleId")]
    pub example_id: i64,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ObjectLineageInput {
    /// The object key (from search_all, describe_object, or lineage_objects).
    pub key: String,
    /// Which way to walk: "upstream" (where the data comes from), "downstream" (where it goes), or
    /// "both" (default).
    pub direction: Option<String>,
    /// How many hops to walk (default 3, max 8). One hop is one flow/module crossing: source table ->
    /// landing table is depth 1.
    pub depth: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct SubscribersInput {
    /// The consuming tool, exact: "PowerBI", "Tableau", "Excel", ... Omit for every type.
    /// Free text by design, so read the types back from an unfiltered call rather than guessing.
    #[serde(rename = "type")]
    pub subscriber_type: Option<String>,
    /// Substring filter over the subscriber's name, owner, description, and notes. Searching
    /// "Incomplete dataset" is the estate-wide audit of reports whose lineage is only partial.
    pub search: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct SearchInput {
    /// Which surface to page. `search`: flows, files, statements. `search_schema`: objects, columns,
    /// definitions, flowColumns. Call search_all first when you do not yet know which one holds the term.
    pub surface: String,
    /// The search term. Prefer ONE identifier token ("SourceRank", "FerryPassengers") over an English
    /// phrase: a multi-word query must match EVERY word (in any field of a row), so a stray word empties
    /// the result. Matching is case-insensitive substring, so a fragment works.
    pub query: String,
    /// How many days back to search, for surface=statements only (default 90). Pass 0 for all retained
    /// history: statements are pruned by the estate's trace retention, so "all history" still means "as far
    /// back as retention kept". Ignored on every other surface.
    pub days: Option<i64>,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

/// How one search surface reaches the control plane: its endpoint, the query-string key that carries the
/// term (the search endpoints are split between `name` and `q`), and whether it honours `days`.
struct SearchSurfaceSpec {
    path: &'static str,
    query_param: &'static str,
    takes_days: bool,
}

/// Resolves a `search` surface: the ETL-side ones, which carry no warehouse schema. Kept separate from
/// [`schema_surface_spec`] because the split is a privacy boundary, not a category. The chat surfaces are
/// given these and denied those, and a name-level allowlist can only express that as two tools.
fn search_surface_spec(surface: &str) -> Option<SearchSurfaceSpec> {
    let (path, query_param, takes_days) = match surface {
        "flows" => ("/api/v1/search/flows", "q", false),
        "files" => ("/api/v1/search/files", "name", false),
        "statements" => ("/api/v1/search/statements", "q", true),
        _ => return None,
    };
    Some(SearchSurfaceSpec { path, query_param, takes_days })
}

/// Resolves a `search_schema` surface: the four that read the warehouse schema itself. These see every
/// catalogued table and column regardless of the semantic layer's column allow-list, which is why they are
/// a separate tool that the chat surfaces exclude.
fn schema_surface_spec(surface: &str) -> Option<SearchSurfaceSpec> {
    let (path, query_param) = match surface {
        "objects" => ("/api/v1/search/objects", "name"),
        "columns" => ("/api/v1/search/columns", "name"),
        "definitions" => ("/api/v1/search/definitions", "q"),
        "flowColumns" => ("/api/v1/search/flow-columns", "name"),
        _ => return None,
    };
    Some(SearchSurfaceSpec { path, query_param, takes_days: false })
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct SearchAllInput {
    /// The term to look for. Prefer ONE identifier token ("SourceRank", "FerryPassengers") over an English
    /// phrase: a multi-word query must match EVERY word, so a stray word empties the result. The reply
    /// echoes back the tokens it actually searched for, so check them when a result looks wrong.
    pub query: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct InsightsWindowInput {
    /// The analysis window in days (1-90; default 7).
    pub days: Option<i64>,
    /// Restrict to one repository (GUID, optional).
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// Restrict to one batch (source-system grouping, optional).
    pub batch: Option<String>,
    /// Most advisories to return (default 20; the counts in the answer cover everything found).
    pub limit: Option<i64>,
    /// Set true to include ready-to-review SQL suggestions inline. Default false: items report
    /// hasSuggestedSql instead, keeping the briefing small; re-ask with includeSql for the ones you act on.
    #[serde(rename = "includeSql")]
    pub include_sql: Option<bool>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct StreamAnomalyInput {
    /// The analysis window in days (1-180; default 60). Longer is better here than for the insights
    /// endpoints: the detectors need a baseline BEHIND the recent slice they are judging.
    pub days: Option<i64>,
    /// Restrict to one repository (GUID, optional).
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// Restrict to one batch (source-system grouping, optional).
    pub batch: Option<String>,
    /// Restrict to one verdict: "stalled", "degraded", "watch", "healthy", or "insufficient-history".
    /// Omit for everything, ranked most urgent first.
    pub status: Option<String>,
    /// Which side of the estate to look at, and the most important parameter here. "source" (the DEFAULT)
    /// is vendor deliveries: data arriving from outside, so a finding means the vendor did not deliver or we
    /// failed to take in what they sent. "internal" is our own processing: tables derived from the archive
    /// onwards, so a finding is ours. "all" mixes both, which is usually the wrong way to read the board,
    /// because one quiet upstream lights up its whole downstream chain as separate findings.
    pub scope: Option<String>,
    /// Count backfills and other operator-driven reprocessing as normal traffic. Default false, which is
    /// what stops a replay of three years of history from redefining a stream's normal and making every
    /// ordinary day after it look like a collapse. Set true only to ask what the raw numbers did.
    #[serde(rename = "includeBackfills")]
    pub include_backfills: Option<bool>,
    /// Analyse streams that join no ENABLED schedule too. Default false, and the default is the point: a flow
    /// nothing schedules has no say in whether data is delivered, so holding it to a delivery expectation
    /// invents an incident about a promise nobody made. Schedule membership is what defines the population
    /// this board is about.
    #[serde(rename = "includeUnscheduled")]
    pub include_unscheduled: Option<bool>,
    /// Most streams to return (default 25; the counts in the answer cover every stream analysed).
    pub limit: Option<i64>,
    /// A pipeline id (GUID) to drill into instead of listing the board: returns that one stream with its
    /// full day-by-day series and every detector's reasoning.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// The flow's NAME (for example "apc_norgesbuss_calls_02_ing") to drill into, for when the question names
    /// the flow rather than its id: resolved to the pipeline server-side, so "why is this flow flagged" is
    /// one call. Ignored when pipelineId is given.
    #[serde(rename = "flowName")]
    pub flow_name: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct InsightsFlowsInput {
    /// The analysis window in days (1-90; default 7).
    pub days: Option<i64>,
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    pub batch: Option<String>,
    /// Most flows to return, ordered by total processing time (default 100, max 500).
    pub limit: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct InsightsStepsInput {
    /// The pipeline id (GUID) to drill into.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: String,
    /// The analysis window in days (1-90; default 30).
    pub days: Option<i64>,
    /// Set true to include one sample SQL statement per step (can be large). Default false: timings only.
    #[serde(rename = "includeSql")]
    pub include_sql: Option<bool>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct WarehouseHealthInput {
    /// Which DMV probe to run: missingIndexes, statisticsHealth, indexUsage, or topQueries.
    pub operation: String,
    /// The datasource connection reference (a whole ${env:...} / ${keyvault:...} token the estate's pipelines
    /// declare, or an @alias). Omit to probe the estate's busiest target datasource (the warehouse).
    pub reference: Option<String>,
    /// The database to scope the probe to; omit for the connection's default database.
    pub database: Option<String>,
    /// Most rows to return (default 20, max 1000). The full row set persists on the compute task either way;
    /// the GUI's warehouse panel shows it, so ask for more rows only when you will read them.
    pub limit: Option<i64>,
    /// Route the probe to a worker pool that can reach the source (optional).
    pub pool: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct TableKeyInput {
    /// The object key, as lineage_objects / search_objects report it.
    pub key: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct TableJoinsInput {
    /// The object key to get join paths for, as lineage_objects / search_objects report it.
    pub key: String,
    /// Narrow to the routes reaching ONE other table, matched on its name or key (case-insensitive,
    /// substring). Use this to answer "how do I join A to B" in a single call, including through a bridge
    /// table when the two are not joined directly.
    pub other: Option<String>,
    /// How many joins a route may chain (default 2, max 4). 1 restricts the answer to tables joined DIRECTLY
    /// to this one; raise it to find a route through a bridge or dimension table.
    #[serde(rename = "maxHops")]
    pub max_hops: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct DetectUniqueKeyInput {
    /// The schema of the table to profile.
    pub schema: String,
    /// The table or view to profile.
    #[serde(rename = "objectName")]
    pub object_name: String,
    /// The datasource connection reference. Omit to use the estate's busiest target datasource.
    pub reference: Option<String>,
    /// The database holding the table; omit for the connection's default database.
    pub database: Option<String>,
    /// Rows to sample: omit to auto-sample by table size, 0 to force a full scan, or an explicit count
    /// between 1000 and 10,000,000. A sampled candidate is still verified against the whole table unless
    /// verifyCandidates is false, so sampling costs accuracy only when verification is also turned off.
    #[serde(rename = "sampleSize")]
    pub sample_size: Option<i64>,
    /// The widest composite key to consider (default 4, max 8). Raising it grows the search combinatorially.
    #[serde(rename = "maxKeyColumns")]
    pub max_key_columns: Option<i64>,
    /// How many candidate keys to report (default 5).
    #[serde(rename = "maxCandidates")]
    pub max_candidates: Option<i64>,
    /// Confirm each sampled candidate against the WHOLE table before reporting it (default true). Leave it
    /// on: a candidate that is unique in a sample and not in the table is exactly the wrong answer.
    #[serde(rename = "verifyCandidates")]
    pub verify_candidates: Option<bool>,
    /// Answer straight from an enforced unique index or constraint without reading rows, when one exists
    /// (default true). Set false to profile the data regardless of what the table declares.
    #[serde(rename = "trustDeclaredKeys")]
    pub trust_declared_keys: Option<bool>,
    /// Route the task to a worker pool that can reach the source (optional).
    pub pool: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct PrepareQueryInput {
    /// The single read-only SELECT to prepare. Compose it from real metadata (get_table_key,
    /// get_table_joins, describe_object), never from guessed column or join names.
    pub sql: String,
    /// The datasource connection reference. Omit it: the control plane works out the datasource from the tables
    /// the query reads, and answers naming the candidates when it cannot tell. Pass one only then, or when the
    /// person named it.
    pub reference: Option<String>,
    /// The database to run in; omit for the connection's default.
    pub database: Option<String>,
    /// Most rows the result carries (default 200, max 5000). Beyond it the result is marked truncated.
    #[serde(rename = "maxRows")]
    pub max_rows: Option<i64>,
    /// Command timeout in seconds (default 120, max 600).
    #[serde(rename = "timeoutSeconds")]
    pub timeout_seconds: Option<i64>,
    /// Route the run to a worker pool that can reach the source (optional).
    pub pool: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct RunQueryInput {
    /// The planId returned by prepare_query. Single-use and short-lived.
    #[serde(rename = "planId")]
    pub plan_id: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct DuplicateKeysInput {
    /// The schema of the table to check.
    pub schema: String,
    /// The table to check.
    #[serde(rename = "objectName")]
    pub object_name: String,
    /// The datasource connection reference. Omit to use the estate's busiest target datasource.
    pub reference: Option<String>,
    /// The database holding the table; omit for the connection's default database.
    pub database: Option<String>,
    /// The columns that identify one real row. LEAVE THIS EMPTY on the first call: the check uses the key the
    /// table itself declares (SQLFlow's NCI_KeyColumn business-key index first, then a natural primary key,
    /// then any other unique index). Supply it only to answer the question the check asks back when the table
    /// declares no usable key, and ask the USER for those columns rather than guessing them.
    pub columns: Option<Vec<String>>,
    /// How many of the worst duplicate groups to return (default 20).
    pub limit: Option<i64>,
    /// Route the task to a worker pool that can reach the source (optional).
    pub pool: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct CompareBaselineInput {
    /// What to compare: "inventory" (every table in a schema, both sides, with row counts), "schema" (one
    /// object's columns position by position), or "data" (one object's rows: count decomposition,
    /// bidirectional key anti-join, and value parity). Climb the ladder in that order.
    pub mode: String,
    /// The schema on the CURRENT (V3) estate.
    pub schema: String,
    /// The object on the current estate. Required for schema and data mode; omit for inventory.
    #[serde(rename = "objectName")]
    pub object_name: Option<String>,
    /// The database on the linked server (the OLD estate's database, e.g. "dw-dwh-prod").
    #[serde(rename = "baselineDatabase")]
    pub baseline_database: String,
    /// The linked server reaching the old estate. Omit to use the deployment's default; call
    /// dwh_maintenance_actions to see which are allowlisted.
    #[serde(rename = "linkedServer")]
    pub linked_server: Option<String>,
    /// The schema on the old side when it differs from `schema`.
    #[serde(rename = "baselineSchema")]
    pub baseline_schema: Option<String>,
    /// The object on the old side when the name differs (a ported table is routinely renamed to match its V3
    /// source, with a compatibility view keeping the old name).
    #[serde(rename = "baselineObjectName")]
    pub baseline_object_name: Option<String>,
    /// DATA MODE ONLY, and required there: the LOGICAL key, as SQL expressions that identify one real-world
    /// reading on BOTH estates (e.g. ["Dato", "Sted", "CAST(Klokkeslett AS time)"]). NOT the surrogate primary
    /// key, which the two estates assign independently and which therefore proves nothing. Establish this with
    /// the user before running; a wrong key invalidates every number the comparison returns.
    #[serde(rename = "keyExpressions")]
    pub key_expressions: Option<Vec<String>>,
    /// DATA MODE ONLY: the columns compared for value parity. Omit for every column except the identity, the
    /// bare key columns, and the provenance/audit columns.
    #[serde(rename = "compareColumns")]
    pub compare_columns: Option<Vec<String>>,
    /// DATA MODE ONLY: a predicate applied to BOTH sides, without the WHERE keyword, so a large table can be
    /// compared one era at a time (e.g. "Dato >= '2025-01-01'").
    #[serde(rename = "where")]
    pub filter: Option<String>,
    /// The datasource connection reference for the CURRENT estate. Omit to use the busiest target datasource.
    pub reference: Option<String>,
    /// Most rows any one section returns (default 50).
    pub limit: Option<i64>,
    /// Route the task to a worker pool that can reach the source (optional).
    pub pool: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct TriggerRunInput {
    /// The repository id (GUID) that owns the flow.
    #[serde(rename = "repoId")]
    pub repo_id: String,
    /// The flow name to run.
    #[serde(rename = "flowName")]
    pub flow_name: String,
    /// Target worker pool (optional).
    pub pool: Option<String>,
    /// Pin to a specific commit sha (optional; defaults to last synced).
    #[serde(rename = "commitSha")]
    pub commit_sha: Option<String>,
    /// Full (re)load instead of incremental.
    #[serde(rename = "fullLoad")]
    pub full_load: Option<bool>,
    /// Backfill window start (ISO-8601).
    #[serde(rename = "backfillFrom")]
    pub backfill_from: Option<String>,
    /// Backfill window end (ISO-8601).
    #[serde(rename = "backfillTo")]
    pub backfill_to: Option<String>,
    /// File-pattern override for file flows.
    #[serde(rename = "filePattern")]
    pub file_pattern: Option<String>,
}

// --- Helpers ---------------------------------------------------------------

fn done(result: anyhow::Result<String>) -> String {
    match result {
        Ok(s) => s,
        Err(e) => format!("Error: {e:#}"),
    }
}

fn json_str(v: &Value) -> String {
    serde_json::to_string_pretty(v).unwrap_or_else(|_| v.to_string())
}

/// How a query answer must be laid out. Carried IN the tool result rather than only in the server instructions,
/// because a client may truncate those instructions long before the business-question section.
const ANSWER_FORMAT: &str = "If your own instructions already define how to lay out a query answer (a chat \
    that embeds the result, for example), follow those and ignore this field, `sqlIntro`, `sqlBlock`, \
    `chartLink`, and `saveOffer`. Otherwise: reply with the finding in one or two plain sentences, then `sqlIntro` on its own \
    line exactly as given, then `sqlBlock` copied exactly as given, as its own fenced code block on its own lines, \
    then, only when `chartLink` is present, a line reading [View this as a chart](chartLink) with that URL, then, only when `saveOffer` is present, \
    `saveOffer` on its own last line exactly as given; \
    nothing else goes before, between, or after these parts. Never put the SQL inline in a sentence or in single backticks, never \
    paraphrase it, and beyond `sqlIntro` do not describe how the answer was found (matches, confirmations, \
    datasources, tool names) unless asked. Running this query is NOT confirmation of the answer: do not call \
    confirm_question now, and only call it later if the person, after seeing this result, explicitly says it \
    is correct, corrects it, or asks you to save it.";

/// THE single source of truth for the silent-retrieval rule: on the business-question path the person hears the
/// answer, never the machinery that found it. Every place that states the rule (the tool descriptions, the server
/// instructions, and the `responseRule` carried in the result) composes this text rather than restating it, so the
/// wording cannot drift between them.
///
/// It is stated in three places on purpose, because each covers a window the others cannot. A tool description is
/// the only one guaranteed to be read BEFORE the first call, so it is the only thing covering the opening preamble;
/// the server instructions can be truncated by a client long before the business-question section; and the
/// `responseRule` in the payload always arrives, but only once a call has already been made. The rule therefore
/// leads the `find_similar_questions` description, where it is read before the model composes anything at all.
/// It is a macro rather than a `const` so the server instructions can splice it with `concat!`, which needs a
/// literal. The one place that cannot use it is the `#[tool(description = ...)]` attribute on
/// `find_similar_questions`: rmcp's macro rejects anything but a bare literal, so that description repeats the
/// text and `tool_description_leads_with_the_silent_rule` fails the build if the two ever drift apart.
macro_rules! silent_retrieval_rule {
    () => {
        "Say nothing to the person before or between the tool calls on this path: no preamble, no restating the \
        question, no announcing what you are about to look up or that you are building a query, and no commentary \
        on what came back (whether a match is trusted, its score, who confirmed it, saved answers, reports, or tool \
        names). All of that is yours to act on, never to narrate: to a business reader it reads as the assistant \
        explaining its own plumbing, and calling a match untrusted or low-scoring reads as the assistant being \
        unreliable. Work silently until you have the answer or a query to offer, then reply once."
    };
}

/// The rule as a value, for the tests that hold every carrier of it to the one wording.
#[cfg(test)]
const SILENT_RETRIEVAL_RULE: &str = silent_retrieval_rule!();

/// How to behave between find_similar_questions and the answer, as carried in that tool's own result. The rule
/// itself is [`SILENT_RETRIEVAL_RULE`]; this adds only where the finished reply's layout comes from.
const RETRIEVAL_RULE: &str = concat!(
    silent_retrieval_rule!(),
    " Lay that one reply out as your instructions describe or, when you have them, as auto_run_trusted_match, \
    run_query, or prepare_query hands back."
);

/// How to read a `describe_semantic_table` payload, carried on the response rather than in the tool
/// description. A field's meaning is only needed once the payload is in hand, while a description is read on
/// every turn by a model choosing between tools, so the explanation is paid where it is used.
const SEMANTIC_TABLE_GUIDE: &str = "Reading this: `columns` are the ONLY columns a query may read (any other \
is refused when the query runs, and SELECT * is refused on a table with columns outside the layer, so always \
name them). `keyOrigin` Curated means an admin stated the key. On a join, `source` Curated = declared by an \
admin, so prefer it; Discovered = inferred from the codebase's own joins, ranked by `occurrences`; \
`isRangeJoin` marks an interval join that must never be treated as a key match. Reuse a measure's \
`expression` verbatim, and mirror an example question's SQL shape for a similar question. `reportModels` \
holds the Power BI models built on this table: their `measures` and `calculatedColumns` carry DAX \
`expression`s, and DAX is not SQL, so mirror the logic rather than pasting it; a `relationships` entry with \
`isActive` false applies only where a measure invokes USERELATIONSHIP. `consumers` answers who uses this \
table.";

/// How to read a `describe_subscriber_report` payload. Same reasoning as [`SEMANTIC_TABLE_GUIDE`]: this is
/// field semantics for a payload in hand, not a signal for choosing the tool.
const SUBSCRIBER_REPORT_GUIDE: &str = "Reading this: a field's `role` (Category/Y/Rows/Values/Size/...) says \
whether it is the axis a chart is broken down by or the value it plots, which a flattened column list cannot \
express. `tableName` on a field is the Power BI MODEL entity name (e.g. \"Sales\"), not resolved to a \
physical warehouse object, so never treat it as a catalog key. Cross-reference a visual's `queryName` \
against describe_subscriber's `queries` for the rendered SQL. An empty `pages` means the subscriber has no \
extracted report (hand-authored, or not yet synced), not that one failed to load; an empty `questions` on a \
visual means question generation is disabled or has not run for it, not that the visual answers nothing.";

/// How to read a `detect_stream_anomalies` payload: the ensemble's verdict fields and the size floors behind
/// them. The METHOD (how each detector works, how reprocessing is trimmed, how cadence is learned) is the
/// concept page's job, which the tool description points at; repeating it inline taught a documented method
/// to every model on every turn, whether or not it called this tool.
const STREAM_ANOMALY_GUIDE: &str = "Reading this: trust a finding with `agreeingDetectors` >= 2. A lone \
detector is a lead, and a lone VOLUME detector (rateChange / levelShift / volumeOutlier) on a stream that \
still loads every day is nearly always noise, worth saying so about. The three primary detectors (silence, \
nullDays, cadence) can raise a finding alone. Every detector, fired or quiet, carries a `detail` sentence \
holding its evidence with the numbers in it. A stream's `stage` says whose problem a finding is: \
'integration' fetches from the vendor, so nothing there usually means the vendor sent nothing; \
'file-ingestion' and 'archive' mean the data arrived and we did not take it in; 'derived' is entirely our \
own processing. `pattern.changeDriven` marks a stream whose empty days ARE its normal (a reference table \
that changes twice a year), with `profile.deliveryShare` as the evidence: its silence is not an outage, and \
it is category rarely-changes rather than stalled. Volume findings carry a size floor as well as a \
significance test, because sigma is the stream's own noise: a very steady feed makes a hundred-row wobble \
look like 4 sigma, and significance without size is not a fault.";

/// Attaches a reading guide to a payload that came back whole, under `readingThis`, the key `search_all`
/// already uses for the same purpose. An error payload is left alone: guidance on fields it does not have
/// would be noise on top of a failure.
fn attach_guide(value: &mut Value, guide: &'static str) {
    if let Some(map) = value.as_object_mut() {
        if map.contains_key("error") {
            return;
        }
        map.insert("readingThis".into(), json!(guide));
    }
}

/// Attaches the silent-retrieval rule to a find_similar_questions response; an error payload is left alone.
fn attach_retrieval_rule(value: &mut Value) {
    if value.get("matches").is_none() {
        return;
    }
    if let Some(map) = value.as_object_mut() {
        map.insert("responseRule".into(), json!(RETRIEVAL_RULE));
    }
}

/// How to ask a person to approve a prepared query. A business reader cannot weigh retrieval internals, and
/// hearing that an answer is "not trusted" reads as the assistant being unreliable, so the ask stays about what
/// the query will show.
const APPROVAL_FORMAT: &str = "If your own instructions already define how to offer a query (a chat with a Run \
    button, for example), follow those and ignore this field. Otherwise: ask for approval in plain business \
    language. Open with where the query comes \
    from: when it is NOT a saved answer (you wrote it for this question, or adapted it from a similar one), say \
    \"I don't have a saved answer for this question yet, so I've put together a query that should answer it.\"; \
    when it reproduces a question one of the estate's reports already answers, say \"This question is already \
    answered in one of our reports, so here's the query behind it.\" Then one sentence on what this query will \
    show, then `sql` verbatim as its own ```sql fenced code block, then a short question such as \"Want me \
    to run it?\". Never mention trust, trusted or untrusted, scores, thresholds, matches, confidence, or that \
    an answer is unverified, and never ask the person to judge the data source, table, or datasource: they \
    decide whether to run it, not how it was found.";

/// Attaches the approval layout to a prepare_query response that minted a plan; an error payload is left alone.
fn attach_approval_format(value: &mut Value) {
    if value.get("planId").and_then(Value::as_str).is_none() {
        return;
    }
    if let Some(map) = value.as_object_mut() {
        map.insert("approvalFormat".into(), json!(APPROVAL_FORMAT));
    }
}

/// The line that introduces the SQL block: a trusted auto-run replays a stored, person-confirmed query, while a
/// run_query result ran a statement composed for this question.
const SAVED_QUERY_INTRO: &str = "Here's the saved query I ran:";
const QUERY_INTRO: &str = "Here's the query I ran:";

/// The closing line inviting a person to save a fresh answer. Only a run_query result carries it: an auto-run
/// replayed an answer someone already saved, so offering to save it again would ask for a decision already made.
const SAVE_OFFER: &str = "If this is right, reply *save* and I'll remember it for next time.";

/// Attaches the answer layout to a query result that actually produced rows: `sqlBlock` is the SQL that ran,
/// already fenced, so the model copies a finished element instead of deciding how to render it. Both the
/// auto-run response and a compute task carry the executed statement at `result.sql`; a result that did not
/// run (or carries no SQL) is left untouched, so nothing suggests an answer exists.
///
/// `chartLink` opens the same result as a chart in the GUI, and is added only when links are absolute (a
/// root-relative link is not clickable outside the GUI). This server cannot tell which client called it, since
/// the GUI chat and Slack share one `surface=assistant` connection, so every layout field says it yields to the
/// client's own instructions: the GUI chat, which draws the chart inline, is told to ignore them.
fn attach_answer_format(value: &mut Value, links: &GuiLinks) {
    let succeeded = value["ran"].as_bool() == Some(true) || value["status"].as_str() == Some("succeeded");
    if !succeeded {
        return;
    }
    let Some(sql) = value["result"]["sql"].as_str().map(str::trim).filter(|s| !s.is_empty()) else {
        return;
    };
    let block = format!("```sql\n{sql}\n```");
    // Only the auto-run response carries `ran`; a compute task from run_query never does.
    let intro = if value.get("ran").is_some() { SAVED_QUERY_INTRO } else { QUERY_INTRO };
    if let Some(map) = value.as_object_mut() {
        map.insert("answerFormat".into(), json!(ANSWER_FORMAT));
        map.insert("sqlIntro".into(), json!(intro));
        map.insert("sqlBlock".into(), json!(block));
        if intro == QUERY_INTRO {
            map.insert("saveOffer".into(), json!(SAVE_OFFER));
        }
    }
    let chart = value["taskId"]
        .as_str()
        .filter(|_| !links.base().is_empty())
        .map(|task_id| links.query_result(task_id));
    if let (Some(chart), Some(map)) = (chart, value.as_object_mut()) {
        map.insert("chartLink".into(), json!(chart));
    }
}

/// Caps every string in a JSON document to `max` characters (marking the cut), recursively. The warehouse
/// probes carry statement bodies that can run to kilobytes each; the model reading the tool result needs the
/// shape and the head of the text, and the untruncated document stays on the compute task for the GUI.
fn truncate_long_strings(value: &mut Value, max: usize) {
    match value {
        Value::String(s) => {
            if s.chars().count() > max {
                let head: String = s.chars().take(max).collect();
                *s = format!("{head}... [truncated]");
            }
        }
        Value::Array(items) => {
            for item in items {
                truncate_long_strings(item, max);
            }
        }
        Value::Object(map) => {
            for (_, item) in map.iter_mut() {
                truncate_long_strings(item, max);
            }
        }
        _ => {}
    }
}

/// The surfaces `/api/v1/search/all` reports, each paired with the tool that pages it in full and the
/// follow-up that turns one hit into an answer. This is the map that makes a search iterative: the model
/// gets counts per surface plus the exact next call for each, instead of a wall of hits it has to guess
/// what to do with.
///
/// ORDER IS PRIORITY, and it is warehouse-first on purpose. A term someone searches for is far more often
/// a table, a column, or the code computing one than it is the name of a report: the warehouse is the
/// subject, and consumption is a convention layered on top of it. So `objects` leads and `subscribers`
/// comes last, and a caller working the plan top-down reaches the answer without being steered into the
/// reporting layer by a name that merely also appears there.
const SEARCH_SURFACES: [(&str, &str, &str); 8] = [
    (
        "objects",
        "search_schema(surface=\"objects\")",
        "A warehouse table/view/proc matched by NAME. Take a hit's `key` to describe_object(key) for its \
         columns, interpreted key, generating code, lineage edges, and join relationships; to \
         describe_object_refresh(key) for how it is populated and how often it updates; or to \
         object_lineage(key) to walk where its data comes from and what depends on it.",
    ),
    (
        "columns",
        "search_schema(surface=\"columns\")",
        "A column on a SYNCED warehouse object matched. Take `objectKey` to describe_object(key) to see the \
         column in context and which flows write it. Note the object must have been schema-synced to appear \
         here; a column that only exists inside a flow shows up under flowColumns instead.",
    ),
    (
        "definitions",
        "search_schema(surface=\"definitions\")",
        "The term appears in an object's CODE (its module body or the DDL that created it). Take `key` to \
         describe_object(key) for the full body; `source` says whether the live module or the emitted script \
         carried the match.",
    ),
    (
        "files",
        "search(surface=\"files\")",
        "A file some run processed matched by name or path. Take `runId` to get_run / run_files for that \
         delivery, `pipelineId` to get_pipeline for the flow that ingested it, or file_provenance for the \
         producer/consumer chain of the file endpoint itself.",
    ),
    (
        "flows",
        "search(surface=\"flows\")",
        "The term appears in a flow's YAML (its name, its repo path, or its BODY: a source query, a selectExp, \
         an embedded statement). `matchedIn` says which. Take `id` to pipeline_definition(id) for the \
         normalized flow, get_pipeline(id) for its identity, list_runs(pipelineId) for what it has done.",
    ),
    (
        "flowColumns",
        "search_schema(surface=\"flowColumns\")",
        "The term is a COLUMN A FLOW PRODUCES. This is the surface that answers \"where is <column> computed\": \
         matchedIn=Expression means this flow COMPUTES the value, Column means it emits it under that name, \
         Source means it reads it from the raw data. Take `pipelineId` to pipeline_definition(pipelineId) for \
         the transform and pipeline_columns(id) for the flow's whole column set.",
    ),
    (
        "statements",
        "search(surface=\"statements\")",
        "The term is in SQL a run ACTUALLY EXECUTED, collapsed to one row per (flow, step) with an occurrence \
         count. This is ground truth that exists nowhere else: an expression the engine composes at run time is \
         in no YAML and in no stored module body. `statementWindowDays` says how far back this looked; \
         search(surface=\"statements\", days=0) searches all retained history. Take `runId` to run_statements(runId) for the \
         full trace of that run.",
    ),
    (
        "subscribers",
        "list_subscribers",
        "A DASHBOARD, REPORT, workbook, notebook, or application that CONSUMES the warehouse matched by its \
         name, owner, description, notes, or location. LAST by priority: this is the consumption layer, and a \
         term is more often a warehouse object than a report, so prefer a hit on the surfaces above when both \
         matched. It is still the right answer for a question phrased about a dashboard or a report, because \
         people name the thing they look at, not the catalog word for it. Take `key` to describe_subscriber(key) \
         for every object it reads and the SQL it runs; then describe_object_refresh on those objects for how \
         each is populated. `notes` says what is wrong with it, and a note beginning \"Incomplete dataset\" \
         means it also reads objects the warehouse does not have, so its object list is a floor, not the \
         whole truth.",
    ),
];

/// The ordered checklist a caller works through when a global search matched nothing. Each step names the
/// tool that widens the net in a different direction, so an empty result becomes the start of the next
/// query rather than a dead end (and, at the end, an honest "not in the catalog" with the reason).
fn no_match_guidance(query: &str) -> Vec<String> {
    vec![
        format!(
            "Nothing matched '{query}' on any surface. Every word of a multi-word query must match, so first \
             retry with a SINGLE identifier token, or with a distinctive fragment of one."
        ),
        "If the name is a warehouse object, it is only searchable once the schema sync has imported it: call \
         list_schemas to see which (server, database, schema) groupings the catalog actually covers, and \
         catalog_tree / lineage_objects to browse the one it should be in. An uncovered schema explains a \
         miss without meaning the object does not exist."
            .to_string(),
        "If it is a column produced inside a pipeline rather than a synced table, search_schema(surface=\"flowColumns\") is the \
         surface for it; if it is a value computed in SQL text, the flows (flow YAML), definitions \
         (object code), and statements (the SQL runs actually executed) surfaces are the three places that text \
         can live. Executed SQL is searched over a recent window by default, so retry \
         search(surface=\"statements\") with days=0 before ruling it out."
            .to_string(),
        "If it names a report, workbook, or application rather than a warehouse object, it is on the \
         consumption side: list_subscribers(search=<term>), then describe_subscriber(key)."
            .to_string(),
        "If it is a SQLFlow product term rather than an estate name (a flow key, a CLI verb, a concept), \
         search_docs is the corpus for it."
            .to_string(),
        "Only after those come back empty is it correct to answer that the catalog has no such name, and the \
         answer should say which surfaces were checked."
            .to_string(),
    ]
}

/// Adds the follow-up plan to a `/search/all` payload: per non-empty surface, the tool that pages it and
/// the tool that turns a hit into an answer; or, when nothing matched, the widen-the-net checklist.
fn annotate_search_all(value: &mut Value, query: &str) {
    let Some(map) = value.as_object_mut() else {
        return;
    };

    let mut steps: Vec<Value> = Vec::new();
    let mut hits = 0i64;
    for (surface, page_tool, follow_up) in SEARCH_SURFACES {
        let total = map.get(surface).and_then(|c| c.get("total")).and_then(Value::as_i64).unwrap_or(0);
        hits += total;
        if total > 0 {
            steps.push(json!({
                "surface": surface,
                "total": total,
                "pageEveryHitWith": page_tool,
                "thenCall": follow_up,
            }));
        }
    }

    if hits == 0 {
        map.insert("nothingMatched".to_string(), json!(no_match_guidance(query)));
    } else {
        map.insert("nextSteps".to_string(), json!(steps));
        map.insert(
            "readingThis".to_string(),
            json!(
                "Each surface reports its FULL total with only the top few items previewed. Work the surface \
                 whose description fits the question, page it with its own tool if the preview is not enough, \
                 then make the drill-down call named in thenCall. `tokens` is what was actually searched for."
            ),
        );
    }
}

/// The `sqlflow` CLI executable the discover tool shells out to: `SQLFLOW_CLI` when
/// set (a full path), otherwise `sqlflow` resolved from `PATH`.
fn sqlflow_cli_command() -> String {
    match std::env::var("SQLFLOW_CLI") {
        Ok(path) if !path.trim().is_empty() => path,
        _ => "sqlflow".to_string(),
    }
}

/// Runs the `sqlflow` CLI with the given arguments and returns its stdout on success.
/// The child is spawned on a blocking thread (the workspace tokio build omits the
/// `process` feature) so it never stalls the async stdio transport. A non-zero exit
/// surfaces the CLI's own stderr; a missing binary surfaces an actionable message.
async fn run_sqlflow_cli(args: Vec<String>) -> Result<String, String> {
    let command = sqlflow_cli_command();
    let display = format!("{} {}", command, args.join(" "));

    let result = tokio::task::spawn_blocking(move || {
        std::process::Command::new(&command)
            .args(&args)
            .output()
            .map_err(|e| (command, e))
    })
    .await
    .map_err(|e| format!("Failed to run the SQLFlow CLI: {e}"))?;

    match result {
        Ok(output) if output.status.success() => {
            // The CLI writes advisory notes (a detected unique key, a skipped candidate) to stderr
            // so stdout stays clean YAML; surface them to the caller under a clear label.
            let stdout = String::from_utf8_lossy(&output.stdout).into_owned();
            let stderr = String::from_utf8_lossy(&output.stderr);
            let notes = stderr.trim();
            Ok(if notes.is_empty() {
                stdout
            } else {
                format!("{stdout}\n[cli notes]\n{notes}")
            })
        }
        Ok(output) => {
            let stderr = String::from_utf8_lossy(&output.stderr);
            let stdout = String::from_utf8_lossy(&output.stdout);
            let detail = if stderr.trim().is_empty() { stdout.trim() } else { stderr.trim() };
            Err(format!(
                "The SQLFlow CLI exited with {} for `{display}`:\n{detail}",
                output.status
            ))
        }
        Err((command, e)) if e.kind() == std::io::ErrorKind::NotFound => Err(format!(
            "The SQLFlow CLI ('{command}') was not found. Install it and put it on PATH, or set the \
SQLFLOW_CLI environment variable to the full path of the `sqlflow` executable."
        )),
        Err((_, e)) => Err(format!("Failed to launch the SQLFlow CLI: {e}")),
    }
}

// --- Tools -----------------------------------------------------------------

#[tool_router]
impl SqlFlowMcp {
    // ---- Documentation (offline) -----------------------------------------

    #[tool(
        description = "Search the embedded SQLFlow reference corpus (CLI commands, flow YAML reference, source types, concepts, guides). Returns ranked {id, path, title, summary}."
    )]
    async fn search_docs(&self, Parameters(input): Parameters<SearchDocsInput>) -> String {
        let limit = input.limit.unwrap_or(15).clamp(1, 50);
        let hits = self.docs.search(&input.query, input.doc_type.as_deref(), limit);
        if hits.is_empty() {
            return format!("No docs matched '{}'.", input.query);
        }
        let list: Vec<Value> = hits
            .iter()
            .map(|h| {
                json!({
                    "id": h.meta.id,
                    "type": h.meta.doc_type,
                    "title": h.meta.title,
                    "summary": h.meta.summary,
                    "yamlPath": h.meta.yaml_path,
                    "cliCommand": h.meta.cli_command,
                    "score": h.score,
                })
            })
            .collect();
        json_str(&json!({ "query": input.query, "results": list }))
    }

    #[tool(description = "Fetch the full markdown body of a reference page by its manifest id.")]
    async fn get_doc(&self, Parameters(input): Parameters<IdInput>) -> String {
        match (self.docs.get_meta(&input.id), self.docs.body(&input.id)) {
            (Some(meta), Some(body)) => {
                format!("# {}\n\n_id: {} · type: {} · path: {}_\n\n{}", meta.title, meta.id, meta.doc_type, meta.path, body)
            }
            _ => format!("No doc with id '{}'. Use search_docs or list_docs to find one.", input.id),
        }
    }

    #[tool(
        description = "Jump from a `.flow.yaml` dot-path to the reference page that documents it (exact, then longest documented prefix)."
    )]
    async fn get_doc_by_yaml_path(&self, Parameters(input): Parameters<YamlPathInput>) -> String {
        match self.docs.by_yaml_path(&input.yaml_path) {
            Some(meta) => match self.docs.body(&meta.id) {
                Some(body) => format!("# {} (yamlPath: {})\n\n{}", meta.title, meta.yaml_path.clone().unwrap_or_default(), body),
                None => json_str(&json!({ "id": meta.id, "title": meta.title })),
            },
            None => format!("No reference page documents the YAML path '{}'.", input.yaml_path),
        }
    }

    #[tool(description = "Look up the reference page for a top-level CLI command.")]
    async fn get_doc_by_cli_command(&self, Parameters(input): Parameters<CliCommandInput>) -> String {
        match self.docs.by_cli_command(&input.command) {
            Some(meta) => match self.docs.body(&meta.id) {
                Some(body) => format!("# {}\n\n{}", meta.title, body),
                None => json_str(&json!({ "id": meta.id, "title": meta.title })),
            },
            None => format!("No CLI command page for '{}'.", input.command),
        }
    }

    #[tool(description = "List the 'see also' pages related to a given reference page id.")]
    async fn related_docs(&self, Parameters(input): Parameters<IdInput>) -> String {
        let related = self.docs.related(&input.id);
        if related.is_empty() {
            return format!("No related docs recorded for '{}'.", input.id);
        }
        let list: Vec<Value> = related
            .iter()
            .map(|m| json!({ "id": m.id, "type": m.doc_type, "title": m.title, "summary": m.summary }))
            .collect();
        json_str(&json!({ "id": input.id, "related": list }))
    }

    #[tool(description = "List every reference page (optionally filtered by type) as {id, type, title}.")]
    async fn list_docs(&self, Parameters(input): Parameters<ListDocsInput>) -> String {
        let list: Vec<Value> = self
            .docs
            .all()
            .iter()
            .filter(|m| input.doc_type.as_deref().is_none_or(|t| m.doc_type.eq_ignore_ascii_case(t)))
            .map(|m| json!({ "id": m.id, "type": m.doc_type, "title": m.title }))
            .collect();
        json_str(&json!({ "count": list.len(), "docs": list }))
    }

    // ---- Flow language (offline) -----------------------------------------

    #[tool(
        description = "Validate a `.flow.yaml` document against SQLFlow's key census AND its canonical-pattern \
lints: parse errors, unknown keys, misplaced keys (with the documented home to move them to), invalid enum \
values, missing required keys, invented macro tokens (SQLFlow has no macro/parameter expansion in SQL, so \
an undeclared '@sf_*' variable is an error), and hand-written incremental watermarks that belong in the \
'incremental' block. MANDATORY before showing or proposing ANY flow YAML you authored or edited: run it \
and fix every finding first."
    )]
    async fn validate_flow(&self, Parameters(input): Parameters<ValidateFlowInput>) -> String {
        let diags = sqlflow_lang::analyze(&input.yaml);
        if diags.is_empty() {
            return "OK: no problems found.".to_string();
        }
        let list: Vec<Value> = diags
            .iter()
            .map(|d| {
                let sev = match d.severity {
                    sqlflow_lang::features::Severity::Error => "error",
                    sqlflow_lang::features::Severity::Warning => "warning",
                    sqlflow_lang::features::Severity::Information => "info",
                    sqlflow_lang::features::Severity::Hint => "hint",
                };
                json!({
                    "severity": sev,
                    "line": d.range.start.line + 1,
                    "column": d.range.start.character + 1,
                    "code": d.code,
                    "message": d.message,
                })
            })
            .collect();
        json_str(&json!({ "problems": list }))
    }

    #[tool(description = "List every documented attribute for a flow kind: {path, type, required}.")]
    async fn list_flow_keys(&self, Parameters(input): Parameters<FlowTypeInput>) -> String {
        let census = Census::for_flow_type(input.flow_type.as_deref());
        let list: Vec<Value> = census
            .entries
            .iter()
            .map(|e| json!({ "path": e.path, "type": e.ty, "required": e.required }))
            .collect();
        json_str(&json!({ "flowType": input.flow_type, "count": list.len(), "keys": list }))
    }

    #[tool(description = "Describe one `.flow.yaml` attribute: type, required, default, allowed values, and the full description with validation notes.")]
    async fn describe_flow_key(&self, Parameters(input): Parameters<DescribeKeyInput>) -> String {
        let census = Census::for_flow_type(input.flow_type.as_deref());
        let target = sqlflow_lang::census::parse_path(&input.path);
        let entry = census.entries.iter().find(|e| e.segs == target || e.path == input.path);
        match entry {
            Some(e) => json_str(&json!({
                "path": e.path,
                "type": e.ty,
                "required": e.required,
                "default": e.default,
                "enumValues": e.enum_values,
                "appliesWhen": e.applies_when,
                "definedIn": e.defined_in,
                "validation": e.validation,
                "description": e.description,
            })),
            None => format!("No attribute '{}' for this flow kind. Use list_flow_keys to browse.", input.path),
        }
    }

    // ---- Control-plane setup & auth --------------------------------------

    #[tool(description = "Report the configured control-plane URL and whether the server is authenticated.")]
    async fn get_control_plane_url(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        if self.http_mode {
            return json_str(&json!({
                "url": self.cp.base_url(),
                "authMode": "per-request: each call runs as the bearer on the inbound Authorization header",
            }));
        }
        json_str(&json!({
            "url": self.cp.base_url(),
            "authenticated": self.cp.is_authenticated(),
        }))
    }

    #[tool(description = "Set (and persist) the control-plane base URL.")]
    async fn set_control_plane_url(&self, Parameters(input): Parameters<UrlInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_URL_NOTE.to_string();
        }
        self.cp.set_base_url(&input.url);
        format!("Control-plane URL set to {}", self.cp.base_url())
    }

    #[tool(description = "Check connectivity to the control plane (its readiness probe).")]
    async fn check_connectivity(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        done(self.cp.check_connectivity().await.map(|s| format!("Reachable: {s}")))
    }

    #[tool(
        description = "Begin device-flow sign-in. Returns a URL and user code to approve in a browser; then call check_auth_status to complete."
    )]
    async fn login(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_AUTH_NOTE.to_string();
        }
        match self.cp.start_device_auth().await {
            Ok(auth) => {
                let complete = auth
                    .verification_uri_complete
                    .clone()
                    .unwrap_or_else(|| auth.verification_uri.clone());
                format!(
                    "To sign in, open:\n  {}\nand enter the code:\n  {}\n\n(Direct link: {})\nThe code expires in {}s. After approving, call check_auth_status (poll about every {}s).",
                    auth.verification_uri, auth.user_code, complete, auth.expires_in, auth.interval
                )
            }
            Err(e) => format!("Error starting sign-in: {e:#}. Alternatively, paste a token with set_access_token."),
        }
    }

    #[tool(
        description = "Poll the pending device-flow sign-in (or report current auth state). With no sign-in \
            in progress, this asks the control plane who the stored credential actually authenticates as \
            (`GET /api/v1/me`) rather than trusting the locally cached expiry, so it never reports \
            \"Authenticated\" for a credential the server has since revoked or lost (a revoked token, a session \
            past its maximum age, or a local-dev catalog reset). A rejected credential is cleared automatically; \
            call login again."
    )]
    async fn check_auth_status(&self, Parameters(input): Parameters<CheckAuthInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_AUTH_NOTE.to_string();
        }
        let code = input.device_code.or_else(|| self.cp.pending_device_code());
        let Some(code) = code else {
            return match self.cp.whoami().await {
                Ok(WhoAmI::Authenticated { subject, role, scopes }) => {
                    let role = role.map(|r| format!(", role {r}")).unwrap_or_default();
                    let scopes = if scopes.is_empty() {
                        String::new()
                    } else {
                        format!(", scopes: {}", scopes.join(" "))
                    };
                    format!("Authenticated as {subject}{role}{scopes}.")
                }
                Ok(WhoAmI::Unauthenticated) => {
                    if let Some(reason) = self.cp.session_ended_reason() {
                        format!("The stored session ended: {reason} Call login to sign in again.")
                    } else if self.cp.has_token() {
                        self.cp.clear_token();
                        "The stored credential was rejected by the control plane (expired, revoked, or the \
                            catalog it lived in was reset). It has been cleared; call login again."
                            .to_string()
                    } else {
                        "Not authenticated and no sign-in in progress. Call login first.".to_string()
                    }
                }
                Err(e) => format!(
                    "Could not verify authentication with the control plane: {e:#}. Check \
                        check_connectivity, then try again."
                ),
            };
        };
        match self.cp.poll_device_token(&code).await {
            Ok(PollOutcome::Approved(t)) => {
                // The device grant hands back a session token with the same lifetime and the same absolute cap as the
                // approving browser session. It is kept as is and rolled on the GUI's schedule; no longer-lived
                // credential is minted in its place.
                let scopes = if t.scope.is_empty() { "(none reported)".to_string() } else { t.scope.clone() };
                if t.renews {
                    format!(
                        "Signed in. Scopes: {scopes}. The session renews itself a few minutes before each token \
                         expires, for as long as the control plane's session limit allows (measured from your \
                         browser sign-in, 30 days by default); after that, call login again."
                    )
                } else {
                    format!(
                        "Signed in. Scopes: {scopes}. This credential does not renew (it was approved from a \
                         personal access token or bootstrap session), so call login again when it expires."
                    )
                }
            }
            Ok(PollOutcome::Pending) => "Still waiting for approval. Approve in the browser, then check again.".to_string(),
            Ok(PollOutcome::SlowDown) => "Polling too fast; wait a few seconds and check again.".to_string(),
            Ok(PollOutcome::Denied) => "Sign-in was denied.".to_string(),
            Ok(PollOutcome::Expired) => "The sign-in code expired. Call login again.".to_string(),
            Err(e) => format!("Error: {e:#}"),
        }
    }

    #[tool(description = "Store a bearer access token directly (alternative to device-flow login).")]
    async fn set_access_token(&self, Parameters(input): Parameters<TokenInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_AUTH_NOTE.to_string();
        }
        // A pasted token is dated from its own exp claim and renews exactly when it is an interactive session (it
        // carries auth_time); a personal access token keeps the lifetime its owner gave it.
        let cache = crate::config::TokenCache::from_bearer(
            input.token.trim().to_string(),
            input.scope.unwrap_or_else(|| "read operate".to_string()),
        );
        let reply = match (cache.renews, cache.expires_at) {
            (true, _) => "Access token stored. It is an interactive session, so it renews itself before each token \
                expires, up to the control plane's session limit; after that, call login again."
                .to_string(),
            (false, Some(exp)) => format!("Access token stored. It does not renew and expires at {exp}."),
            (false, None) => "Access token stored.".to_string(),
        };
        self.cp.set_token(cache);
        reply
    }

    #[tool(description = "Forget the stored access token.")]
    async fn logout(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_AUTH_NOTE.to_string();
        }
        self.cp.clear_token();
        "Signed out.".to_string()
    }

    // ---- Catalog (read) --------------------------------------------------

    #[tool(description = "List the synced repositories in the catalog.")]
    async fn list_repos(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/repos", &[]).await
    }

    #[tool(description = "Get one repository by id.")]
    async fn get_repo(&self, Parameters(input): Parameters<GuidInput>) -> String {
        self.get(&format!("/api/v1/repos/{}", input.id), &[]).await
    }

    #[tool(description = "List pipelines (flows), filterable by repo, kind, active state, name, and batch.")]
    async fn list_pipelines(&self, Parameters(i): Parameters<ListPipelinesInput>) -> String {
        let q = vec![
            ("repoId", i.repo_id.unwrap_or_default()),
            ("kind", i.kind.unwrap_or_default()),
            ("active", i.active.map(|b| b.to_string()).unwrap_or_default()),
            ("name", i.name.unwrap_or_default()),
            ("batch", i.batch.unwrap_or_default()),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/pipelines", &q).await
    }

    #[tool(
        description = "List the flow batches (source-system groupings) per repository with flow counts; a \
            flow that declares no batch reports under the \"default\" batch. Then browse one batch's flows \
            with list_pipelines(repoId, batch)."
    )]
    async fn list_flow_batches(&self, Parameters(i): Parameters<ListBatchesInput>) -> String {
        let q = vec![
            ("repoId", i.repo_id.unwrap_or_default()),
            ("active", i.active.map(|b| b.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/pipelines/batches", &q).await
    }

    #[tool(
        description = "Get one pipeline (flow) by id: its identity, YAML, definition, `runsOnSchedule` (whether a \
            schedule fire runs it at all: active and in auto mode), and `loadProfile`, the server's statement \
            of how it loads (`readMode` full / incremental / window / generated / external / notApplicable / \
            unknown, `summary`, `read`, `write`, `keyColumns`, `watermarkColumns`, `replacesTargetEachRun`). \
            Answer \"is this flow a full load or incremental\" from `loadProfile`, never from reading the YAML."
    )]
    async fn get_pipeline(&self, Parameters(input): Parameters<GuidInput>) -> String {
        self.get(&format!("/api/v1/pipelines/{}", input.id), &[]).await
    }

    #[tool(description = "Get a pipeline's normalized flow definition as JSON.")]
    async fn pipeline_definition(&self, Parameters(input): Parameters<GuidInput>) -> String {
        self.get(&format!("/api/v1/pipelines/{}/definition", input.id), &[]).await
    }

    #[tool(
        description = "Get a pipeline's file-size profile: what a normal delivery from this flow weighs. \
            Returns fileCount, totalBytes, avgBytes, medianBytes, minBytes, maxBytes, stdDevBytes (population), \
            totalRows, avgRows, the oldest/newest file timestamps, and a 'recent' window over the newest files \
            (fileCount, avgBytes, minBytes, maxBytes). Computed over every distinct file in the flow's run \
            history (deduplicated by name+path, so a re-run does not re-weight it). Use it to say whether a \
            delivery is normal: compare a file against avgBytes/medianBytes and call it anomalous only beyond a \
            few stdDevBytes, and read 'recent' against the all-time numbers to spot a source whose deliveries \
            have changed size. A flow that has processed no files reports zeros (not an error)."
    )]
    async fn pipeline_file_stats(&self, Parameters(input): Parameters<GuidInput>) -> String {
        let links = json!({ "page": self.links.pipeline(&input.id) });
        self.get_about(
            &format!("/api/v1/pipelines/{}/files/stats", input.id), &[],
            ("pipelineId", &input.id), links,
        ).await
    }

    #[tool(description = "Get a pipeline's pre-ingestion transform columns (kind: declared|detected).")]
    async fn pipeline_columns(&self, Parameters(input): Parameters<PipelineColumnsInput>) -> String {
        let q = vec![("kind", input.kind.unwrap_or_default())];
        let links = json!({ "page": self.links.pipeline(&input.id) });
        self.get_about(
            &format!("/api/v1/pipelines/{}/columns", input.id), &q,
            ("pipelineId", &input.id), links,
        ).await
    }

    // ---- Runs (read) -----------------------------------------------------

    #[tool(
        description = "List runs, filterable by repo, pipeline, status, flow name, batch, and latest-only. Each row \
            carries the outcome (status, error, rows loaded / inserted / updated / deleted) and what kind of \
            run it was: `triggerSource` (schedule / manual / cli), `fullLoad` and `backfillFrom`/`backfillTo` \
            (operator overrides), and `incrementalMode` / `incrementalFilter` / `incrementalWatermark`, the \
            read scope the engine actually applied (null when the run never reached the read or the flow \
            kind has no incremental surface). Read those before comparing row counts across runs."
    )]
    async fn list_runs(&self, Parameters(i): Parameters<ListRunsInput>) -> String {
        let q = vec![
            ("repoId", i.repo_id.unwrap_or_default()),
            ("pipelineId", i.pipeline_id.unwrap_or_default()),
            ("status", i.status.unwrap_or_default()),
            ("flowName", i.flow_name.unwrap_or_default()),
            ("batch", i.batch.unwrap_or_default()),
            ("latest", i.latest.map(|b| b.to_string()).unwrap_or_default()),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/runs", &q).await
    }

    #[tool(
        description = "Get one run by id: status, timings, row counts, error, the failed statement (on a failed run), \
            what started it (`triggerSource`, `triggerScheduleId`), the operator's overrides (fullLoad, \
            backfill window, source filter), and the incremental scope the engine actually applied \
            (`incrementalMode`, `incrementalFilter`, `incrementalWatermark`, `incrementalWatermarkSource`). \
            The SQL it executed is in run_statements; quote that, never reconstruct it."
    )]
    async fn get_run(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        self.get(&format!("/api/v1/runs/{}", i.run_id), &[]).await
    }

    #[tool(description = "Get the SQL statement trace for a run.")]
    async fn run_statements(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        self.get(&format!("/api/v1/runs/{}/statements", i.run_id), &[]).await
    }

    #[tool(description = "Get the assertion results for a run.")]
    async fn run_assertions(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        self.get(&format!("/api/v1/runs/{}/assertions", i.run_id), &[]).await
    }

    #[tool(description = "Get the processed files for a run.")]
    async fn run_files(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        self.get(&format!("/api/v1/runs/{}/files", i.run_id), &[]).await
    }

    #[tool(description = "Get the health-check metrics captured by a run.")]
    async fn run_health_metrics(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        self.get(&format!("/api/v1/runs/{}/health-metrics", i.run_id), &[]).await
    }

    // ---- Lineage (read) --------------------------------------------------

    #[tool(
        description = "List the schema hierarchy: each (server, database, schema) grouping in the catalog with \
            its object count. Start here to browse what schemas exist, then list objects within one. Filter by \
            serverRef and database."
    )]
    async fn list_schemas(&self, Parameters(i): Parameters<ListSchemasInput>) -> String {
        let q = vec![
            ("serverRef", i.server_ref.unwrap_or_default()),
            ("database", i.database.unwrap_or_default()),
        ];
        self.get("/api/v1/lineage/schemas", &q).await
    }

    #[tool(description = "List catalog lineage objects (tables/views/procs/functions/files), filterable by name, server, database, schema, and kind.")]
    async fn lineage_objects(&self, Parameters(i): Parameters<LineageObjectsInput>) -> String {
        let q = vec![
            ("name", i.name.unwrap_or_default()),
            ("serverRef", i.server_ref.unwrap_or_default()),
            ("database", i.database.unwrap_or_default()),
            ("schema", i.schema.unwrap_or_default()),
            ("kind", i.kind.unwrap_or_default()),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/lineage/objects", &q).await
    }

    #[tool(description = "Get a lineage object's detail (including its module definition) by catalog key.")]
    async fn lineage_object_detail(&self, Parameters(i): Parameters<KeyInput>) -> String {
        self.get("/api/v1/lineage/objects/detail", &[("key", i.key)]).await
    }

    #[tool(description = "Get a lineage object's columns by catalog key.")]
    async fn lineage_object_columns(&self, Parameters(i): Parameters<KeyInput>) -> String {
        let links = json!({
            "page": self.links.object(&i.key),
            "lineage": self.links.object_lineage(&i.key),
        });
        self.get_about(
            "/api/v1/lineage/objects/columns", &[("key", i.key.clone())],
            ("key", &i.key), links,
        ).await
    }

    #[tool(
        description = "Describe one object in a single payload for text-to-query: its identity, columns \
            (name/type/nullability), interpreted key (keyColumns/keyOrigin, read from the codebase since \
            warehouses rarely declare physical keys), generating script and module body, the lineage edges \
            that reference it, and the interpreted data-model relationships (references/referencedBy: how \
            this table JOINS other tables, inferred from the codebase's own join predicates and constraint \
            clauses, with occurrence counts ranking the canonical join path). The primary tool for answering \
            questions about, or authoring SQL against, a specific table or view."
    )]
    async fn describe_object(&self, Parameters(i): Parameters<KeyInput>) -> String {
        self.get("/api/v1/lineage/objects/dossier", &[("key", i.key)]).await
    }

    // ---- Semantic layer (the allow-listed schema) --------------------------

    #[tool(
        description = "START HERE to write SQL for someone, unless the person's message begins with the literal `!cwd` \
            prefix, which activates find_similar_questions instead and is that tool's alone to answer: the governed \
            schema a query may read. Answers which tables exist, what \
            their columns mean, and what may be queried at all. The semantic layer is the governed schema: only the tables \
            and columns an admin has allow-listed exist in it, each with its business description and synonyms. Returns the \
            layer's general instructions (read and follow them whenever you write SQL), where its tables live \
            (database/schema with table counts), and every measure: a named SQL expression anchored to one table, to reuse \
            verbatim rather than re-derive. A table or column that is not in the layer is not available to query, however it \
            is named: say so rather than guessing at it."
    )]
    async fn get_semantic_layer(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/semantic-layer", &[]).await
    }

    #[tool(
        description = "Search the semantic layer (the allow-listed schema) for tables, columns, and measures by \
            physical name OR business term. Every word must match somewhere on one table: its name, schema, \
            business name, description, or synonyms, or one of its allowed columns' names, descriptions, or \
            synonyms. So a business word like \"turnover\" finds the column an admin tagged with it even when \
            the column is called NetRevenue. Each hit carries the table (with its `key`), `matchedColumns` \
            (which allowed columns matched), and `matchedOn` (which fields carried the match); measures \
            matching every word come back beside the tables. Search single words or short terms, not whole \
            sentences, and try a synonym when a word finds nothing. Follow a hit with \
            describe_semantic_table(key) before writing any SQL."
    )]
    async fn search_semantic_layer(&self, Parameters(i): Parameters<SemanticSearchInput>) -> String {
        let q = vec![
            ("q", i.q),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/semantic-layer/search", &q).await
    }

    #[tool(
        description = "List the semantic layer's tables, optionally within one database and/or schema \
            (get_semantic_layer names which exist), each with its business name, description, synonyms, and \
            how many allowed columns it has. Use it to browse the layer when no name or business term is \
            known; use search_semantic_layer when one is."
    )]
    async fn list_semantic_tables(&self, Parameters(i): Parameters<SemanticTablesInput>) -> String {
        let q = vec![
            ("database", i.database.unwrap_or_default()),
            ("schema", i.schema.unwrap_or_default()),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/semantic-layer/tables", &q).await
    }

    #[tool(
        description = "Everything needed to write SQL against ONE semantic layer table, in one call: its identity and business \
            description, its allowed columns, the key identifying one row, its joins to other layer tables with a ready ON \
            clause each, the measures anchored to it, example questions with the SQL that answers them, the Power BI models \
            built on it, the reports that consume it, and the layer's general instructions. This is the last call before \
            composing a query: search_semantic_layer or list_semantic_tables finds the table, this serves everything you \
            write the SQL from. The response explains its own fields in `readingThis`. Takes the `key` from either. A 404 \
            means the table is not in the semantic layer, so it cannot be queried: say so rather than composing SQL against \
            it."
    )]
    async fn describe_semantic_table(&self, Parameters(i): Parameters<KeyInput>) -> String {
        self.get_with_guide(
            "/api/v1/semantic-layer/tables/describe",
            &[("key", i.key)],
            SEMANTIC_TABLE_GUIDE,
        )
        .await
    }

    #[tool(
        description = "What identifies ONE row of a table: its interpreted primary/business key columns in key order, and where that \
            interpretation came from (a declared constraint, a flow's merge key, or the codebase). Metadata only, so it is \
            instant and reads no data. Use it for the grain of a table, a column to join or group on, or a key to \
            deduplicate by. If it answers that no key is known, or you need to know what the DATA actually supports rather \
            than what the codebase claims, follow up with detect_unique_key, which profiles the rows."
    )]
    async fn get_table_key(&self, Parameters(i): Parameters<TableKeyInput>) -> String {
        done(self.table_key(i).await)
    }

    #[tool(
        description = "Every way to join a table, and how. Returns every route the estate itself uses, each as an ordered chain of hops \
            with a ready-to-paste ON clause per hop, ranked best first: fewest joins, then how well used the weakest link \
            is, then a declared foreign key over a predicate inferred from the code. Called with just `key` it answers \
            \"what can I join this to\"; with `other` it answers \"how do I join A to B\", finding a route through a bridge \
            table when the two are not related directly (raise `maxHops` if it finds nothing). several routes to the same \
            table are returned deliberately, not deduplicated: that means the codebase joins those tables on more than one \
            column set, which is a choice to make rather than one to have made for you. Use it before writing any query \
            spanning more than one table. A warehouse rarely declares foreign keys, so these observed predicates ARE the \
            data model, and a join guessed from matching column names is not a substitute. If it reports no route, say so \
            rather than inventing one."
    )]
    async fn get_table_joins(&self, Parameters(i): Parameters<TableJoinsInput>) -> String {
        done(self.table_joins(i).await)
    }

    #[tool(
        description = "PROFILE a table's rows to discover what actually identifies them uniquely: the minimal column combination(s) \
            with no duplicates, reported with the duplicate counts that ruled the others out. This reads DATA on a worker \
            node and can take a while on a large table, so prefer get_table_key first, which answers from metadata \
            instantly. Use it when no key is declared, when you suspect the declared key is wrong, or when \
            check_duplicate_keys came back asking which columns identify a row. By default it answers straight from an \
            enforced unique index when one exists (trustDeclaredKeys) and verifies every sampled candidate against the whole \
            table."
    )]
    async fn detect_unique_key(&self, Parameters(i): Parameters<DetectUniqueKeyInput>) -> String {
        done(self.run_detect_unique_key(i).await)
    }

    #[tool(
        description = "Walk an object's lineage transitively: upstream is where its data comes from (each hop names the flow or module \
            that writes the level below and the object it reads: a landing table's depth-1 upstream is the source system's \
            own table or file it is loaded from), downstream is where the data goes and what breaks if the object changes. \
            Steps come back depth-annotated in BFS order, each object reported once at its shortest distance. THE tool for \
            \"where does <table> get its data\", \"what feeds this\", \"what depends on this\", and impact analysis beyond \
            one hop; describe_object's edges stop at the object itself, this crosses the flows. `truncated: true` means a \
            cap cut the walk, so absence of a node is then not proof of absence; re-ask with a smaller depth or one \
            direction. Takes the object `key` from search_all / describe_object / lineage_objects."
    )]
    async fn object_lineage(&self, Parameters(i): Parameters<ObjectLineageInput>) -> String {
        let q = vec![
            ("key", i.key),
            ("direction", i.direction.unwrap_or_default()),
            ("depth", i.depth.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/lineage/objects/graph", &q).await
    }

    #[tool(
        description = "How an object is populated and how often it updates, in one call: every flow that writes the table, each with \
            its `loadProfile` (derived from the flow definition: `readMode` full / incremental / window / generated / \
            external / notApplicable / unknown, a one-sentence `summary`, the `read` and `write` behavior, `keyColumns` the \
            upsert matches on, `watermarkColumns` that bound an incremental read, and `replacesTargetEachRun`), its \
            `lastRun` (newest of any status, with error, trigger source, and the `incrementalMode` / `incrementalFilter` the \
            engine actually applied), its `lastSuccessfulRun` (the last time the table was actually loaded; null if never), \
            `runsOnSchedule` (false means no schedule fire runs it: inactive, manual, or disabled), and the schedules that \
            fire it (cron/interval, timezone, `fires` = enabled and not paused, next and last fire; a chained schedule \
            carries `parentSchedules` with the parents' own clocks). The one-call answer to \"when does <table> update\", \
            \"how is <table> loaded\", \"is it a full load or incremental\", and \"did its last load work\": read those \
            fields, never infer the load mode from the YAML or from the key (an upsert key is not a watermark). A view with \
            no writing flow reports viaModules instead: the derivation lives in that module's body (describe_object shows \
            it). An object with neither producers nor modules is loaded outside SQLFlow, and that absence IS the answer. \
            Takes the object `key` from search_all / describe_object / lineage_objects."
    )]
    async fn describe_object_refresh(&self, Parameters(i): Parameters<KeyInput>) -> String {
        self.get("/api/v1/lineage/objects/refresh", &[("key", i.key)]).await
    }

    #[tool(
        description = "List the file sources of the catalog: every file endpoint decomposed to its canonical \
            parent, so files group by the system they live in exactly as tables group by their database. Each \
            entry has an originKind, which is also the PROVIDER to group under (AzureStorage / AmazonS3 / \
            GoogleCloud / Sftp / NetworkShare / Local / Other), the origin (the storage account, S3/GCS \
            bucket, or SFTP host:port; the constant filesystem for Local), the container (Azure container or \
            UNC share; null otherwise), the folder path, and the leaf name, plus the object key. Fold them \
            into provider > origin > container > folder > file, and use describe_object on a key for which \
            flows read or write that file."
    )]
    async fn list_file_sources(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/lineage/file-tree", &[]).await
    }

    #[tool(
        description = "A file source's provenance: given a file object key, the pipelines that PRODUCE it \
            (where it comes from) and the pipelines that CONSUME it, each with the tables the data LANDS in \
            (where it goes). Answers 'what pipelines use this source and where does the data land' in one call, \
            the source-to-target chain for a file."
    )]
    async fn file_provenance(&self, Parameters(i): Parameters<KeyInput>) -> String {
        let links = json!({
            "page": self.links.object(&i.key),
            "lineage": self.links.object_lineage(&i.key),
        });
        self.get_about(
            "/api/v1/lineage/file-flows", &[("key", i.key.clone())],
            ("key", &i.key), links,
        ).await
    }

    #[tool(
        description = "Browse the catalog as a folder tree (the semantic layer): with no arguments it lists \
            every (server, database, schema) grouping with object counts; add serverRef and/or database to \
            narrow; add schema to get that schema's object-kind groups (Tables/Views/Procedures/...) with \
            counts; add kind to list the objects in that group, paged. This is the database branch; use \
            list_file_sources for the file branch (storage accounts / SFTP servers) and list_flow_batches / \
            list_pipelines for the flow branch. Use describe_object on any returned key for a node's full \
            details (columns, key, code, relationships)."
    )]
    async fn catalog_tree(&self, Parameters(i): Parameters<CatalogTreeInput>) -> String {
        // The argument ladder mirrors the tree's levels; a deeper argument without its parents would silently
        // aggregate across the missing level, so it is refused with directions instead.
        if i.kind.is_some() && i.schema.is_none() {
            return "Give schema (and serverRef/database) together with kind: kind lists the objects of one \
                schema's kind group. Call catalog_tree with schema first to see the kind groups."
                .to_string();
        }
        if i.schema.is_some() && i.server_ref.is_none() {
            return "Give serverRef (and database) together with schema: schema names are only unique within \
                a database. Call catalog_tree with no arguments to see the schema hierarchy."
                .to_string();
        }

        if let Some(kind) = i.kind {
            let q = vec![
                ("serverRef", i.server_ref.unwrap_or_default()),
                ("database", i.database.unwrap_or_default()),
                ("schema", i.schema.unwrap_or_default()),
                ("kind", kind),
                ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
                ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
            ];
            return self.get("/api/v1/lineage/objects", &q).await;
        }
        if i.schema.is_some() {
            let q = vec![
                ("serverRef", i.server_ref.unwrap_or_default()),
                ("database", i.database.unwrap_or_default()),
                ("schema", i.schema.unwrap_or_default()),
            ];
            return self.get("/api/v1/lineage/schemas/kinds", &q).await;
        }
        let q = vec![
            ("serverRef", i.server_ref.unwrap_or_default()),
            ("database", i.database.unwrap_or_default()),
        ];
        self.get("/api/v1/lineage/schemas", &q).await
    }

    #[tool(description = "List the lineage edges (read/write relations) for a repository.")]
    async fn lineage_edges(&self, Parameters(i): Parameters<RepoIdInput>) -> String {
        let links = json!({ "page": self.links.repo(&i.repo_id) });
        self.get_about(
            &format!("/api/v1/repos/{}/lineage/edges", i.repo_id), &[],
            ("repoId", &i.repo_id), links,
        ).await
    }

    #[tool(description = "Get the execution waves (concurrency plan) for a repository's flows.")]
    async fn lineage_waves(&self, Parameters(i): Parameters<RepoIdInput>) -> String {
        let links = json!({ "page": self.links.repo(&i.repo_id) });
        self.get_about(
            &format!("/api/v1/repos/{}/waves", i.repo_id), &[],
            ("repoId", &i.repo_id), links,
        ).await
    }

    #[tool(
        description = "Get the flow-to-flow execution dependencies for a repository; pass pipelineId to keep \
            only the edges touching one flow (what it waits for and what it unblocks)."
    )]
    async fn lineage_dependencies(&self, Parameters(i): Parameters<DependenciesInput>) -> String {
        let q = vec![("pipelineId", i.pipeline_id.unwrap_or_default())];
        let links = json!({ "page": self.links.repo(&i.repo_id) });
        self.get_about(
            &format!("/api/v1/repos/{}/dependencies", i.repo_id), &q,
            ("repoId", &i.repo_id), links,
        ).await
    }

    // ---- Change history: DATABASE SCHEMAS (read) -------------------------
    //
    // Two histories, two families of tool, deliberately not merged. This family answers "what changed in a
    // DATABASE": tables, views, procedures in the SQL Server databases SQLFlow manages. The flow_definition_*
    // family below answers "what changed in a PIPELINE": the YAML that defines the ETL. Picking the wrong one
    // yields a confident answer to a question nobody asked.

    #[tool(
        description = "WHAT CHANGED IN A DATABASE: its tables, views, procedures, and functions. Use this for questions about database \
            objects: 'what changed in the warehouse last week', 'when did this column appear', 'was anything dropped from \
            arc', 'has pre.v_Citybikes_Trips been edited'. Returns one row per object per change: database, category \
            (Table/View/StoredProcedure/...), schema, name, changeType (Added|Changed|Deleted), the commit that holds the \
            DDL diff, and occurredUtc. Filter with database, changeType, since (ISO instant), and search (matches the object \
            name, its schema, or its category). The history is recorded by source-control (scm) flows that snapshot each \
            managed database on a schedule, so a change is dated to the snapshot that first saw it: on a daily cadence that \
            is the day, not the minute, the DDL ran, and a database with no scm flow has no history at all. For the actual \
            DDL text, follow up with database_object_compare (the net change over a window) or database_object_ddl (what one \
            single snapshot did). Not for pipeline or YAML edits: that is flow_definition_history."
    )]
    async fn database_schema_changes(&self, Parameters(i): Parameters<SchemaChangesInput>) -> String {
        let q = vec![
            ("repoId", i.repo_id.unwrap_or_default()),
            ("database", i.database.unwrap_or_default()),
            ("changeType", i.change_type.unwrap_or_default()),
            ("since", i.since.unwrap_or_default()),
            ("search", i.search.unwrap_or_default()),
            ("page", i.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", i.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/schema-changes", &q).await
    }

    #[tool(
        description = "List which MANAGED DATABASES have a schema history and how much they have changed: per \
            database, the total plus added/changed/deleted counts and when it last changed. Start here to \
            learn which databases are tracked at all, then narrow with database_schema_changes(database). A \
            database missing from this list has no source-control (scm) flow snapshotting it, which is a \
            configuration answer, not an empty result. Optional since (ISO instant) scopes the tally."
    )]
    async fn database_schema_history_databases(
        &self,
        Parameters(i): Parameters<SchemaChangeDatabasesInput>,
    ) -> String {
        let q = vec![
            ("repoId", i.repo_id.unwrap_or_default()),
            ("since", i.since.unwrap_or_default()),
        ];
        self.get("/api/v1/schema-changes/databases", &q).await
    }

    #[tool(
        description = "Show the actual DDL that changed for ONE database object at one snapshot, as a unified diff (the CREATE TABLE / \
            CREATE VIEW text before and after). Arguments come straight from a database_schema_changes row: pipelineId (its \
            scm flow), sha (its commitSha), and path (the object's file, '<database>/<category>/<schema>.<name>.sql'). Use \
            it to answer 'what exactly changed about this table', after database_schema_changes has told you THAT it \
            changed. Reports truncated=true when the patch was clipped, so a large generated snapshot is never mistaken for \
            a complete one."
    )]
    async fn database_object_ddl(&self, Parameters(i): Parameters<ObjectDdlInput>) -> String {
        let pipeline_id = i.pipeline_id.clone();
        let links = json!({
            "page": self.links.schema_changes(None),
            "flow": self.links.pipeline(&pipeline_id),
        });
        let q = vec![("pipelineId", i.pipeline_id), ("sha", i.sha), ("path", i.path)];
        self.get_about("/api/v1/schema-changes/ddl", &q, ("pipelineId", &pipeline_id), links).await
    }

    #[tool(
        description = "Show one database object's whole DDL as it stood BEFORE a window against how it stands NOW: both scripts in \
            full, the snapshot commits each side came from, and the line tally between them. Takes changeId (the id on a \
            database_schema_changes row) and optional since (an ISO instant, the window start); omit since to compare \
            against the start of the recorded history, which reads the whole script as added. Prefer this over \
            database_object_ddl whenever the question is the net change ('what is different about this table since last \
            week'): several snapshots may have touched the object, and this stays one before and one after, where \
            database_object_ddl answers only what one snapshot did. beforeText is null when the object did not exist at the \
            window start (it was added inside the window) and afterText is null when it has since been dropped, in which \
            case beforeText still carries its last known script so the drop stays reviewable. Reports truncated=true when a \
            side was clipped, so a large generated script is never mistaken for a complete one."
    )]
    async fn database_object_compare(&self, Parameters(i): Parameters<ObjectCompareInput>) -> String {
        let q = vec![("since", i.since.unwrap_or_default())];
        let change_id = i.change_id.to_string();
        let links = json!({ "page": self.links.schema_changes(None) });
        self.get_about(
            &format!("/api/v1/schema-changes/{}/compare", i.change_id), &q,
            ("changeId", &change_id), links,
        ).await
    }

    // ---- Change history: FLOW DEFINITIONS / YAML (read) ------------------

    #[tool(
        description = "WHAT CHANGED IN A PIPELINE: the flow YAML in git. Use this for questions about ETL code: 'what pipelines changed \
            this week', 'who edited the cyclehire flows', 'which commits mention watermark', 'what changed under the apc \
            folder'. Returns commits newest first: sha, shortSha, author name and email, committedUtc, the message, and the \
            paths each commit touched. Filter with path (a repo-relative file or folder prefix), author, message \
            (substring), since/until (ISO instants) and limit. Pass repoId when more than one repository is synced. Not for \
            database tables, views, or procedures: that is database_schema_changes."
    )]
    async fn flow_definition_history(&self, Parameters(i): Parameters<FlowHistoryInput>) -> String {
        let q = vec![
            ("repoId", i.repo_id.unwrap_or_default()),
            ("path", i.path.unwrap_or_default()),
            ("author", i.author.unwrap_or_default()),
            ("message", i.message.unwrap_or_default()),
            ("since", i.since.unwrap_or_default()),
            ("until", i.until.unwrap_or_default()),
            ("limit", i.limit.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get("/api/v1/flow-history/commits", &q).await
    }

    #[tool(
        description = "The edit history of ONE FLOW's definition: every commit that touched that pipeline's \
            own YAML file, newest first. Takes the pipelineId you already have from list_pipelines or \
            get_pipeline, so you never need to know the file path. Use it for 'when was this flow last \
            changed and by whom', then flow_definition_diff on a returned sha to see the edit itself."
    )]
    async fn flow_definition_file_history(&self, Parameters(i): Parameters<FlowFileHistoryInput>) -> String {
        let q = vec![("limit", i.limit.map(|n| n.to_string()).unwrap_or_default())];
        let links = json!({ "page": self.links.pipeline(&i.pipeline_id) });
        self.get_about(
            &format!("/api/v1/flow-history/flows/{}", i.pipeline_id), &q,
            ("pipelineId", &i.pipeline_id), links,
        ).await
    }

    #[tool(
        description = "Show the YAML that changed in one pipeline commit, as a unified diff. Pass the sha \
            from flow_definition_history or flow_definition_file_history, and optionally path to narrow a \
            multi-file commit to one flow. Use it to answer 'what did this change actually do' about ETL \
            code. For a database object's DDL, use database_object_ddl instead."
    )]
    async fn flow_definition_diff(&self, Parameters(i): Parameters<FlowDiffInput>) -> String {
        let q = vec![
            ("repoId", i.repo_id.unwrap_or_default()),
            ("sha", i.sha),
            ("path", i.path.unwrap_or_default()),
        ];
        self.get("/api/v1/flow-history/diff", &q).await
    }

    // ---- Search (read) ---------------------------------------------------

    #[tool(
        description = "Locate a named identifier anywhere in the estate - a table, column, flow, or file whose name (or a fragment of \
            it) you already have, as in \"where does CustomerId live\" or \"where is Sales computed\". Not for a message \
            given as the `!cwd <question>` command (e.g. \"!cwd what is our revenue by region\", \"!cwd how many rows \
            landed last night\") - that exact prefix routes to find_similar_questions instead, called before this tool, \
            since a question to be answered from warehouse data names no single identifier this can search for. A question phrased the same way but without \
            the `!cwd` prefix still belongs here, not there. One term fanned across every catalog surface at once - \
            warehouse objects, their columns, their code, processed files, flow YAML, and the columns flows produce - \
            returning each surface's full match count with a preview of its top hits, plus a nextSteps plan naming the tool \
            that pages each surface and the tool that turns a hit into an answer. A term absent from the synced warehouse \
            schema is routinely present in a flow's YAML or in a flow's computed columns, which is exactly what the \
            single-surface search tools miss; this call checks all of them in one round trip. When nothing matches, the \
            reply carries an ordered checklist for widening the search instead of a bare empty result: work it before \
            answering that the name does not exist."
    )]
    async fn search_all(&self, Parameters(i): Parameters<SearchAllInput>) -> String {
        done(
            self.cp
                .get("/api/v1/search/all", &[("q", i.query.clone())])
                .await
                .map(|mut v| {
                    annotate_search_all(&mut v, &i.query);
                    self.links.decorate(&mut v);
                    json_str(&v)
                }),
        )
    }

    #[tool(
        description = "Page one ETL-side surface in full, once search_all has named which surface holds the \
            term. `surface` picks it: flows (flow YAML: a flow's name, its repo path, or its body, so this is \
            how you find which pipeline mentions a term), files (files runs have processed, by name or path), \
            or statements (SQL runs actually executed, collapsed to one row per flow+step). Reach for \
            search_all first unless you already know the surface: it fans one term across every surface at \
            once and reports each one's full count, which is what tells you the surface to page here. \
            statements is the only record of SQL the engine composes at run time (staging DDL, merge \
            projections, generated casts, resolved watermarks), text that lives in no YAML and no stored \
            module body; it searches the last 90 days, so pass days=0 for all retained history before \
            concluding a term was never executed. `days` applies to statements alone. To search the warehouse \
            schema instead (tables, their columns, or the code computing a value), use search_schema. Follow \
            a hit by surface: flows to pipeline_definition(id), files to run_files(runId), statements to \
            run_statements(runId)."
    )]
    async fn search(&self, Parameters(i): Parameters<SearchInput>) -> String {
        self.page_surface(i, search_surface_spec, &["flows", "files", "statements"]).await
    }

    #[tool(
        description = "Page one warehouse-schema surface in full, once search_all has named which surface holds the term. `surface` \
            picks it: objects (tables, views, procedures, functions by name), columns (columns of schema-synced objects), \
            definitions (object code: the live module body and the generating DDL, which is where a value computed in a view \
            or procedure is found), or flowColumns (the columns flows produce, by output name, by the raw source column \
            behind them, or by the SQL expression that computes them). columns and flowColumns are the pair worth keeping \
            straight: columns sees only what the schema sync has imported, while flowColumns needs no sync and so sees \
            columns that exist only inside a pipeline, so a column missing from one is routinely present in the other. On a \
            flowColumns hit, `matchedIn` says whether the flow computes the value (Expression), emits it under that name \
            (Column), or reads it from the raw data (Source). How the allow-list applies here differs BY SURFACE: columns \
            serves only columns an admin has allow-listed (a column with no policy row is as invisible as a denied one), \
            while objects, definitions, and flowColumns are unfiltered, and definitions returns raw DDL and module bodies, \
            which name every column a table has. So this tool is kept off the chat surfaces as a whole. To write SQL for \
            someone, use search_semantic_layer and describe_semantic_table, which serve the governed schema with its \
            business context. Follow a hit with describe_object(key), or a flowColumns hit with \
            pipeline_definition(pipelineId)."
    )]
    async fn search_schema(&self, Parameters(i): Parameters<SearchInput>) -> String {
        self.page_surface(i, schema_surface_spec, &["objects", "columns", "definitions", "flowColumns"])
            .await
    }

    // ---- Data subscribers: the consumption side (read) -------------------

    #[tool(
        description = "List the DASHBOARDS and REPORTS (plus workbooks, notebooks, and applications) declared as consuming the \
            warehouse, which is where lineage ends. These are what the catalog calls data subscribers, but almost nobody \
            asks for them by that word: a question about \"the sales dashboard\", \"the Power BI report for X\", \"who looks \
            at this data\", or \"what does <report name> use\" is a question about this tool. It is the consumption layer \
            and ranks below the warehouse: a bare term is far more often a table, a column, or the code computing one, so \
            try the warehouse surfaces first and come here when the question is explicitly about a thing a person VIEWS, or \
            when those surfaces found nothing. Each entry has the subscriber's key (pass it to describe_subscriber), name, \
            type (the consuming tool: PowerBI / Tableau / Excel / ...), owner (who to tell before a breaking change), the \
            subscribers.yaml that declares it, how many queries it runs, and how many distinct objects those queries read, \
            plus `notes`: remarks about the report's state rather than its purpose (not refreshed since a given month, \
            apparently superseded, could not be opened, or an 'Incomplete dataset' naming objects it reads that the \
            warehouse does not have). A subscriber carrying that last note registers partial lineage, so its object list is \
            a floor rather than the whole truth. Filter by `type` for one tool, or `search` over \
            name/owner/description/notes. Subscribers are not database objects and never appear in browse_catalog; this is \
            their branch. For the reverse question, which subscribers consume a given table, use describe_object and read \
            its `subscribers`."
    )]
    async fn list_subscribers(&self, Parameters(i): Parameters<SubscribersInput>) -> String {
        let q = vec![
            ("type", i.subscriber_type.unwrap_or_default()),
            ("search", i.search.unwrap_or_default()),
        ];
        self.get("/api/v1/lineage/subscribers", &q).await
    }

    #[tool(
        description = "Describe one dashboard, report, or other data subscriber in a single payload: its identity and owner, its notes \
            (what is stale, superseded, or incomplete about it), every warehouse object its queries read (named and located \
            from the object registry, with the level and the specific queries that reference each), and the query texts \
            themselves. This is how you answer \"what does this dashboard use\" or \"where does this report get its data\". \
            The consumption-side twin of describe_object: that answers 'who consumes this table', this answers 'what does \
            this report consume'. Use it for impact analysis before changing a table, and to see the SQL a report actually \
            runs. A report visual's query carries two texts: `sql`, its query against the report's model, and `sourceSql`, \
            the same question translated into T-SQL over the source tables (the one prepare_query can run), or a \
            `translationProblem` saying why there is none. For the report's pages, visuals, and each field's role (the axis \
            a chart is broken down by versus the value it plots, which this payload's query texts alone do not label), use \
            describe_subscriber_report instead. Takes the `key` from list_subscribers."
    )]
    async fn describe_subscriber(&self, Parameters(i): Parameters<KeyInput>) -> String {
        self.get("/api/v1/lineage/subscribers/dossier", &[("key", i.key)]).await
    }

    #[tool(
        description = "The PAGES, VISUALS, and FIELD ROLES of one Power BI report behind a data subscriber: every page, every visual on \
            it (chart type, title, the query it was rendered as, and `questions`, 1-3 natural-language business questions \
            the visual answers), and every field a visual projects with the role it plays. Use it for \"what questions does \
            this dashboard already ask, and in what shape\", and match a user's typed question against `questions` before \
            writing new SQL from scratch. For what the report READS rather than what it asks, use describe_subscriber; for \
            how it COMPUTES its numbers (Power BI measures, calculated columns, relationships), describe_semantic_table \
            serves that as `reportModels`. The response explains its own fields in `readingThis`. Takes the `key` from \
            list_subscribers."
    )]
    async fn describe_subscriber_report(&self, Parameters(i): Parameters<KeyInput>) -> String {
        self.get_with_guide(
            "/api/v1/lineage/subscribers/report",
            &[("key", i.key)],
            SUBSCRIBER_REPORT_GUIDE,
        )
        .await
    }

    #[tool(
        // The first sentence is SILENT_RETRIEVAL_RULE, kept verbatim in sync by the
        // `tool_description_leads_with_the_silent_rule` test below: rmcp's #[tool] attribute takes only a
        // literal, so this one site cannot compose the macro and is asserted instead.
        description = "THE `!cwd` COMMAND ROUTES HERE. Call this, before any schema or semantic-layer lookup, whenever the \
            user's message is the explicit command `!cwd <question>` - e.g. \"!cwd what is our revenue by region\", \
            \"!cwd how many rows landed last night\". The `!cwd` prefix is what marks a message as a question to be \
            answered from warehouse data, whatever it is about: a reported figure, an operational count, a data-quality \
            check. Who is asking and whether the subject sounds commercial or technical make no difference; needing SQL \
            over the warehouse to answer is the whole criterion. Strip the prefix and pass the remainder as `question`. \
            Do not guess from phrasing alone that a message unprefixed by `!cwd` belongs here, even one that reads like \
            \"what is our revenue by region\" or names things that sound like table or column names: without the `!cwd` \
            prefix, route it through search_semantic_layer/describe_semantic_table like any other question instead. A \
            full question is not an identifier to a schema search's index; it is what this tool matches, once `!cwd` has \
            activated it. \
            Say nothing to the person before or between the tool calls on this path: no preamble, no restating the question, \
            no announcing what you are about to look up or that you are building a query, and no commentary on what came \
            back (whether a match is trusted, its score, who confirmed it, saved answers, reports, or tool names). All of \
            that is yours to act on, never to narrate: to a business reader it reads as the assistant explaining its own \
            plumbing, and calling a match untrusted or low-scoring reads as the assistant being unreliable. Work silently \
            until you have the answer or a query to offer, then reply once. \
            Finds the questions this estate's dashboards or a person already answered that mean the same thing as the \
            one just typed. Wording need not match, because you \
            expand the question first: pass `expanded_terms` with the question's own meaningful words plus the business \
            synonyms and related terms a dashboard could have used for the same thing, in their common grammatical forms \
            (\"what drives our turnover\" -> turnover, revenue, sales, income, drive, driver, ...), so it can find \"revenue \
            by product category\". Always pass them: without them the server searches the typed words alone unless its own \
            expansion is configured. Each match carries the SQL that already answers it, the warehouse objects that SQL \
            reads, and a `score`: a close match is a query a real report already runs in production, so adapting it beats \
            composing one from the schema, and beats searching the schema for the words in the question. `score` (how many \
            distinct searched words the question matched, several forms of one word counting once) is the ONLY trustworthy \
            confidence signal here, and `trusted` reports whether it cleared this deployment's threshold or is the same \
            question as the one typed (the same meaningful words). Do not substitute your own confidence for it: a query you \
            wrote from a 1-term match can read exactly as convincingly as one from a 4-term match and still be wrong. \
            `searchedTerms` shows what was actually looked for (the question expanded into business vocabulary) and \
            `matchedTerms` which of those each hit, for your own judgement; do not recite them to the person, who wants the \
            answer, not how it was found. A TRUSTED match needs no further checking against the schema: do not call \
            describe_semantic_table, search_semantic_layer, or any other lookup to confirm the table it reads is real before \
            offering it. That verification is what being trusted already means, and re-deriving it defeats the reason this \
            store exists, which is to reuse a checked answer instead of re-checking one. Only an untrusted match (or none at \
            all) is a lead rather than an answer; just ask whether to run the query. Only then fall back to \
            search_semantic_layer/describe_semantic_table to compose or verify something yourself. A trusted confirmed match \
            runs with auto_run_trusted_match; anything else goes through prepare_query/run_query. Each match also carries a \
            `provenance`: \"powerbi\" means a dashboard asks this question, \"user-confirmed\" means a person accepted or \
            corrected this exact answer before (and `confirmedBy` names them), which is the stronger precedent of the two at \
            the same score. `sourceRef` names the datasource that match's sql runs against: the one it was confirmed with, \
            or else the one worked out from the tables it reads. It is null only when those tables do not point at exactly \
            one datasource; never ask the person which datasource to use while it is present. A match that is `trusted` and \
            carries an `exampleId` should be run immediately with auto_run_trusted_match, without asking the person, \
            skipping the prepare_query/run_query approval round trip entirely, because this exact SQL was already confirmed \
            by a person when it was stored; a trusted match without an `exampleId` (a dashboard question) still needs \
            prepare_query/run_query. A dashboard match's `sql` is the report visual translated into T-SQL over the source \
            tables, so it runs as it is; `reportSql` is the visual's own query against the report's model and never runs. A \
            dashboard match that carries a `translationProblem` (and an empty `sql`) could not be translated: treat it as a \
            lead only, telling you which question a report answers and which fields it uses, and compose the query yourself \
            from the semantic layer. When a person, after seeing the result, explicitly tells you your answer was right, or \
            tells you how to fix it, record that with confirm_question so the next similar question finds it; agreeing to \
            run a query is not that. An empty `matches` means nothing stored matched those terms (or no questions are stored \
            yet), not that the question is unanswerable: fall back to search_semantic_layer/describe_semantic_table without \
            announcing it. When a person tells you an answer was wrong, nothing is saved, so do not call confirm_question \
            for it: offer to fix the query instead. Only correct, verified answers become precedent here."
    )]
    async fn find_similar_questions(&self, Parameters(i): Parameters<SimilarQuestionsInput>) -> String {
        let q = similar_questions_query(i);
        done(
            self.cp
                .get("/api/v1/lineage/subscribers/similar-questions", &q)
                .await
                .map(|mut v| {
                    self.links.decorate(&mut v);
                    attach_retrieval_rule(&mut v);
                    json_str(&v)
                }),
        )
    }

    #[tool(
        description = "Record what a person decided about an answer you gave, so the estate learns from it: the question, the SQL, and \
            whether they accepted it, corrected it, or rejected it. APPROVING A RUN IS NEVER CONFIRMATION: \"yes\", \"go \
            ahead\", \"run it\" in reply to a prepared query only lets it run, and says nothing about whether the result is \
            right. Storing an example is a separate decision with lasting effect (every later similar question reuses it, \
            and a trusted one auto-runs with no approval), so call this ONLY when the person, after seeing the result, \
            explicitly says the answer is correct, tells you how to fix it, or asks you to save it. Never call it in the \
            same turn you present a result, and never on a run approval alone. Tell them in one line what you will save \
            as the question, quoting it, and let them amend the wording before you call this: the question is stored as \
            typed and is what every later question is matched against, so fixing a typo or an offhand phrasing is the \
            person's call, never a tidy-up you make silently. An amended question never changes the query: pass the sql \
            they judged, unchanged, with whatever wording they settled on. This is the other half of \
            find_similar_questions, and it is what makes retrieval improve with use rather than staying frozen at whatever \
            the dashboards happened to ask. Call it after a person has actually told you the answer was right (or told you \
            what to fix), never on your own judgement that a query looks correct: the whole value of the store is that a \
            human checked every row in it, and one self-confirmed guess in there becomes precedent that grounds later \
            answers. On outcome=\"corrected\" pass the corrected sql, not what you first proposed. On outcome=\"rejected\" \
            nothing is stored: a refuted query is not knowledge, so it never becomes an example a later answer is adapted \
            from. Pass `confidence` as the `score` of the find_similar_questions match you built on, when you built on one; \
            omit it when you composed the query from the schema. The sql must be a single read-only SELECT and is parsed and \
            refused otherwise, and confirming the same question and query twice refreshes the one example rather than \
            storing a duplicate. Storing an example does not run anything: execution still goes through \
            prepare_query/run_query and their human gate, unchanged."
    )]
    async fn confirm_question(&self, Parameters(i): Parameters<ConfirmQuestionInput>) -> String {
        let mut body = json!({ "question": i.question, "sql": i.sql, "outcome": i.outcome });
        if let Some(keys) = i.object_keys {
            body["objectKeys"] = json!(keys);
        }
        if let Some(confidence) = i.confidence {
            body["confidence"] = json!(confidence);
        }
        if let Some(repo_id) = i.repo_id {
            body["repoId"] = json!(repo_id);
        }
        if let Some(source_ref) = i.source_ref {
            body["sourceRef"] = json!(source_ref);
        }
        done(
            self.cp
                .post("/api/v1/powerai/questions/confirm", body)
                .await
                .map(|v| json_str(&v)),
        )
    }

    #[tool(
        description = "Run a TRUSTED find_similar_questions match's SQL immediately, capped small, with no separate approval step - the \
            whole point being that this exact SQL was already shown to and confirmed by a person when it was stored, so \
            re-asking for the same confirmation on every later match adds friction without adding safety. Use it ONLY on a \
            match whose `trusted` is true and that carries an `exampleId` (a match without one came from a dashboard, not a \
            confirmed example, and goes through prepare_query/run_query instead). Do not ask the person for a datasource: \
            the one stored with the example is used, or else the one worked out from the tables its SQL reads, and a 409 \
            means neither exists. The response carries a `taskId` identifying the stored result. The row cap and timeout are \
            fixed by the deployment, not by you: read `maxRows`/`timeoutSeconds` back from the response rather than assuming \
            defaults. Read `ran` first: true means `result` holds the answer and you can present it: state the answer once, \
            in plain language, without preamble about the match, then always show the SQL: the response's `sqlIntro` line, \
            then its `sqlBlock` copied exactly as its own fenced code block, following `answerFormat`; never inline it in a \
            sentence and never leave it out, even for a one-line count. False means the query did not finish inside the \
            budget (or failed against the live source): do not retry auto-run for the same example, and do not tell the user \
            it ran - instead call prepare_query with the `sql` and `sourceRef` this response still carries, so a person \
            approves it the normal way. This surface needs the same 'operate' scope and ControlPlane:DataOps:Enabled switch \
            prepare_query does, plus its own ControlPlane:PowerAI:Retrieval:AutoRun:Enabled switch, off by default; a 501 \
            here means it is not turned on in this deployment."
    )]
    async fn auto_run_trusted_match(&self, Parameters(i): Parameters<AutoRunQuestionInput>) -> String {
        done(
            self.cp
                .post(
                    &format!("/api/v1/powerai/questions/{}/auto-run", i.example_id),
                    json!({}),
                )
                .await
                .map(|mut v| {
                    attach_answer_format(&mut v, &self.links);
                    json_str(&v)
                }),
        )
    }

    // ---- Schedules, nodes, sources, summary (read) -----------------------

    #[tool(
        description = "List schedules: when each fires (cron/interval, timezone, next fire) and its scope \
                       ('flow' runs one flow; 'node'/'batch' expand through lineage and run a whole set in wave \
                       order). Start here to answer 'when does <source> get updated'."
    )]
    async fn list_schedules(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/schedules", &[]).await
    }

    #[tool(description = "Get one schedule by id.")]
    async fn get_schedule(&self, Parameters(i): Parameters<GuidInput>) -> String {
        self.get(&format!("/api/v1/schedules/{}", i.id), &[]).await
    }

    #[tool(
        description = "When a source next gets updated AND exactly what that run executes: the schedule's cadence \
                       (cron, timezone, next/last fire) plus the lineage-resolved flows in wave order. Each member \
                       carries its wave; members sharing a wave run concurrently, and a wave starts only once the \
                       previous wave has finished. Use this to answer 'when does <source> update', 'what runs when \
                       it fires', and 'in what order'."
    )]
    async fn get_schedule_plan(&self, Parameters(i): Parameters<GuidInput>) -> String {
        self.get(&format!("/api/v1/schedules/{}/plan", i.id), &[]).await
    }

    #[tool(description = "List worker nodes (the compute fleet) and their heartbeats.")]
    async fn list_nodes(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/nodes", &[]).await
    }

    #[tool(description = "List the registered git repo sources the control plane auto-syncs.")]
    async fn list_repo_sources(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/repos/sources", &[]).await
    }

    #[tool(description = "Get the dashboard summary rollup (repos, pipelines, runs, health).")]
    async fn summary(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get_about("/api/v1/summary", &[], ("board", "dashboard"),
            json!({ "page": self.links.dashboard() })).await
    }

    // ---- Insights (read) -------------------------------------------------

    #[tool(
        description = "Per-flow performance over a window: run/failure counts, avg/max/total durations, \
            rows/sec throughput, last-run outcome, and the duration trend versus the previous window. Ordered \
            by total processing time, so the first rows ARE 'where the time goes'. Start here for 'what is \
            slow' and 'what got slower'."
    )]
    async fn insights_flows(&self, Parameters(i): Parameters<InsightsFlowsInput>) -> String {
        let q = vec![
            ("days", i.days.map(|n| n.to_string()).unwrap_or_default()),
            ("repoId", i.repo_id.unwrap_or_default()),
            ("batch", i.batch.unwrap_or_default()),
            // A context-frugal default: the list is ordered by total time, so 25 rows carry the story.
            ("limit", i.limit.unwrap_or(25).to_string()),
        ];
        self.get_about("/api/v1/insights/flows", &q, ("board", "insights"),
            json!({ "page": self.links.insights() })).await
    }

    #[tool(
        description = "The ranked 'what needs attention' list computed from the window's run history: flows \
            failing repeatedly, whose last run failed, getting slower, gone quiet (loaded rows before, none \
            now), dominating processing time, or active-but-silent. Deduplicated (one item per flow, extra \
            findings in an 'Also:' note) and batch-collapsed (a source whose flows tripped together reads as \
            one item with flowCount). Capped at limit; totalItems and the severity counts cover everything \
            found."
    )]
    async fn insights_attention(&self, Parameters(i): Parameters<InsightsWindowInput>) -> String {
        let q = vec![
            ("days", i.days.map(|n| n.to_string()).unwrap_or_default()),
            ("repoId", i.repo_id.unwrap_or_default()),
            ("batch", i.batch.unwrap_or_default()),
            ("limit", i.limit.map(|n| n.to_string()).unwrap_or_default()),
        ];
        self.get_about("/api/v1/insights/attention", &q, ("board", "insights"),
            json!({ "page": self.links.insights() })).await
    }

    #[tool(
        description = "The one-call optimization briefing: run-history advisories merged with the newest warehouse DMV probe results \
            (missing indexes, stale statistics, unused indexes, expensive queries) into a single ranked list, deduplicated \
            and capped at limit (default 20; totalItems and the severity counts cover everything found). Compact by default: \
            items report hasSuggestedSql; pass includeSql=true to carry the ready-to-review statements (create-index, \
            update-statistics, DROP INDEX) - present them for human review, never execute them unreviewed. The \
            warehouseProbes field reports how fresh each DMV dimension is; when it is empty or stale, run \
            analyze_warehouse_health first and re-read. The best first call for 'what should we optimize'."
    )]
    async fn insights_recommendations(&self, Parameters(i): Parameters<InsightsWindowInput>) -> String {
        let q = vec![
            ("days", i.days.map(|n| n.to_string()).unwrap_or_default()),
            ("repoId", i.repo_id.unwrap_or_default()),
            ("batch", i.batch.unwrap_or_default()),
            ("limit", i.limit.map(|n| n.to_string()).unwrap_or_default()),
            ("includeSql", i.include_sql.map(|b| b.to_string()).unwrap_or_default()),
        ];
        self.get_about("/api/v1/insights/recommendations", &q, ("board", "insights"),
            json!({ "page": self.links.insights() })).await
    }

    #[tool(
        description = "Drill one flow down to its engine steps: avg/max/total elapsed per step across the \
            window's runs, rows processed, and a sample of the SQL the step executed in the newest traced run. \
            Use after insights_flows/attention names a slow flow, to see WHICH step (and which SQL) eats the time."
    )]
    async fn insights_steps(&self, Parameters(i): Parameters<InsightsStepsInput>) -> String {
        let q = vec![
            ("days", i.days.map(|n| n.to_string()).unwrap_or_default()),
            ("includeSql", i.include_sql.map(|b| b.to_string()).unwrap_or_default()),
        ];
        self.get(&format!("/api/v1/insights/pipelines/{}/steps", i.pipeline_id), &q)
            .await
    }

    #[tool(
        description = "DataStream anomaly detection: which tables have stopped receiving data. Answers 'is anything broken that nobody \
            noticed' from the run history's own insert/update/delete statistics, with no per-table setup, so it covers every \
            stream the platform writes rather than the few with a health-check flow. Six detectors vote per table and the \
            verdict is ranked by urgency: zero data (an outage, the only category that can be critical), less data than \
            normal (a degradation, capped at warning), more data than normal (information). Categories: stalled, failing, \
            gap-days, not-running, less-than-normal, more-than-normal, healthy, never-loaded, insufficient-history. \
            Reprocessing is trimmed before anything is measured, and a zero-row day is judged against what that table \
            normally does on that kind of day, so a feed that never loads at weekends is not reported every Saturday. Only \
            streams on an enabled schedule are analysed by default; the answer reports how many were left out. Pass \
            pipelineId or flowName to drill one stream down to its day-by-day series and every detector's reasoning: that is \
            the answer to \"why is this table flagged\". The response explains its own verdict fields in `readingThis`, and \
            the method behind them is the concept page data-stream-detection (search_docs). Use insights_attention for run \
            failures and durations; use this for whether the DATA IS ARRIVING."
    )]
    async fn detect_stream_anomalies(&self, Parameters(i): Parameters<StreamAnomalyInput>) -> String {
        let pipeline_id = match self
            .resolve_stream_pipeline(i.pipeline_id.as_deref(), i.flow_name.as_deref())
            .await
        {
            Ok(id) => id,
            Err(e) => return format!("Error: {e:#}"),
        };
        if let Some(id) = pipeline_id {
            let q = vec![
                ("days", i.days.map(|n| n.to_string()).unwrap_or_default()),
                ("includeBackfills", i.include_backfills.map(|b| b.to_string()).unwrap_or_default()),
            ];
            return self
                .get_about_with_guide(
                    &format!("/api/v1/datastreams/{id}"),
                    &q,
                    ("board", "datastreams"),
                    json!({ "page": self.links.datastreams() }),
                    STREAM_ANOMALY_GUIDE,
                )
                .await;
        }

        let q = vec![
            ("days", i.days.map(|n| n.to_string()).unwrap_or_default()),
            ("repoId", i.repo_id.unwrap_or_default()),
            ("batch", i.batch.unwrap_or_default()),
            ("status", i.status.unwrap_or_default()),
            ("scope", i.scope.unwrap_or_default()),
            ("includeBackfills", i.include_backfills.map(|b| b.to_string()).unwrap_or_default()),
            ("includeUnscheduled", i.include_unscheduled.map(|b| b.to_string()).unwrap_or_default()),
            // The board is ranked most urgent first, so a short page carries the story; the counts still
            // report the whole estate.
            ("limit", i.limit.unwrap_or(25).to_string()),
        ];
        self.get_about_with_guide(
            "/api/v1/datastreams",
            &q,
            ("board", "datastreams"),
            json!({ "page": self.links.datastreams() }),
            STREAM_ANOMALY_GUIDE,
        )
        .await
    }

    /// Which single stream a caller means, or none for the board: the pipeline id they gave, else the flow
    /// whose NAME they gave, resolved through the pipeline list. The list filters by substring, so the exact
    /// name wins when it is among the hits, a single hit is taken as the answer, and anything else is
    /// reported with the candidates rather than guessed, because the drill-down of the wrong stream reads
    /// exactly like the drill-down of the right one.
    async fn resolve_stream_pipeline(
        &self,
        pipeline_id: Option<&str>,
        flow_name: Option<&str>,
    ) -> anyhow::Result<Option<String>> {
        if let Some(id) = pipeline_id.map(str::trim).filter(|s| !s.is_empty()) {
            return Ok(Some(id.to_string()));
        }

        let Some(name) = flow_name.map(str::trim).filter(|s| !s.is_empty()) else {
            return Ok(None);
        };

        let page = self
            .cp
            .get("/api/v1/pipelines", &[("name", name.to_string()), ("pageSize", "50".to_string())])
            .await?;
        let items: Vec<&Value> = page["items"].as_array().into_iter().flatten().collect();
        let exact: Vec<&Value> = items
            .iter()
            .copied()
            .filter(|p| p["name"].as_str().is_some_and(|n| n.eq_ignore_ascii_case(name)))
            .collect();
        let chosen = match (exact.len(), items.len()) {
            (1, _) => exact[0],
            (0, 1) => items[0],
            (0, 0) => anyhow::bail!(
                "No pipeline is named '{name}'. Try search_flows with a fragment of the name, then pass pipelineId."
            ),
            _ => {
                let candidates = if exact.is_empty() { &items } else { &exact };
                let listed: Vec<String> = candidates
                    .iter()
                    .map(|p| {
                        format!(
                            "{} ({})",
                            p["name"].as_str().unwrap_or("?"),
                            p["id"].as_str().unwrap_or("?")
                        )
                    })
                    .collect();
                anyhow::bail!(
                    "'{name}' matches {} pipelines: {}. Pass pipelineId to choose one.",
                    listed.len(),
                    listed.join(", ")
                );
            }
        };

        chosen["id"]
            .as_str()
            .map(|id| Some(id.to_string()))
            .ok_or_else(|| anyhow::anyhow!("The pipeline list returned no id for '{name}'."))
    }

    #[tool(
        description = "Run a live warehouse DMV probe on a worker node (requires the 'operate' scope) and wait \
            for its result: missingIndexes (sys.dm_db_missing_index_*, with CREATE INDEX suggestions), \
            statisticsHealth (sys.dm_db_stats_properties staleness, with UPDATE STATISTICS suggestions), \
            indexUsage (sys.dm_db_index_usage_stats, flagging write-only indexes), or topQueries \
            (sys.dm_exec_query_stats by total elapsed time). Omit reference to probe the estate's busiest \
            target datasource (the warehouse). SQL Server / Azure SQL sources only. The result also feeds \
            insights_recommendations for later calls."
    )]
    async fn analyze_warehouse_health(&self, Parameters(i): Parameters<WarehouseHealthInput>) -> String {
        done(self.run_warehouse_probe(i).await)
    }

    #[tool(
        description = "Report what the data-operations surface offers in THIS deployment: whether the feature             switch is on, which read-only checks are available, and the linked servers a baseline comparison             may name. Call this FIRST before check_duplicate_keys or compare_baseline, because the whole             surface sits behind a deployment switch and the response says whether it is enabled here."
    )]
    async fn dataops_capabilities(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.get("/api/v1/dataops/capabilities", &[]).await
    }

    #[tool(
        description = "Step 1 of running a business query: validate a SELECT and get back the exact statement plus a one-time token. \
            NOTHING RUNS HERE and no data is touched. The statement is parsed and refused unless it is a single read-only \
            SELECT, so anything that writes, calls a procedure, or reaches another server is rejected with the reason. After \
            calling this you MUST show the returned `sql` to the user verbatim and get their agreement BEFORE calling \
            run_query with the planId. Do not paraphrase it, do not summarise it, and do not redeem the token on your own \
            initiative: preparing is not permission to run. Phrase the ask as the response's `approvalFormat` says: a line \
            saying there is no saved answer yet (or that a report already answers it), what the query will show, the SQL, \
            and \"Want me to run it?\", never a word about trust, scores, or matches, and never a request to judge the data \
            source. Compose the SQL from real metadata first (get_table_key for the grain, get_table_joins for the ON \
            clauses, describe_object for columns); never guess a join or a column name. This surface is behind a deployment \
            switch, so check dataops_capabilities if it answers that it is not enabled."
    )]
    async fn prepare_query(&self, Parameters(i): Parameters<PrepareQueryInput>) -> String {
        done(self.run_prepare_query(i).await)
    }

    #[tool(
        description = "Step 2 of running a business query: redeem an approved plan token and return the rows. Call this ONLY after \
            prepare_query returned a statement, you showed that statement to the user, and the user agreed to it. The token \
            is single-use: redeeming it twice fails, and running the same query again means preparing it again so each \
            execution is separately approved. Read `truncated` in the result before describing the answer. A truncated \
            result is a PAGE, not the whole answer, and summing or counting one gives a confidently wrong total: say it was \
            truncated, or re-prepare with a higher maxRows or an aggregate that answers the question directly. Whenever you \
            present the rows, always include the SQL that ran: the result's `sqlIntro` line, then its `sqlBlock` copied \
            exactly as its own fenced code block, following `answerFormat`, even though you already showed the statement \
            before running it. The person's approval to run is approval to RUN, not a judgement that the answer is right: \
            never call confirm_question because they agreed to run it."
    )]
    async fn run_query(&self, Parameters(i): Parameters<RunQueryInput>) -> String {
        done(self.run_prepared_query(i).await)
    }

    #[tool(
        description = "Check one table for duplicate rows on its key, and wait for the answer. The key is the one the table declares, \
            in this order: SQLFlow's own NCI_KeyColumn business-key index (the key the load merges on), then a primary key \
            that is not a bare identity, then any other unique index. A surrogate identity key is never used, because it is \
            unique by construction and would report zero duplicates on every table. If the table declares no usable key the \
            result comes back with a `question` and the candidate columns instead of a count: ASK THE USER which columns \
            identify one row, then call again passing them as `columns`. Do not guess the key yourself."
    )]
    async fn check_duplicate_keys(&self, Parameters(i): Parameters<DuplicateKeysInput>) -> String {
        done(self.run_duplicate_keys(i).await)
    }

    #[tool(
        description = "Compare the current V3 estate against the OLD production baseline over a linked server, \
            and wait for the report. Read-only on both estates; the aggregation and the anti-join run \
            server-side, so a large table never streams to a node. Three modes, meant to be climbed in order: \
            'inventory' (which tables of a schema disagree at all, with row counts), 'schema' (one object's \
            columns position by position, which decides whether a direct transfer is legal or a compatibility \
            view is needed), and 'data' (one object's rows: duplicates, a BIDIRECTIONAL key anti-join, and \
            value parity). Data mode needs the LOGICAL key, which you must agree with the user first: a wrong \
            key invalidates every number below it, and the surrogate primary key is never the right answer."
    )]
    async fn compare_baseline(&self, Parameters(i): Parameters<CompareBaselineInput>) -> String {
        done(self.run_compare_baseline(i).await)
    }

    // ---- Operate (write) -------------------------------------------------

    #[tool(
        description = "Trigger a run of a flow (requires the 'operate' scope). Returns the queued run id; poll get_run for the outcome."
    )]
    async fn trigger_run(&self, Parameters(i): Parameters<TriggerRunInput>) -> String {
        let mut body = json!({
            "repoId": i.repo_id,
            "flowName": i.flow_name,
            "fullLoad": i.full_load.unwrap_or(false),
        });
        if let Some(p) = i.pool { body["pool"] = json!(p); }
        if let Some(c) = i.commit_sha { body["commitSha"] = json!(c); }
        if let Some(f) = i.backfill_from { body["backfillFrom"] = json!(f); }
        if let Some(t) = i.backfill_to { body["backfillTo"] = json!(t); }
        if let Some(fp) = i.file_pattern { body["filePattern"] = json!(fp); }
        done(self.cp.post("/api/v1/runs", body).await.map(|mut v| {
            // The acknowledgement carries the minted run (or group) id, so the answer can hand back a
            // link to watch it rather than only the id.
            self.links.decorate(&mut v);
            json_str(&v)
        }))
    }

    #[tool(description = "Cancel an in-flight or queued run (requires the 'operate' scope).")]
    async fn cancel_run(&self, Parameters(i): Parameters<RunIdInput>) -> String {
        done(
            self.cp
                .post(&format!("/api/v1/runs/{}/cancel", i.run_id), json!({}))
                .await
                .map(|v| if v.is_null() { "Cancellation requested.".to_string() } else { json_str(&v) }),
        )
    }

    #[tool(
        description = "Propose pipelines to a tracked repo source as a pull request (requires the 'author' scope). \
Writes the given flow files onto a fresh branch off the source's tracked branch, commits them under the calling \
user, and opens a pull request for review; it never writes the catalog directly, so a human reviews and merges \
before the managed sync imports the flows. Returns the pull-request URL and number, the pushed head branch, and \
the head commit SHA. Feed that commitSha to trigger_run to test the proposal pinned to the PR commit before it \
merges (a changed flow already in the catalog runs this way; a brand-new flow is not in the catalog until the PR \
merges and syncs). The canonical authoring loop is: generate each file with scaffold_ingestion_flow (a \
relational source) or discover_source (a JSON/NDJSON/XML file source), edit only what the generator could \
not know, validate with validate_flow, THEN propose; never compose flow YAML from scratch when a generator \
covers the source. The control plane preflights every proposed file with the engine's own loader: a file \
that would not import is rejected with the loader's message, and warnings (a revised flow whose declared \
source/target endpoints changed, a duplicate flow name) come back in the response and are stamped into the \
pull-request body for the reviewer. Works over both stdio and HTTP (it only proxies the control plane)."
    )]
    async fn propose_pipelines(&self, Parameters(i): Parameters<ProposePipelinesInput>) -> String {
        let files: Vec<Value> = i
            .files
            .into_iter()
            .map(|f| json!({ "path": f.path, "content": f.content }))
            .collect();
        let mut body = json!({ "title": i.title, "files": files });
        if let Some(b) = i.body {
            body["body"] = json!(b);
        }
        if let Some(bb) = i.base_branch {
            body["baseBranch"] = json!(bb);
        }
        if let Some(hb) = i.head_branch {
            body["headBranch"] = json!(hb);
        }
        done(
            self.cp
                .post(&format!("/api/v1/repos/sources/{}/proposals", i.source_id), body)
                .await
                .map(|v| json_str(&v)),
        )
    }

    #[tool(
        description = "Scan a JSON/NDJSON/XML sample file or folder and generate a ready-to-run `.flow.yaml` \
stub (mode=flatten), or report its path structure (mode=paths|discover). The record grain (JSON rootPath / \
XML rowXPath) is auto-detected from sample statistics, so an envelope like {items:[...]} or an RSS feed \
produces one row per record without a hand-written config. This is the canonical starting point for a new \
FILE-source pipeline (scaffold_ingestion_flow is its relational twin); do not compose file-flow YAML from \
scratch when a sample is available. Runs the local `sqlflow` CLI, so it reads any path (local, UNC, or \
cloud) the CLI's file stores can reach. Validate the emitted YAML with validate_flow before returning or \
proposing it."
    )]
    async fn discover_source(&self, Parameters(i): Parameters<DiscoverSourceInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_DISCOVER_NOTE.to_string();
        }
        let mode = i.mode.as_deref().unwrap_or("flatten").to_ascii_lowercase();
        let subcommand = match mode.as_str() {
            "flatten" | "paths" | "discover" => mode.as_str(),
            other => {
                return format!("Unknown mode '{other}'. Use 'flatten', 'paths', or 'discover'.");
            }
        };

        let mut args: Vec<String> = vec![subcommand.to_string(), i.path.clone()];
        if let Some(p) = &i.pattern {
            args.push("--pattern".into());
            args.push(p.clone());
        }
        if i.recursive.unwrap_or(false) {
            args.push("--recursive".into());
        }
        if let Some(n) = i.max_files {
            args.push("--max-files".into());
            args.push(n.to_string());
        }
        if let Some(n) = i.max_records {
            args.push("--max-records".into());
            args.push(n.to_string());
        }
        if let Some(n) = i.max_depth {
            args.push("--max-depth".into());
            args.push(n.to_string());
        }
        if let Some(r) = &i.root {
            args.push("--root".into());
            args.push(r.clone());
        }

        // Flatten-only rule flags (the other modes ignore them).
        if subcommand == "flatten" {
            for (flag, value) in [
                ("--include", &i.include),
                ("--exclude", &i.exclude),
                ("--explode", &i.explode),
                ("--keep", &i.keep),
                ("--array", &i.array),
                ("--separator", &i.separator),
            ] {
                if let Some(v) = value {
                    args.push(flag.into());
                    args.push(v.clone());
                }
            }
        }

        match run_sqlflow_cli(args).await {
            Ok(output) => output,
            Err(message) => message,
        }
    }

    #[tool(
        description = "Scaffold the CANONICAL table-to-table ingestion flow (`flowType: ing`) from a live \
relational source: introspects the object, fills `load.keyColumns` from its primary key (detecting a \
minimal unique key when none is declared), and suggests candidate `incremental` date columns as comments, \
so the emitted YAML is the engine's own idiomatic design rather than a hand-assembled one. This is the \
REQUIRED starting point for a new relational pipeline; do not compose ing YAML from scratch. After \
scaffolding: pick the change-tracking column and uncomment the `incremental` block (never hand-write \
watermark SQL; the engine probes and composes the predicate), add batch/schedule, then validate_flow, \
then propose_pipelines. Runs the local `sqlflow` CLI (`catalog scaffold`), so it needs the CLI on PATH \
(or SQLFLOW_CLI) and a reachable source connection."
    )]
    async fn scaffold_ingestion_flow(&self, Parameters(i): Parameters<ScaffoldIngestionInput>) -> String {
        if self.http_mode {
            return HTTP_MODE_SCAFFOLD_NOTE.to_string();
        }

        let mut args: Vec<String> = vec![
            "catalog".into(),
            "scaffold".into(),
            "--source".into(),
            i.source.clone(),
            "--object".into(),
            i.object.clone(),
            "--target-object".into(),
            i.target_object.clone(),
        ];
        if let Some(provider) = &i.provider {
            args.push("--provider".into());
            args.push(provider.clone());
        }
        if let Some(target) = &i.target {
            args.push("--target".into());
            args.push(target.clone());
        }
        if let Some(name) = &i.name {
            args.push("--name".into());
            args.push(name.clone());
        }
        if let Some(keys) = i.keys.as_ref().filter(|k| !k.is_empty()) {
            args.push("--keys".into());
            args.push(keys.join(","));
        }
        if i.detect_keys.unwrap_or(true) {
            args.push("--detect-keys".into());
        }
        if let Some(sample) = i.sample {
            args.push("--sample".into());
            args.push(sample.to_string());
        }

        match run_sqlflow_cli(args).await {
            Ok(output) => output,
            Err(message) => message,
        }
    }
}

/// Puts a subject's links on a payload's envelope: an object keeps any links it already carries (the row rules
/// know the row better than the caller does), and a bare array is wrapped in an envelope naming the subject, so
/// there is somewhere for them to live without touching the rows themselves.
fn with_subject(value: Value, subject: (&str, &str), links: Value) -> Value {
    match value {
        Value::Object(mut map) => {
            map.entry("links").or_insert(links);
            Value::Object(map)
        }
        items => json!({ subject.0: subject.1, "links": links, "items": items }),
    }
}

impl SqlFlowMcp {
    /// Shared GET-and-render used by every read tool: the control plane's payload with a `links`
    /// object added to every row that names something the GUI can open.
    /// Pages one search surface for [`Self::search`] and [`Self::search_schema`]. Both resolve a `surface`
    /// argument the same way and differ only in which set they accept, so the paging lives here rather than
    /// twice: an unknown surface names the ones this tool does serve, which lets a caller that guessed the
    /// wrong tool recover in one step instead of retrying blind.
    async fn page_surface(
        &self,
        input: SearchInput,
        resolve: fn(&str) -> Option<SearchSurfaceSpec>,
        valid: &[&str],
    ) -> String {
        let surface = input.surface.trim();
        let Some(spec) = resolve(surface) else {
            let served_elsewhere =
                search_surface_spec(surface).is_some() || schema_surface_spec(surface).is_some();
            return json_str(&json!({
                "error": format!("This tool does not page the '{surface}' surface."),
                "validSurfaces": valid,
                "hint": if served_elsewhere {
                    "That surface exists, but on the other search tool: `search` pages flows, files, and \
                     statements; `search_schema` pages objects, columns, definitions, and flowColumns."
                } else {
                    "Call search_all to learn which surface holds the term, then page it with the tool that \
                     serves that surface."
                },
            }));
        };

        let mut query = vec![
            (spec.query_param, input.query),
            ("page", input.page.map(|n| n.to_string()).unwrap_or_default()),
            ("pageSize", input.page_size.map(|n| n.to_string()).unwrap_or_default()),
        ];
        if spec.takes_days {
            query.push(("days", input.days.map(|n| n.to_string()).unwrap_or_default()));
        }
        self.get(spec.path, &query).await
    }

    async fn get(&self, path: &str, query: &[(&str, String)]) -> String {
        done(self.cp.get(path, query).await.map(|mut v| {
            self.links.decorate(&mut v);
            json_str(&v)
        }))
    }

    /// [`Self::get`] plus a reading guide on the payload. Used by the tools whose descriptions used to carry
    /// their own field semantics inline.
    async fn get_with_guide(
        &self,
        path: &str,
        query: &[(&str, String)],
        guide: &'static str,
    ) -> String {
        done(self.cp.get(path, query).await.map(|mut v| {
            self.links.decorate(&mut v);
            attach_guide(&mut v, guide);
            json_str(&v)
        }))
    }

    /// The same, for a result that is ABOUT something the CALLER named rather than about the rows it
    /// returns: a flow's columns, an object's columns, a repo's edges, one file's provenance. Those rows
    /// carry no identity of their own (they are already scoped by the path), so without this the answer
    /// has nothing to link even though the subject is known. The rows are decorated exactly as everywhere
    /// else; the subject's links then go on the envelope, and a bare array is wrapped in one so there is
    /// an envelope to put them on.
    async fn get_about(
        &self,
        path: &str,
        query: &[(&str, String)],
        subject: (&str, &str),
        links: Value,
    ) -> String {
        done(self.cp.get(path, query).await.map(|mut v| {
            self.links.decorate(&mut v);
            json_str(&with_subject(v, subject, links))
        }))
    }

    /// [`Self::get_about`] plus a reading guide, for a subject payload whose fields need interpreting.
    async fn get_about_with_guide(
        &self,
        path: &str,
        query: &[(&str, String)],
        subject: (&str, &str),
        links: Value,
        guide: &'static str,
    ) -> String {
        done(self.cp.get(path, query).await.map(|mut v| {
            self.links.decorate(&mut v);
            let mut v = with_subject(v, subject, links);
            attach_guide(&mut v, guide);
            json_str(&v)
        }))
    }

    /// The datasource a data-operations tool measures when the caller names none: the estate's busiest
    /// resolvable SQL Server target (by pipelines writing through it). In this product's model that IS the
    /// warehouse, so the common ask needs no reference at all.
    async fn default_warehouse_reference(&self) -> anyhow::Result<String> {
        let datasources = self.cp.get("/api/v1/datasources", &[]).await?;
        datasources
            .as_array()
            .into_iter()
            .flatten()
            .filter(|d| {
                d["resolvable"].as_bool() == Some(true)
                    && matches!(d["kind"].as_str(), None | Some("MSSQL") | Some("AZDB"))
            })
            .max_by_key(|d| d["targetPipelines"].as_i64().unwrap_or(0))
            .and_then(|d| d["reference"].as_str().map(String::from))
            .ok_or_else(|| {
                anyhow::anyhow!(
                    "No resolvable SQL Server datasource found. Pass `reference` explicitly \
                     (see the datasources list)."
                )
            })
    }

    /// Resolves the reference a tool will run against: the caller's, or the estate's warehouse.
    async fn resolve_reference(&self, reference: Option<String>) -> anyhow::Result<String> {
        match reference {
            Some(reference) if !reference.trim().is_empty() => Ok(reference.trim().to_string()),
            _ => self.default_warehouse_reference().await,
        }
    }

    /// Enqueues one compute task and long-polls it to a terminal state. Every interactive datasource tool
    /// goes through here, so the enqueue, the poll budget, and the truncation of a fat result are decided in
    /// one place. Each poll long-polls server-side for up to 20s; twelve rounds outlast the server's own
    /// probe budget, so a hung task still terminates here carrying the task's own timeout error.
    async fn run_compute_task(&self, label: &str, body: Value) -> anyhow::Result<String> {
        let accepted = self.cp.post("/api/v1/datasources/tasks", body).await?;
        let task_id = accepted["taskId"]
            .as_str()
            .map(String::from)
            .ok_or_else(|| anyhow::anyhow!("The control plane's accept response carried no taskId: {accepted}"))?;

        for _ in 0..12 {
            let mut task = self
                .cp
                .get(&format!("/api/v1/datasources/tasks/{task_id}"), &[("waitMs", "20000".to_string())])
                .await?;
            match task["status"].as_str() {
                Some("succeeded") | Some("failed") | Some("cancelled") | Some("skipped") => {
                    truncate_long_strings(&mut task, 400);
                    return Ok(json_str(&task));
                }
                _ => {}
            }
        }

        anyhow::bail!(
            "The {label} task {task_id} did not reach a terminal state in time; check it with the \
             datasources task list."
        )
    }

    /// The four warehouse-health DMV probes. They keep their own operation (and therefore their historical
    /// result shape, which the insights recommendations parse) while sharing the enqueue and poll above.
    async fn run_warehouse_probe(&self, input: WarehouseHealthInput) -> anyhow::Result<String> {
        const OPERATIONS: [&str; 4] = ["missingIndexes", "statisticsHealth", "indexUsage", "topQueries"];
        if !OPERATIONS.contains(&input.operation.as_str()) {
            anyhow::bail!(
                "Unknown warehouse-health operation '{}'. Valid operations: {}.",
                input.operation,
                OPERATIONS.join(", ")
            );
        }

        let reference = self.resolve_reference(input.reference).await?;
        let mut body = json!({ "reference": reference, "operation": input.operation });
        if let Some(database) = input.database.filter(|d| !d.trim().is_empty()) {
            body["database"] = json!(database.trim());
        }
        // Context-frugal default: 20 ranked rows answer the question; the task row keeps whatever ran.
        body["limit"] = json!(input.limit.unwrap_or(20));
        if let Some(pool) = input.pool.filter(|p| !p.trim().is_empty()) {
            body["pool"] = json!(pool);
        }

        self.run_compute_task(&input.operation, body).await
    }

    /// The interpreted key, projected out of the dossier. The dossier is one catalog read; trimming it here
    /// is what keeps a narrow question from spending a wide answer's worth of context.
    async fn table_key(&self, input: TableKeyInput) -> anyhow::Result<String> {
        let dossier = self.cp.get("/api/v1/lineage/objects/dossier", &[("key", input.key.clone())]).await?;
        let object = &dossier["object"];
        if object.is_null() {
            anyhow::bail!(
                "No lineage object matches the key '{}'. Find it with search_objects or lineage_objects.",
                input.key
            );
        }

        let key_columns = object["keyColumns"].as_str().unwrap_or_default();
        let columns: Vec<&str> = if key_columns.is_empty() {
            Vec::new()
        } else {
            key_columns.split(',').map(str::trim).filter(|c| !c.is_empty()).collect()
        };

        Ok(json_str(&json!({
            "object": object["key"],
            "name": object["name"],
            "database": object["database"],
            "schema": object["schema"],
            "kind": object["kind"],
            "keyColumns": columns,
            // Constraint = a declared PK/unique constraint, Declared = named by a flow, Merge = the key the
            // load merges on. Null means nothing in the codebase names a key for this object at all.
            "keyOrigin": object["keyOrigin"],
            "hasKey": !columns.is_empty(),
            "columnCount": dossier["columns"].as_array().map(|c| c.len()).unwrap_or(0),
            "note": if columns.is_empty() {
                "No key is interpreted for this object from the codebase. Run detect_unique_key to profile \
                 the rows and find what actually identifies them."
            } else {
                "This is the key as the CODEBASE interprets it, not a measurement of the data. Run \
                 detect_unique_key to confirm it holds, or check_duplicate_keys to count violations."
            },
        })))
    }

    /// The join routes. The walk itself lives in the control plane, which holds the relationship graph and
    /// can breadth-first it in a bounded number of round trips; this only shapes the answer.
    async fn table_joins(&self, input: TableJoinsInput) -> anyhow::Result<String> {
        let mut query = vec![("key", input.key.clone())];
        if let Some(other) = input.other.filter(|o| !o.trim().is_empty()) {
            query.push(("target", other.trim().to_string()));
        }
        if let Some(hops) = input.max_hops {
            query.push(("maxHops", hops.to_string()));
        }

        let mut result = self.cp.get("/api/v1/lineage/objects/join-paths", &query).await?;
        self.links.decorate(&mut result);
        Ok(json_str(&result))
    }

    async fn run_detect_unique_key(&self, input: DetectUniqueKeyInput) -> anyhow::Result<String> {
        let reference = self.resolve_reference(input.reference).await?;
        let mut body = json!({
            "reference": reference,
            "operation": "detectUniqueKey",
            "schema": input.schema.trim(),
            "objectName": input.object_name.trim(),
        });
        if let Some(database) = input.database.filter(|d| !d.trim().is_empty()) {
            body["database"] = json!(database.trim());
        }
        if let Some(sample) = input.sample_size {
            body["sampleSize"] = json!(sample);
        }
        if let Some(columns) = input.max_key_columns {
            body["maxKeyColumns"] = json!(columns);
        }
        if let Some(candidates) = input.max_candidates {
            body["maxCandidates"] = json!(candidates);
        }
        if let Some(verify) = input.verify_candidates {
            body["verifyCandidates"] = json!(verify);
        }
        if let Some(trust) = input.trust_declared_keys {
            body["trustDeclaredKeys"] = json!(trust);
        }
        if let Some(pool) = input.pool.filter(|p| !p.trim().is_empty()) {
            body["pool"] = json!(pool);
        }

        self.run_compute_task("detectUniqueKey", body).await
    }

    async fn run_prepare_query(&self, input: PrepareQueryInput) -> anyhow::Result<String> {
        // No local default: the control plane infers the datasource from the tables the query reads, which a
        // "busiest datasource" guess here would silently override with a connection the query may not live on.
        let mut body = json!({ "sql": input.sql });
        if let Some(reference) = input.reference.filter(|r| !r.trim().is_empty()) {
            body["reference"] = json!(reference.trim());
        }
        if let Some(database) = input.database.filter(|d| !d.trim().is_empty()) {
            body["database"] = json!(database.trim());
        }
        if let Some(rows) = input.max_rows {
            body["maxRows"] = json!(rows);
        }
        if let Some(seconds) = input.timeout_seconds {
            body["timeoutSeconds"] = json!(seconds);
        }
        if let Some(pool) = input.pool.filter(|p| !p.trim().is_empty()) {
            body["pool"] = json!(pool);
        }

        let mut prepared = self.cp.post("/api/v1/dataops/queries/prepare", body).await?;
        attach_approval_format(&mut prepared);
        Ok(json_str(&prepared))
    }

    /// Redeems the plan and waits for the rows. The wait is the same enqueue-and-poll every other interactive
    /// datasource tool uses, so a query behaves like the rest of the surface.
    async fn run_prepared_query(&self, input: RunQueryInput) -> anyhow::Result<String> {
        let plan = input.plan_id.trim();
        if plan.is_empty() {
            anyhow::bail!("run_query needs the planId that prepare_query returned.");
        }

        let accepted = self
            .cp
            .post(&format!("/api/v1/dataops/queries/{plan}/run"), json!({}))
            .await?;
        let task_id = accepted["taskId"]
            .as_str()
            .map(String::from)
            .ok_or_else(|| anyhow::anyhow!("The control plane's accept response carried no taskId: {accepted}"))?;

        for _ in 0..12 {
            let mut task = self
                .cp
                .get(&format!("/api/v1/datasources/tasks/{task_id}"), &[("waitMs", "20000".to_string())])
                .await?;
            match task["status"].as_str() {
                Some("succeeded") | Some("failed") | Some("cancelled") | Some("skipped") => {
                    // Deliberately NOT truncate_long_strings here: the caller asked for these values, and
                    // silently trimming a cell would report altered data as the answer. The runner already
                    // bounds rows and cell size at the source.
                    attach_answer_format(&mut task, &self.links);
                    return Ok(json_str(&task));
                }
                _ => {}
            }
        }

        anyhow::bail!(
            "The query (task {task_id}) did not finish in time. It may still be running; check it with the \
             datasources task list."
        )
    }

    async fn run_duplicate_keys(&self, input: DuplicateKeysInput) -> anyhow::Result<String> {
        let reference = self.resolve_reference(input.reference).await?;
        let mut body = json!({
            "reference": reference,
            "operation": "duplicateKeys",
            "schema": input.schema.trim(),
            "objectName": input.object_name.trim(),
            "limit": input.limit.unwrap_or(20),
        });
        if let Some(database) = input.database.filter(|d| !d.trim().is_empty()) {
            body["database"] = json!(database.trim());
        }
        if let Some(columns) = input.columns.filter(|c| !c.is_empty()) {
            body["columns"] = json!(columns);
        }
        if let Some(pool) = input.pool.filter(|p| !p.trim().is_empty()) {
            body["pool"] = json!(pool);
        }

        self.run_compute_task("duplicateKeys", body).await
    }

    async fn run_compare_baseline(&self, input: CompareBaselineInput) -> anyhow::Result<String> {
        const MODES: [&str; 3] = ["inventory", "schema", "data"];
        let mode = input.mode.trim().to_ascii_lowercase();
        if !MODES.contains(&mode.as_str()) {
            anyhow::bail!(
                "Unknown compare mode '{}'. Valid modes: {}.",
                input.mode,
                MODES.join(", ")
            );
        }

        if mode == "data" && input.key_expressions.as_ref().is_none_or(|k| k.is_empty()) {
            anyhow::bail!(
                "Data mode needs `keyExpressions`: the LOGICAL key identifying one real-world reading on both \
                 estates. Agree it with the user before running; the surrogate primary key is not it, because \
                 the two estates assign it independently."
            );
        }

        let reference = self.resolve_reference(input.reference).await?;
        let mut body = json!({
            "reference": reference,
            "operation": "compareBaseline",
            "compareMode": mode,
            "schema": input.schema.trim(),
            "baselineDatabase": input.baseline_database.trim(),
            "limit": input.limit.unwrap_or(50),
        });
        if let Some(object) = input.object_name.filter(|v| !v.trim().is_empty()) {
            body["objectName"] = json!(object.trim());
        }
        if let Some(server) = input.linked_server.filter(|v| !v.trim().is_empty()) {
            body["linkedServer"] = json!(server.trim());
        }
        if let Some(schema) = input.baseline_schema.filter(|v| !v.trim().is_empty()) {
            body["baselineSchema"] = json!(schema.trim());
        }
        if let Some(object) = input.baseline_object_name.filter(|v| !v.trim().is_empty()) {
            body["baselineObjectName"] = json!(object.trim());
        }
        if let Some(keys) = input.key_expressions.filter(|k| !k.is_empty()) {
            body["keyExpressions"] = json!(keys);
        }
        if let Some(columns) = input.compare_columns.filter(|c| !c.is_empty()) {
            body["compareColumns"] = json!(columns);
        }
        if let Some(filter) = input.filter.filter(|v| !v.trim().is_empty()) {
            body["where"] = json!(filter.trim());
        }
        if let Some(pool) = input.pool.filter(|p| !p.trim().is_empty()) {
            body["pool"] = json!(pool);
        }

        self.run_compute_task(&format!("compareBaseline:{mode}"), body).await
    }
}

/// Tools that take no arguments still need a parameter type for the macro.
#[derive(Debug, Deserialize, JsonSchema)]
pub struct EmptyInput {}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct SchemaChangesInput {
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// One managed database, exactly as database_schema_history_databases reports it (e.g. "dw-dwh-prod").
    pub database: Option<String>,
    /// Added | Changed | Deleted.
    #[serde(rename = "changeType")]
    pub change_type: Option<String>,
    /// ISO instant; only changes observed at or after it are returned.
    pub since: Option<String>,
    /// Substring match on the object name, its schema, or its category.
    pub search: Option<String>,
    pub page: Option<i64>,
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct SchemaChangeDatabasesInput {
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// ISO instant; scopes the tally to changes at or after it.
    pub since: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ObjectDdlInput {
    /// The scm flow that recorded the change (the pipelineId on a database_schema_changes row).
    #[serde(rename = "pipelineId")]
    pub pipeline_id: String,
    /// The snapshot commit (the commitSha on a database_schema_changes row).
    pub sha: String,
    /// The object's repository path: "<database>/<category>/<schema>.<name>.sql".
    pub path: String,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ObjectCompareInput {
    /// The schema-change row to compare (the id on a database_schema_changes row). It carries the object's
    /// identity, so no repository path is passed: the server rebuilds it.
    #[serde(rename = "changeId")]
    pub change_id: i64,
    /// ISO instant: the window start. The "before" side is the newest snapshot at or before it. Omit to
    /// compare against the start of the recorded history.
    pub since: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct FlowHistoryInput {
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// A repo-relative file or folder prefix, e.g. "cyclehire" or "cyclehire/cyclehire_00_api.yaml".
    pub path: Option<String>,
    /// Substring match on the commit author's name or email.
    pub author: Option<String>,
    /// Substring match on the commit message.
    pub message: Option<String>,
    /// ISO instant lower bound on the commit date.
    pub since: Option<String>,
    /// ISO instant upper bound on the commit date.
    pub until: Option<String>,
    /// Maximum commits to return (default 50, capped at 200).
    pub limit: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct FlowFileHistoryInput {
    #[serde(rename = "pipelineId")]
    pub pipeline_id: String,
    /// Maximum commits to return (default 50, capped at 200).
    pub limit: Option<i64>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct FlowDiffInput {
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// The commit to diff, from flow_definition_history.
    pub sha: String,
    /// Narrow a multi-file commit to one file.
    pub path: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct DiscoverSourceInput {
    /// Path to a JSON/NDJSON/XML sample file, or a folder of them. Local or UNC
    /// paths, and any cloud location the SQLFlow CLI's file stores handle. For a
    /// folder, set `pattern` (and `recursive` to walk sub-folders).
    pub path: String,
    /// One of: "flatten" (default) emits a runnable `.flow.yaml` stub with the
    /// resolved column schema; "paths" lists every addressable path; "discover"
    /// reports the path structure and the starter options. All three auto-detect
    /// the record grain (JSON rootPath / XML rowXPath) from sample statistics.
    pub mode: Option<String>,
    /// Glob narrowing a folder scan, e.g. "*.json" or "orders_*.xml". Ignored for a single file.
    pub pattern: Option<String>,
    /// Walk sub-folders of a folder path (default false).
    pub recursive: Option<bool>,
    /// Cap the number of sample files scanned (default 100).
    #[serde(rename = "maxFiles")]
    pub max_files: Option<u32>,
    /// Cap the number of sample records scanned (0 = unbounded).
    #[serde(rename = "maxRecords")]
    pub max_records: Option<u32>,
    /// Max nesting depth to inspect (flatten/paths default 20, discover 10).
    #[serde(rename = "maxDepth")]
    pub max_depth: Option<u32>,
    /// Pin the record grain instead of auto-detecting it: a JSON rootPath (e.g.
    /// "$.items") or an XML rowXPath (e.g. "/rss/channel/item"). Overrides detection.
    pub root: Option<String>,
    /// Comma-separated paths to flatten (whitelist). Flatten mode only.
    pub include: Option<String>,
    /// Comma-separated paths whose subtree is dropped. Flatten mode only.
    pub exclude: Option<String>,
    /// Comma-separated repeating paths to explode into one row each. Flatten mode only.
    pub explode: Option<String>,
    /// Comma-separated paths kept verbatim as one string column (jsonPaths/xmlPaths). Flatten mode only.
    pub keep: Option<String>,
    /// JSON array handling: to_json | first_element | join | count | skip | explode. Flatten mode only.
    pub array: Option<String>,
    /// Column-name separator (default "_"). Flatten mode only.
    pub separator: Option<String>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ScaffoldIngestionInput {
    /// The source connection: a ${env:NAME} / ${keyvault:vault/secret} reference (embedded
    /// verbatim in the YAML) or a literal connection string (used for introspection only; the
    /// YAML gets a ${env:SQLFLOW_SOURCE} placeholder so no secret is ever written).
    pub source: String,
    /// The source object to scaffold from: database.schema.table (or database.table for MySQL).
    pub object: String,
    /// The target object on the warehouse, as schema.table (e.g. "raw.Orders").
    #[serde(rename = "targetObject")]
    pub target_object: String,
    /// Source provider: mssql (default) | azdb | mysql | postgres.
    pub provider: Option<String>,
    /// The target connection reference embedded under connections: (default ${env:SQLFLOW_DW}).
    pub target: Option<String>,
    /// Explicit key columns for load.keyColumns; omit to use the source's introspected primary
    /// key (with live unique-key detection as the fallback).
    pub keys: Option<Vec<String>>,
    /// When the source declares no primary key and no keys are given, profile the table to
    /// detect a minimal unique key (default true). SQL Server sources only.
    #[serde(rename = "detectKeys")]
    pub detect_keys: Option<bool>,
    /// The flow name (defaults to "schema-table" lowercased).
    pub name: Option<String>,
    /// Row sample size for key detection; omit to auto-sample large tables only.
    pub sample: Option<u32>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ProposePipelinesInput {
    /// The tracked repo source to propose into (its id from list_repo_sources).
    #[serde(rename = "sourceId")]
    pub source_id: String,
    /// The pull-request title; also the commit summary.
    pub title: String,
    /// Optional pull-request description / commit body.
    pub body: Option<String>,
    /// Branch to open the pull request against; defaults to the source's tracked branch.
    #[serde(rename = "baseBranch")]
    pub base_branch: Option<String>,
    /// Feature branch to push; defaults to a content-derived `sqlflow/proposal-*` name.
    #[serde(rename = "headBranch")]
    pub head_branch: Option<String>,
    /// The flow files to write: each a repo-relative path plus its full content.
    pub files: Vec<ProposeFileInput>,
}

#[derive(Debug, Deserialize, JsonSchema)]
pub struct ProposeFileInput {
    /// Repo-relative path ending in a flow extension (.flow.yaml / .yaml / .yml / .sql / .json / .md),
    /// e.g. "flows/orders.01_pre.flow.yaml".
    pub path: String,
    /// The file's full content.
    pub content: String,
}

// --- Server handler --------------------------------------------------------

/// The bearer on an inbound HTTP request's `Authorization` header, when present and
/// well-formed (`Bearer <non-empty token>`, scheme case-insensitive per RFC 6750).
pub(crate) fn bearer_token(headers: &http::HeaderMap) -> Option<String> {
    let value = headers.get(http::header::AUTHORIZATION)?.to_str().ok()?;
    let (scheme, rest) = value.trim().split_once(' ')?;
    if !scheme.eq_ignore_ascii_case("bearer") {
        return None;
    }
    let token = rest.trim();
    (!token.is_empty()).then(|| token.to_string())
}

/// The client surface an inbound HTTP request declares through the MCP URL's query, when it is the
/// assistant (`surface=assistant`, `McpOptions.AssistantSurfaceQuery`). Any other value is ignored
/// rather than forwarded, so only the one surface the control plane understands is ever sent.
pub(crate) fn request_surface(query: Option<&str>) -> Option<String> {
    query?.split('&').find_map(|pair| {
        let (name, value) = pair.split_once('=')?;
        (name == "surface" && value == crate::control_plane::ASSISTANT_SURFACE).then(|| value.to_string())
    })
}

#[cfg(test)]
mod surface_tests {
    use super::request_surface;

    #[test]
    fn the_assistant_surface_is_read_from_the_query() {
        assert_eq!(request_surface(Some("surface=assistant")).as_deref(), Some("assistant"));
        assert_eq!(request_surface(Some("a=1&surface=assistant")).as_deref(), Some("assistant"));
    }

    #[test]
    fn anything_else_declares_no_surface() {
        assert_eq!(request_surface(None), None);
        assert_eq!(request_surface(Some("")), None);
        assert_eq!(request_surface(Some("surface=gui")), None);
        assert_eq!(request_surface(Some("surfaces=assistant")), None);
    }
}

// `router = self.tool_router` on purpose: the macro otherwise generates `list_tools`/`get_tool` against
// `Self::tool_router()`, the freshly generated router, so the GROUNDING_RULE that `grounded_router` appends
// would be applied to a router no client ever reads. The field is the grounded one built in `with_mode`.
#[tool_handler(router = self.tool_router)]
impl ServerHandler for SqlFlowMcp {
    /// Hand-written dispatch (the #[tool_handler] macro only generates `call_tool`
    /// when the impl lacks one): over HTTP, rmcp injects the request's
    /// `http::request::Parts` into the context extensions, and the caller's bearer is
    /// scoped into `HTTP_BEARER` around the tool call so every control-plane request
    /// runs as that caller. Over stdio there are no parts and dispatch is unchanged.
    async fn call_tool(
        &self,
        request: rmcp::model::CallToolRequestParams,
        context: rmcp::service::RequestContext<rmcp::RoleServer>,
    ) -> Result<rmcp::model::CallToolResult, rmcp::ErrorData> {
        let parts = context.extensions.get::<http::request::Parts>();
        let bearer = parts.and_then(|parts| bearer_token(&parts.headers));
        let surface = parts.and_then(|parts| request_surface(parts.uri.query()));
        let tcc = rmcp::handler::server::tool::ToolCallContext::new(self, request, context);
        let call = self.tool_router.call(tcc);
        match (bearer, surface) {
            (Some(token), Some(surface)) => {
                crate::control_plane::HTTP_BEARER
                    .scope(token, crate::control_plane::HTTP_SURFACE.scope(surface, call))
                    .await
            }
            (Some(token), None) => crate::control_plane::HTTP_BEARER.scope(token, call).await,
            (None, _) => call.await,
        }
    }

    fn get_info(&self) -> ServerInfo {
        let online_setup = if self.http_mode { ONLINE_SETUP_HTTP } else { ONLINE_SETUP_STDIO };
        let discovery = if self.http_mode { "" } else { DISCOVERY_STDIO };
        ServerInfo::new(ServerCapabilities::builder().enable_tools().build())
            .with_instructions(format!(
                "{INSTRUCTIONS_OFFLINE}{discovery}\n{online_setup}{INSTRUCTIONS_ONLINE_TAIL}"
            ))
    }
}

const INSTRUCTIONS_OFFLINE: &str = "\
SQLFlow MCP server.

ROUTING, BEFORE ANYTHING ELSE: a message beginning with the literal `!cwd` is a question to be answered
from warehouse data. Strip the prefix and call find_similar_questions FIRST, before any schema lookup,
any semantic-layer search, and any word to the person. It returns SQL that already answers the question,
confirmed by a human; a `trusted` match with an `exampleId` is then run with auto_run_trusted_match. Do
not compose SQL by hand until that path has come up empty. A message WITHOUT the `!cwd` prefix never goes
to find_similar_questions, however much it reads like a question the warehouse could answer. The fuller
rule, including how to lay out the answer, is in the online section below.

Two tiers of tools:

OFFLINE (always available):
- Docs: search_docs, get_doc, get_doc_by_yaml_path, get_doc_by_cli_command, related_docs, list_docs.
  For ANY question about a SQLFlow CLI command, a `.flow.yaml` key, a source type, or a concept,
  search the docs FIRST and cite the page id; do not answer from memory.
- Flow language: validate_flow (parse + census diagnostics), list_flow_keys, describe_flow_key.
  Before returning any `.flow.yaml` you authored or edited, run validate_flow and fix what it reports.
";

const DISCOVERY_STDIO: &str = "\
- Source discovery: discover_source scans a JSON/NDJSON/XML sample (local, UNC, or cloud) and generates a
  runnable `.flow.yaml` stub (mode=flatten) or reports its path structure (mode=paths|discover). It auto-
  detects the record grain, so an envelope or nested-repeater document yields one row per record. Prefer it
  over hand-writing a file-source flow; then validate_flow the result. Needs the `sqlflow` CLI on PATH (or
  SQLFLOW_CLI set).
";

const ONLINE_SETUP_STDIO: &str = "\
ONLINE (needs the control plane; sign in first):
- Setup: get/set_control_plane_url, check_connectivity, login (device flow) then check_auth_status,
  or set_access_token to paste a bearer token. A signed-in session renews itself in the background up to
  the control plane's session limit; check_auth_status reports when a fresh sign-in is needed.
";

const ONLINE_SETUP_HTTP: &str = "\
ONLINE (needs the control plane):
- Auth is per request: every call to this server already carries the caller's bearer token, verified
  with the control plane before any tool runs, and control-plane requests run as that caller with its
  authority enforced server-side. There is no login step; check_connectivity probes reachability.
";

const INSTRUCTIONS_ONLINE_TAIL: &str = concat!(
    "\
- Read: list_repos, list_pipelines, get_pipeline, pipeline_definition, pipeline_columns,
  pipeline_file_stats, list_runs,
  get_run, run_statements/assertions/files/health_metrics, lineage_objects/_detail/_columns/_edges/
  _waves/_dependencies, search_all and search_objects/_columns/_definitions/_flows/_flow_columns/_files/
  _statements, list_schedules, get_schedule, get_schedule_plan, list_nodes, list_repo_sources, summary.
- Every online result carries GUI deep links: each row gains a `links` object holding the page for the row
  itself (`page`, whatever it is: a table's catalog page, a flow, a run, a run group, a schedule's runs, a
  repo, a schema or database folder, a report, the fleet board), its lineage graph (`lineage`), and the
  things it references (`flow`, `object`, `objectLineage`, `otherObject`, `run`, `runGroup`, `schedule`,
  `lastRun`, `sampleRun`, `fromFlow`, `toFlow`). Links that leave SQLFlow arrive under their own names and
  are already absolute: `url` (a report's own address in Power BI/Tableau), `remote` (a repo's git remote),
  `source` (a flow's source location). When an answer names a table, flow, run, schedule, repo, or report,
  link that name with the URL the row carried, and render an external address as a link too rather than as
  bare text or inline code. Use them verbatim: never hand-build a SQLFlow URL, and never invent one for a
  row that came back without links. A result about something the CALL named rather than about its rows (a
  flow's columns, an object's columns, a repo's edges, one file's provenance, the insights boards, summary)
  carries the subject's links on the envelope beside `items`, so those answers have a destination too.
- The command `!cwd <question>` (e.g. \"!cwd what is our revenue by region?\", \"!cwd how many rows landed
  last night\"): this exact prefix, not phrasing, is what activates the path for a question answered from
  warehouse data. It covers any such question, commercial or operational; needing SQL over the warehouse is
  the criterion, not the subject.
  Strip `!cwd` and call find_similar_questions FIRST, before any schema lookup, with the
  remainder as the question. It matches the question against ones this estate's dashboards and confirmed
  answers already answer, expanding the wording so a paraphrase still finds them, and returns the SQL
  that already answers it plus a trustworthy score (`trusted`) rather than an LLM's own confidence.
  Adapting a close match beats composing new SQL from scratch. A `trusted` match carrying an `exampleId`
  MUST be run immediately with auto_run_trusted_match: never prepare_query/run_query for it, never ask the
  person for permission, and never ask which datasource to use (it is worked out from the tables the SQL
  reads). ",
    silent_retrieval_rule!(),
    " Answer ONCE for a business reader, opening with the
  finding in a plain sentence (\"You have 4 customers.\"). ALWAYS close that answer with the SQL
  that actually ran: the result's `sqlIntro` line (\"Here's the saved query I ran:\"), then its `sqlBlock`
  copied exactly as its own fenced code block (the result's `answerFormat` spells this out), so the person can check it, rerun
  it, or confirm it. Never put it inline in a sentence, paraphrase it, or omit it, however obvious the
  query looks. The same goes for every answer backed by run_query.
  Only fall through to search_semantic_layer/describe_semantic_table when nothing
  matches, or every match is untrusted and you need to understand the schema to write a fresh query.
  Never ask them to judge the data source. When a query needs their
  approval, open with \"I don't have a saved answer for this question yet, so I've put together a query that
  should answer it.\" (or, for a question a report already answers, that a report already answers it), say
  what it will show, give the SQL, and ask \"Want me to run it?\" (prepare_query's
  `approvalFormat` spells this out).
  describe_subscriber_report then shows which report VISUAL a matched question came from, with the
  field ROLE (axis vs. value) a flattened column list cannot express. Once a person has judged an answer
  right or in need of a fix, call confirm_question so the next similar question finds it too; call
  it only on an answer a human actually judged, never on your own sense that a query looks correct.
  Approving a run (\"yes\", \"go ahead\", \"run it\") is NOT judging the answer: running and saving are
  separate decisions, so never call confirm_question on a run approval or in the same turn you present a
  result; wait for the person to say, after seeing it, that it is right, needs a fix, or should be
  saved. When you do call it, first say in one line what you will save as the question, quoting it, so they
  can amend the wording; the question is stored as typed and is what later questions match against, so a
  typo is theirs to fix, not yours to correct silently, and amending it leaves the query untouched. A
  message with NO `!cwd` prefix is never routed to find_similar_questions, no matter how much it reads
  like a question the warehouse could answer: treat it as a normal schema/lookup question and work it
  through the semantic layer and the rest of this list instead.
- Writing SQL, or any question about which tables and columns exist and what they mean: use the SEMANTIC
  LAYER, the governed schema holding only the tables and columns an admin has allow-listed, with their
  business descriptions, synonyms, curated keys and joins, measures, and example queries. get_semantic_layer
  first (its general instructions apply to every query you write), then search_semantic_layer with a name or
  a business term, then describe_semantic_table(key) for the columns, key, joins, and measures of one table.
  A query may read only the allowed columns describe_semantic_table lists: anything else is refused when it
  runs. The raw catalog readers below (describe_object, search_objects/_columns/_definitions, get_table_key,
  get_table_joins) see every catalogued column regardless of the allow-list; where a client exposes them,
  use them for lineage and code questions, never to find columns to query.
- \"Where does <name> live / where is <X> computed / what is <X>?\": call search_all FIRST. It fans one term
  across all seven surfaces at once and answers with each surface's full count plus a nextSteps plan naming
  the tool that pages it and the tool that turns a hit into an answer; work that plan rather than guessing a
  single-surface tool. The reason to start here is that the surfaces disagree about what exists: a name absent
  from the synced warehouse schema is routinely present in a flow's YAML (search_flows), in the columns a flow
  produces (search_flow_columns), or only in the SQL a run executed (search_statements). Searching one surface
  and reporting \"no mention in the catalog\" is how a real answer gets missed.
  Search matches word by word, not as a phrase: prefer a single identifier token, and read back the `tokens`
  field to see what was actually searched. When nothing matches, the reply carries an ordered checklist for
  widening the search (uncovered schemas, the flow surfaces, subscribers, the docs corpus); work it before
  answering that the name does not exist, and say which surfaces you checked.
- \"When does <source> update?\": list_schedules finds the schedule (its cron/timezone/next fire), then
  get_schedule_plan returns both the cadence AND the wave-ordered flows that fire runs. A schedule whose
  scope is 'node'/'batch' runs a whole set resolved through lineage, so the plan (not the schedule's own
  flow name) is what actually gets updated; members sharing a wave run concurrently.
- \"Is this delivery normal / how big are this source's files?\": pipeline_file_stats returns the flow's
  size profile (average, median, spread, extremes, totals) over its whole file history plus a window over
  the newest files. Read it before calling a load small, large, or missing: judge a file against the
  median and the standard deviation, and a regime change by recent-vs-all-time.
- Browse the schema: list_schemas gives every (server, database, schema) with its object count; then
  lineage_objects filters by database/schema/kind/name to enumerate the tables and views in one. This is how
  you answer open schema questions without a pre-known object key.
- Ask about an object (text-to-query): describe_object returns one object's identity, columns, generating
  script, module body, and lineage edges in one call: start here to reason about, or author SQL against, a
  specific table or view.
- \"When does <table> update / how is it populated / is it full or incremental / did its last load work?\":
  describe_object_refresh(key) answers all four at once: the writing flows, each with its `loadProfile`
  (`readMode`, `summary`, `keyColumns`, `watermarkColumns`, `replacesTargetEachRun`), its `lastRun` and
  `lastSuccessfulRun`, `runsOnSchedule`, and the schedules that fire it with the next fire time. State the
  load mode from `loadProfile.readMode`, never from the YAML or the key. When the latest run failed, say so
  and give `lastSuccessfulRun` as the last time the table was loaded. For a view it names the modules the
  content derives from instead; read them with describe_object.
- Every fact you state must be a value a tool returned. If no tool returned it, say so and name the tool
  that would; never fill a gap with what a flow of that kind usually does. Executed SQL comes only from
  run_statements; never reconstruct it from a flow settings.
- \"Where does <table>'s data come from / what feeds it / what depends on it?\": object_lineage(key) walks
  the graph transitively, upstream to the true origin (the source system's table, file, or API endpoint) and
  downstream to every dependent, each step naming the flow that carries the hop. Use it whenever the answer
  is more than one hop away; describe_object's edge list stops at the object itself.
- \"Which tables does this dashboard/report use?\": subscribers are the consumption side. list_subscribers
  (filter by type or search by name/owner) finds the report; describe_subscriber(key) lists every object its
  queries read and the SQL itself. The reverse (\"who consumes this table\") is in describe_semantic_table's
  `consumers` (and describe_object's `subscribers` where a client exposes it). Then answer \"and how are those tables populated\" per table with describe_object_refresh.
- \"What is the computation formula for <column>?\": three places compute values, check in this order:
  search_flow_columns (an expression in a flow's transform), the owning view/procedure body via
  describe_object or search_definitions, and search_statements (SQL the engine composed at run time).
- \"It looks like data is missing\": never conclude from metadata silence. Locate the table (search_all),
  walk to its producers (describe_object_refresh), read their recent runs (list_runs, run_files) and compare
  the newest delivery against the flow's own norm (pipeline_file_stats: median, spread, recent window).
  insights_attention lists flows already failing, silent, or delivering zero rows.
- Performance and optimization: insights_recommendations is the one-call briefing (run-history advisories
  merged with warehouse DMV findings, each with ready-to-review SQL); insights_flows ranks flows by
  processing time with trends; insights_attention lists what is failing/degrading/silent; insights_steps
  drills a slow flow to its hot steps and their SQL. When the warehouse dimension is missing or stale, run
  analyze_warehouse_health (missingIndexes / statisticsHealth / indexUsage / topQueries) and re-read.
  Suggested SQL from these tools is for human review, never for unreviewed execution.
- Operate (privileged): trigger_run, cancel_run, analyze_warehouse_health.

SQLFlow authors T-SQL against SQL Server and orchestrates it with `.flow.yaml` documents. It is a
distinct product from DeltaForge; use these tools and the embedded corpus as the source of truth."
);

#[cfg(test)]
mod tests {
    use super::*;

    /// A `/search/all` payload with the given per-surface totals; items are irrelevant to the annotation.
    fn payload(totals: &[(&str, i64)]) -> Value {
        let mut map = serde_json::Map::new();
        map.insert("query".into(), json!("SourceRank"));
        map.insert("tokens".into(), json!(["SourceRank"]));
        for (surface, total) in totals {
            map.insert((*surface).to_string(), json!({ "total": total, "items": [] }));
        }
        Value::Object(map)
    }

    #[test]
    fn annotate_plans_only_the_surfaces_that_matched() {
        // The miss this whole surface exists for: nothing in the warehouse schema, but the term is a column a
        // flow produces. The plan must point at the flow surfaces and stay silent about the empty ones.
        let mut value = payload(&[
            ("objects", 0),
            ("columns", 0),
            ("definitions", 0),
            ("files", 0),
            ("flows", 2),
            ("flowColumns", 1),
            ("statements", 0),
        ]);

        annotate_search_all(&mut value, "SourceRank");

        let steps = value["nextSteps"].as_array().expect("nextSteps");
        let surfaces: Vec<&str> = steps.iter().map(|s| s["surface"].as_str().unwrap()).collect();
        assert_eq!(surfaces, ["flows", "flowColumns"]);
        assert_eq!(steps[0]["total"], json!(2));
        assert_eq!(steps[0]["pageEveryHitWith"], json!("search(surface=\"flows\")"));
        assert_eq!(
            steps[1]["pageEveryHitWith"],
            json!("search_schema(surface=\"flowColumns\")")
        );
        assert!(value["nothingMatched"].is_null());
        assert!(value["readingThis"].is_string());
    }

    #[test]
    fn annotate_returns_the_widening_checklist_when_nothing_matched() {
        let mut value = payload(&[("objects", 0), ("columns", 0), ("flows", 0)]);

        annotate_search_all(&mut value, "ferry passengers");

        let guidance = value["nothingMatched"].as_array().expect("nothingMatched");
        // An empty result has to hand back the next query, not a dead end: every widening direction is named.
        assert!(guidance.len() >= 5);
        let joined = guidance.iter().filter_map(Value::as_str).collect::<Vec<_>>().join(" ");
        assert!(joined.contains("ferry passengers"), "the checklist echoes the query back");
        for tool in ["list_schemas", "flowColumns", "list_subscribers", "search_docs"] {
            assert!(joined.contains(tool), "the checklist names {tool}");
        }
        assert!(value["nextSteps"].is_null());
    }

    #[test]
    fn annotate_treats_a_missing_surface_as_empty_rather_than_panicking() {
        // An older control plane answers without the newest surfaces; the plan must degrade, not fail.
        let mut value = payload(&[("objects", 3)]);

        annotate_search_all(&mut value, "orders");

        let steps = value["nextSteps"].as_array().expect("nextSteps");
        assert_eq!(steps.len(), 1);
        assert_eq!(steps[0]["surface"], json!("objects"));
    }

    #[test]
    fn a_subject_wraps_a_bare_array_and_leaves_an_envelope_alone() {
        let links = json!({ "page": "/pipelines/p-1" });
        let wrapped = with_subject(json!([{ "columnName": "bike_id" }]), ("pipelineId", "p-1"), links.clone());
        assert_eq!(wrapped["pipelineId"], json!("p-1"));
        assert_eq!(wrapped["links"], links);
        assert_eq!(wrapped["items"][0]["columnName"], json!("bike_id"));

        // An object result takes the subject's links on its envelope, without disturbing its own fields.
        let envelope = with_subject(json!({ "fileCount": 12 }), ("pipelineId", "p-1"), links.clone());
        assert_eq!(envelope["links"], links);
        assert_eq!(envelope["fileCount"], json!(12));

        // A row that already resolved its own links keeps them: the rows know better than the caller.
        let own = json!({ "runId": "run-1", "links": { "page": "/runs/run-1" } });
        assert_eq!(with_subject(own.clone(), ("pipelineId", "p-1"), links), own);
    }

    #[test]
    fn a_successful_auto_run_carries_the_sql_as_a_fenced_block() {
        let mut value = json!({
            "ran": true,
            "status": "succeeded",
            "sql": "SELECT COUNT(*) AS CustomerCount FROM demo.Customers",
            "result": { "sql": "SELECT COUNT(*) AS CustomerCount FROM demo.Customers\n", "rows": [["4"]] }
        });
        attach_answer_format(&mut value, &GuiLinks::new(""));
        assert_eq!(
            value["sqlBlock"],
            json!("```sql\nSELECT COUNT(*) AS CustomerCount FROM demo.Customers\n```")
        );
        assert_eq!(value["answerFormat"], json!(ANSWER_FORMAT));
        assert_eq!(value["sqlIntro"], json!("Here's the saved query I ran:"));
        assert!(value.get("saveOffer").is_none(), "a replayed saved answer must not offer to be saved again");
    }

    #[test]
    fn a_succeeded_query_task_carries_the_sql_as_a_fenced_block() {
        let mut value = json!({ "status": "succeeded", "result": { "sql": "SELECT 1 AS One" } });
        attach_answer_format(&mut value, &GuiLinks::new(""));
        assert_eq!(value["sqlBlock"], json!("```sql\nSELECT 1 AS One\n```"));
        assert_eq!(value["sqlIntro"], json!("Here's the query I ran:"));
        assert_eq!(value["saveOffer"], json!(SAVE_OFFER));
    }

    #[test]
    fn a_succeeded_result_links_its_chart_only_when_links_are_absolute() {
        let task = json!({
            "status": "succeeded",
            "taskId": "01a0a5e7-18fa-71c1-a2b3-842b8df8dfd2",
            "result": { "sql": "SELECT 1 AS One" }
        });

        let mut absolute = task.clone();
        attach_answer_format(&mut absolute, &GuiLinks::new("http://localhost:8081"));
        assert_eq!(
            absolute["chartLink"],
            json!("http://localhost:8081/query-results/01a0a5e7-18fa-71c1-a2b3-842b8df8dfd2")
        );

        let mut relative = task;
        attach_answer_format(&mut relative, &GuiLinks::new(""));
        assert!(relative.get("chartLink").is_none());
        assert!(relative.get("sqlBlock").is_some());
    }

    #[test]
    fn expanded_terms_travel_as_repeated_keys_and_blanks_are_dropped() {
        let query = similar_questions_query(SimilarQuestionsInput {
            question: "what drives our turnover".into(),
            expanded_terms: Some(vec![
                "turnover".into(),
                "  revenue ".into(),
                "   ".into(),
                "sales".into(),
            ]),
            top_k: Some(5),
            repo_id: Some("r1".into()),
        });

        assert_eq!(
            query,
            vec![
                ("question", "what drives our turnover".to_string()),
                ("expandedTerms", "turnover".to_string()),
                ("expandedTerms", "revenue".to_string()),
                ("expandedTerms", "sales".to_string()),
                ("topK", "5".to_string()),
                ("repoId", "r1".to_string()),
            ]
        );
    }

    #[test]
    fn without_expanded_terms_only_the_question_is_sent() {
        let query = similar_questions_query(SimilarQuestionsInput {
            question: "revenue by country".into(),
            expanded_terms: None,
            top_k: None,
            repo_id: None,
        });

        assert_eq!(query, vec![("question", "revenue by country".to_string())]);
    }

    #[test]
    fn a_similar_questions_response_carries_the_silent_retrieval_rule_and_an_error_does_not() {
        let mut found = json!({ "matches": [], "question": "how much have customers spent?" });
        attach_retrieval_rule(&mut found);
        assert_eq!(found["responseRule"], json!(RETRIEVAL_RULE));

        let mut error = json!("Error: the control plane is unreachable");
        attach_retrieval_rule(&mut error);
        assert_eq!(error, json!("Error: the control plane is unreachable"));
    }

    /// The silent rule has to reach the model BEFORE it writes its opening line, and a tool description is the
    /// only carrier guaranteed to do that: the server instructions can be truncated by the client, and the
    /// `responseRule` in the payload arrives only after a call has been made. So the rule leads the description
    /// rather than sitting deep inside it, where a model composing a preamble has already passed it by.
    #[test]
    fn tool_description_leads_with_the_cwd_trigger_and_carries_the_silent_rule() {
        let router = SqlFlowMcp::tool_router();
        let tool = router
            .list_all()
            .into_iter()
            .find(|t| t.name == "find_similar_questions")
            .expect("find_similar_questions is a registered tool");
        let description = tool.description.clone().unwrap_or_default();

        // This tool is surfaced to some clients by NAME ALONE, deferred until a tool search loads its
        // schema. The name says nothing about `!cwd`, so the trigger has to be the first thing in the
        // description for the routing to be discoverable at all: an agent that has not yet read this
        // text is exactly the one that needs to know the command routes here.
        assert!(
            description.starts_with("THE `!cwd` COMMAND ROUTES HERE."),
            "find_similar_questions must lead with the `!cwd` trigger, but began:\n{}",
            &description[..description.len().min(240)]
        );

        // rmcp's #[tool] attribute takes only a literal, so this description cannot compose
        // silent_retrieval_rule!(). Asserting it appears verbatim keeps that one copy from drifting.
        assert!(
            description.contains(SILENT_RETRIEVAL_RULE),
            "find_similar_questions must carry SILENT_RETRIEVAL_RULE verbatim"
        );
    }

    /// Every carrier of the rule states the same thing, so a fix to one is a fix to all.
    #[test]
    fn the_response_rule_is_built_from_the_shared_silent_rule() {
        assert!(RETRIEVAL_RULE.starts_with(SILENT_RETRIEVAL_RULE));
        assert!(INSTRUCTIONS_ONLINE_TAIL.contains(SILENT_RETRIEVAL_RULE));
    }

    #[test]
    fn a_prepared_plan_carries_the_approval_format_and_an_error_does_not() {
        let mut prepared = json!({ "planId": "efcca224", "sql": "SELECT 1 AS One" });
        attach_approval_format(&mut prepared);
        assert_eq!(prepared["approvalFormat"], json!(APPROVAL_FORMAT));

        let mut refused = json!({ "error": "Only a single read-only SELECT is allowed." });
        attach_approval_format(&mut refused);
        assert!(refused.get("approvalFormat").is_none());
    }

    #[test]
    fn a_result_that_did_not_run_gets_no_answer_format() {
        let mut not_ran = json!({ "ran": false, "status": "cancelled", "sql": "SELECT 1", "result": null });
        attach_answer_format(&mut not_ran, &GuiLinks::new("http://localhost:8081"));
        assert!(
            not_ran.get("sqlBlock").is_none()
                && not_ran.get("answerFormat").is_none()
                && not_ran.get("sqlIntro").is_none()
        );

        let mut failed = json!({ "status": "failed", "result": { "sql": "SELECT 1" } });
        attach_answer_format(&mut failed, &GuiLinks::new("http://localhost:8081"));
        assert!(failed.get("sqlBlock").is_none());

        let mut error = json!("Error: the control plane is unreachable");
        attach_answer_format(&mut error, &GuiLinks::new("http://localhost:8081"));
        assert_eq!(error, json!("Error: the control plane is unreachable"));
    }

    #[test]
    fn annotate_leaves_a_non_object_payload_alone() {
        let mut value = json!("Error: the control plane is unreachable");
        annotate_search_all(&mut value, "orders");
        assert_eq!(value, json!("Error: the control plane is unreachable"));
    }

    #[test]
    fn search_surfaces_rank_the_warehouse_above_the_reporting_layer() {
        // Order IS priority: the annotated plan is emitted in this order and a caller works it top-down. A bare
        // term is far more often a warehouse object than the name of a report, so `objects` must lead and
        // `subscribers` must trail. This regressed once by appending the consumption surface at the top.
        let order: Vec<&str> = SEARCH_SURFACES.iter().map(|(name, _, _)| *name).collect();
        assert_eq!(order.first(), Some(&"objects"), "the warehouse must be the first surface offered");
        assert_eq!(order.last(), Some(&"subscribers"), "consumption must be the last surface offered");
        // And the consumption surface must say so, so a model reading one entry in isolation still ranks it.
        let (_, _, subscribers_doc) = SEARCH_SURFACES
            .iter()
            .find(|(name, _, _)| *name == "subscribers")
            .expect("subscribers surface");
        assert!(
            subscribers_doc.contains("LAST by priority"),
            "the subscribers surface should state its rank in its own description"
        );
    }

    /// Acronyms and keywords that are simply spelled in capitals; not emphasis, so they never count
    /// against a description's budget.
    const NOT_EMPHASIS: [&str; 33] = [
        "SQL", "YAML", "JSON", "NDJSON", "XML", "DDL", "DML", "CLI", "URL", "API", "HTTP", "MCP", "ID",
        "UTC", "ISO", "GUI", "DMV", "PK", "FK", "DAX", "BI", "PBIX", "CSV", "SFTP", "UNC", "BFS", "PR",
        "NULL", "SELECT", "CREATE", "TABLE", "VIEW", "T",
    ];

    /// Capitals are a routing signal, and a signal only works while it is scarce; see the convention
    /// documented above `GROUNDING_RULE`. This caps the budget per description so the surface cannot drift back to
    /// shouting, which is the state it was in when descriptions opened with START HERE or USE THIS so often
    /// that none of them stood out.
    #[test]
    fn capitals_are_rationed_to_routing_and_safety() {
        let server = SqlFlowMcp::new(Arc::new(DocsIndex::load()), Arc::new(ControlPlane::from_env(true)));

        let mut start_here = Vec::new();
        for tool in server.tool_router.list_all() {
            let description = tool.description.as_deref().unwrap_or("");
            if description.contains("START HERE") {
                start_here.push(tool.name.to_string());
            }

            let emphasised: Vec<&str> = description
                .split(|c: char| !c.is_ascii_alphabetic())
                .filter(|w| w.len() > 1 && w.chars().all(|c| c.is_ascii_uppercase()))
                .filter(|w| !NOT_EMPHASIS.contains(w))
                .collect();

            assert!(
                emphasised.len() <= 6,
                "'{}' capitalises {} words ({}); the convention rations capitals to a routing lead and a \
                 real safety constraint, and carries every other contrast in lowercase prose",
                tool.name,
                emphasised.len(),
                emphasised.join(", "),
            );
        }

        // START HERE marks the entry point of a family. Two tools claiming it is the ambiguity the
        // convention exists to prevent: the model has no way to choose between them.
        assert!(
            start_here.len() <= 1,
            "START HERE must mark at most one entry point, but {start_here:?} all claim it"
        );
    }

    /// A reading guide belongs ON the payload, not in the description: the tools below moved their field
    /// semantics there, and a description that still carried them would be paying for the move twice.
    #[test]
    fn a_reading_guide_lands_on_the_payload_and_not_on_an_error() {
        let mut ok = json!({ "pages": [] });
        attach_guide(&mut ok, SUBSCRIBER_REPORT_GUIDE);
        assert_eq!(ok["readingThis"], json!(SUBSCRIBER_REPORT_GUIDE));

        // An error payload keeps its own shape: guidance about fields it does not have is noise on a failure.
        let mut failed = json!({ "error": "not found" });
        attach_guide(&mut failed, SEMANTIC_TABLE_GUIDE);
        assert!(failed["readingThis"].is_null());

        // And the descriptions that moved their semantics out must not still carry them.
        let server = SqlFlowMcp::new(Arc::new(DocsIndex::load()), Arc::new(ControlPlane::from_env(true)));
        for (tool, moved) in [
            ("describe_semantic_table", "isRangeJoin"),
            ("describe_subscriber_report", "tableName"),
            ("detect_stream_anomalies", "agreeingDetectors"),
        ] {
            let served = ServerHandler::get_tool(&server, tool).expect("tool is served");
            let description = served.description.as_deref().unwrap_or("");
            assert!(
                !description.contains(moved),
                "'{tool}' still explains `{moved}` in its description; that moved to the response guide"
            );
            assert!(
                description.contains("readingThis"),
                "'{tool}' should point at the response's own `readingThis` guide"
            );
        }
    }

    #[test]
    fn every_search_surface_names_a_real_paging_tool() {
        // The plan is only useful if the tool names it hands back are callable; a renamed tool must be
        // caught here rather than by a model trying to call a tool that does not exist. Read off the
        // CONSTRUCTED server rather than a hand-copied list, so this cannot go stale the way a duplicate
        // roster does. The subscribers surface is paged by list_subscribers rather than by `search`: that
        // tool already filters by the same fields, so a second path to the same rows would be a parallel one.
        let server = SqlFlowMcp::new(Arc::new(DocsIndex::load()), Arc::new(ControlPlane::from_env(true)));
        let shipped: Vec<String> =
            server.tool_router.list_all().into_iter().map(|t| t.name.to_string()).collect();

        for (surface, page_tool, follow_up) in SEARCH_SURFACES {
            // Either a bare tool name, or the `tool(surface="x")` form naming the surface it pages.
            let (name, paged) = match page_tool.split_once("(surface=") {
                Some((name, rest)) => (name, Some(rest.trim_end_matches(')').trim_matches('"'))),
                None => (page_tool, None),
            };
            assert!(shipped.iter().any(|t| t == name), "{name} is not a tool on this server");

            if let Some(paged) = paged {
                assert_eq!(paged, surface, "the plan pages the wrong surface for {surface}");
                // The split is a privacy boundary, not a category: the schema surfaces must be served by
                // search_schema, which the chat surfaces exclude, and never leak onto the ETL-side `search`.
                let expected =
                    if schema_surface_spec(paged).is_some() { "search_schema" } else { "search" };
                assert_eq!(name, expected, "{paged} is paged by the wrong tool");
                assert!(
                    search_surface_spec(paged).is_some() || schema_surface_spec(paged).is_some(),
                    "{paged} is not a surface either search tool can page"
                );
            }
            assert!(!follow_up.is_empty());
        }
    }
}
