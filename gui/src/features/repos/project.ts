/**
 * The project a pipeline belongs to: the ROOT folder inside the repo that holds a source's pipelines. A repo is a
 * git repository; a project is a top-level folder within it (e.g. "sales", "erp"); the flows in that folder are the
 * project's pipelines. Derived from the flow's repo-relative path. A flow at the repo root has no folder and
 * belongs to the "(root)" project.
 */
export function projectOf(relativePath: string): string {
  const slash = relativePath.indexOf("/");
  return slash > 0 ? relativePath.slice(0, slash) : "(root)";
}
