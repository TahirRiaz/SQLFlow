import { useCallback, useRef } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { toast } from "sonner";
import { Download, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { cn } from "@/lib/utils";
import { downloadText } from "@/lib/download";
import { aiContextApi, semanticLayerApi } from "../../api/endpoints";
import { useAuth } from "../../auth/AuthContext";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { useScrollFit } from "../../hooks/useScrollFit";
import { AiContextsPanel } from "./AiContextsPanel";
import { ReportsPanel } from "./ReportsPanel";
import { SavedAnswersPanel } from "./SavedAnswersPanel";
import { SemanticObjectPanel } from "./SemanticObjectPanel";
import { SemanticTree } from "./SemanticTree";
import { errorText, FIT_BELOW, SEMANTIC_ROOT, useAiContexts } from "./shared";

type PageTab = "contexts" | "tables" | "examples" | "reports";

/** The file the earlier GUI content is saved as: a context file, ready to review and commit. */
const EARLIER_CONTENT_FILE = "earlier-gui-content.ai-context.yaml";

/** The tab a URL asks for; a link to an object with no tab named opens the Tables tab it lives on. */
const parseTab = (value: string | null, hasObject: boolean): PageTab =>
  value === "tables" || value === "examples" || value === "reports" || value === "contexts"
    ? value
    : hasObject ? "tables" : "contexts";

/** One fact of the header line: a count, quiet when it is zero, and what it counts. */
function Fact({ count, label, testId }: { count: number | undefined; label: string; testId: string }) {
  return (
    <span className="whitespace-nowrap" data-testid={testId}>
      <span className={cn("font-mono tabular-nums", count !== undefined && count > 0 && "text-foreground")}>
        {count === undefined ? "-" : count.toLocaleString()}
      </span>
      {` ${label}`}
    </span>
  );
}

/** The table explorer: the tree on the left, the chosen object on the right, both scrolling inside the room left. */
function TablesExplorer({ selectedKey, onSelect }: { selectedKey: string | null; onSelect: (key: string) => void }) {
  const explorer = useRef<HTMLDivElement>(null);
  useScrollFit(explorer, FIT_BELOW, 360, undefined, "height");

  return (
    <div ref={explorer} className="flex min-h-0 flex-col overflow-hidden rounded-lg border bg-card md:flex-row" data-testid="semantic-explorer">
      <div className="max-h-72 shrink-0 overflow-y-auto border-b p-2 md:max-h-none md:w-56 md:border-b-0 md:border-r xl:w-64">
        <SemanticTree selectedKey={selectedKey} onSelect={onSelect} />
      </div>
      <div className="min-h-0 min-w-0 flex-1 overflow-y-auto p-3" data-testid="semantic-object-pane">
        {selectedKey === null ? (
          <p className="text-[13px] text-muted-foreground" data-testid="semantic-details-placeholder">
            Select a table or view to see which of its columns the assistant may use, what its AI context says it means,
            and the joins, measures and examples around it. The badge beside each name is open/total columns.
          </p>
        ) : (
          <SemanticObjectPanel objectKey={selectedKey} />
        )}
      </div>
    </div>
  );
}

/**
 * The AI context page: what the AI assistant may use for business questions (those that start with "!"), read-only.
 * Everything it uses is defined in AI context files in source control and used only once an admin approves the exact
 * version a file proposes, so this page shows the contexts and where each stands (approved, changed, awaiting approval,
 * invalid), lets an admin approve or revoke one, and shows what the approved contexts serve table by table, the saved
 * answers the assistant reuses, and the Power BI reports it learns business terms from. Nothing is edited here: a
 * context is changed in its file. The tab and the selected table live in the URL (?tab=, ?object=), so both are
 * deep-linkable.
 */
export default function SemanticLayerPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const selectedKey = searchParams.get("object");
  const tab = parseTab(searchParams.get("tab"), selectedKey !== null);
  const { hasScope } = useAuth();

  const contexts = useAiContexts();
  const overview = useQuery({ queryKey: [SEMANTIC_ROOT, "overview"], queryFn: () => semanticLayerApi.overview() });

  const download = useMutation({
    mutationFn: () => aiContextApi.earlierContent(),
    onSuccess: (text) => {
      downloadText(EARLIER_CONTENT_FILE, text, "application/yaml");
      toast.success(`Saved ${EARLIER_CONTENT_FILE}.`, {
        description: "Review it, keep closed what should stay closed, then commit it to a repository and approve it here.",
      });
    },
    onError: (error) => toast.error(`The earlier content was not downloaded: ${errorText(error)}`),
  });

  const setParams = useCallback((changes: Record<string, string | null>) => {
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      for (const [name, value] of Object.entries(changes)) {
        if (value === null) {
          next.delete(name);
        } else {
          next.set(name, value);
        }
      }
      return next;
    });
  }, [setSearchParams]);

  const openObject = useCallback((key: string) => setParams({ tab: "tables", object: key }), [setParams]);

  const list = contexts.data?.contexts;
  const approved = list?.filter((context) => context.approvedVersion != null).length;

  return (
    <Page data-testid="page-semantic-layer">
      <PageHeader
        title="AI context"
        subtitle={(
          <div className="flex flex-col gap-0.5">
            <span>
              What the AI assistant may use for questions that start with <span className="font-mono">!</span>. Contexts are
              defined in <span className="font-mono">*.ai-context.yaml</span> files and used only once an admin approves them.
            </span>
            <span className="flex flex-wrap gap-x-4 text-[12px]" data-testid="semantic-layer-stats">
              <Fact count={list?.length} label={list?.length === 1 ? "context" : "contexts"} testId="ai-context-count" />
              <Fact count={approved} label="approved" testId="ai-context-approved-count" />
              <Fact count={overview.data?.tableCount} label={overview.data?.tableCount === 1 ? "table served" : "tables served"} testId="ai-context-served-count" />
            </span>
          </div>
        )}
        actions={contexts.data?.hasEarlierContent === true && hasScope("admin") ? (
          <Button
            variant="outline"
            size="sm"
            onClick={() => download.mutate()}
            disabled={download.isPending}
            data-testid="ai-context-download-earlier"
          >
            {download.isPending ? <Loader2 className="animate-spin" /> : <Download />}
            Download earlier content
          </Button>
        ) : undefined}
      />

      <Tabs
        value={tab}
        onValueChange={(value) => setParams({ tab: value === "contexts" && selectedKey === null ? null : value })}
        className="gap-3"
      >
        <TabsList variant="line">
          <TabsTrigger value="contexts" data-testid="semantic-page-tab-contexts">Contexts</TabsTrigger>
          <TabsTrigger value="tables" data-testid="semantic-page-tab-tables">Tables</TabsTrigger>
          <TabsTrigger value="examples" data-testid="semantic-page-tab-examples">Saved answers</TabsTrigger>
          <TabsTrigger value="reports" data-testid="semantic-page-tab-reports">Power BI reports</TabsTrigger>
        </TabsList>

        <TabsContent value="contexts">
          <AiContextsPanel />
        </TabsContent>

        <TabsContent value="tables">
          <TablesExplorer selectedKey={selectedKey} onSelect={openObject} />
        </TabsContent>

        <TabsContent value="examples">
          <SavedAnswersPanel />
        </TabsContent>

        <TabsContent value="reports">
          <ReportsPanel />
        </TabsContent>
      </Tabs>
    </Page>
  );
}
