// The catalog tree's Databases branch: one node per database however many connection references reach it and however
// its name is spelled, a database the sync could not work out grouped per connection, folders that ask for exactly the
// objects they count, and the node ids every one of them is reached by.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { SchemaKindCount } from "../src/api/types.ts";
import { foldDatabases, kindFolderQuery } from "../src/features/catalog/databaseTree.ts";
import { ancestorIds, decodeNodeId, encodeNodeId } from "../src/features/catalog/nodeIds.ts";

const byKind = (a: string, b: string) => a.localeCompare(b);

const row = (serverRef: string, database: string | null, schema: string | null, kind: string, objectCount: number): SchemaKindCount =>
  ({ serverRef, database, schema, kind, objectCount });

describe("foldDatabases", () => {
  it("makes one database of every spelling and every connection reference that reaches it", () => {
    const [database, ...rest] = foldDatabases([
      row("${env:WAREHOUSE_DB}", "Warehouse", "sales", "Table", 20),
      row("${env:WAREHOUSE_DB}", "Warehouse", "dbo", "Table", 3),
      row("${env:REPORTING_DB}", "warehouse", "SALES", "View", 5),
    ], byKind);

    assert.equal(rest.length, 0);
    assert.equal(database.database, "warehouse");
    assert.equal(database.label, "Warehouse", "the spelling the most objects carry");
    assert.equal(database.connection, null);
    assert.deepEqual(database.connections, ["${env:REPORTING_DB}", "${env:WAREHOUSE_DB}"]);
    assert.equal(database.objectCount, 28);
    assert.deepEqual(database.schemas.map((s) => [s.schema, s.label, s.objectCount]), [["dbo", "dbo", 3], ["sales", "sales", 25]]);
    assert.deepEqual(database.schemas[1].kinds, [{ kind: "Table", objectCount: 20 }, { kind: "View", objectCount: 5 }]);
  });

  it("keeps a database the sync could not work out apart per connection, after the named ones", () => {
    const branches = foldDatabases([
      row("${env:WAREHOUSE}", null, "pre", "Table", 9),
      row("${env:STAGING}", null, "pre", "Table", 2),
      row("${env:WAREHOUSE}", "Dwh", "dbo", "Table", 4),
    ], byKind);

    assert.deepEqual(branches.map((b) => [b.database, b.connection, b.label, b.objectCount]), [
      ["dwh", null, "Dwh", 4],
      [null, "${env:STAGING}", null, 2],
      [null, "${env:WAREHOUSE}", null, 9],
    ]);
  });

  it("lists a schema the sync could not work out after the named ones", () => {
    const [database] = foldDatabases([
      row("${env:DWH}", "Dwh", null, "Table", 1),
      row("${env:DWH}", "Dwh", "stg", "Table", 2),
      row("${env:DWH}", "Dwh", "Arc", "Table", 3),
    ], byKind);

    assert.deepEqual(database.schemas.map((s) => s.label), ["Arc", "stg", null]);
  });

  it("breaks a tie between spellings by ordinal order, so the label never depends on the order rows arrive in", () => {
    const one = foldDatabases([row("${env:DWH}", "dwh", "dbo", "Table", 2), row("${env:DWH}", "DWH", "dbo", "View", 2)], byKind);
    const other = foldDatabases([row("${env:DWH}", "DWH", "dbo", "View", 2), row("${env:DWH}", "dwh", "dbo", "Table", 2)], byKind);

    assert.equal(one[0].label, "DWH");
    assert.equal(other[0].label, "DWH");
  });
});

describe("kindFolderQuery", () => {
  it("asks for the database and schema a folder counts, by name", () => {
    const [database] = foldDatabases([row("${env:DWH}", "Dwh", "Dbo", "Table", 1)], byKind);
    assert.deepEqual(kindFolderQuery(database, database.schemas[0], "Table"), { database: "dwh", schema: "dbo", kind: "Table" });
  });

  it("asks for an unknown database on its connection, and an unknown schema as unknown", () => {
    const [database] = foldDatabases([row("${env:STAGING}", null, null, "Table", 1)], byKind);
    assert.deepEqual(kindFolderQuery(database, database.schemas[0], "Table"), {
      serverRef: "${env:STAGING}", databaseUnknown: true, schemaUnknown: true, kind: "Table",
    });
  });
});

describe("database node ids", () => {
  it("name a database, a schema and a kind case-folded, so every spelling reaches one node", () => {
    assert.equal(encodeNodeId({ type: "database", database: "Warehouse", connection: null }), "db:warehouse");
    assert.equal(
      encodeNodeId({ type: "kind", database: "Warehouse", connection: null, schema: "SALES", kind: "View" }),
      "knd:warehouse|sales|View");
  });

  it("carry the connection of an unknown database, and round-trip it", () => {
    const node = { type: "kind", database: null, connection: "${env:STAGING}", schema: "pre", kind: "Table" } as const;
    const id = encodeNodeId(node);
    assert.equal(id, "knd:~|pre|Table|%24%7Benv%3ASTAGING%7D");
    assert.deepEqual(decodeNodeId(id), node);
    assert.deepEqual(ancestorIds(node), [
      "dbs",
      "db:~|%24%7Benv%3ASTAGING%7D",
      "sch:~|pre|%24%7Benv%3ASTAGING%7D",
    ]);
  });

  it("refuse a connection on a known database, and too many parts", () => {
    assert.equal(decodeNodeId("db:dwh|%24%7Benv%3ADWH%7D"), null);
    assert.equal(decodeNodeId("sch:~|pre|a|b"), null);
    assert.deepEqual(decodeNodeId("db:dwh"), { type: "database", database: "dwh", connection: null });
  });
});
