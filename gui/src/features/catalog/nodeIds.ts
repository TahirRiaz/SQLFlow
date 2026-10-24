// The catalog tree's single node-identity contract: every tree node, the ?node= deep link, and the details
// panel agree on these ids. An id is a type prefix plus URI-encoded segments joined with "|", so any value
// (a database name, a storage path, a batch) round-trips regardless of the characters it contains. A null
// database/schema/container encodes as the sentinel "~".
//
// Five perspectives:
//   Databases    database > schema > kind > object                (the SQL estate; files excluded)
//   Sources      origin system > container > folder > file        (storage accounts, SFTP servers, filesystem)
//   Datasets     system > namespace > group > dataset             (external systems the flows read and write)
//   Flows        repo > batch > flow                              (the pipeline estate, for navigation)
//   Subscribers  type > subscriber                                (the consumption estate: who reads it all)
//
// A database and a schema are named as the catalog's node keys name them, case-folded, so two spellings of one database
// are one node. An object whose database a sync could not work out has no database to be named by: it is grouped by the
// connection reference it was reached through instead, which its database, schema and kind ids then carry.

export type CatalogNode =
  // Databases: `connection` is set only when `database` is null (a database the sync could not work out)
  | { type: "databasesRoot" }
  | { type: "database"; database: string | null; connection: string | null }
  | { type: "schema"; database: string | null; connection: string | null; schema: string | null }
  | { type: "kind"; database: string | null; connection: string | null; schema: string | null; kind: string }
  // Sources (file origins): provider (Azure/AWS/GCP/SFTP/...) > origin (account/bucket/host) > container > folder
  | { type: "sourcesRoot" }
  | { type: "provider"; provider: string }
  | { type: "origin"; provider: string; origin: string }
  | { type: "container"; provider: string; origin: string; container: string }
  | { type: "folder"; provider: string; origin: string; container: string | null; path: string }
  // Datasets of external systems: system > namespace > group; the dataset itself is an object leaf
  | { type: "datasetsRoot" }
  | { type: "datasetSystem"; system: string }
  | { type: "datasetNamespace"; system: string; namespace: string }
  | { type: "datasetGroup"; system: string; namespace: string; group: string }
  // Flows: repo > folder (the repository directory structure) > batch > flow
  | { type: "flowsRoot" }
  | { type: "repo"; repoId: string }
  | { type: "flowFolder"; repoId: string; path: string }
  | { type: "batch"; repoId: string; path: string; batch: string }
  | { type: "flow"; repoId: string; pipelineId: string }
  // Subscribers: the consuming tool (PowerBI/Tableau/...) > the subscriber itself
  | { type: "subscribersRoot" }
  | { type: "subscriberType"; subscriberType: string }
  | { type: "subscriber"; key: string }
  // A leaf object (a database object, a file, or a dataset), keyed by its global object key
  | { type: "object"; objectKey: string };

/** How a tree names a database a sync could not work out. */
export const UNKNOWN_DATABASE_LABEL = "(database unknown)";

/** How a tree names a schema a sync could not work out. */
export const UNKNOWN_SCHEMA_LABEL = "(schema unknown)";

const NULL_SENTINEL = "~";

function seg(value: string | null): string {
  return value === null ? NULL_SENTINEL : encodeURIComponent(value);
}

/** A database or schema name as ids carry it: case-folded, as the node key folds it. */
export function foldName(value: string | null): string | null {
  return value === null ? null : value.toLowerCase();
}

/** The segment an unknown database's ids end with: the connection that groups it. Nothing for a known database. */
function connectionSuffix(database: string | null, connection: string | null): string {
  return database === null && connection !== null ? `|${encodeURIComponent(connection)}` : "";
}

function unseg(value: string): string | null {
  return value === NULL_SENTINEL ? null : decodeURIComponent(value);
}

export function encodeNodeId(node: CatalogNode): string {
  switch (node.type) {
    case "databasesRoot":
      return "dbs";
    case "database":
      return `db:${seg(foldName(node.database))}${connectionSuffix(node.database, node.connection)}`;
    case "schema":
      return `sch:${seg(foldName(node.database))}|${seg(foldName(node.schema))}${connectionSuffix(node.database, node.connection)}`;
    case "kind":
      return `knd:${seg(foldName(node.database))}|${seg(foldName(node.schema))}|${seg(node.kind)}${connectionSuffix(node.database, node.connection)}`;
    case "sourcesRoot":
      return "sources";
    case "provider":
      return `prov:${seg(node.provider)}`;
    case "origin":
      return `org:${seg(node.provider)}|${seg(node.origin)}`;
    case "container":
      return `orgc:${seg(node.provider)}|${seg(node.origin)}|${seg(node.container)}`;
    case "folder":
      return `orgf:${seg(node.provider)}|${seg(node.origin)}|${seg(node.container)}|${seg(node.path)}`;
    case "datasetsRoot":
      return "sets";
    case "datasetSystem":
      return `dss:${seg(node.system)}`;
    case "datasetNamespace":
      return `dsn:${seg(node.system)}|${seg(node.namespace)}`;
    case "datasetGroup":
      return `dsg:${seg(node.system)}|${seg(node.namespace)}|${seg(node.group)}`;
    case "flowsRoot":
      return "flows";
    case "repo":
      return `repo:${seg(node.repoId)}`;
    case "flowFolder":
      return `flwd:${seg(node.repoId)}|${seg(node.path)}`;
    case "batch":
      return `bat:${seg(node.repoId)}|${seg(node.path)}|${seg(node.batch)}`;
    case "flow":
      return `flw:${seg(node.repoId)}|${seg(node.pipelineId)}`;
    case "subscribersRoot":
      return "subs";
    case "subscriberType":
      return `subt:${seg(node.subscriberType)}`;
    case "subscriber":
      return `sub:${encodeURIComponent(node.key)}`;
    case "object":
      return `obj:${encodeURIComponent(node.objectKey)}`;
  }
}

/** Decodes a tree/deep-link id back into its node; null for anything malformed (a hand-edited URL). */
export function decodeNodeId(id: string): CatalogNode | null {
  if (id === "dbs") {
    return { type: "databasesRoot" };
  }
  if (id === "sources") {
    return { type: "sourcesRoot" };
  }
  if (id === "flows") {
    return { type: "flowsRoot" };
  }
  if (id === "sets") {
    return { type: "datasetsRoot" };
  }
  if (id === "subs") {
    return { type: "subscribersRoot" };
  }

  const colon = id.indexOf(":");
  if (colon <= 0) {
    return null;
  }
  const prefix = id.slice(0, colon);
  const parts = id.slice(colon + 1).split("|");
  const notNull = (value: string) => value !== NULL_SENTINEL;
  try {
    switch (prefix) {
      case "db": {
        const connection = connectionOf(parts, 1);
        return connection === undefined ? null : { type: "database", database: unseg(parts[0]), connection };
      }
      case "sch": {
        const connection = connectionOf(parts, 2);
        return connection === undefined
          ? null
          : { type: "schema", database: unseg(parts[0]), connection, schema: unseg(parts[1]) };
      }
      case "knd": {
        const connection = connectionOf(parts, 3);
        return connection === undefined || !notNull(parts[2])
          ? null
          : { type: "kind", database: unseg(parts[0]), connection, schema: unseg(parts[1]), kind: decodeURIComponent(parts[2]) };
      }
      case "prov":
        return parts.length === 1 && notNull(parts[0])
          ? { type: "provider", provider: decodeURIComponent(parts[0]) }
          : null;
      case "org":
        return parts.length === 2 && notNull(parts[0]) && notNull(parts[1])
          ? { type: "origin", provider: decodeURIComponent(parts[0]), origin: decodeURIComponent(parts[1]) }
          : null;
      case "orgc":
        return parts.length === 3 && notNull(parts[0]) && notNull(parts[1]) && notNull(parts[2])
          ? {
            type: "container",
            provider: decodeURIComponent(parts[0]),
            origin: decodeURIComponent(parts[1]),
            container: decodeURIComponent(parts[2]),
          }
          : null;
      case "orgf":
        return parts.length === 4 && notNull(parts[0]) && notNull(parts[1]) && notNull(parts[3])
          ? {
            type: "folder",
            provider: decodeURIComponent(parts[0]),
            origin: decodeURIComponent(parts[1]),
            container: unseg(parts[2]),
            path: decodeURIComponent(parts[3]),
          }
          : null;
      case "dss":
        return parts.length === 1 && notNull(parts[0])
          ? { type: "datasetSystem", system: decodeURIComponent(parts[0]) }
          : null;
      case "dsn":
        return parts.length === 2 && parts.every(notNull)
          ? { type: "datasetNamespace", system: decodeURIComponent(parts[0]), namespace: decodeURIComponent(parts[1]) }
          : null;
      case "dsg":
        return parts.length === 3 && parts.every(notNull)
          ? {
            type: "datasetGroup",
            system: decodeURIComponent(parts[0]),
            namespace: decodeURIComponent(parts[1]),
            group: decodeURIComponent(parts[2]),
          }
          : null;
      case "repo":
        return parts.length === 1 && notNull(parts[0])
          ? { type: "repo", repoId: decodeURIComponent(parts[0]) }
          : null;
      case "flwd":
        return parts.length === 2 && notNull(parts[0]) && notNull(parts[1])
          ? { type: "flowFolder", repoId: decodeURIComponent(parts[0]), path: decodeURIComponent(parts[1]) }
          : null;
      case "bat":
        return parts.length === 3 && notNull(parts[0]) && notNull(parts[2])
          ? {
            type: "batch",
            repoId: decodeURIComponent(parts[0]),
            path: unseg(parts[1]) ?? "",
            batch: decodeURIComponent(parts[2]),
          }
          : null;
      case "flw":
        return parts.length === 2 && notNull(parts[0]) && notNull(parts[1])
          ? { type: "flow", repoId: decodeURIComponent(parts[0]), pipelineId: decodeURIComponent(parts[1]) }
          : null;
      case "subt":
        return parts.length === 1 && notNull(parts[0])
          ? { type: "subscriberType", subscriberType: decodeURIComponent(parts[0]) }
          : null;
      case "sub":
        return parts.length === 1 ? { type: "subscriber", key: decodeURIComponent(parts[0]) } : null;
      case "obj":
        return parts.length === 1 ? { type: "object", objectKey: decodeURIComponent(parts[0]) } : null;
      default:
        return null;
    }
  } catch {
    // decodeURIComponent throws on a malformed escape sequence in a hand-edited URL.
    return null;
  }
}

/**
 * The connection a database, schema or kind id carries after its `segments` own parts: null when there is none (a known
 * database, or an unknown one from before ids carried it), undefined when the id is malformed (a connection on a known
 * database, or too many parts).
 */
function connectionOf(parts: string[], segments: number): string | null | undefined {
  if (parts.length === segments) {
    return null;
  }
  return parts.length === segments + 1 && parts[0] === NULL_SENTINEL && parts[segments] !== NULL_SENTINEL
    ? decodeURIComponent(parts[segments])
    : undefined;
}

/** The ancestor chain of ids to expand so the given node is visible (excludes the node itself). Derives only
 * what the id itself carries; a file object's origin/container/folder chain lives in the loaded file list, so
 * the tree reveals it from there, and a flow's batch is not in its id, so a deep-linked flow opens its repo. */
export function ancestorIds(node: CatalogNode): string[] {
  switch (node.type) {
    case "databasesRoot":
    case "sourcesRoot":
    case "datasetsRoot":
    case "flowsRoot":
    case "subscribersRoot":
      return [];
    case "datasetSystem":
      return [encodeNodeId({ type: "datasetsRoot" })];
    case "datasetNamespace":
      return [encodeNodeId({ type: "datasetsRoot" }), encodeNodeId({ type: "datasetSystem", system: node.system })];
    case "datasetGroup":
      return [
        ...ancestorIds({ type: "datasetNamespace", system: node.system, namespace: node.namespace }),
        encodeNodeId({ type: "datasetNamespace", system: node.system, namespace: node.namespace }),
      ];
    case "subscriberType":
      return [encodeNodeId({ type: "subscribersRoot" })];
    case "subscriber":
      // The id does not carry the subscriber's type, so a deep link opens the Subscribers root; the tree
      // reveals the type branch from the loaded list, exactly as a file's chain is revealed.
      return [encodeNodeId({ type: "subscribersRoot" })];
    case "database":
      return [encodeNodeId({ type: "databasesRoot" })];
    case "schema":
      return [
        encodeNodeId({ type: "databasesRoot" }),
        encodeNodeId({ type: "database", database: node.database, connection: node.connection }),
      ];
    case "kind": {
      const schema: CatalogNode = { type: "schema", database: node.database, connection: node.connection, schema: node.schema };
      return [...ancestorIds(schema), encodeNodeId(schema)];
    }
    case "object":
      // A file's or a dataset's chain is revealed by the tree from its loaded list; a bare object opens Databases.
      return [encodeNodeId({ type: "databasesRoot" })];
    case "provider":
      return [encodeNodeId({ type: "sourcesRoot" })];
    case "origin":
      return [encodeNodeId({ type: "sourcesRoot" }), encodeNodeId({ type: "provider", provider: node.provider })];
    case "container":
      return [
        ...ancestorIds({ type: "origin", provider: node.provider, origin: node.origin }),
        encodeNodeId({ type: "origin", provider: node.provider, origin: node.origin }),
      ];
    case "folder": {
      const ids = [
        ...ancestorIds({ type: "origin", provider: node.provider, origin: node.origin }),
        encodeNodeId({ type: "origin", provider: node.provider, origin: node.origin }),
      ];
      if (node.container !== null) {
        ids.push(encodeNodeId({ type: "container", provider: node.provider, origin: node.origin, container: node.container }));
      }
      const segments = node.path.split("/");
      let acc = "";
      for (let i = 0; i < segments.length - 1; i++) {
        acc = acc ? `${acc}/${segments[i]}` : segments[i];
        ids.push(encodeNodeId({ type: "folder", provider: node.provider, origin: node.origin, container: node.container, path: acc }));
      }
      return ids;
    }
    case "repo":
      return [encodeNodeId({ type: "flowsRoot" })];
    case "flowFolder": {
      const ids = [encodeNodeId({ type: "flowsRoot" }), encodeNodeId({ type: "repo", repoId: node.repoId })];
      const segments = node.path.split("/");
      let acc = "";
      for (let i = 0; i < segments.length - 1; i++) {
        acc = acc ? `${acc}/${segments[i]}` : segments[i];
        ids.push(encodeNodeId({ type: "flowFolder", repoId: node.repoId, path: acc }));
      }
      return ids;
    }
    case "batch": {
      const ids = [encodeNodeId({ type: "flowsRoot" }), encodeNodeId({ type: "repo", repoId: node.repoId })];
      if (node.path !== "") {
        const segments = node.path.split("/");
        let acc = "";
        for (const segment of segments) {
          acc = acc ? `${acc}/${segment}` : segment;
          ids.push(encodeNodeId({ type: "flowFolder", repoId: node.repoId, path: acc }));
        }
      }
      return ids;
    }
    case "flow":
      // The flow id does not carry its folder/batch, so a deep link opens its repo; the panel shows it regardless.
      return [encodeNodeId({ type: "flowsRoot" }), encodeNodeId({ type: "repo", repoId: node.repoId })];
  }
}
