import { Suspense } from "react";
import { Navigate, Route, Routes, useLocation } from "react-router-dom";
import LoginPage from "./auth/LoginPage";
import { TopProgressBar } from "./components/TopProgressBar";
import { RequireAuth, RequireScope } from "./auth/RequireAuth";
import AppShell from "./layout/AppShell";
import { lazyRoute } from "./lib/lazyRoute";

// Feature pages are lazy so heavy dependencies (Monaco, React Flow, Recharts) load with their page, not at boot.
const DashboardPage = lazyRoute("DashboardPage", () => import("./features/dashboard/DashboardPage"));
const ChatPage = lazyRoute("ChatPage", () => import("./features/chat/ChatPage"));
const InsightsPage = lazyRoute("InsightsPage", () => import("./features/insights/InsightsPage"));
const DataStreamsPage = lazyRoute("DataStreamsPage", () => import("./features/datastreams/DataStreamsPage"));
const RunsPage = lazyRoute("RunsPage", () => import("./features/runs/RunsPage"));
const RunDetailPage = lazyRoute("RunDetailPage", () => import("./features/runs/RunDetailPage"));
const RunGroupPage = lazyRoute("RunGroupPage", () => import("./features/runs/RunGroupPage"));
const NodesPage = lazyRoute("NodesPage", () => import("./features/nodes/NodesPage"));
const ReposPage = lazyRoute("ReposPage", () => import("./features/repos/ReposPage"));
const RepoDetailPage = lazyRoute("RepoDetailPage", () => import("./features/repos/RepoDetailPage"));
const PipelinesPage = lazyRoute("PipelinesPage", () => import("./features/pipelines/PipelinesPage"));
const PipelineDetailPage = lazyRoute("PipelineDetailPage", () => import("./features/pipelines/PipelineDetailPage"));
const SchedulesPage = lazyRoute("SchedulesPage", () => import("./features/schedules/SchedulesPage"));
const ScheduleTimelinePage = lazyRoute("ScheduleTimelinePage", () => import("./features/schedules/ScheduleTimelinePage"));
const DatasourcesPage = lazyRoute("DatasourcesPage", () => import("./features/datasources/DatasourcesPage"));
const IntegrationsPage = lazyRoute("IntegrationsPage", () => import("./features/integrations/IntegrationsPage"));
const DatasourceBrowsePage = lazyRoute("DatasourceBrowsePage", () => import("./features/datasources/DatasourceBrowsePage"));
const DiscoverPage = lazyRoute("DiscoverPage", () => import("./features/discover/DiscoverPage"));
const UniqueKeyDetectionPage = lazyRoute("UniqueKeyDetectionPage", () => import("./features/datasources/UniqueKeyDetectionPage"));
const CatalogPage = lazyRoute("CatalogPage", () => import("./features/catalog/CatalogPage"));
const SchemaChangesPage = lazyRoute("SchemaChangesPage", () => import("./features/catalog/SchemaChangesPage"));
const LineagePage = lazyRoute("LineagePage", () => import("./features/lineage/LineagePage"));
const SubscribersPage = lazyRoute("SubscribersPage", () => import("./features/subscribers/SubscribersPage"));
const LineageGraphPage = lazyRoute("LineageGraphPage", () => import("./features/lineage/LineageGraphPage"));
const SearchPage = lazyRoute("SearchPage", () => import("./features/search/SearchPage"));
const UsersPage = lazyRoute("UsersPage", () => import("./features/users/UsersPage"));
const AccessTokensPage = lazyRoute("AccessTokensPage", () => import("./features/tokens/AccessTokensPage"));
const NotificationsPage = lazyRoute("NotificationsPage", () => import("./features/notifications/NotificationsPage"));
const MaintenancePage = lazyRoute("MaintenancePage", () => import("./features/maintenance/MaintenancePage"));

/** The graph moved from /lineage/graph to /lineage; forward old links, preserving the repo/view query string. */
function LineageGraphRedirect() {
  const { search } = useLocation();
  return <Navigate to={`/lineage${search}`} replace />;
}

export default function App() {
  return (
    <Suspense fallback={<TopProgressBar />}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route
          element={(
            <RequireAuth>
              <AppShell />
            </RequireAuth>
          )}
        >
          <Route path="/" element={<DashboardPage />} />
          {/* The SQLFlow assistant: chat over the estate, tools served by the SQLFlow MCP server. */}
          <Route path="/chat" element={<ChatPage />} />
          <Route path="/insights" element={<InsightsPage />} />
          {/* Which tables have stopped receiving data, from the run history's own write statistics. */}
          <Route path="/datastreams" element={<DataStreamsPage />} />
          <Route path="/runs" element={<RunsPage />} />
          <Route path="/runs/groups/:groupId" element={<RunGroupPage />} />
          <Route path="/runs/:runId" element={<RunDetailPage />} />
          <Route path="/nodes" element={<NodesPage />} />
          <Route path="/repos" element={<ReposPage />} />
          <Route path="/repos/:repoId" element={<RepoDetailPage />} />
          <Route path="/pipelines" element={<PipelinesPage />} />
          <Route path="/pipelines/:pipelineId" element={<PipelineDetailPage />} />
          {/* Timeline route sits above the table route so the sidebar's Timeline entry resolves; both live under
              /schedules so the Schedules nav group stays highlighted. */}
          <Route path="/schedules/timeline" element={<ScheduleTimelinePage />} />
          <Route path="/schedules" element={<SchedulesPage />} />
          <Route path="/datasources" element={<DatasourcesPage />} />
          <Route path="/integrations" element={<IntegrationsPage />} />
          {/* The Integrations page briefly shipped as "REST APIs"; keep the old path working for bookmarks. */}
          <Route path="/rest-apis" element={<Navigate to="/integrations" replace />} />
          <Route path="/datasources/browse" element={<DatasourceBrowsePage />} />
          <Route path="/discover" element={<DiscoverPage />} />
          {/* Top-level path (not under /datasources) so the sidebar's startsWith selection stays unambiguous. */}
          <Route path="/key-detection" element={<UniqueKeyDetectionPage />} />
          {/* Repo sources merged into the Repos page; keep the old path working for bookmarks. */}
          <Route path="/repo-sources" element={<Navigate to="/repos" replace />} />
          {/* The explorer tree over every catalog object and flow, with per-node details. */}
          <Route path="/catalog" element={<CatalogPage />} />
          {/* The graph is the lineage landing; the searchable object catalog is the secondary explorer. */}
          <Route path="/lineage" element={<LineageGraphPage />} />
          <Route path="/lineage/objects" element={<LineagePage />} />
          {/* The consumption side: who reads the warehouse, and through which queries. */}
          <Route path="/schema-changes" element={<SchemaChangesPage />} />
          <Route path="/subscribers" element={<SubscribersPage />} />
          {/* The graph used to live here; keep the old link working, carrying any repo/view query through. */}
          <Route path="/lineage/graph" element={<LineageGraphRedirect />} />
          <Route path="/search" element={<SearchPage />} />
          {/* Self-service and available to any authenticated user; no scope guard beyond RequireAuth. */}
          <Route path="/settings/tokens" element={<AccessTokensPage />} />
          <Route path="/settings/notifications" element={<NotificationsPage />} />
          <Route path="/settings/maintenance" element={<MaintenancePage />} />
          <Route
            path="/users"
            element={(
              <RequireScope scope="admin">
                <UsersPage />
              </RequireScope>
            )}
          />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </Suspense>
  );
}
