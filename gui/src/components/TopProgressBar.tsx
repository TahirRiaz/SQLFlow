/**
 * The slim indeterminate progress bar shown while a lazy route chunk loads (DESIGN.md 8.4): a 2px
 * primary sweep pinned under the title bar, never a blank screen or a centered spinner.
 */
export function TopProgressBar() {
  return (
    <div className="fixed inset-x-0 top-0 z-50 h-0.5 overflow-hidden" role="progressbar" aria-label="Loading page">
      <div className="h-full w-2/5 animate-[top-progress_1.2s_ease-in-out_infinite] rounded-r bg-primary" />
    </div>
  );
}
