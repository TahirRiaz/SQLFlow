/** The scope a key-detection deep link carries: which object on which datasource to prefill. */
export interface KeyDetectionScope {
  reference: string;
  kind?: string | null;
  database?: string | null;
  schema?: string | null;
  objectName?: string | null;
}

/**
 * Builds the Key detection page URL for one object, shared by every entry point (the inspector drawer, the
 * page's own state writes) so the parameter names can never drift apart.
 */
export function keyDetectionPath(scope: KeyDetectionScope): string {
  const params = new URLSearchParams();
  params.set("ref", scope.reference);
  if (scope.kind) {
    params.set("kind", scope.kind);
  }

  if (scope.database) {
    params.set("db", scope.database);
  }

  if (scope.schema) {
    params.set("schema", scope.schema);
  }

  if (scope.objectName) {
    params.set("object", scope.objectName);
  }

  return `/key-detection?${params.toString()}`;
}
