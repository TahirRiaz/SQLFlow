import { useCallback, useEffect, useRef, type ReactNode } from "react";
import { DiffEditor } from "@monaco-editor/react";
import type { editor } from "monaco-editor";
import { Columns2, Copy, Rows3 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { defineSqlflowTheme, sqlflowEditorTheme } from "@/lib/monacoTheme";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { useThemeMode } from "../theme/ThemeModeContext";
import type { Monaco } from "../lib/monacoSetup";
import "../lib/monacoSetup";
import { useExplain } from "../features/chat/ExplainContext";
import { ExplainIcon } from "../features/chat/ExplainIcon";

/**
 * What one side of a comparison is: its name, and a detail that tells it apart (a version, a path), shown quieter than
 * the name so the name is what the eye finds.
 */
export type DiffSideLabel = string | { name: string; detail?: string };

function sideName(label: DiffSideLabel): string {
  return typeof label === "string" ? label : label.name;
}

function sideDetail(label: DiffSideLabel): string | null {
  return typeof label === "string" || !label.detail ? null : label.detail;
}

/** The whole label as one line: its name, then its detail. */
function sideText(label: DiffSideLabel): string {
  const detail = sideDetail(label);
  return detail === null ? sideName(label) : `${sideName(label)} ${detail}`;
}

/** The heading over one side: the name in the foreground, its detail muted beside it, cut short rather than wrapped. */
function SideHeading({ label, mark, className, testId }: {
  label: DiffSideLabel;
  /** The patch's sign for the side ("-", "+"), inline, where the sides are lines of one patch rather than two panes. */
  mark?: string;
  className?: string;
  testId: string;
}) {
  const detail = sideDetail(label);
  return (
    <div className={cn("flex min-w-0 items-baseline gap-2", className)} title={sideText(label)} data-testid={testId}>
      {mark !== undefined && <span className="font-mono text-[13px] text-muted-foreground">{mark}</span>}
      <span className="truncate text-[13px] font-semibold text-foreground">{sideName(label)}</span>
      {detail !== null && <span className="flex-1 truncate text-[12px] tabular-nums text-muted-foreground">{detail}</span>}
    </div>
  );
}

interface DiffViewProps {
  /** The left pane side by side, the removed lines inline: the earlier text, unless `sideLabels` names the sides. */
  original: string;
  /** The right pane side by side, the added lines inline: the later text, unless `sideLabels` names the sides. */
  modified: string;
  language: "yaml" | "json" | "sql" | "plaintext";
  height?: number | string;
  /** What the two sides ARE (a commit, a date). Rendered in the toolbar, left of the view controls. */
  caption?: ReactNode;
  /**
   * A name for each side (a version, a release, a file): a heading over its pane side by side, and the removed and
   * added side of the patch inline. For comparisons whose sides need naming one by one, where a caption would not do,
   * and for two documents neither of which is the earlier: Explain and Copy then name the sides by these, not as the
   * earlier and the later version.
   */
  sideLabels?: { original: DiffSideLabel; modified: DiffSideLabel };
  /**
   * Folds the unchanged stretches away, so the differences are what is in view. For long documents that differ in a
   * few places; a folded stretch opens on demand.
   */
  foldUnchanged?: boolean;
  "data-testid"?: string;
}

/**
 * The Monaco comparison surface: two revisions of one document, side by side or as a single inline patch,
 * themed with the workbench tokens exactly as `CodeView` is (DESIGN.md 7.6, one theme for every code surface).
 * Read-only on both sides, since git is the authoring surface.
 *
 * Whitespace is NOT ignored when diffing. Monaco's default trims it, which would quietly hide a re-indent, and
 * in generated DDL a changed indent is a real change to the scripted definition rather than cosmetic noise.
 *
 * The side-by-side / inline choice is remembered across pages and reloads: it tracks how wide the reader's
 * window is and how they prefer to read a patch, neither of which changes between one object and the next.
 */
export function DiffView({
  original, modified, language, height = 480, caption, sideLabels, foldUnchanged = false, "data-testid": testId,
}: DiffViewProps) {
  const { mode } = useThemeMode();
  const monacoRef = useRef<Monaco | null>(null);
  const [sideBySide, setSideBySide] = useLocalStorageState("sqlflow.diff-view.side-by-side", true);
  const rootTestId = testId ?? "diff-view";

  const handleBeforeMount = useCallback((monaco: Monaco) => {
    monacoRef.current = monaco;
    defineSqlflowTheme(monaco, mode);
  }, [mode]);

  // The two documents being compared. The library disposes them before the editor on unmount, which monaco refuses
  // ("TextModel got disposed before DiffEditorWidget model got reset", an uncaught error on every view that unmounts a
  // comparison). So the library keeps them, and they are disposed here once the editor is gone.
  const modelsRef = useRef<editor.IDiffEditorModel | null>(null);
  const handleMount = useCallback((diffEditor: editor.IStandaloneDiffEditor) => {
    modelsRef.current = diffEditor.getModel();
  }, []);
  useEffect(() => () => {
    const models = modelsRef.current;
    modelsRef.current = null;
    if (models !== null) {
      setTimeout(() => {
        for (const model of [models.original, models.modified]) {
          if (!model.isDisposed()) {
            model.dispose();
          }
        }
      }, 0);
    }
  }, []);

  // The design tokens change when the app toggles; re-define and re-apply so the comparison tracks the theme.
  useEffect(() => {
    if (monacoRef.current) {
      defineSqlflowTheme(monacoRef.current, mode);
      monacoRef.current.editor.setTheme(sqlflowEditorTheme);
    }
  }, [mode]);

  // "Explain": the assistant explains what changed between the two versions, wherever the assistant is configured.
  const explainer = useExplain();
  const rootRef = useRef<HTMLDivElement | null>(null);
  const originalText = sideLabels === undefined ? null : sideText(sideLabels.original);
  const modifiedText = sideLabels === undefined ? null : sideText(sideLabels.modified);
  const handleExplain = useCallback(() => {
    const sides = originalText === null || modifiedText === null ? {} : { sides: { original: originalText, modified: modifiedText } };
    explainer?.explainChange({ original, modified, language, ...sides }, rootRef.current);
  }, [explainer, original, modified, language, originalText, modifiedText]);
  // What Copy takes: the right side, named as its heading names it where the sides are named.
  const copied = sideLabels === undefined ? "the later version" : `"${sideName(sideLabels.modified)}"`;

  const handleCopy = useCallback(() => {
    void navigator.clipboard
      .writeText(modified)
      .then(() => toast.success("Copied to clipboard"))
      .catch(() => toast.error("Could not copy to clipboard"));
  }, [modified]);

  return (
    <div
      ref={rootRef}
      data-testid={rootTestId}
      className="overflow-hidden rounded-lg border border-border"
    >
      <div className="flex items-center gap-2 border-b border-border bg-muted/50 px-2 py-0.5">
        {caption !== undefined && <div className="min-w-0 flex-1 truncate text-xs">{caption}</div>}
        <div className={cn("flex items-center gap-0.5", caption === undefined && "ml-auto")}>
          {explainer !== null && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  variant="ghost"
                  size="xs"
                  onClick={handleExplain}
                  data-testid="diff-view-explain"
                  className="text-muted-foreground hover:text-foreground"
                >
                  <ExplainIcon />
                  Explain
                </Button>
              </TooltipTrigger>
              <TooltipContent>The assistant explains what changed between the two versions</TooltipContent>
            </Tooltip>
          )}
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon-xs"
                aria-label={sideBySide ? "Show as one inline patch" : "Show side by side"}
                aria-pressed={sideBySide}
                onClick={() => setSideBySide((on) => !on)}
                data-testid="diff-view-layout"
              >
                {sideBySide ? <Columns2 /> : <Rows3 />}
              </Button>
            </TooltipTrigger>
            <TooltipContent>{sideBySide ? "Show as one inline patch" : "Show side by side"}</TooltipContent>
          </Tooltip>
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon-xs"
                aria-label={`Copy ${copied} to clipboard`}
                onClick={handleCopy}
                data-testid="diff-view-copy"
              >
                <Copy />
              </Button>
            </TooltipTrigger>
            <TooltipContent>{`Copy ${copied}`}</TooltipContent>
          </Tooltip>
        </div>
      </div>
      {sideLabels !== undefined && (sideBySide
        ? (
          <div className="grid grid-cols-2 border-b border-border bg-muted">
            <SideHeading label={sideLabels.original} className="px-3 py-1.5" testId={`${rootTestId}-original`} />
            <SideHeading label={sideLabels.modified} className="border-l border-border px-3 py-1.5" testId={`${rootTestId}-modified`} />
          </div>
        )
        : (
          <div className="flex flex-col gap-0.5 border-b border-border bg-muted px-3 py-1.5">
            <SideHeading label={sideLabels.original} mark="-" testId={`${rootTestId}-original`} />
            <SideHeading label={sideLabels.modified} mark="+" testId={`${rootTestId}-modified`} />
          </div>
        ))}
      <DiffEditor
        original={original}
        modified={modified}
        language={language}
        height={height}
        beforeMount={handleBeforeMount}
        onMount={handleMount}
        keepCurrentOriginalModel
        keepCurrentModifiedModel
        theme={sqlflowEditorTheme}
        options={{
          readOnly: true,
          originalEditable: false,
          renderSideBySide: sideBySide,
          ignoreTrimWhitespace: false,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          wordWrap: "on",
          fontSize: 12,
          fontFamily: "'JetBrains Mono Variable', 'JetBrains Mono', Consolas, monospace",
          padding: { top: 12, bottom: 12 },
          renderLineHighlight: "none",
          smoothScrolling: true,
          hideUnchangedRegions: foldUnchanged
            ? { enabled: true, contextLineCount: 3, minimumLineCount: 6, revealLineCount: 20 }
            : { enabled: false },
        }}
      />
    </div>
  );
}
