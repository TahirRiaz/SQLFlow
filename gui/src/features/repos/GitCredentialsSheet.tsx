import { useMutation, useQuery } from "@tanstack/react-query";
import { CircleOff, ExternalLink, Info, KeyRound, Loader2, ShieldCheck, TriangleAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { isApiError } from "../../api/client";
import { repoSourceApi } from "../../api/endpoints";
import type { GitHubAppCheck } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { StatePill } from "../../components/StatusBadge";
import { GITHUB_APP_GUIDE, GITHUB_APP_REFERENCE_EXAMPLE, hostCredentialSummary } from "./gitCredential";

const headClass = "h-8 whitespace-nowrap px-3 text-left text-xs font-medium text-muted-foreground";
const cellClass = "whitespace-nowrap px-3 py-1.5 text-[13px]";

function Row({ label, value }: { label: string; value: string | null | undefined }) {
  return (
    <div className="grid grid-cols-[110px_1fr] gap-2 text-[13px]">
      <span className="text-muted-foreground">{label}</span>
      <span className="break-all font-mono text-[12px]">{value == null || value === "" ? "-" : value}</span>
    </div>
  );
}

/**
 * The git credentials this control plane supplies to a repository source that names none: its GitHub App (from its
 * SQLFLOW_GITHUB_APP_* variables) and its SQLFLOW_GIT_TOKEN, in the order a sync uses them, with a check that asks
 * GitHub, as the App, whether its ID and key belong together and where it is installed. Never shows a secret.
 */
export function GitCredentialsSheet({ onClose }: { onClose: () => void }) {
  const status = useQuery({ queryKey: ["repo-credentials"], queryFn: repoSourceApi.credentials });
  const check = useMutation<GitHubAppCheck>({ mutationFn: repoSourceApi.checkGitHubApp });

  const app = status.data?.gitHubApp;
  const token = status.data?.hostToken;
  const appProblem = app?.problem != null && app.problem !== "" ? app.problem : null;

  return (
    <Sheet open onOpenChange={(open) => !open && onClose()}>
      <SheetContent className="w-full gap-0 sm:max-w-xl" data-testid="git-credentials-sheet">
        <SheetHeader>
          <SheetTitle>Git credentials</SheetTitle>
          <SheetDescription>
            What this control plane reads a repository with when its source names no credential reference.
          </SheetDescription>
        </SheetHeader>

        <div className="flex-1 overflow-y-auto px-4 pb-4">
          <div className="flex flex-col gap-4">
            {status.isError && (isApiError(status.error)
              ? <CorrelationError error={status.error} />
              : <p className="text-[13px] text-destructive">{String(status.error)}</p>)}
            {status.isPending && <Loader2 className="size-4 animate-spin text-muted-foreground" />}

            {status.data !== undefined && (
              <>
                <p className="text-[13px]" data-testid="git-credentials-summary">{hostCredentialSummary(status.data)}</p>

                <section className="flex flex-col gap-2" data-testid="git-credentials-app">
                  <div className="flex items-center gap-2">
                    <h3 className="text-sm font-medium">GitHub App</h3>
                    {appProblem !== null
                      ? <StatePill tone="warning" label="cannot be used" icon={TriangleAlert} testId="git-app-state" />
                      : app?.configured
                        ? <StatePill tone="success" label="configured" icon={ShieldCheck} testId="git-app-state" />
                        : <StatePill tone="muted" label="none" icon={CircleOff} testId="git-app-state" />}
                  </div>

                  {appProblem !== null && (
                    <Alert variant="destructive" data-testid="git-app-problem">
                      <TriangleAlert />
                      <AlertDescription className="break-words">{appProblem}</AlertDescription>
                    </Alert>
                  )}

                  {(app?.configured || appProblem !== null) && (
                    <div className="flex flex-col gap-1">
                      <Row label="App" value={app?.appId} />
                      <Row label="Private key" value={app?.keySource} />
                      <Row label="API" value={app?.apiUrl} />
                      <Row label="Reads" value={app === undefined ? null : `https://${app.host}/... repositories it is installed on`} />
                    </div>
                  )}

                  {!app?.configured && appProblem === null && (
                    <Alert data-testid="git-app-none">
                      <Info />
                      <AlertTitle>No GitHub App on this control plane</AlertTitle>
                      <AlertDescription>
                        <p>
                          Set <code className="font-mono text-[12px]">SQLFLOW_GITHUB_APP_ID</code> and{" "}
                          <code className="font-mono text-[12px]">SQLFLOW_GITHUB_APP_PRIVATE_KEY</code> (or{" "}
                          <code className="font-mono text-[12px]">SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH</code>) on the control
                          plane and every worker, or name an App in a source&apos;s credential reference:{" "}
                          <code className="break-all font-mono text-[12px]">{GITHUB_APP_REFERENCE_EXAMPLE}</code>.
                        </p>
                        <p className="mt-1">
                          How to create one, read-only: <code className="font-mono text-[12px]">{GITHUB_APP_GUIDE}</code>.
                        </p>
                      </AlertDescription>
                    </Alert>
                  )}

                  {app?.configured && (
                    <div className="flex flex-col gap-2">
                      <div>
                        <Button
                          variant="outline"
                          size="sm"
                          disabled={check.isPending}
                          onClick={() => check.mutate()}
                          data-testid="git-app-check"
                        >
                          {check.isPending ? <Loader2 className="animate-spin" /> : <KeyRound />}
                          Check with GitHub
                        </Button>
                      </div>
                      {check.isError && (isApiError(check.error)
                        ? <CorrelationError error={check.error} />
                        : <p className="text-[13px] text-destructive">{String(check.error)}</p>)}
                      {check.data !== undefined && <AppCheckResult result={check.data} />}
                    </div>
                  )}
                </section>

                <Separator />

                <section className="flex flex-col gap-2" data-testid="git-credentials-token">
                  <div className="flex items-center gap-2">
                    <h3 className="text-sm font-medium">Host token</h3>
                    {token?.configured
                      ? <StatePill tone="info" label="set" icon={KeyRound} testId="git-token-state" />
                      : <StatePill tone="muted" label="not set" icon={CircleOff} testId="git-token-state" />}
                  </div>
                  <p className="text-xs text-muted-foreground">
                    <code className="font-mono">SQLFLOW_GIT_TOKEN</code>
                    {token?.username ? <>, signing in as <code className="font-mono">{token.username}</code></> : null}
                    : read for a remote the GitHub App does not reach, when a source names no credential.
                  </p>
                </section>

                <Separator />

                <section className="flex flex-col gap-1 text-xs text-muted-foreground">
                  <p>A source that names no credential reference is read with:</p>
                  <ol className="list-decimal pl-5">
                    <li>this control plane&apos;s GitHub App, for a repository on its GitHub that it is installed on, with a token for that repository alone;</li>
                    <li>otherwise its SQLFLOW_GIT_TOKEN;</li>
                    <li>otherwise no credential, which serves a public repository.</li>
                  </ol>
                  <p>
                    This is the control plane&apos;s own environment. Each worker reads its own when it checks out the commit a run is
                    pinned to, so give the workers the same variables, or name the credential on the source, which reaches them with
                    every run.
                  </p>
                </section>
              </>
            )}
          </div>
        </div>
      </SheetContent>
    </Sheet>
  );
}

function AppCheckResult({ result }: { result: GitHubAppCheck }) {
  return (
    <div className="flex flex-col gap-2 rounded-md border border-border p-3" data-testid="git-app-check-result">
      <div className="flex flex-wrap items-center gap-2 text-[13px]">
        <ShieldCheck className="size-4 text-success" />
        <span>
          GitHub knows <span className="font-medium">{result.name}</span>
          {result.owner ? <>, owned by <span className="font-mono text-[12px]">{result.owner}</span></> : null}.
        </span>
        {result.htmlUrl && (
          <a className="inline-flex items-center gap-1 text-xs text-primary hover:underline" href={result.htmlUrl} target="_blank" rel="noreferrer">
            Open on GitHub
            <ExternalLink className="size-3" />
          </a>
        )}
      </div>
      {result.installations.length === 0 ? (
        <p className="text-[13px] text-warning" data-testid="git-app-no-installations">
          The App is installed nowhere: install it on the repositories it should read.
        </p>
      ) : (
        <table className="w-full" data-testid="git-app-installations">
          <thead>
            <tr className="border-b border-border">
              <th className={headClass}>Installed on</th>
              <th className={headClass}>Repositories</th>
              <th className={headClass}>Installation</th>
              <th className={headClass}>State</th>
            </tr>
          </thead>
          <tbody>
            {result.installations.map((installation) => (
              <tr key={installation.id} className="border-b border-border last:border-b-0">
                <td className={cellClass}>
                  <span className="font-mono text-[12px]">{installation.account}</span>
                  {installation.accountType ? <span className="ml-1 text-xs text-muted-foreground">{installation.accountType.toLowerCase()}</span> : null}
                </td>
                <td className={cellClass}>{installation.repositorySelection === "all" ? "all" : "selected"}</td>
                <td className={`${cellClass} font-mono text-[12px]`}>{installation.id}</td>
                <td className={cellClass}>
                  {installation.suspendedUtc
                    ? <StatePill tone="warning" label="suspended" icon={TriangleAlert} testId="git-app-installation-state" />
                    : <StatePill tone="success" label="active" icon={ShieldCheck} testId="git-app-installation-state" />}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {result.moreInstallations && (
        <p className="text-xs text-muted-foreground">The App has more installations than are listed here.</p>
      )}
    </div>
  );
}
