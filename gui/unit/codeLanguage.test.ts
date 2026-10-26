// The language a repository file is coloured as in the code window, read from its name: the product's own documents,
// the notes, scripts and configuration a repository of flows keeps beside them, and plain text for anything else.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { languageOfPath } from "../src/lib/codeLanguage.ts";
import { languageName } from "../src/features/chat/explain.ts";

describe("languageOfPath", () => {
  it("reads the product's own documents by their extension, whatever its case", () => {
    assert.equal(languageOfPath("sales/orders.flow.yaml"), "yaml");
    assert.equal(languageOfPath("schedules.yml"), "yaml");
    assert.equal(languageOfPath("templates/WellLog.1.4.0.JSON"), "json");
    assert.equal(languageOfPath("sql/views/compat_views.sql"), "sql");
  });

  it("reads the notes, scripts and configuration beside them", () => {
    assert.equal(languageOfPath("README.md"), "markdown");
    assert.equal(languageOfPath("tools/load.ps1"), "powershell");
    assert.equal(languageOfPath("tools/load.sh"), "shell");
    assert.equal(languageOfPath("src/Loader.cs"), "csharp");
    assert.equal(languageOfPath("pyproject.toml"), "ini");
    assert.equal(languageOfPath(".editorconfig"), "ini");
    assert.equal(languageOfPath("deploy/Dockerfile"), "dockerfile");
  });

  it("shows anything else as plain text", () => {
    assert.equal(languageOfPath("data/curves/LOG-0001/chunk-0.csv"), "plaintext");
    assert.equal(languageOfPath("LICENSE"), "plaintext");
    assert.equal(languageOfPath("notes.yaml.bak"), "plaintext");
  });
});

describe("languageName", () => {
  it("names every language the code window colours as a sentence says it", () => {
    assert.equal(languageName("markdown"), "Markdown");
    assert.equal(languageName("shell"), "shell script");
    assert.equal(languageName("csharp"), "C#");
    assert.equal(languageName("plaintext"), "text");
  });
});
