import { schemaChangeApi, type SchemaChangeQuery } from "../../api/endpoints";
import type { SchemaChange } from "../../api/types";

/** The API clamps pageSize to PageRequest.MaxPageSize (200); matching it keeps the round-trip count honest. */
const PAGE_SIZE = 200;

/** The most changes the tree pulls into the browser at once. A schema history is normally small (a quiet estate
 * records nothing at all), but a first snapshot of an untracked database records every object it finds, so the
 * sweep is bounded and the page says when it truncated rather than fetching without limit. */
const FETCH_CAP = 4000;

export interface SchemaChangeFetch {
  items: SchemaChange[];
  /** How many rows match on the server, which exceeds items.length once the cap is hit. */
  total: number;
  capped: boolean;
}

/**
 * Pull every schema change matching the filters by paging until the window is exhausted or the cap is hit. The
 * tree groups by database and schema, so a single page would silently truncate mid-alphabet and whole databases
 * would be missing from the tree with nothing on screen to say so.
 */
export async function fetchSchemaChanges(filters: SchemaChangeQuery): Promise<SchemaChangeFetch> {
  const items: SchemaChange[] = [];
  let total = 0;
  let page = 1;
  for (;;) {
    const res = await schemaChangeApi.list({ ...filters, page, pageSize: PAGE_SIZE });
    total = res.total;
    items.push(...res.items);
    if (items.length >= total || res.items.length === 0 || items.length >= FETCH_CAP) {
      break;
    }
    page += 1;
  }
  return { items, total, capped: items.length < total };
}
