//! The embedded reference corpus and its search surface.
//!
//! `manifest.json` (the machine index) and every page body are compiled into
//! the binary (see `build.rs`). This module parses the manifest once at startup
//! and answers the doc tools the reference README prescribes: `search_docs`,
//! `get_doc`, `get_doc_by_yaml_path`, `get_doc_by_cli_command`, `related_docs`,
//! and `list_docs`.

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
    bodies: HashMap<&'static str, &'static str>,
}

impl DocsIndex {
    /// Parse the embedded manifest and index the embedded bodies.
    pub fn load() -> DocsIndex {
        let manifest: Manifest =
            serde_json::from_str(MANIFEST_JSON).expect("embedded manifest.json is invalid");
        let bodies = DOC_BODIES.iter().copied().collect();
        DocsIndex {
            product: manifest.product,
            docs: manifest.docs,
            bodies,
        }
    }

    pub fn len(&self) -> usize {
        self.docs.len()
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
}
