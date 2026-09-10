// Helpers over a pipeline's stored definition JSON (the parsed flow document the catalog sync serializes).

/** The derived health-check flow an ing document's embedded healthCheck: block declares, or null. The catalog
 * stores the parsed document as JSON, so the derived flow's name is read straight from the definition. */
export function embeddedHealthCheckName(definitionJson: string): string | null {
  try {
    const parsed = JSON.parse(definitionJson) as { document?: { healthCheck?: { sysAlias?: string } } };
    return parsed.document?.healthCheck?.sysAlias ?? null;
  } catch {
    return null;
  }
}
