//! Module census files, read from the directories the client names.
//!
//! A module that adds flow kinds, or documents of its own, ships a census file for each (the same `keys` format
//! SQLFlow's own census files use, with a top-level `flowType` or `documentType`). A client passes the directories
//! holding them in its initialization options:
//!
//! ```json
//! { "censusDirectories": ["/path/to/module/census"] }
//! ```
//!
//! Every `*.json` file directly in each directory is registered with the engine, in name order. A file that cannot
//! be read or is not a census is reported and skipped; the rest still register.

use std::path::{Path, PathBuf};

/// How loading one file went, for the client's log.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Loaded {
    /// The file registered a census for the kind named.
    Registered { file: PathBuf, kind: String },
    /// The file or directory could not be used, with the reason.
    Skipped { path: PathBuf, reason: String },
}

/// The census directories an `initializationOptions` value names: its `censusDirectories` array of paths.
pub fn directories(options: Option<&serde_json::Value>) -> Vec<PathBuf> {
    options
        .and_then(|o| o.get("censusDirectories"))
        .and_then(|d| d.as_array())
        .map(|dirs| {
            dirs.iter()
                .filter_map(|d| d.as_str())
                .map(str::trim)
                .filter(|d| !d.is_empty())
                .map(PathBuf::from)
                .collect()
        })
        .unwrap_or_default()
}

/// Registers every census file of every directory, returning what happened to each.
pub fn load(directories: &[PathBuf]) -> Vec<Loaded> {
    let mut outcomes = Vec::new();
    for directory in directories {
        load_directory(directory, &mut outcomes);
    }
    outcomes
}

fn load_directory(directory: &Path, outcomes: &mut Vec<Loaded>) {
    let entries = match std::fs::read_dir(directory) {
        Ok(entries) => entries,
        Err(e) => {
            outcomes.push(Loaded::Skipped { path: directory.to_path_buf(), reason: format!("cannot read the directory: {e}") });
            return;
        }
    };

    let mut files: Vec<PathBuf> = entries
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.is_file() && path.extension().is_some_and(|ext| ext.eq_ignore_ascii_case("json")))
        .collect();
    files.sort();

    for file in files {
        match std::fs::read_to_string(&file) {
            Ok(json) => match sqlflow_lang::census::register(&json) {
                Ok(kind) => outcomes.push(Loaded::Registered { file, kind: kind.to_string() }),
                Err(reason) => outcomes.push(Loaded::Skipped { path: file, reason }),
            },
            Err(e) => outcomes.push(Loaded::Skipped { path: file, reason: format!("cannot read the file: {e}") }),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_directories_come_from_the_initialization_options() {
        let options = serde_json::json!({ "censusDirectories": ["/a", " ", "/b", 3] });
        assert_eq!(directories(Some(&options)), vec![PathBuf::from("/a"), PathBuf::from("/b")]);
        assert!(directories(None).is_empty());
        assert!(directories(Some(&serde_json::json!({ "other": true }))).is_empty());
    }

    #[test]
    fn every_census_file_of_a_directory_registers_and_the_rest_are_reported() {
        let dir = std::env::temp_dir().join(format!("sqlflow-lsp-census-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(
            dir.join("keys.lsp-tests-thing.json"),
            r#"{ "documentType": "lsp-tests-thing", "keys": [{ "path": "name", "type": "string" }] }"#,
        )
        .unwrap();
        std::fs::write(dir.join("notes.json"), "not json").unwrap();
        std::fs::write(dir.join("readme.md"), "not a census").unwrap();

        let outcomes = load(&[dir.clone(), dir.join("missing")]);
        std::fs::remove_dir_all(&dir).unwrap();

        assert!(outcomes.iter().any(|o| matches!(o, Loaded::Registered { kind, .. } if kind == "documentType 'lsp-tests-thing'")));
        assert!(outcomes.iter().any(|o| matches!(o, Loaded::Skipped { path, reason } if path.ends_with("notes.json") && reason.contains("not a census file"))));
        assert!(outcomes.iter().any(|o| matches!(o, Loaded::Skipped { reason, .. } if reason.contains("cannot read the directory"))));
        assert!(!outcomes.iter().any(|o| matches!(o, Loaded::Skipped { path, .. } if path.ends_with("readme.md"))));
        assert!(sqlflow_lang::census::Census::for_document_type("lsp-tests-thing").is_some());
    }
}
