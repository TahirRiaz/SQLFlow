import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import Editor from "@monaco-editor/react";
import type { editor } from "monaco-editor";
import { Copy, WandSparkles } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { CodeLanguage } from "@/lib/codeLanguage";
import { prettyPrintSql } from "@/lib/sql";
import { defineSqlflowTheme, sqlflowEditorTheme } from "@/lib/monacoTheme";
import { useThemeMode } from "../theme/ThemeModeContext";
import type { Monaco } from "../lib/monacoSetup";
import "../lib/monacoSetup";
import { markFlowModel, refreshDiagnostics, registerSqlflowYamlProviders } from "../lib/lsp/sqlflowLsp";
import { useExplain } from "../features/chat/ExplainContext";
import { ExplainIcon } from "../features/chat/ExplainIcon";
import type { ExplainCode } from "../features/chat/explain";

interface CodeViewProps {
  value: string;
  language: CodeLanguage;
  height?: number | string;
  /**
   * Fill mode: the view grows to the free space of a flex column parent (a sheet body, a panel) instead of taking
   * `height`, and never shrinks below a readable floor. `height` is ignored when set.
   */
  fill?: boolean;
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
  /**
   * An optional label on the left of the toolbar, alongside the format/copy buttons on the right.
   * Every catalog/run viewer omits it (the surrounding page already says what the code is); the
   * chat transcript passes the fenced block's language, so the language name and its buttons sit
   * in the one row a finished block gets, rather than a second header bar drawn above this one.
   */
  label?: string;
  /** Extra buttons in the same toolbar row as the label, to the left of the built-in format/copy
   * buttons (a chat "Run" affordance is the first caller). Every other CodeView surface omits it. */
  extraActions?: ReactNode;
  /**
   * "Explain": the assistant explains the selected lines, or the whole document when nothing is selected, from the
   * toolbar or the editor's context menu. On by default wherever the assistant is configured; the chat's own code
   * blocks pass false, since they are the assistant's answer already.
   */
  explain?: boolean;
  "data-testid"?: string;
}

/** The code an explanation is asked about: the selected lines of the editor, or its whole document. */
function explainedCode(
  editorInstance: editor.IStandaloneCodeEditor | null, fallback: string, language: string,
): ExplainCode {
  const model = editorInstance?.getModel() ?? null;
  if (model === null) {
    return { code: fallback, language, lines: null, totalLines: fallback.split("\n").length };
  }

  const totalLines = model.getLineCount();
  const selection = editorInstance?.getSelection() ?? null;
  if (selection === null || selection.isEmpty()) {
    return { code: model.getValue(), language, lines: null, totalLines };
  }

  // A selection dragged to the start of the next line ends on the line above it.
  const end = selection.endColumn === 1 && selection.endLineNumber > selection.startLineNumber
    ? selection.endLineNumber - 1
    : selection.endLineNumber;
  return { code: model.getValueInRange(selection), language, lines: { start: selection.startLineNumber, end }, totalLines };
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
  value, language, height = 480, fill = false, lsp = true, readOnly = true, onChange, label, extraActions,
  explain = true, "data-testid": testId,
}: CodeViewProps) {
  const { mode } = useThemeMode();
  const monacoRef = useRef<Monaco | null>(null);
  const editorRef = useRef<editor.IStandaloneCodeEditor | null>(null);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const isSql = language === "sql";
  const lspOn = lsp && language === "yaml";
  const [formatSqlOn, setFormatSqlOn] = useState(true);
  const explainer = useExplain();
  const canExplain = explain && explainer !== null;
  // The context menu's action is registered once, when the editor mounts, so it calls through this ref, and its
  // context key shows it only while explaining is offered.
  const explainRef = useRef<() => void>(() => undefined);
  const canExplainRef = useRef(canExplain);
  const explainKeyRef = useRef<editor.IContextKey<boolean> | null>(null);

  const handleBeforeMount = useCallback((monaco: Monaco) => {
    monacoRef.current = monaco;
    defineSqlflowTheme(monaco, mode);
    if (lspOn) {
      registerSqlflowYamlProviders(monaco);
    }
  }, [mode, lspOn]);

  const handleMount = useCallback((editorInstance: editor.IStandaloneCodeEditor, monaco: Monaco) => {
    editorRef.current = editorInstance;
    explainKeyRef.current = editorInstance.createContextKey("sqlflowExplain", canExplainRef.current);
    editorInstance.addAction({
      id: "sqlflow.explain",
      label: "Explain this",
      precondition: "sqlflowExplain",
      contextMenuGroupId: "navigation",
      contextMenuOrder: 0,
      run: () => explainRef.current(),
    });
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

  const handleExplain = useCallback(() => {
    if (explainer !== null) {
      explainer.explainCode(explainedCode(editorRef.current, displayValue, language), rootRef.current);
    }
  }, [explainer, displayValue, language]);

  useEffect(() => {
    explainRef.current = handleExplain;
    canExplainRef.current = canExplain;
    explainKeyRef.current?.set(canExplain);
  }, [handleExplain, canExplain]);

  return (
    <div
      ref={rootRef}
      data-testid={testId ?? "code-view"}
      className={cn("overflow-hidden rounded-lg border border-border", fill && "flex min-h-80 flex-1 flex-col")}
    >
      <div
        className={cn(
          "flex items-center border-b border-border bg-muted/50 px-1 py-0.5",
          label ? "justify-between" : "justify-end gap-0.5",
        )}
      >
        {label && (
          <span className="px-2 text-xs font-medium text-muted-foreground lowercase">{label}</span>
        )}
        <div className="flex items-center gap-0.5">
          {extraActions}
          {canExplain && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  variant="ghost"
                  size="xs"
                  onClick={handleExplain}
                  data-testid="code-view-explain"
                  className="text-muted-foreground hover:text-foreground"
                >
                  <ExplainIcon />
                  Explain
                </Button>
              </TooltipTrigger>
              <TooltipContent>The assistant explains the selected lines, or all of it when nothing is selected</TooltipContent>
            </Tooltip>
          )}
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
      </div>
      <Editor
        value={displayValue}
        language={language}
        height={fill ? "100%" : height}
        wrapperProps={fill ? { className: "min-h-0 flex-1" } : undefined}
        beforeMount={handleBeforeMount}
        onMount={handleMount}
        onChange={readOnly || !onChange ? undefined : (text) => onChange(text ?? "")}
        theme={sqlflowEditorTheme}
        options={{
          readOnly,
          // Re-measure when the container resizes: a filled view follows its sheet or panel, a fixed one its width.
          automaticLayout: true,
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
