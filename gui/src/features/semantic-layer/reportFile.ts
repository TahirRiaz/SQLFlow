// The report name rule the upload dialog checks before a round trip. Mirrors ReportSpecs.ReportFileProblem on the
// server, which has the last word. Kept free of React and of the app's path aliases so the unit suite (gui/unit) can
// run it under Node directly.

export const MAX_REPORT_FILE_LENGTH = 260;

/** Why a report name cannot key the report's rows, or null. A name is a relative, forward-slashed path: it is joined
 * into every row key with '#', so it may not hold one, nor a backslash, a colon (a drive or a stream), or a control
 * character, and none of its segments may be empty, '.' or '..'. */
export function reportFileProblem(value: string): string | null {
  if (value.trim() === "") {
    return "A report name is required.";
  }
  if (value !== value.trim()) {
    return "The name may not start or end with a space.";
  }
  if (value.length > MAX_REPORT_FILE_LENGTH) {
    return `The name is longer than ${MAX_REPORT_FILE_LENGTH} characters.`;
  }
  if (/[#\\:\u0000-\u001f\u007f-\u009f]/.test(value)) {
    return "The name may not contain '#', ':', a backslash, or a control character.";
  }
  if (value.startsWith("/") || value.split("/").some((part) => part === "" || part === "." || part === "..")) {
    return "The name must be a relative path with no empty, '.' or '..' parts.";
  }
  return null;
}
