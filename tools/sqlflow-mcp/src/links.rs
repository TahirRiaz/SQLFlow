//! GUI deep links attached to online tool results.
//!
//! Answers get read by people, and "arc.Citybike_Bikes is stale" is worth more when the table name
//! can be clicked through to its catalog page and its lineage graph. The control plane's JSON carries
//! identities (a global object key, a pipeline id, a run id) but, with two exceptions, no URLs, so this
//! module maps those identities onto the GUI's routes and decorates every read result with a `links`
//! object per row. The exceptions are the addresses that leave SQLFlow (a report's own URL in Power BI,
//! a repo's git remote): those are carried through verbatim, and only when they really are http(s).
//! The routes mirror the GUI router (`gui/src/App.tsx`), the lineage graph's `?focus=`/`?project=` deep
//! links, the runs board's `?scheduleId=` filter, and the catalog tree's node-id contract
//! (`gui/src/features/catalog/nodeIds.ts`).
//!
//! Rows are recognised by the identity fields they carry, not by which tool returned them, so a shape
//! that appears inside a dossier, a search hit, or a lineage walk is linked the same way everywhere,
//! and a tool added later is linked without being wired up. The flip side is that a rule has to be tight
//! enough not to fire on a lookalike: a repo source is not a repo, and a subscriber is not a table, so
//! each rule below names the field that separates it from its neighbours.
//!
//! `page` is the row's own destination and every rule competes for it in one order: the row's subject
//! wins, and anything the row merely references lands under its own name (`flow`, `object`, `run`, ...).
//!
//! `SQLFLOW_GUI_URL` makes the links absolute, which is what a client rendering outside the GUI (the
//! Slack assistant, a desktop MCP client) needs. Unset, they stay root-relative: correct for the chat
//! assistant, which renders inside the GUI itself.

use serde_json::{Map, Value};

/// How deep the decorator walks before it stops. Every catalog payload nests far shallower than this;
/// the ceiling only exists so a pathological document cannot recurse without bound.
const MAX_DEPTH: usize = 16;

/// The catalog tree's sentinel for a null database/schema/container segment (`nodeIds.ts`).
const NULL_SEGMENT: &str = "~";

/// Percent-encodes one URL component, escaping everything outside RFC 3986's unreserved set. Object
/// keys and file paths carry `/`, `:`, spaces and (in this estate) Norwegian letters, all of which
/// have to survive the round trip into a query value.
fn encode(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for byte in value.as_bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' => {
                out.push(*byte as char)
            }
            other => out.push_str(&format!("%{other:02X}")),
        }
    }
    out
}

/// One segment of a catalog-tree node id: an absent database/schema encodes as the tree's sentinel.
fn segment(value: Option<&str>) -> String {
    match value {
        Some(text) => encode(text),
        None => NULL_SEGMENT.to_string(),
    }
}

/// Whether a free-text location field is actually a web address. A subscriber's `url` is documented as
/// free text (a Power BI link, but equally a workbook path or a file share), so only http(s) is a link.
fn is_web_url(value: &str) -> bool {
    let lowered = value.trim_start().to_ascii_lowercase();
    lowered.starts_with("http://") || lowered.starts_with("https://")
}

/// Whether an id is a GUID. The catalog's own entity ids are GUIDs and its row ids are JSON numbers, so
/// this only separates a stable entity id from an opaque string id on a row that carries no other clue.
fn is_guid(value: &str) -> bool {
    value.len() == 36
        && value
            .as_bytes()
            .iter()
            .enumerate()
            .all(|(index, byte)| match index {
                8 | 13 | 18 | 23 => *byte == b'-',
                _ => byte.is_ascii_hexdigit(),
            })
}

/// The GUI's public base URL (empty for root-relative links) and the routes built from it.
#[derive(Clone, Debug, Default)]
pub struct GuiLinks {
    base: String,
}

impl GuiLinks {
    /// Reads `SQLFLOW_GUI_URL`; absent or blank leaves links root-relative.
    pub fn from_env() -> Self {
        Self::new(&std::env::var("SQLFLOW_GUI_URL").unwrap_or_default())
    }

    pub fn new(base: &str) -> Self {
        GuiLinks {
            base: base.trim().trim_end_matches('/').to_string(),
        }
    }

    /// The configured base, or "" when links are relative.
    pub fn base(&self) -> &str {
        &self.base
    }

    fn at(&self, path_and_query: &str) -> String {
        format!("{}{}", self.base, path_and_query)
    }

    /// The catalog explorer opened on one tree node. The node id's own segments are already encoded
    /// (`nodeIds.ts` builds it that way); encoding the composed id again is what makes the query value
    /// decode back to exactly that id, which the tree then decodes to its parts.
    fn catalog_node(&self, node_id: &str) -> String {
        self.at(&format!("/catalog?node={}", encode(node_id)))
    }

    /// A warehouse object's catalog page: its definition, columns, key, and consumers.
    pub fn object(&self, key: &str) -> String {
        self.catalog_node(&format!("obj:{}", encode(key)))
    }

    /// A database folder in the catalog tree.
    pub fn database(&self, database: Option<&str>) -> String {
        self.catalog_node(&format!("db:{}", segment(database)))
    }

    /// A schema folder in the catalog tree.
    pub fn schema(&self, database: Option<&str>, schema: Option<&str>) -> String {
        self.catalog_node(&format!("sch:{}|{}", segment(database), segment(schema)))
    }

    /// A kind folder (the Tables / Views / Procedures grouping) under one schema.
    pub fn schema_kind(&self, database: Option<&str>, schema: Option<&str>, kind: &str) -> String {
        self.catalog_node(&format!(
            "knd:{}|{}|{}",
            segment(database),
            segment(schema),
            encode(kind)
        ))
    }

    /// The lineage graph focused on one node. An object key needs no repo (the walk is cross-repo).
    pub fn object_lineage(&self, key: &str) -> String {
        self.at(&format!("/lineage?focus={}", encode(key)))
    }

    pub fn pipeline(&self, id: &str) -> String {
        self.at(&format!("/pipelines/{}", encode(id)))
    }

    /// The lineage graph focused on a flow. Its repo scopes the graph, which is what makes the node's
    /// Run action available once the user is there.
    pub fn pipeline_lineage(&self, repo_id: &str, id: &str) -> String {
        self.at(&format!("/lineage?repoId={}&focus={}", encode(repo_id), encode(id)))
    }

    /// The lineage graph scoped to one project (a repo-root folder), which is how the graph is picked.
    pub fn project_lineage(&self, repo_id: &str, project: &str) -> String {
        self.at(&format!("/lineage?repoId={}&project={}", encode(repo_id), encode(project)))
    }

    pub fn run(&self, id: &str) -> String {
        self.at(&format!("/runs/{}", encode(id)))
    }

    pub fn run_group(&self, id: &str) -> String {
        self.at(&format!("/runs/groups/{}", encode(id)))
    }

    pub fn repo(&self, id: &str) -> String {
        self.at(&format!("/repos/{}", encode(id)))
    }

    /// The repos board, which is also where the managed git sources are administered. A repo SOURCE has
    /// its own id that is NOT the synced repo's id (they are joined by name), so a source row can only
    /// be linked to the board, never to /repos/{its own id}.
    pub fn repos(&self) -> String {
        self.at("/repos")
    }

    /// The schema-history board. A change is dated to the snapshot that saw it, so a deep link always widens
    /// the window to all time (the board defaults to 24 hours, which would hide the very row that linked
    /// here); `q` narrows its tree to one object or database.
    pub fn schema_changes(&self, term: Option<&str>) -> String {
        match term {
            Some(term) => self.at(&format!("/schema-changes?q={}&window=all", encode(term))),
            None => self.at("/schema-changes?window=all"),
        }
    }

    /// The insights board: the estate's run-history advisories and warehouse recommendations.
    pub fn insights(&self) -> String {
        self.at("/insights")
    }

    /// The data-stream board: which tables have stopped receiving data, and which load abnormally.
    pub fn datastreams(&self) -> String {
        self.at("/datastreams")
    }

    /// The dashboard, which is what the summary rollup is the data for.
    pub fn dashboard(&self) -> String {
        self.at("/")
    }

    /// The fleet board. Workers and pools have no per-row route, so a fleet row links to the board.
    pub fn nodes(&self) -> String {
        self.at("/nodes")
    }

    pub fn subscriber(&self, key: &str) -> String {
        self.at(&format!("/subscribers?key={}", encode(key)))
    }

    /// A schedule has no page of its own; its runs board, filtered to it, is where an operator looks.
    pub fn schedule_runs(&self, id: &str) -> String {
        self.at(&format!("/runs?scheduleId={}", encode(id)))
    }

    /// Adds a `links` object to every row in a control-plane payload that carries an identity we can
    /// resolve to a GUI route. Rows without one are left exactly as they arrived.
    pub fn decorate(&self, value: &mut Value) {
        self.walk(value, 0);
    }

    fn walk(&self, value: &mut Value, depth: usize) {
        if depth >= MAX_DEPTH {
            return;
        }
        match value {
            Value::Array(items) => {
                for item in items.iter_mut() {
                    self.walk(item, depth + 1);
                }
            }
            Value::Object(map) => {
                for (_, nested) in map.iter_mut() {
                    self.walk(nested, depth + 1);
                }
                if let Some(links) = self.links_for(map) {
                    map.insert("links".to_string(), links);
                }
            }
            _ => {}
        }
    }

    /// The links for one row, from the identity fields it carries. Returns None when it carries none.
    fn links_for(&self, map: &Map<String, Value>) -> Option<Value> {
        // A row that already carries links (a re-decorated payload) keeps the ones it has.
        if map.contains_key("links") {
            return None;
        }

        let mut links = Map::new();

        // ---- The row's own subject, by the identity it is keyed on ----------------------------
        match (text(map, "key"), text(map, "kind"), text(map, "type")) {
            // A data subscriber (a report or dashboard): `type` is the tool that consumes the estate.
            // Every subscriber shape in the API says `type`, which is what separates it from an object
            // row (`kind`); its own address is picked up by the external-URL rule at the end.
            (Some(key), None, Some(_)) => {
                links.insert("page".to_string(), self.subscriber(key).into());
            }
            // A file endpoint in the source tree: keyed like an object but classified by origin system
            // (a storage account, an SFTP host) rather than by a database kind.
            (Some(key), None, None) if map.contains_key("originKind") => {
                links.insert("page".to_string(), self.object(key).into());
                links.insert("lineage".to_string(), self.object_lineage(key).into());
            }
            // A lineage object: the global key plus the kind that says what it is. A node script carries
            // the same pair for a flow, where the key IS the pipeline id, so it links as the flow it is.
            (Some(key), Some(kind), _) => {
                if kind.eq_ignore_ascii_case("pipeline") || kind.eq_ignore_ascii_case("flow") {
                    links.insert("page".to_string(), self.pipeline(key).into());
                } else if kind.eq_ignore_ascii_case("subscriber") {
                    // The lineage graph draws a subscriber as a node beside the objects it reads, so it
                    // arrives here keyed like one. It has no catalog page; its consumption page is keyed
                    // by exactly this key.
                    links.insert("page".to_string(), self.subscriber(key).into());
                } else {
                    links.insert("page".to_string(), self.object(key).into());
                }
                links.insert("lineage".to_string(), self.object_lineage(key).into());
            }
            _ => {}
        }

        // A counted grouping in the catalog tree rather than an object: a schema, or one kind folder
        // within it. `objectCount` beside a `serverRef` is what makes it a folder and not a row.
        if !links.contains_key("page") && map.contains_key("objectCount") && map.contains_key("serverRef") {
            let database = text(map, "database");
            let schema = text(map, "schema");
            let node = match text(map, "kind") {
                Some(kind) => self.schema_kind(database, schema, kind),
                None => self.schema(database, schema),
            };
            links.insert("page".to_string(), node.into());
        }

        // The schema-history rollup for one tracked database: its own board, filtered to that database, with
        // the catalog's database folder beside it for the objects themselves.
        if !links.contains_key("page") && map.contains_key("lastChangeUtc") {
            if let Some(database) = text(map, "database") {
                links.insert("page".to_string(), self.schema_changes(Some(database)).into());
                links.insert("catalog".to_string(), self.database(Some(database)).into());
            }
        }

        // One recorded schema change. Its object has no catalog key here (the feed records the database, not
        // the server reference that keys the catalog), so the board's own tree, filtered to the object's name,
        // is where the change is read. Claimed before the run rule below, so the run that saw it stays a
        // reference rather than becoming the row's page.
        if !links.contains_key("page") && map.contains_key("changeType") {
            if let Some(name) = text(map, "name") {
                links.insert("page".to_string(), self.schema_changes(Some(name)).into());
            }
        }

        if let Some(id) = text(map, "id") {
            if let Some(repo_id) = text(map, "repoId") {
                if map.contains_key("name") && map.contains_key("kind") {
                    // A pipeline (flow) row.
                    links.insert("page".to_string(), self.pipeline(id).into());
                    links.insert("lineage".to_string(), self.pipeline_lineage(repo_id, id).into());
                } else if map.contains_key("timezone") && map.contains_key("enabled") {
                    // A schedule row.
                    links.insert("page".to_string(), self.schedule_runs(id).into());
                }
            } else if map.contains_key("firstSeenUtc") && map.contains_key("lastSyncUtc") {
                // A repository row.
                links.insert("page".to_string(), self.repo(id).into());
            } else if map.contains_key("syncIntervalSeconds") {
                // A managed git source. Its id is the source's, not the synced repo's, so the board is
                // as deep as this can link.
                links.insert("page".to_string(), self.repos().into());
            } else if map.contains_key("memberCount") {
                // One link of a schedule chain: the schedule it will fire.
                links.insert("page".to_string(), self.schedule_runs(id).into());
            } else if is_guid(id) && map.contains_key("name") && map.contains_key("kind") {
                // A pipeline named inside a repo-scoped payload (an execution wave), where the repo is
                // the request's parameter rather than a field, so only the flow page is reachable.
                links.insert("page".to_string(), self.pipeline(id).into());
            }
        }

        // A run: `runId` is its identity in every shape that carries it, so it becomes the row's page
        // unless the row is already something else (a flow that reports its latest run, say).
        if let Some(run_id) = text(map, "runId") {
            let slot = if links.contains_key("page") { "run" } else { "page" };
            links.insert(slot.to_string(), self.run(run_id).into());
        }

        // A schedule referenced by its own id field (a plan, a definition, a producer's cadence).
        if let Some(schedule_id) = text(map, "scheduleId") {
            let slot = if links.contains_key("page") { "schedule" } else { "page" };
            links.insert(slot.to_string(), self.schedule_runs(schedule_id).into());
        }

        // A run group: the header of a multi-flow fire, or a reference to one from a member run.
        if let Some(group_id) = text(map, "groupId") {
            let slot = if links.contains_key("page") { "runGroup" } else { "page" };
            links.insert(slot.to_string(), self.run_group(group_id).into());
        }

        // References to other entities, on rows whose own identity is something else: a lineage step,
        // an edge, a column hit, a file receipt, a schedule's last outcome.
        if let Some(pipeline_id) = text(map, "pipelineId") {
            if !links.contains_key("lineage") {
                // A row ABOUT a flow (it names one, and carries no other subject) makes the flow its
                // page; a row that merely references one keeps it under `flow`.
                let names_a_flow = map.contains_key("flowName")
                    || map.contains_key("flow")
                    || map.contains_key("pipelineName");
                let slot = if names_a_flow && !links.contains_key("page") && !map.contains_key("objectKey") {
                    "page"
                } else {
                    "flow"
                };
                links.insert(slot.to_string(), self.pipeline(pipeline_id).into());
            }
        }
        if let Some(from_pipeline_id) = text(map, "fromPipelineId") {
            links.insert("fromFlow".to_string(), self.pipeline(from_pipeline_id).into());
        }
        if let Some(to_pipeline_id) = text(map, "toPipelineId") {
            links.insert("toFlow".to_string(), self.pipeline(to_pipeline_id).into());
        }
        if let Some(object_key) = text(map, "objectKey") {
            links.insert("object".to_string(), self.object(object_key).into());
            links.insert("objectLineage".to_string(), self.object_lineage(object_key).into());
        }
        if let Some(other_key) = text(map, "otherObjectKey") {
            links.insert("otherObject".to_string(), self.object(other_key).into());
        }
        if let Some(last_run_id) = text(map, "lastRunId") {
            links.insert("lastRun".to_string(), self.run(last_run_id).into());
        }
        if let Some(last_group_id) = text(map, "lastGroupId") {
            links.insert("lastRunGroup".to_string(), self.run_group(last_group_id).into());
        }
        if let Some(sample_run_id) = text(map, "sampleRunId") {
            links.insert("sampleRun".to_string(), self.run(sample_run_id).into());
        }

        // A row whose subject is a repo, or a project (a repo-root folder) within one: reached only
        // when nothing above claimed the page, so a flow or run that merely travels with its repo name
        // is unaffected.
        if !links.contains_key("page") {
            if let (Some(repo_id), true) = (text(map, "repoId"), map.contains_key("repoName")) {
                let page = match text(map, "project") {
                    Some(project) => self.project_lineage(repo_id, project),
                    None => self.repo(repo_id),
                };
                links.insert("page".to_string(), page.into());
            }
        }

        // A grouping that belongs to a repo and nothing more specific (a flow batch): the repo's page lists
        // what it holds. Rows whose subject is elsewhere (an edge's object, a run's flow) are excluded, so
        // this only fires where the alternative is no link at all.
        if !links.contains_key("page")
            && !map.contains_key("objectKey")
            && !map.contains_key("pipelineId")
        {
            if let Some(repo_id) = text(map, "repoId") {
                links.insert("page".to_string(), self.repo(repo_id).into());
            }
        }

        // A worker or a worker pool. Neither has a per-row route, so both link the fleet board.
        if !links.contains_key("page")
            && (map.contains_key("minReplicas")
                || (map.contains_key("online") && map.contains_key("lastSeenUtc")))
        {
            links.insert("page".to_string(), self.nodes().into());
        }

        // Addresses that leave SQLFlow, carried through verbatim rather than rebased on the GUI. Each is
        // free text in the catalog (a report can be a workbook path, a remote can be an SSH remote), so
        // only a real web address becomes a link.
        for (field, name) in [("url", "url"), ("remoteUrl", "remote"), ("sourceLocation", "source")] {
            if let Some(value) = text(map, field) {
                if is_web_url(value) {
                    links.insert(name.to_string(), value.into());
                }
            }
        }

        if links.is_empty() {
            None
        } else {
            Some(Value::Object(links))
        }
    }
}

/// A non-empty string field, or None.
fn text<'a>(map: &'a Map<String, Value>, key: &str) -> Option<&'a str> {
    map.get(key)
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn links_of(value: &Value, pointer: &str) -> Value {
        value.pointer(pointer).cloned().unwrap_or(Value::Null)
    }

    #[test]
    fn object_rows_link_to_the_catalog_and_the_graph() {
        let links = GuiLinks::new("https://sqlflow.example.com/");
        let mut payload = json!([{ "key": "dw.arc.citybike_bikes", "kind": "Table", "serverRef": "dw" }]);
        links.decorate(&mut payload);

        assert_eq!(
            links_of(&payload, "/0/links/page"),
            json!("https://sqlflow.example.com/catalog?node=obj%3Adw.arc.citybike_bikes")
        );
        assert_eq!(
            links_of(&payload, "/0/links/lineage"),
            json!("https://sqlflow.example.com/lineage?focus=dw.arc.citybike_bikes")
        );
    }

    #[test]
    fn keys_needing_escapes_survive_both_encodings() {
        let links = GuiLinks::new("");
        // The catalog page decodes the query value once, then the node id's key segment once more.
        assert_eq!(links.object("dw.arc.Målt Verdi"), "/catalog?node=obj%3Adw.arc.M%25C3%25A5lt%2520Verdi");
        assert_eq!(links.object_lineage("dw.arc.Målt Verdi"), "/lineage?focus=dw.arc.M%C3%A5lt%20Verdi");
    }

    #[test]
    fn pipeline_rows_link_to_the_flow_and_its_graph() {
        let links = GuiLinks::new("");
        let mut payload = json!({
            "items": [{ "id": "p-1", "repoId": "r-1", "name": "citybike_00_api", "kind": "api" }],
            "page": 1, "pageSize": 50, "total": 1
        });
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/items/0/links/page"), json!("/pipelines/p-1"));
        assert_eq!(links_of(&payload, "/items/0/links/lineage"), json!("/lineage?repoId=r-1&focus=p-1"));
        // The page envelope itself carries no identity, so it is untouched.
        assert_eq!(links_of(&payload, "/links"), Value::Null);
    }

    #[test]
    fn a_wave_member_links_to_its_flow_without_a_repo() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "wave": 1,
            "pipelines": [{ "id": "8f14e45f-ceea-467a-9575-0b1a2b3c4d5e", "name": "citybike_00_api", "kind": "api" }]
        }]);
        links.decorate(&mut payload);

        assert_eq!(
            links_of(&payload, "/0/pipelines/0/links/page"),
            json!("/pipelines/8f14e45f-ceea-467a-9575-0b1a2b3c4d5e")
        );
        // The wave itself is a grouping, not an entity: nothing to open.
        assert_eq!(links_of(&payload, "/0/links"), Value::Null);
    }

    #[test]
    fn a_run_row_links_to_the_run_its_flow_and_its_group() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "runId": "run-1", "pipelineId": "p-1", "repoId": "r-1", "groupId": "g-1",
            "flowName": "citybike_bikes_01_jsn", "status": "Succeeded"
        }]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/page"), json!("/runs/run-1"));
        assert_eq!(links_of(&payload, "/0/links/flow"), json!("/pipelines/p-1"));
        assert_eq!(links_of(&payload, "/0/links/runGroup"), json!("/runs/groups/g-1"));
    }

    #[test]
    fn a_run_group_header_opens_the_group_itself() {
        let links = GuiLinks::new("");
        let mut payload = json!({ "groupId": "g-1", "repoId": "r-1", "mode": "Batch", "memberCount": 12 });
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/links/page"), json!("/runs/groups/g-1"));
    }

    #[test]
    fn a_triggered_run_is_linked_from_its_acknowledgement() {
        let links = GuiLinks::new("");
        let mut payload = json!({ "runId": "run-9", "status": "Queued" });
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/links/page"), json!("/runs/run-9"));
    }

    #[test]
    fn a_flow_subject_row_opens_the_flow_rather_than_only_referencing_it() {
        let links = GuiLinks::new("");
        // An insights row and a flow-column hit are both ABOUT a flow.
        let mut payload = json!([
            { "pipelineId": "p-1", "flowName": "citybike_00_api", "runs": 30, "failures": 2 },
            { "pipelineId": "p-2", "flowName": "citybike_bikes_02_ing", "repoId": "r-1", "kind": "declared",
              "columnName": "bike_id" }
        ]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/page"), json!("/pipelines/p-1"));
        assert_eq!(links_of(&payload, "/1/links/page"), json!("/pipelines/p-2"));
    }

    #[test]
    fn a_lineage_step_keeps_the_object_as_its_subject() {
        let links = GuiLinks::new("");
        let mut payload = json!({
            "key": "dw.dwh.fact_trips", "kind": "Table",
            "upstream": [{ "depth": 1, "objectKey": "dw.arc.citybike_trips", "pipelineId": "p-2", "flowName": "f" }]
        });
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/links/page"), json!("/catalog?node=obj%3Adw.dwh.fact_trips"));
        assert_eq!(
            links_of(&payload, "/upstream/0/links/objectLineage"),
            json!("/lineage?focus=dw.arc.citybike_trips")
        );
        // The step is about the object it reached, so the flow that wrote it stays a reference.
        assert_eq!(links_of(&payload, "/upstream/0/links/flow"), json!("/pipelines/p-2"));
        assert_eq!(links_of(&payload, "/upstream/0/links/page"), Value::Null);
    }

    #[test]
    fn a_subscriber_gains_a_page_and_a_link_to_the_report_itself() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "key": "powerbi:analyse_bysykkel", "name": "Analyse_Bysykkel_statistikk",
            "type": "PowerBI", "url": "https://app.powerbi.com/report"
        }]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/url"), json!("https://app.powerbi.com/report"));
        assert_eq!(
            links_of(&payload, "/0/links/page"),
            json!("/subscribers?key=powerbi%3Aanalyse_bysykkel")
        );
        // External, so it is carried through verbatim rather than rebased on the GUI.
        assert_eq!(links_of(&payload, "/0/links/url"), json!("https://app.powerbi.com/report"));
    }

    #[test]
    fn a_subscriber_drawn_as_a_graph_node_still_opens_its_own_page() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "key": "subscriber|||analyse_sanntid", "name": "Analyse_Sanntid", "kind": "subscriber",
            "location": null, "frontier": true
        }]);
        links.decorate(&mut payload);

        assert_eq!(
            links_of(&payload, "/0/links/page"),
            json!("/subscribers?key=subscriber%7C%7C%7Canalyse_sanntid")
        );
        // The graph does accept the subscriber's key as a focus, so that link stays.
        assert_eq!(
            links_of(&payload, "/0/links/lineage"),
            json!("/lineage?focus=subscriber%7C%7C%7Canalyse_sanntid")
        );
    }

    #[test]
    fn a_subscriber_whose_location_is_a_file_path_gets_no_url_link() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "key": "excel:budsjett", "name": "Budsjett", "type": "Excel",
            "url": "\\\\fileserver\\rapporter\\budsjett.xlsx"
        }]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/page"), json!("/subscribers?key=excel%3Abudsjett"));
        assert_eq!(links_of(&payload, "/0/links/url"), Value::Null);
    }

    #[test]
    fn a_schema_and_a_kind_folder_open_the_catalog_tree() {
        let links = GuiLinks::new("");
        let mut payload = json!([
            { "serverRef": "dw", "database": "dwh", "schema": "arc", "objectCount": 412 },
            { "serverRef": "dw", "database": "dwh", "schema": "arc", "kind": "Table", "objectCount": 380 },
            { "serverRef": "dw", "database": null, "schema": null, "objectCount": 3 }
        ]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/page"), json!("/catalog?node=sch%3Adwh%7Carc"));
        assert_eq!(links_of(&payload, "/1/links/page"), json!("/catalog?node=knd%3Adwh%7Carc%7CTable"));
        // A partially-resolved schema keeps the tree's null sentinel, so the deep link still resolves.
        assert_eq!(links_of(&payload, "/2/links/page"), json!("/catalog?node=sch%3A~%7C~"));
    }

    #[test]
    fn a_file_source_links_to_its_catalog_node_and_graph() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "key": "dwdatalakeprodv2/raw/citybike/history/bikes/2026-08-25.json",
            "originKind": "AzureStorage", "origin": "dwdatalakeprodv2", "container": "raw",
            "path": "citybike/history/bikes", "name": "2026-08-25.json"
        }]);
        links.decorate(&mut payload);

        assert_eq!(
            links_of(&payload, "/0/links/page"),
            json!("/catalog?node=obj%3Adwdatalakeprodv2%252Fraw%252Fcitybike%252Fhistory%252Fbikes%252F2026-08-25.json")
        );
        assert_ne!(links_of(&payload, "/0/links/lineage"), Value::Null);
    }

    #[test]
    fn a_repo_links_to_its_page_but_a_managed_source_links_to_the_board() {
        let links = GuiLinks::new("");
        // A repo source is joined to its repo by NAME, so its own id is not a repo id.
        let mut payload = json!([
            { "id": "r-1", "name": "dwh-pipelines-prod", "remoteUrl": "https://bitbucket.org/kolumbus/dwh.git",
              "rootPath": null, "firstSeenUtc": "2026-01-01T00:00:00Z", "lastSyncUtc": "2026-08-25T04:00:00Z" },
            { "id": "s-1", "name": "dwh-pipelines-prod", "remoteUrl": "https://bitbucket.org/kolumbus/dwh.git",
              "branch": "main", "enabled": true, "syncIntervalSeconds": 300,
              "lastSyncUtc": "2026-08-25T04:00:00Z" }
        ]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/page"), json!("/repos/r-1"));
        assert_eq!(links_of(&payload, "/1/links/page"), json!("/repos"));
        // The git remote is a real web address on both, so both link it.
        assert_eq!(links_of(&payload, "/0/links/remote"), json!("https://bitbucket.org/kolumbus/dwh.git"));
        assert_eq!(links_of(&payload, "/1/links/remote"), json!("https://bitbucket.org/kolumbus/dwh.git"));
    }

    #[test]
    fn an_ssh_remote_is_not_linked() {
        let links = GuiLinks::new("");
        let mut payload = json!({
            "id": "s-2", "name": "internal", "remoteUrl": "git@bitbucket.org:kolumbus/dwh.git",
            "branch": "main", "syncIntervalSeconds": 300
        });
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/links/remote"), Value::Null);
        assert_eq!(links_of(&payload, "/links/page"), json!("/repos"));
    }

    #[test]
    fn a_schedule_is_reachable_by_either_id_field() {
        let links = GuiLinks::new("");
        let mut payload = json!([
            { "id": "sch-1", "repoId": "r-1", "name": "citybike_daily", "timezone": "Europe/Oslo",
              "enabled": true, "lastRunId": "run-3", "lastGroupId": "g-2" },
            { "scheduleId": "sch-1", "repoId": "r-1", "name": "citybike_daily", "timezone": "Europe/Oslo",
              "enabled": true, "anchor": "citybike_00_api", "memberCount": 7 }
        ]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/page"), json!("/runs?scheduleId=sch-1"));
        assert_eq!(links_of(&payload, "/0/links/lastRun"), json!("/runs/run-3"));
        assert_eq!(links_of(&payload, "/0/links/lastRunGroup"), json!("/runs/groups/g-2"));
        assert_eq!(links_of(&payload, "/1/links/page"), json!("/runs?scheduleId=sch-1"));
    }

    #[test]
    fn a_flow_dependency_links_both_of_its_ends() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "id": 42, "repoId": "r-1", "fromFlow": "citybike_00_api", "toFlow": "citybike_bikes_01_jsn",
            "fromPipelineId": "p-1", "toPipelineId": "p-2", "viaObjects": "raw/citybike"
        }]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/fromFlow"), json!("/pipelines/p-1"));
        assert_eq!(links_of(&payload, "/0/links/toFlow"), json!("/pipelines/p-2"));
    }

    #[test]
    fn a_project_opens_the_graph_scoped_to_it_and_a_repo_reference_opens_the_repo() {
        let links = GuiLinks::new("");
        let mut payload = json!([
            { "repoId": "r-1", "repoName": "dwh-pipelines-prod", "project": "Citybike", "flowCount": 9 },
            { "repoId": "r-1", "repoName": "dwh-pipelines-prod", "edgeCount": 14, "writes": true }
        ]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/page"), json!("/lineage?repoId=r-1&project=Citybike"));
        assert_eq!(links_of(&payload, "/1/links/page"), json!("/repos/r-1"));
    }

    #[test]
    fn a_flow_batch_falls_back_to_the_repo_that_holds_it() {
        let links = GuiLinks::new("");
        // A batch has no page of its own, so the repo listing its flows is the closest real destination.
        let mut payload = json!([{ "repoId": "r-1", "batch": "bikes", "flowCount": 3, "activeCount": 3 }]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/page"), json!("/repos/r-1"));
    }

    #[test]
    fn an_edge_links_both_of_its_ends_and_claims_no_page_of_its_own() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "id": 7, "repoId": "r-1", "flow": "citybike_00_api", "pipelineId": "p-1", "relation": "Writes",
            "objectKey": "dw.arc.citybike_bikes", "objectName": "Citybike_Bikes", "tier": "Declared"
        }]);
        links.decorate(&mut payload);

        // An edge is a relation between two things, not a thing: it links both ends and claims no page,
        // and the repo fallback stays out of the way.
        assert_eq!(links_of(&payload, "/0/links/page"), Value::Null);
        assert_eq!(links_of(&payload, "/0/links/flow"), json!("/pipelines/p-1"));
        assert_eq!(links_of(&payload, "/0/links/object"), json!("/catalog?node=obj%3Adw.arc.citybike_bikes"));
    }

    #[test]
    fn fleet_rows_link_the_nodes_board() {
        let links = GuiLinks::new("");
        let mut payload = json!([
            { "name": "worker-3", "online": true, "firstSeenUtc": "2026-08-01T00:00:00Z",
              "lastSeenUtc": "2026-08-25T06:00:00Z" },
            { "pool": "default", "minReplicas": 1, "replicaTarget": 2, "onlineNodes": 2 }
        ]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/page"), json!("/nodes"));
        assert_eq!(links_of(&payload, "/1/links/page"), json!("/nodes"));
    }

    #[test]
    fn a_schema_change_database_rollup_opens_its_database_folder() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "database": "dwh", "total": 12, "added": 3, "changed": 8, "deleted": 1,
            "lastChangeUtc": "2026-08-24T22:10:00Z"
        }]);
        links.decorate(&mut payload);

        assert_eq!(links_of(&payload, "/0/links/page"), json!("/schema-changes?q=dwh&window=all"));
        assert_eq!(links_of(&payload, "/0/links/catalog"), json!("/catalog?node=db%3Adwh"));
    }

    #[test]
    fn a_schema_change_opens_the_board_filtered_to_the_object_it_moved() {
        let links = GuiLinks::new("");
        let mut payload = json!([{
            "id": 91, "repoId": "r-1", "runId": "run-4", "pipelineId": "p-9", "database": "dwh",
            "category": "Tables", "schema": "arc", "name": "Citybike_Bikes", "changeType": "Changed",
            "commitSha": "abc123", "occurredUtc": "2026-08-24T22:10:00Z"
        }]);
        links.decorate(&mut payload);

        assert_eq!(
            links_of(&payload, "/0/links/page"),
            json!("/schema-changes?q=Citybike_Bikes&window=all")
        );
        // The snapshot run that saw it, and the scm flow that took it, stay references.
        assert_eq!(links_of(&payload, "/0/links/run"), json!("/runs/run-4"));
        assert_eq!(links_of(&payload, "/0/links/flow"), json!("/pipelines/p-9"));
    }

    #[test]
    fn rows_without_an_identity_are_left_alone() {
        let links = GuiLinks::new("");
        let mut payload = json!({ "fileCount": 12, "avgBytes": 900, "recent": { "fileCount": 3 } });
        let untouched = payload.clone();
        links.decorate(&mut payload);
        assert_eq!(payload, untouched);
    }
}
