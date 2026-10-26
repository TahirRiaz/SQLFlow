// A project's folder tree on the repo and pipelines pages: every folder starts shut, whether it holds flows or thousands of
// files, and opens only as the user opens it, while a tree of a search's or a filter's matches starts with every folder on
// the way to them open, keeping any folder the user closed shut.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { PipelineSummary, RepoTreeEntry } from "../src/api/types.ts";
import { buildFolderTree, expandedFolders, opensFolder } from "../src/features/pipelines/folderTree.ts";

const pipeline = (relativePath: string): PipelineSummary =>
  ({ id: relativePath, name: relativePath.slice(relativePath.lastIndexOf("/") + 1), relativePath } as PipelineSummary);

const file = (path: string): RepoTreeEntry => ({ path, isFolder: false, sizeBytes: 1 });

/** A project laid out as an estate is: its flows in one folder, and a data drop of a folder per log beside them. */
function estate() {
  const curves = Array.from({ length: 3000 }, (_, i) => file(`welldb/data/curves/LOG-${i}/chunk-0.json`));
  return buildFolderTree(
    "welldb",
    [pipeline("welldb/flows/welldb-welllog-01-pre.yaml"), pipeline("welldb/flows/welldb-welllog-03-delivery.yaml")],
    [file("welldb/flows/README.md"), ...curves],
  );
}

describe("expandedFolders", () => {
  it("opens no folder of a project nobody searched, the one holding its flows included", () => {
    assert.deepEqual([...expandedFolders(estate(), {}, false)], []);
  });

  it("opens the folders the user opened, and none beneath them", () => {
    const opened = expandedFolders(estate(), { data: true, "data/curves": true }, false);

    assert.deepEqual([...opened].sort(), ["data", "data/curves"]);
  });

  it("opens every folder of a search's matches, except one the user closed", () => {
    const tree = buildFolderTree("welldb", [pipeline("welldb/flows/welldb-welllog-03-delivery.yaml")], [file("welldb/data/curves/LOG-1/chunk-0.json")]);

    assert.deepEqual([...expandedFolders(tree, {}, true)].sort(), ["data", "data/curves", "data/curves/LOG-1", "flows"]);
    assert.deepEqual([...expandedFolders(tree, { data: false }, true)].sort(), ["data/curves", "data/curves/LOG-1", "flows"]);
  });
});

describe("opensFolder", () => {
  it("reads a folder as the user last set it, else shut, else open under a search", () => {
    assert.equal(opensFolder({}, "flows", false), false);
    assert.equal(opensFolder({}, "flows", true), true);
    assert.equal(opensFolder({ flows: true }, "flows", false), true);
    assert.equal(opensFolder({ flows: false }, "flows", true), false);
  });
});
