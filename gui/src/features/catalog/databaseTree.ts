// The catalog tree's Databases branch, folded from the per-kind rows the schema endpoint returns (one per connection
// reference, database, schema and kind): database > schema > kind. A database is one node however many connection
// references reach it and however its name is spelled, since the catalog's node keys fold case; each name shows in the
// spelling the most objects carry. A database a sync could not work out is one node per connection reference instead,
// since nothing says two such connections reach the same database, and it lists after the named ones.

import type { SchemaKindCount } from "../../api/types.ts";
import { foldName } from "./nodeIds.ts";

export interface KindBranch {
  kind: string;
  objectCount: number;
}

export interface SchemaBranch {
  /** The schema as ids name it (case-folded), or null when the sync could not work it out. */
  schema: string | null;
  /** The spelling shown: the one the most objects carry. Null with `schema`. */
  label: string | null;
  objectCount: number;
  kinds: KindBranch[];
}

export interface DatabaseBranch {
  /** The database as ids name it (case-folded), or null when the sync could not work it out. */
  database: string | null;
  /** For an unknown database, the connection reference its objects were reached through; null for a known one. */
  connection: string | null;
  /** The spelling shown: the one the most objects carry. Null with `database`. */
  label: string | null;
  /** Every connection reference whose objects the branch holds, ordered. */
  connections: string[];
  objectCount: number;
  schemas: SchemaBranch[];
}

const byNameCi = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: "base" });

/** The spelling the most objects carry, the ordinal-first on a tie. */
function majority(spellings: Map<string, number>): string | null {
  let best: string | null = null;
  let bestCount = -1;
  for (const [spelling, count] of spellings) {
    if (count > bestCount || (count === bestCount && best !== null && spelling < best)) {
      best = spelling;
      bestCount = count;
    }
  }
  return best;
}

function tally(spellings: Map<string, number>, spelling: string | null, count: number): void {
  if (spelling !== null) {
    spellings.set(spelling, (spellings.get(spelling) ?? 0) + count);
  }
}

interface SchemaAccumulator {
  schema: string | null;
  spellings: Map<string, number>;
  objectCount: number;
  kinds: Map<string, number>;
}

interface DatabaseAccumulator {
  database: string | null;
  connection: string | null;
  spellings: Map<string, number>;
  connections: Set<string>;
  objectCount: number;
  schemas: Map<string, SchemaAccumulator>;
}

/** Folds the per-kind rows into the Databases branch. Kinds are ordered by `compareKinds`; everything else by name. */
export function foldDatabases(rows: SchemaKindCount[], compareKinds: (a: string, b: string) => number): DatabaseBranch[] {
  const databases = new Map<string, DatabaseAccumulator>();
  for (const row of rows) {
    const database = foldName(row.database);
    // A known database is one branch whichever connection reached it; an unknown one is a branch per connection.
    const branchKey = database === null ? `~|${row.serverRef.toLowerCase()}` : `db|${database}`;
    let branch = databases.get(branchKey);
    if (branch === undefined) {
      branch = {
        database,
        connection: database === null ? row.serverRef : null,
        spellings: new Map(),
        connections: new Set(),
        objectCount: 0,
        schemas: new Map(),
      };
      databases.set(branchKey, branch);
    }
    tally(branch.spellings, row.database, row.objectCount);
    branch.connections.add(row.serverRef);
    branch.objectCount += row.objectCount;

    const schema = foldName(row.schema);
    const schemaKey = schema === null ? "~" : `s|${schema}`;
    let schemaBranch = branch.schemas.get(schemaKey);
    if (schemaBranch === undefined) {
      schemaBranch = { schema, spellings: new Map(), objectCount: 0, kinds: new Map() };
      branch.schemas.set(schemaKey, schemaBranch);
    }
    tally(schemaBranch.spellings, row.schema, row.objectCount);
    schemaBranch.objectCount += row.objectCount;
    schemaBranch.kinds.set(row.kind, (schemaBranch.kinds.get(row.kind) ?? 0) + row.objectCount);
  }

  const branches: DatabaseBranch[] = [...databases.values()].map((branch) => ({
    database: branch.database,
    connection: branch.connection,
    label: majority(branch.spellings),
    connections: [...branch.connections].sort(byNameCi),
    objectCount: branch.objectCount,
    schemas: [...branch.schemas.values()]
      .map((schema) => ({
        schema: schema.schema,
        label: majority(schema.spellings),
        objectCount: schema.objectCount,
        kinds: [...schema.kinds.entries()]
          .map(([kind, objectCount]) => ({ kind, objectCount }))
          .sort((a, b) => compareKinds(a.kind, b.kind)),
      }))
      // A schema the sync could not work out lists after the named ones.
      .sort((a, b) => (a.label === null) !== (b.label === null)
        ? (a.label === null ? 1 : -1)
        : byNameCi(a.label ?? "", b.label ?? "")),
  }));

  return branches.sort((a, b) => {
    if ((a.database === null) !== (b.database === null)) {
      return a.database === null ? 1 : -1;
    }
    return a.database === null
      ? byNameCi(a.connection ?? "", b.connection ?? "")
      : byNameCi(a.label ?? "", b.label ?? "");
  });
}

/** The filters that list exactly the objects a kind folder counts: the same database and schema, or the same unknown. */
export interface KindFolderQuery {
  serverRef?: string;
  database?: string;
  databaseUnknown?: true;
  schema?: string;
  schemaUnknown?: true;
  kind: string;
}

export function kindFolderQuery(database: DatabaseBranch, schema: SchemaBranch, kind: string): KindFolderQuery {
  return {
    ...(database.database === null
      ? { serverRef: database.connection ?? undefined, databaseUnknown: true as const }
      : { database: database.database }),
    ...(schema.schema === null ? { schemaUnknown: true as const } : { schema: schema.schema }),
    kind,
  };
}
