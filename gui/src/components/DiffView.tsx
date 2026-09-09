import { useCallback, useEffect, useRef, type ReactNode } from "react";
import { DiffEditor, type Monaco } from "@monaco-editor/react";
import { Columns2, Copy, Rows3 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { defineSqlflowTheme, sqlflowEditorTheme } from "@/lib/monacoTheme";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { useThemeMode } from "../theme/ThemeModeContext";
import "../lib/monacoSetup";

interface DiffViewProps {
  /** The earlier text: the left pane side by side, the removed lines inline. */
  original: string;
  /** The later text: the right pane side by side, the added lines inline. */
  modified: string;
  language: "yaml" | "json" | "sql" | "plaintext";
  height?: number | string;
  /** What the two sides ARE (a commit, a date). Rendered in the toolbar, left of the view controls. */
  caption?: ReactNode;
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
  original, modified, language, height = 480, caption, "data-testid": testId,
}: DiffViewProps) {
  const { mode } = useThemeMode();
  const monacoRef = useRef<Monaco | null>(null);
  const [sideBySide, setSideBySide] = useLocalStorageState("sqlflow.diff-view.side-by-side", true);

  const handleBeforeMount = useCallback((monaco: Monaco) => {
    monacoRef.current = monaco;
    defineSqlflowTheme(monaco, mode);
  }, [mode]);

  // The design tokens change when the app toggles; re-define and re-apply so the comparison tracks the theme.
  useEffect(() => {
    if (monacoRef.current) {
      defineSqlflowTheme(monacoRef.current, mode);
      monacoRef.current.editor.setTheme(sqlflowEditorTheme);
    }
  }, [mode]);

  const handleCopy = useCallback(() => {
    void navigator.clipboard
      .writeText(modified)
      .then(() => toast.success("Copied to clipboard"))
      .catch(() => toast.error("Could not copy to clipboard"));
  }, [modified]);

  return (
    <div
      data-testid={testId ?? "diff-view"}
      className="overflow-hidden rounded-lg border border-border"
    >
      <div className="flex items-center gap-2 border-b border-border bg-muted/50 px-2 py-0.5">
        {caption !== undefined && <div className="min-w-0 flex-1 truncate text-xs">{caption}</div>}
        <div className={cn("flex items-center gap-0.5", caption === undefined && "ml-auto")}>
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
                aria-label="Copy the later version to clipboard"
                onClick={handleCopy}
                data-testid="diff-view-copy"
              >
                <Copy />
              </Button>
            </TooltipTrigger>
            <TooltipContent>Copy the later version</TooltipContent>
          </Tooltip>
        </div>
      </div>
      <DiffEditor
        original={original}
        modified={modified}
        language={language}
        height={height}
        beforeMount={handleBeforeMount}
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
        }}
      />
    </div>
  );
}
