"use client";

import "@assistant-ui/react-markdown/styles/dot.css";

import {
  type CodeHeaderProps,
  type SyntaxHighlighterProps,
  MarkdownTextPrimitive,
  unstable_memoizeMarkdownComponents as memoizeMarkdownComponents,
  useIsMarkdownCodeBlock,
} from "@assistant-ui/react-markdown";
import { useAuiState } from "@assistant-ui/react";
import remarkGfm from "remark-gfm";
import { type FC, memo, useState } from "react";
import { CheckIcon, CopyIcon } from "lucide-react";

import { TooltipIconButton } from "@/components/assistant-ui/tooltip-icon-button";
import { CodeView } from "@/components/CodeView";
import { cn } from "@/lib/utils";
import { useSqlRun } from "../../features/chat/SqlRunPanel";
import { QueryResultEmbed } from "../../features/chat/QueryResultEmbed";

const MarkdownTextImpl = () => {
  return (
    <MarkdownTextPrimitive
      remarkPlugins={[remarkGfm]}
      className="aui-md"
      components={defaultComponents}
      componentsByLanguage={componentsByLanguage}
      defer
    />
  );
};

export const MarkdownText = memo(MarkdownTextImpl);

/**
 * SQL / YAML / JSON blocks graduate into the workbench code viewer (DESIGN.md 7.6: YAML and SQL
 * always render in CodeView) once the answer has finished streaming; while tokens are still
 * arriving the default lightweight block renders, so Monaco never re-mounts per token.
 */
function makeCodeViewHighlighter(language: "sql" | "yaml" | "json"): FC<SyntaxHighlighterProps> {
  return function CodeViewHighlighter({ components, code }: SyntaxHighlighterProps) {
    const isStreaming = useAuiState((s) => s.message.status?.type === "running");
    if (isStreaming) {
      const { Pre, Code } = components;
      return (
        <Pre>
          <Code>{code}</Code>
        </Pre>
      );
    }
    const trimmed = code.replace(/\n$/, "");
    const height = Math.min(400, Math.max(60, trimmed.split("\n").length * 18 + 26));
    // CodeView draws its own full toolbar (language label plus its copy/format buttons in one
    // row), so nothing more is needed here; CodeHeader below skips drawing a second one on top of
    // it for exactly this case.
    return (
      <div className="aui-md-codeview mb-3">
        {/* Chat YAML is illustrative, not necessarily a flow document; the flow LSP stays off. */}
        <CodeView value={trimmed} language={language} height={height} lsp={false} label={language} />
      </div>
    );
  };
}

/**
 * A finished SQL block additionally carries a Run affordance in the same toolbar row as its
 * format/copy buttons (CodeView's `extraActions` slot), and the result it opens renders as a
 * sibling below CodeView. `useSqlRun` owns that button/panel pair as one piece of state (the panel
 * needs to know what the button did), which is why this is its own component rather than reusing
 * `makeCodeViewHighlighter("sql")`.
 */
const SqlCodeViewHighlighter: FC<SyntaxHighlighterProps> = ({ components, code }) => {
  const isStreaming = useAuiState((s) => s.message.status?.type === "running");
  const trimmed = code.replace(/\n$/, "");
  // The hook is called unconditionally (rules of hooks): it reads whether Run is enabled itself and
  // hands back null pieces when it is not, same as while still streaming.
  const { button, panel } = useSqlRun(trimmed);
  if (isStreaming) {
    const { Pre, Code } = components;
    return (
      <Pre>
        <Code>{code}</Code>
      </Pre>
    );
  }
  const height = Math.min(400, Math.max(60, trimmed.split("\n").length * 18 + 26));
  return (
    <div className="aui-md-codeview mb-3 flex flex-col gap-2">
      <CodeView value={trimmed} language="sql" height={height} lsp={false} label="sql" extraActions={button} />
      {panel}
    </div>
  );
};

/**
 * A `query-result` block carries only the compute task id of a query that already ran; once the answer has
 * finished streaming it renders that task's stored rows as a chart or table (QueryResultEmbed). While
 * streaming, nothing is drawn, since a half-written id is not an id.
 */
const QueryResultHighlighter: FC<SyntaxHighlighterProps> = ({ code }) => {
  const isStreaming = useAuiState((s) => s.message.status?.type === "running");
  return isStreaming ? null : <QueryResultEmbed code={code} />;
};

const componentsByLanguage = {
  sql: { SyntaxHighlighter: SqlCodeViewHighlighter },
  "query-result": { SyntaxHighlighter: QueryResultHighlighter },
  yaml: { SyntaxHighlighter: makeCodeViewHighlighter("yaml") },
  yml: { SyntaxHighlighter: makeCodeViewHighlighter("yaml") },
  json: { SyntaxHighlighter: makeCodeViewHighlighter("json") },
};

/** Languages whose finished block graduates into {@link CodeView}, which draws its own toolbar
 * (language label plus copy/format buttons in one row). This header must render nothing at all
 * for those once finished, or the block would carry two header rows with two copy buttons. */
const CODE_VIEW_LANGUAGES = new Set(Object.keys(componentsByLanguage));

const CodeHeader: FC<CodeHeaderProps> = ({ language, code }) => {
  const { isCopied, copyToClipboard } = useCopyToClipboard();
  const isStreaming = useAuiState((s) => s.message.status?.type === "running");
  // While still streaming, a graduating language renders as the plain <pre>/<code> block below
  // (see makeCodeViewHighlighter), which has no toolbar of its own, so this header is the only
  // label/copy affordance and must stay; once finished, CodeView's own toolbar takes over.
  // A query-result block is a chart, never code: it has no header to label or copy, streaming or not.
  if (language === "query-result" || (language !== undefined && CODE_VIEW_LANGUAGES.has(language) && !isStreaming)) {
    return null;
  }
  const onCopy = () => {
    if (!code || isCopied) return;
    copyToClipboard(code);
  };

  return (
    <div className="aui-code-header-root border-border/50 bg-muted/50 mt-3 flex items-center justify-between rounded-t-md border border-b-0 px-3.5 py-1.5 text-xs">
      <span className="aui-code-header-language text-muted-foreground font-medium lowercase">
        {language}
      </span>
      <TooltipIconButton tooltip="Copy" onClick={onCopy}>
        {!isCopied && (
          <CopyIcon className="animate-in zoom-in-75 fade-in duration-150" />
        )}
        {isCopied && (
          <CheckIcon className="animate-in zoom-in-50 fade-in duration-200 ease-out" />
        )}
      </TooltipIconButton>
    </div>
  );
};

const useCopyToClipboard = ({
  copiedDuration = 3000,
}: {
  copiedDuration?: number;
} = {}) => {
  const [isCopied, setIsCopied] = useState<boolean>(false);

  const copyToClipboard = (value: string) => {
    if (!value || typeof navigator === "undefined" || !navigator.clipboard) {
      return;
    }

    navigator.clipboard.writeText(value).then(
      () => {
        setIsCopied(true);
        setTimeout(() => setIsCopied(false), copiedDuration);
      },
      () => {},
    );
  };

  return { isCopied, copyToClipboard };
};

const defaultComponents = memoizeMarkdownComponents({
  h1: ({ className, ...props }) => (
    <h1
      className={cn(
        "aui-md-h1 mt-5 mb-2 scroll-m-20 text-xl font-semibold first:mt-0 last:mb-0",
        className,
      )}
      {...props}
    />
  ),
  h2: ({ className, ...props }) => (
    <h2
      className={cn(
        "aui-md-h2 mt-5 mb-2 scroll-m-20 text-lg font-semibold first:mt-0 last:mb-0",
        className,
      )}
      {...props}
    />
  ),
  h3: ({ className, ...props }) => (
    <h3
      className={cn(
        "aui-md-h3 mt-4 mb-1.5 scroll-m-20 text-base font-semibold first:mt-0 last:mb-0",
        className,
      )}
      {...props}
    />
  ),
  h4: ({ className, ...props }) => (
    <h4
      className={cn(
        "aui-md-h4 mt-3.5 mb-1 scroll-m-20 text-base font-medium first:mt-0 last:mb-0",
        className,
      )}
      {...props}
    />
  ),
  h5: ({ className, ...props }) => (
    <h5
      className={cn(
        "aui-md-h5 mt-3 mb-1 text-sm font-semibold first:mt-0 last:mb-0",
        className,
      )}
      {...props}
    />
  ),
  h6: ({ className, ...props }) => (
    <h6
      className={cn(
        "aui-md-h6 mt-3 mb-1 text-sm font-medium first:mt-0 last:mb-0",
        className,
      )}
      {...props}
    />
  ),
  p: ({ className, ...props }) => (
    <p
      className={cn(
        "aui-md-p my-3 leading-relaxed first:mt-0 last:mb-0",
        className,
      )}
      {...props}
    />
  ),
  a: ({ className, ...props }) => (
    <a
      className={cn(
        "aui-md-a text-primary hover:text-primary/80 underline underline-offset-2",
        className,
      )}
      {...props}
    />
  ),
  blockquote: ({ className, ...props }) => (
    <blockquote
      className={cn(
        "aui-md-blockquote border-muted-foreground/30 text-muted-foreground my-3 border-s-2 ps-4",
        className,
      )}
      {...props}
    />
  ),
  ul: ({ className, ...props }) => (
    <ul
      className={cn(
        "aui-md-ul marker:text-muted-foreground my-3 ms-5 list-disc [&>li]:mt-1",
        className,
      )}
      {...props}
    />
  ),
  ol: ({ className, ...props }) => (
    <ol
      className={cn(
        "aui-md-ol marker:text-muted-foreground my-3 ms-5 list-decimal [&>li]:mt-1",
        className,
      )}
      {...props}
    />
  ),
  hr: ({ className, ...props }) => (
    <hr
      className={cn("aui-md-hr border-muted-foreground/20 my-3", className)}
      {...props}
    />
  ),
  table: ({ className, ...props }) => (
    <table
      className={cn(
        "aui-md-table my-3 w-full border-separate border-spacing-0 overflow-y-auto",
        className,
      )}
      {...props}
    />
  ),
  th: ({ className, ...props }) => (
    <th
      className={cn(
        "aui-md-th bg-muted px-3 py-1.5 text-start font-medium first:rounded-ss-lg last:rounded-se-lg [[align=center]]:text-center [[align=right]]:text-right",
        className,
      )}
      {...props}
    />
  ),
  td: ({ className, ...props }) => (
    <td
      className={cn(
        "aui-md-td border-muted-foreground/20 border-s border-b px-3 py-1.5 text-start last:border-e [[align=center]]:text-center [[align=right]]:text-right",
        className,
      )}
      {...props}
    />
  ),
  tr: ({ className, ...props }) => (
    <tr
      className={cn(
        "aui-md-tr m-0 border-b p-0 first:border-t [&:last-child>td:first-child]:rounded-es-lg [&:last-child>td:last-child]:rounded-ee-lg",
        className,
      )}
      {...props}
    />
  ),
  li: ({ className, ...props }) => (
    <li className={cn("aui-md-li leading-relaxed", className)} {...props} />
  ),
  strong: ({ className, ...props }) => (
    <strong
      className={cn("aui-md-strong font-semibold", className)}
      {...props}
    />
  ),
  sup: ({ className, ...props }) => (
    <sup
      className={cn("aui-md-sup [&>a]:text-xs [&>a]:no-underline", className)}
      {...props}
    />
  ),
  pre: ({ className, ...props }) => (
    <pre
      className={cn(
        "aui-md-pre border-border/50 bg-muted/30 overflow-x-auto rounded-t-none rounded-b-md border border-t-0 p-3.5 font-mono text-[12px] leading-5",
        className,
      )}
      {...props}
    />
  ),
  code: function Code({ className, ...props }) {
    const isCodeBlock = useIsMarkdownCodeBlock();
    return (
      <code
        className={cn(
          !isCodeBlock &&
            "aui-md-inline-code bg-muted rounded-md px-1.5 py-0.5 font-mono text-[0.85em]",
          className,
        )}
        {...props}
      />
    );
  },
  CodeHeader,
});
