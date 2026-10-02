//! The embedded reference corpus and its search surface.
//!
//! `manifest.json` (the machine index) and every page body are compiled into
//! the binary (see `build.rs`). This module parses the manifest once at startup
//! and answers the doc tools the reference README prescribes: `search_docs`,
//! `get_doc`, `get_doc_by_yaml_path`, `get_doc_by_cli_command`, `related_docs`,
//! and `list_docs`.
//!
//! A host module adds reference pages of its own ([`DocPage`], [`DocsIndex::add`]):
//! they are indexed beside the embedded corpus and answered by the same tools.

use serde::Deserialize;
use std::collections::HashMap;

// Generated: `pub static DOC_BODIES: &[(&str, &str)]`.
include!(concat!(env!("OUT_DIR"), "/docs_bodies.rs"));

const MANIFEST_JSON: &str = include_str!("../../../docs/reference/manifest.json");

#[derive(Debug, Clone, Deserialize)]
pub struct DocMeta {
    pub id: String,
    pub path: String,
    pub title: String,
    #[serde(rename = "type")]
    pub doc_type: String,
    #[serde(default)]
    pub summary: String,
    #[serde(default)]
    pub keywords: Vec<String>,
    #[serde(default, rename = "yamlPath")]
    pub yaml_path: Option<String>,
    #[serde(default, rename = "cliCommand")]
    pub cli_command: Option<String>,
    #[serde(default)]
    pub related: Vec<String>,
    /// Traceability back to the code each fact was verified against. Retained
    /// from the manifest so it can be surfaced on demand.
    #[serde(default, rename = "sourceRefs")]
    #[allow(dead_code)]
    pub source_refs: Vec<String>,
}

impl DocMeta {
    /// The metadata of a page a host module adds: its id (unique across the whole index), title,
    /// type (one of the corpus's own: cli-command, flow-reference, source-type, concept, guide), the
    /// path it is shown under, its one-line summary, and the keywords a search matches it on.
    pub fn page(id: &str, title: &str, doc_type: &str, path: &str, summary: &str, keywords: &[&str]) -> DocMeta {
        DocMeta {
            id: id.to_string(),
            path: path.to_string(),
            title: title.to_string(),
            doc_type: doc_type.to_string(),
            summary: summary.to_string(),
            keywords: keywords.iter().map(|k| k.to_string()).collect(),
            yaml_path: None,
            cli_command: None,
            related: Vec::new(),
            source_refs: Vec::new(),
        }
    }

    /// The `.flow.yaml` dot-path the page documents, so `get_doc_by_yaml_path` finds it.
    pub fn for_yaml_path(mut self, yaml_path: &str) -> DocMeta {
        self.yaml_path = Some(yaml_path.to_string());
        self
    }

    /// The top-level CLI command the page documents, so `get_doc_by_cli_command` finds it.
    pub fn for_cli_command(mut self, command: &str) -> DocMeta {
        self.cli_command = Some(command.to_string());
        self
    }

    /// The ids of the pages `related_docs` lists for this one.
    pub fn related_to(mut self, ids: &[&str]) -> DocMeta {
        self.related = ids.iter().map(|id| id.to_string()).collect();
        self
    }
}

/// A reference page with its body: what a host module adds to the index. The body is compiled into
/// the module's own binary (`include_str!`), as the embedded corpus is compiled into this one.
#[derive(Debug, Clone)]
pub struct DocPage {
    pub meta: DocMeta,
    pub body: &'static str,
}

#[derive(Deserialize)]
struct Manifest {
    #[serde(default)]
    product: String,
    #[serde(default)]
    docs: Vec<DocMeta>,
}

/// A search hit with its relevance score.
pub struct Hit<'a> {
    pub meta: &'a DocMeta,
    pub score: u32,
}

/// The loaded corpus: metadata plus page bodies keyed by id.
pub struct DocsIndex {
    pub product: String,
    docs: Vec<DocMeta>,
    bodies: HashMap<String, &'static str>,
}

impl DocsIndex {
    /// Parse the embedded manifest and index the embedded bodies.
    pub fn load() -> DocsIndex {
        let manifest: Manifest =
            serde_json::from_str(MANIFEST_JSON).expect("embedded manifest.json is invalid");
        let bodies = DOC_BODIES.iter().map(|(id, body)| (id.to_string(), *body)).collect();
        DocsIndex {
            product: manifest.product,
            docs: manifest.docs,
            bodies,
        }
    }

    /// Indexes a host module's pages beside the embedded corpus. Nothing is added unless every page
    /// can be: an id has to be a non-empty token and unique across the whole index (it is what
    /// `get_doc` fetches by), and a page needs a title, a type and a body.
    pub fn add(&mut self, pages: Vec<DocPage>) -> Result<(), String> {
        let mut seen: Vec<&str> = Vec::with_capacity(pages.len());
        for page in &pages {
            let id = page.meta.id.as_str();
            if id.is_empty() || id.chars().any(|c| c.is_whitespace() || c.is_control()) {
                return Err(format!("a reference page has the id '{id}', which is not one token"));
            }
            if self.bodies.contains_key(id) || self.docs.iter().any(|d| d.id == id) || seen.contains(&id) {
                return Err(format!("the reference page id '{id}' is already indexed; a page's id is its own"));
            }
            if page.meta.title.trim().is_empty() || page.meta.doc_type.trim().is_empty() {
                return Err(format!("the reference page '{id}' has no title or no type"));
            }
            if page.body.trim().is_empty() {
                return Err(format!("the reference page '{id}' has no body"));
            }
            seen.push(id);
        }
        for page in pages {
            self.bodies.insert(page.meta.id.clone(), page.body);
            self.docs.push(page.meta);
        }
        Ok(())
    }

    pub fn len(&self) -> usize {
        self.docs.len()
    }

    pub fn is_empty(&self) -> bool {
        self.docs.is_empty()
    }

    /// The product label from the manifest (e.g. "SQLFlow V3").
    pub fn product(&self) -> &str {
        &self.product
    }

    pub fn all(&self) -> &[DocMeta] {
        &self.docs
    }

    pub fn get_meta(&self, id: &str) -> Option<&DocMeta> {
        self.docs.iter().find(|d| d.id == id)
    }

    pub fn body(&self, id: &str) -> Option<&'static str> {
        self.bodies.get(id).copied()
    }

    /// Rank docs against a free-text query. Title matches weigh most, then
    /// keywords, then summary. Optional `type` filter narrows the corpus.
    pub fn search(&self, query: &str, doc_type: Option<&str>, limit: usize) -> Vec<Hit<'_>> {
        let tokens: Vec<String> = query
            .to_lowercase()
            .split(|c: char| !c.is_alphanumeric() && c != '_')
            .filter(|t| !t.is_empty())
            .map(|t| t.to_string())
            .collect();
        let mut hits: Vec<Hit> = self
            .docs
            .iter()
            .filter(|d| doc_type.is_none_or(|t| d.doc_type.eq_ignore_ascii_case(t)))
            .filter_map(|d| {
                let title = d.title.to_lowercase();
                let summary = d.summary.to_lowercase();
                let keywords: Vec<String> = d.keywords.iter().map(|k| k.to_lowercase()).collect();
                let id = d.id.to_lowercase();
                let mut score = 0u32;
                for tok in &tokens {
                    if id == *tok {
                        score += 6;
                    }
                    if title.contains(tok) {
                        score += 3;
                    }
                    if keywords.iter().any(|k| k.contains(tok)) {
                        score += 2;
                    }
                    if summary.contains(tok) {
                        score += 1;
                    }
                }
                (score > 0).then_some(Hit { meta: d, score })
            })
            .collect();
        hits.sort_by(|a, b| b.score.cmp(&a.score).then_with(|| a.meta.id.cmp(&b.meta.id)));
        hits.truncate(limit);
        hits
    }

    /// Find the doc for a YAML path: exact match first, then the longest
    /// documented prefix (so `source.options.header` falls back to `source`).
    pub fn by_yaml_path(&self, yaml_path: &str) -> Option<&DocMeta> {
        if let Some(exact) = self.docs.iter().find(|d| d.yaml_path.as_deref() == Some(yaml_path)) {
            return Some(exact);
        }
        self.docs
            .iter()
            .filter(|d| {
                d.yaml_path
                    .as_deref()
                    .is_some_and(|yp| yaml_path == yp || yaml_path.starts_with(&format!("{yp}.")))
            })
            .max_by_key(|d| d.yaml_path.as_deref().map(str::len).unwrap_or(0))
    }

    /// Find the doc for a CLI command (e.g. `run`, `lineage`).
    pub fn by_cli_command(&self, command: &str) -> Option<&DocMeta> {
        self.docs
            .iter()
            .find(|d| d.cli_command.as_deref() == Some(command))
    }

    /// Resolve the `related` ids of a doc to their metadata.
    pub fn related(&self, id: &str) -> Vec<&DocMeta> {
        let Some(meta) = self.get_meta(id) else {
            return Vec::new();
        };
        meta.related
            .iter()
            .filter_map(|rid| self.get_meta(rid))
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loads_every_page_body() {
        let idx = DocsIndex::load();
        assert!(idx.len() >= 70, "expected the full corpus");
        for meta in idx.all() {
            assert!(idx.body(&meta.id).is_some(), "missing body for {}", meta.id);
        }
    }

    #[test]
    fn searches_and_resolves() {
        let idx = DocsIndex::load();
        let hits = idx.search("lineage", None, 10);
        assert!(!hits.is_empty());
        // yamlPath fallback to the longest documented prefix.
        assert!(idx.by_yaml_path("source.options.header").is_some());
    }

    fn page(id: &str, body: &'static str) -> DocPage {
        DocPage {
            meta: DocMeta::page(id, "Probe targets", "concept", "probe/targets.md", "What a probe target is.", &["probe", "target"])
                .for_yaml_path("probe.target")
                .for_cli_command("probe")
                .related_to(&["cli-run"]),
            body,
        }
    }

    #[test]
    fn a_module_page_is_answered_by_every_doc_lookup() {
        let mut idx = DocsIndex::load();
        let before = idx.len();
        idx.add(vec![page("probe-targets", "# Probe targets")]).expect("the page is added");

        assert_eq!(idx.len(), before + 1);
        assert_eq!(idx.body("probe-targets"), Some("# Probe targets"));
        assert_eq!(idx.get_meta("probe-targets").map(|m| m.path.as_str()), Some("probe/targets.md"));
        assert_eq!(idx.search("probe target", None, 3)[0].meta.id, "probe-targets");
        assert_eq!(idx.search("probe", Some("concept"), 3)[0].meta.id, "probe-targets");
        assert_eq!(idx.by_yaml_path("probe.target.timeout").map(|m| m.id.as_str()), Some("probe-targets"));
        assert_eq!(idx.by_cli_command("probe").map(|m| m.id.as_str()), Some("probe-targets"));
        assert_eq!(idx.related("probe-targets").iter().map(|m| m.id.as_str()).collect::<Vec<_>>(), ["cli-run"]);
        // The embedded corpus is untouched.
        assert!(idx.body("cli-run").is_some());
    }

    #[test]
    fn a_page_that_cannot_be_indexed_adds_nothing() {
        let mut idx = DocsIndex::load();
        let before = idx.len();

        // One good page beside one that takes an embedded page's id: neither is added.
        let refused = idx.add(vec![page("probe-targets", "# ok"), page("cli-run", "# clash")]).unwrap_err();
        assert!(refused.contains("'cli-run' is already indexed"), "{refused}");
        assert_eq!(idx.len(), before);
        assert!(idx.body("probe-targets").is_none());

        assert!(idx.add(vec![page("probe-a", "# a"), page("probe-a", "# b")]).unwrap_err().contains("already indexed"));
        assert!(idx.add(vec![page("probe targets", "# a")]).unwrap_err().contains("not one token"));
        assert!(idx.add(vec![page("", "# a")]).unwrap_err().contains("not one token"));
        assert!(idx.add(vec![page("probe-empty", "  ")]).unwrap_err().contains("has no body"));
        let mut untitled = page("probe-untitled", "# a");
        untitled.meta.title = " ".to_string();
        assert!(idx.add(vec![untitled]).unwrap_err().contains("no title"));
        assert_eq!(idx.len(), before);
    }
}
