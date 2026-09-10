// The catalog tree's single node-identity contract: every tree node, the ?node= deep link, and the details
// panel agree on these ids. An id is a type prefix plus URI-encoded segments joined with "|", so any value
// (a database name, a storage path, a batch) round-trips regardless of the characters it contains. A null
// database/schema/container encodes as the sentinel "~".
//
// Four perspectives:
//   Databases    connection > database > schema > kind > object   (the SQL estate; files excluded)
//   Sources      origin system > container > folder > file        (storage accounts, SFTP servers, filesystem)
//   Flows        repo > batch > flow                              (the pipeline estate, for navigation)
//   Subscribers  type > subscriber                                (the consumption estate: who reads it all)

export type CatalogNode =
  // Databases
  | { type: "databasesRoot" }
  | { type: "database"; database: string | null }
  | { type: "schema"; database: string | null; schema: string | null }
  | { type: "kind"; database: string | null; schema: string | null; kind: string }
  // Sources (file origins): provider (Azure/AWS/GCP/SFTP/...) > origin (account/bucket/host) > container > folder
  | { type: "sourcesRoot" }
  | { type: "provider"; provider: string }
  | { type: "origin"; provider: string; origin: string }
  | { type: "container"; provider: string; origin: string; container: string }
  | { type: "folder"; provider: string; origin: string; container: string | null; path: string }
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
  // A leaf object (a database object OR a file), keyed by its global object key
  | { type: "object"; objectKey: string };

export const UNRESOLVED_LABEL = "(unresolved)";

const NULL_SENTINEL = "~";

function seg(value: string | null): string {
  return value === null ? NULL_SENTINEL : encodeURIComponent(value);
}

function unseg(value: string): string | null {
  return value === NULL_SENTINEL ? null : decodeURIComponent(value);
}

export function encodeNodeId(node: CatalogNode): string {
  switch (node.type) {
    case "databasesRoot":
      return "dbs";
    case "database":
      return `db:${seg(node.database)}`;
    case "schema":
      return `sch:${seg(node.database)}|${seg(node.schema)}`;
    case "kind":
      return `knd:${seg(node.database)}|${seg(node.schema)}|${seg(node.kind)}`;
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
      case "db":
        return parts.length === 1 ? { type: "database", database: unseg(parts[0]) } : null;
      case "sch":
        return parts.length === 2
          ? { type: "schema", database: unseg(parts[0]), schema: unseg(parts[1]) }
          : null;
      case "knd":
        return parts.length === 3 && notNull(parts[2])
          ? { type: "kind", database: unseg(parts[0]), schema: unseg(parts[1]), kind: decodeURIComponent(parts[2]) }
          : null;
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

/** The ancestor chain of ids to expand so the given node is visible (excludes the node itself). Derives only
 * what the id itself carries; a file object's origin/container/folder chain lives in the loaded file list, so
 * the tree reveals it from there, and a flow's batch is not in its id, so a deep-linked flow opens its repo. */
export function ancestorIds(node: CatalogNode): string[] {
  switch (node.type) {
    case "databasesRoot":
    case "sourcesRoot":
    case "flowsRoot":
    case "subscribersRoot":
      return [];
    case "subscriberType":
      return [encodeNodeId({ type: "subscribersRoot" })];
    case "subscriber":
      // The id does not carry the subscriber's type, so a deep link opens the Subscribers root; the tree
      // reveals the type branch from the loaded list, exactly as a file's chain is revealed.
      return [encodeNodeId({ type: "subscribersRoot" })];
    case "database":
      return [encodeNodeId({ type: "databasesRoot" })];
    case "schema":
      return [encodeNodeId({ type: "databasesRoot" }), encodeNodeId({ type: "database", database: node.database })];
    case "kind":
      return [
        ...ancestorIds({ type: "schema", database: node.database, schema: node.schema }),
        encodeNodeId({ type: "schema", database: node.database, schema: node.schema }),
      ];
    case "object":
      // A file object's chain is revealed by the tree from the loaded file list; a bare object opens Databases.
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
