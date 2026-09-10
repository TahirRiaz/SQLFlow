import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import Editor, { type Monaco } from "@monaco-editor/react";
import type { editor } from "monaco-editor";
import { Copy, WandSparkles } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { prettyPrintSql } from "@/lib/sql";
import { defineSqlflowTheme, sqlflowEditorTheme } from "@/lib/monacoTheme";
import { useThemeMode } from "../theme/ThemeModeContext";
import "../lib/monacoSetup";
import { markFlowModel, refreshDiagnostics, registerSqlflowYamlProviders } from "../lib/lsp/sqlflowLsp";

interface CodeViewProps {
  value: string;
  language: "yaml" | "json" | "sql" | "plaintext";
  height?: number | string;
  /**
   * SQLFlow flow-YAML language intelligence (hover docs per attribute, census-driven colouring, and validation
   * squiggles), served by the wasm analysis engine. ON by default for `language: "yaml"` (every YAML shown in the
   * product is a flow document); pass false for a YAML surface that is not a flow document.
   */
  lsp?: boolean;
  /**
   * Editable mode: the editor accepts typing and reports changes through `onChange` (the debugger's YAML input).
   * Defaults to read-only, the mode every catalog/code viewer uses (git is the authoring surface).
   */
  readOnly?: boolean;
  onChange?: (value: string) => void;
  "data-testid"?: string;
}

/**
 * The Monaco surface for YAML documents, definition JSON, generated SQL, and plain-text trace payloads: syntax
 * highlight, folding, and in-editor search, themed with the workbench tokens in both light and dark. Flow-YAML
 * language intelligence (hover docs, census colouring, diagnostics) is on by default for YAML. Read-only by
 * default (git is the authoring surface); the debugger opts into editable mode via `readOnly={false}` +
 * `onChange`. For SQL, a toolbar offers pretty-printing (on by default, since captured statements arrive as
 * unformatted single-line blobs) and copy-to-clipboard of whatever is currently shown.
 */
export function CodeView({
  value, language, height = 480, lsp = true, readOnly = true, onChange, "data-testid": testId,
}: CodeViewProps) {
  const { mode } = useThemeMode();
  const monacoRef = useRef<Monaco | null>(null);
  const editorRef = useRef<editor.IStandaloneCodeEditor | null>(null);
  const isSql = language === "sql";
  const lspOn = lsp && language === "yaml";
  const [formatSqlOn, setFormatSqlOn] = useState(true);

  const handleBeforeMount = useCallback((monaco: Monaco) => {
    monacoRef.current = monaco;
    defineSqlflowTheme(monaco, mode);
    if (lspOn) {
      registerSqlflowYamlProviders(monaco);
    }
  }, [mode, lspOn]);

  const handleMount = useCallback((editorInstance: editor.IStandaloneCodeEditor, monaco: Monaco) => {
    editorRef.current = editorInstance;
    if (!lspOn) {
      return;
    }
    const model = editorInstance.getModel();
    if (model) {
      markFlowModel(model);
      void refreshDiagnostics(monaco, model);
    }
  }, [lspOn]);

  // Re-validate when the document changes (navigating between flows swaps the
  // value on the same read-only model); semantic-token colouring re-pulls on its
  // own. On unmount, drop this model's markers.
  useEffect(() => {
    if (!lspOn) {
      return;
    }
    const monaco = monacoRef.current;
    const model = editorRef.current?.getModel();
    if (monaco && model) {
      markFlowModel(model);
      void refreshDiagnostics(monaco, model);
      return () => monaco.editor.setModelMarkers(model, "sqlflow", []);
    }
  }, [lspOn, value]);

  // The design tokens change when the app toggles; re-define and re-apply so the editor tracks the theme.
  useEffect(() => {
    if (monacoRef.current) {
      defineSqlflowTheme(monacoRef.current, mode);
      monacoRef.current.editor.setTheme(sqlflowEditorTheme);
    }
  }, [mode]);

  const displayValue = useMemo(
    () => (isSql && formatSqlOn ? prettyPrintSql(value) : value),
    [isSql, formatSqlOn, value],
  );

  const handleCopy = useCallback(() => {
    void navigator.clipboard
      .writeText(displayValue)
      .then(() => toast.success("Copied to clipboard"))
      .catch(() => toast.error("Could not copy to clipboard"));
  }, [displayValue]);

  return (
    <div
      data-testid={testId ?? "code-view"}
      className="overflow-hidden rounded-lg border border-border"
    >
      <div className="flex items-center justify-end gap-0.5 border-b border-border bg-muted/50 px-1 py-0.5">
        {isSql && (
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon-xs"
                aria-label={formatSqlOn ? "Show original SQL" : "Format SQL"}
                aria-pressed={formatSqlOn}
                onClick={() => setFormatSqlOn((on) => !on)}
                data-testid="code-view-format"
                className={cn(formatSqlOn && "text-primary hover:text-primary")}
              >
                <WandSparkles />
              </Button>
            </TooltipTrigger>
            <TooltipContent>{formatSqlOn ? "Show original SQL" : "Format SQL"}</TooltipContent>
          </Tooltip>
        )}
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              variant="ghost"
              size="icon-xs"
              aria-label="Copy to clipboard"
              onClick={handleCopy}
              data-testid="code-view-copy"
            >
              <Copy />
            </Button>
          </TooltipTrigger>
          <TooltipContent>Copy</TooltipContent>
        </Tooltip>
      </div>
      <Editor
        value={displayValue}
        language={language}
        height={height}
        beforeMount={handleBeforeMount}
        onMount={handleMount}
        onChange={readOnly || !onChange ? undefined : (text) => onChange(text ?? "")}
        theme={sqlflowEditorTheme}
        options={{
          readOnly,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          wordWrap: "on",
          fontSize: 12,
          fontFamily: "'JetBrains Mono Variable', 'JetBrains Mono', Consolas, monospace",
          padding: { top: 12, bottom: 12 },
          renderLineHighlight: readOnly ? "none" : "line",
          smoothScrolling: true,
          "semanticHighlighting.enabled": true,
        }}
      />
    </div>
  );
}
