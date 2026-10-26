import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { useQuery, type UseQueryResult } from "@tanstack/react-query";
import { ExternalLink, FileCode2, FileWarning, Lock } from "lucide-react";
import { DrawerHeader, LeftDrawer } from "@/components/LeftDrawer";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { languageOfPath } from "@/lib/codeLanguage";
import { isApiError } from "../../api/client";
import { repoApi } from "../../api/endpoints";
import type { RepoFile } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { formatBytes } from "../../lib/time";
import { languageName } from "../chat/explain";

/** The window's width, in pixels, until the person drags it: room for a flow's lines beside the page's tree. */
const DEFAULT_WIDTH = 640;
/** The narrowest it is dragged to. */
const MIN_WIDTH = 380;
const WIDTH_KEY = "sqlflow.codeWindow.width";

/** A file of a repository to show: which repository, its path there, and the pipeline it defines when the catalog
 * imported it, whose page the window then opens. */
export interface OpenedRepoFile {
  repoId: string;
  path: string;
  pipeline: { id: string; name: string } | null;
}

interface RepoFileWindowState {
  /** The file shown, while the page it was opened on is the one in view; null shows nothing. */
  file: OpenedRepoFile | null;
  open: (file: OpenedRepoFile) => void;
  close: () => void;
  maximized: boolean;
  setMaximized: (maximized: boolean) => void;
}

const RepoFileWindowContext = createContext<RepoFileWindowState | null>(null);

/**
 * The code window: a repository file's code, syntax-coloured, in a drawer that slides in from the left over the page
 * (RepoFileDrawer, placed by the frame), its edge dragged to make it wider or narrower, maximized to read it alone. A page
 * opens a file in it (`useRepoFileWindow`); the window belongs to that page, so it is out of view on any other and back
 * when its page is, and a file of a pipeline offers the pipeline's page. Explain, asked of the code, opens over it.
 */
export function RepoFileWindowProvider({ children }: { children: ReactNode }) {
  const { pathname } = useLocation();
  const [opened, setOpened] = useState<{ file: OpenedRepoFile; pathname: string } | null>(null);
  const [maximized, setMaximized] = useState(false);

  const open = useCallback((file: OpenedRepoFile) => setOpened({ file, pathname }), [pathname]);
  const close = useCallback(() => {
    setOpened(null);
    setMaximized(false);
  }, []);

  const file = opened !== null && opened.pathname === pathname ? opened.file : null;
  const state = useMemo(
    (): RepoFileWindowState => ({ file, open, close, maximized, setMaximized }),
    [file, open, close, maximized],
  );
  return <RepoFileWindowContext.Provider value={state}>{children}</RepoFileWindowContext.Provider>;
}

/** Opens a repository file in the code window, and says which file it shows. */
export function useRepoFileWindow(): { open: (file: OpenedRepoFile) => void; shown: OpenedRepoFile | null } {
  const state = useContext(RepoFileWindowContext);
  if (state === null) {
    throw new Error("useRepoFileWindow needs a RepoFileWindowProvider above it.");
  }

  return { open: state.open, shown: state.file };
}

/** The code window over the page, placed by the frame where Explain's drawer is, one layer under it. */
export function RepoFileDrawer({ maximizedClassName }: { maximizedClassName: string }) {
  const state = useContext(RepoFileWindowContext);
  if (state === null || state.file === null) {
    return null;
  }

  const { file, maximized, setMaximized, close } = state;
  return (
    <LeftDrawer
      label="Code"
      widthKey={WIDTH_KEY}
      defaultWidth={DEFAULT_WIDTH}
      minWidth={MIN_WIDTH}
      maximized={maximized}
      maximizedClassName={maximizedClassName}
      layerClassName="z-20"
      testId="code-window"
    >
      <RepoFileSurface key={`${file.repoId}:${file.path}`} file={file} maximized={maximized} onMaximizedChange={setMaximized} onClose={close} />
    </LeftDrawer>
  );
}

/** The file's header (its name, path and the pipeline it defines) and its code, as the repository holds it. */
function RepoFileSurface({ file, maximized, onMaximizedChange, onClose }: {
  file: OpenedRepoFile;
  maximized: boolean;
  onMaximizedChange: (maximized: boolean) => void;
  onClose: () => void;
}) {
  const navigate = useNavigate();
  const query = useQuery({
    queryKey: ["repos", "file", file.repoId, file.path],
    queryFn: () => repoApi.file(file.repoId, file.path),
    retry: false,
  });
  const pipeline = file.pipeline;

  return (
    <>
      <DrawerHeader
        icon={<FileCode2 className="size-4" />}
        title={<h2 className="truncate text-[13px] leading-5 font-semibold">{file.path.slice(file.path.lastIndexOf("/") + 1)}</h2>}
        subtitle={file.path}
        actions={pipeline !== null && (
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="outline"
                size="xs"
                onClick={() => {
                  onClose();
                  navigate(`/pipelines/${pipeline.id}`);
                }}
                data-testid="code-window-open-pipeline"
              >
                <ExternalLink />
                Open pipeline
              </Button>
            </TooltipTrigger>
            <TooltipContent>{`Open ${pipeline.name}: its runs, schedules and what it reads and writes`}</TooltipContent>
          </Tooltip>
        )}
        maximized={maximized}
        onMaximizedChange={onMaximizedChange}
        onClose={onClose}
        maximizeHint="Maximize: hide the page and read the code alone"
        restoreHint="Restore: show the page again"
        closeHint="Close"
        testIdPrefix="code-window"
      />
      <div className="flex min-h-0 flex-1 flex-col">
        <FileBody query={query} file={file} />
      </div>
    </>
  );
}

/** The file's code, or why it is not shown: still being read, not readable, binary, or a file that holds credentials. */
function FileBody({ query, file }: { query: UseQueryResult<RepoFile>; file: OpenedRepoFile }) {
  if (query.isPending) {
    return (
      <div className="flex flex-col gap-2 p-3" data-testid="code-window-loading">
        {[85, 70, 92, 60, 78].map((width) => <Skeleton key={width} className="h-4" style={{ width: `${width}%` }} />)}
      </div>
    );
  }

  if (query.isError) {
    const error = query.error;
    if (isApiError(error) && error.status === 403) {
      return (
        <EmptyState icon={<Lock />} title={error.title} description={error.detail ?? undefined} data-testid="code-window-not-shown" />
      );
    }

    return (
      <div className="p-3">
        {isApiError(error)
          ? <CorrelationError error={error} data-testid="code-window-error" />
          : <p className="text-[13px] text-destructive" data-testid="code-window-error">{String(error)}</p>}
      </div>
    );
  }

  const read = query.data;
  const language = languageOfPath(file.path);
  const source = read.branch !== null && read.commitSha !== null
    ? `branch ${read.branch} at ${read.commitSha.slice(0, 10)}`
    : "read from the synced folder";
  return (
    <>
      <p className="flex shrink-0 items-center gap-1.5 border-b px-3 py-1 text-[11.5px] text-muted-foreground" data-testid="code-window-facts">
        <span>{formatBytes(read.sizeBytes)}</span>
        <span aria-hidden>·</span>
        <span>{read.binary ? "binary" : languageName(language)}</span>
        <span aria-hidden>·</span>
        <span className="truncate">{source}</span>
      </p>
      {read.binary || read.text === null ? (
        <EmptyState
          icon={<FileWarning />}
          title="Binary file"
          description="Its bytes are not text, so the code window does not show them."
          data-testid="code-window-binary"
        />
      ) : (
        <>
          {read.truncated && (
            <p className="shrink-0 border-b bg-warning/10 px-3 py-1 text-[11.5px] text-warning" data-testid="code-window-truncated">
              {`Showing the first megabyte of ${formatBytes(read.sizeBytes)}; the rest is not shown.`}
            </p>
          )}
          <CodeView
            value={read.text}
            language={language}
            fill
            lsp={language === "yaml" && /^(flowType|documentType):/m.test(read.text)}
            data-testid="code-window-code"
          />
        </>
      )}
    </>
  );
}
