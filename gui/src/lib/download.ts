// Handing the user a document the GUI holds (a trace, a generated flow, a drawn graph) as a file in the browser's
// downloads, rather than as a view of it.

/** The longest file name stem a download is given, well inside every file system's 255-character limit. */
const MAX_STEM = 150;

/**
 * A file name built from the parts that identify what was downloaded (a flow, a run id, a subject), joined with
 * dashes, with `extension` appended. Anything other than a letter, a digit, a dot, a dash or an underscore becomes an
 * underscore, so a URL or a path never yields a separator or a character Windows refuses; the stem is capped, and
 * never starts or ends with a dot or a dash. Empty and missing parts are skipped; `fallback` names a download whose
 * parts leave nothing.
 */
export function downloadFileName(
  parts: ReadonlyArray<string | null | undefined>,
  extension: string,
  fallback = "download",
): string {
  const joined = parts
    .filter((part): part is string => typeof part === "string" && part.trim() !== "")
    .map((part) => part.trim().replace(/[^\p{L}\p{N}._-]+/gu, "_"))
    .join("-");
  // Capped by code point, so a letter outside the basic plane is never cut in half.
  const stem = Array.from(joined).slice(0, MAX_STEM).join("").replace(/^[._-]+|[._-]+$/g, "");
  return `${stem === "" ? fallback : stem}.${extension}`;
}

/**
 * Saves `text` as a UTF-8 file named `fileName` through the browser's download, the way a link with a `download`
 * attribute does. The object URL is released once the click has been handled, on the next tick rather than during
 * it, so the download has taken hold of the blob first.
 */
export function downloadText(fileName: string, text: string, type = "text/plain"): void {
  const url = URL.createObjectURL(new Blob([text], { type: `${type};charset=utf-8` }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  try {
    anchor.click();
  } finally {
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
}
