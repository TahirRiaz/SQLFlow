import { schemaChangeApi, type SchemaChangeQuery } from "../../api/endpoints";
import type { SchemaChange } from "../../api/types";
import { fetchEveryPage, type PageSweep } from "../../lib/fetchEveryPage";

/** The most changes the tree pulls into the browser at once. A schema history is normally small (a quiet estate
 * records nothing at all), but a first snapshot of an untracked database records every object it finds, so the
 * sweep is bounded and the page says when it truncated rather than fetching without limit. */
const FETCH_CAP = 4000;

export type SchemaChangeFetch = PageSweep<SchemaChange>;

/**
 * Pull every schema change matching the filters by paging until the window is exhausted or the cap is hit. The
 * tree groups by database and schema, so a single page would silently truncate mid-alphabet and whole databases
 * would be missing from the tree with nothing on screen to say so.
 */
export function fetchSchemaChanges(filters: SchemaChangeQuery): Promise<SchemaChangeFetch> {
  return fetchEveryPage((page, pageSize) => schemaChangeApi.list({ ...filters, page, pageSize }), FETCH_CAP);
}
