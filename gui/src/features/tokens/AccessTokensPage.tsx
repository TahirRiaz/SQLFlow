import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Clock3, KeyRound, Loader2, Power, PowerOff, TriangleAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
  Sheet, SheetContent, SheetFooter, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { isApiError } from "../../api/client";
import { tokenApi } from "../../api/endpoints";
import type { AccessToken, CreatedAccessToken } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CopyButton } from "../../components/CopyButton";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { StatePill } from "../../components/StatusBadge";
import { parseUtc } from "../../lib/time";

/** One error-to-text mapping for every toast on this page (the API's detail wins over a generic title). */
function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : String(error);
}

/** The lifecycle state a token is in, derived from its revoked/expires timestamps. */
type TokenState = "active" | "expired" | "revoked";

function tokenState(token: AccessToken, nowMs: number): TokenState {
  if (token.revokedUtc !== null) {
    return "revoked";
  }
  if (token.expiresUtc !== null && parseUtc(token.expiresUtc).getTime() <= nowMs) {
    return "expired";
  }

  return "active";
}

/**
 * The token lifecycle pill. A lifecycle is a state, not a run outcome, so it renders through the shared
 * `StatePill` (DESIGN.md 7.3): outlined chip and an on/off glyph, never the filled check mark that means a
 * run succeeded.
 */
function StateBadge({ state }: { state: TokenState }) {
  const { tone, Icon } = state === "active"
    ? { tone: "success" as const, Icon: Power }
    : state === "expired"
      ? { tone: "warning" as const, Icon: Clock3 }
      : { tone: "muted" as const, Icon: PowerOff };
  return <StatePill tone={tone} label={state} icon={Icon} testId="token-state" />;
}

const EXPIRY_OPTIONS: { label: string; days: number | null }[] = [
  { label: "No expiry", days: null },
  { label: "30 days", days: 30 },
  { label: "60 days", days: 60 },
  { label: "90 days", days: 90 },
  { label: "1 year", days: 365 },
];

/** The one-time reveal of a freshly created secret: the only moment the full token is ever shown. */
function SecretReveal({ created, onClose }: { created: CreatedAccessToken; onClose: () => void }) {
  return (
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full sm:max-w-xl" data-testid="token-secret-dialog">
        <SheetHeader>
          <SheetTitle>Copy your new token</SheetTitle>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4">
          <Alert>
            <TriangleAlert className="text-warning" />
            <AlertTitle>Shown only once</AlertTitle>
            <AlertDescription>
              This is the only time the token is shown. Copy it now and store it somewhere safe; you cannot
              see it again.
            </AlertDescription>
          </Alert>
          <div className="flex items-start justify-between gap-2 rounded-md border border-border bg-muted/50 p-2">
            <code className="min-w-0 break-all font-mono text-[12px] leading-5" data-testid="token-secret-value">
              {created.secret}
            </code>
            <CopyButton label="Copy" text={created.secret} testId="token-secret-copy" />
          </div>
          <p className="text-[13px] text-muted-foreground">
            Use it as a bearer credential: send the header{" "}
            <code className="font-mono text-[12px]">Authorization: Bearer {created.token.prefix}...</code>{" "}
            to the control plane API.
          </p>
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button size="sm" onClick={onClose} data-testid="token-secret-done">Done</Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

/** The create-token form in a right side sheet (DESIGN.md 7.4); the old dialog's testid stays on the content. */
function CreateTokenSheet({
  ownScopes,
  onCreated,
  onClose,
}: {
  ownScopes: string[];
  onCreated: (created: CreatedAccessToken) => void;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [scopes, setScopes] = useState<string[]>(ownScopes);
  const [expiryDays, setExpiryDays] = useState<number | null>(null);

  const create = useMutation({
    mutationFn: () => tokenApi.create({
      name: name.trim(),
      scopes,
      expiresInDays: expiryDays,
    }),
    onSuccess: (created) => {
      toast.success(`Token "${created.token.name}" created.`);
      void queryClient.invalidateQueries({ queryKey: ["access-tokens"] });
      onCreated(created);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const toggleScope = (scope: string) =>
    setScopes((current) =>
      current.includes(scope) ? current.filter((s) => s !== scope) : [...current, scope]);

  const canSubmit = name.trim() !== "" && scopes.length > 0 && !create.isPending;

  return (
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next && !create.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full sm:max-w-xl" data-testid="create-token-dialog">
        <SheetHeader>
          <SheetTitle>Create personal access token</SheetTitle>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4">
          {create.isError && isApiError(create.error) && <CorrelationError error={create.error} />}
          {create.isError && !isApiError(create.error) && (
            <p className="text-[13px] text-destructive">{String(create.error)}</p>
          )}

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="create-token-name">Name</Label>
            <Input
              id="create-token-name"
              required
              maxLength={200}
              placeholder="e.g. vscode-laptop"
              value={name}
              onChange={(e) => setName(e.target.value)}
              className="h-8"
              data-testid="create-token-name"
            />
          </div>

          <div className="flex flex-col gap-1.5">
            <Label>Scopes</Label>
            <p className="text-xs text-muted-foreground">
              A token can grant at most the scopes your own account holds.
            </p>
            <div className="flex flex-col gap-2 pt-1">
              {ownScopes.map((scope) => (
                <Label key={scope} className="flex items-center gap-2 text-[13px] font-normal">
                  <Checkbox
                    checked={scopes.includes(scope)}
                    onCheckedChange={() => toggleScope(scope)}
                    data-testid={`create-token-scope-${scope}`}
                  />
                  <span className="font-mono text-[12px]">{scope}</span>
                </Label>
              ))}
            </div>
            {scopes.length === 0 && (
              <p className="text-xs text-destructive">Select at least one scope.</p>
            )}
          </div>

          <div className="flex flex-col gap-1.5">
            <Label>Expiry</Label>
            <Select
              value={expiryDays === null ? "none" : String(expiryDays)}
              onValueChange={(value) => setExpiryDays(value === "none" ? null : Number(value))}
            >
              <SelectTrigger size="sm" className="h-8 w-full" data-testid="create-token-expiry">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {EXPIRY_OPTIONS.map((option) => (
                  <SelectItem key={option.label} value={option.days === null ? "none" : String(option.days)}>
                    {option.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button
            variant="ghost"
            size="sm"
            onClick={onClose}
            disabled={create.isPending}
            data-testid="create-token-cancel"
          >
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => create.mutate()}
            disabled={!canSubmit}
            data-testid="create-token-submit"
          >
            {create.isPending && <Loader2 className="animate-spin" />}
            Create
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

const TOKEN_HEADERS = ["Name", "Prefix", "Scopes", "Status", "Last used", "Expires", "Created", "Actions"];

/**
 * Self-service management of the signed-in user's personal access tokens: long-lived bearer credentials for headless
 * clients (the CLI, the VSCode extension, automation) that cannot hold the browser's short session token.
 */
export default function AccessTokensPage() {
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const ownScopes = useMemo(() => session?.scopes ?? [], [session]);

  const [createOpen, setCreateOpen] = useState(false);
  const [secret, setSecret] = useState<CreatedAccessToken | null>(null);
  const [revokeTarget, setRevokeTarget] = useState<AccessToken | null>(null);

  const tokensQuery = useQuery({ queryKey: ["access-tokens"], queryFn: tokenApi.list });
  const tokens = tokensQuery.data;
  const nowMs = Date.now();

  const revoke = useMutation({
    mutationFn: (token: AccessToken) => tokenApi.revoke(token.id),
    onSuccess: (_result, token) => {
      toast.success(`Token "${token.name}" revoked.`);
      void queryClient.invalidateQueries({ queryKey: ["access-tokens"] });
      setRevokeTarget(null);
    },
    onError: (error) => {
      toast.error(errorText(error));
      setRevokeTarget(null);
    },
  });

  return (
    <Page data-testid="page-access-tokens">
      <PageHeader
        title="Personal access tokens"
        subtitle="Use a token as a bearer credential to authenticate headless clients, such as the CLI or the VSCode extension, without signing in through the browser. Each token carries at most the scopes your account holds, and you can revoke any token here at any time."
        actions={(
          <Button size="sm" onClick={() => setCreateOpen(true)} data-testid="open-create-token">
            <KeyRound />
            Create token
          </Button>
        )}
      />

      {tokensQuery.isError && (
        isApiError(tokensQuery.error)
          ? <CorrelationError error={tokensQuery.error} />
          : <p className="text-[13px] text-destructive">{errorText(tokensQuery.error)}</p>
      )}

      {/* Hand-rolled on the ui table primitives (not DataTable) so each row keeps its `token-row` testid. */}
      <Card className="gap-0 overflow-hidden rounded-lg p-0">
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {TOKEN_HEADERS.map((header, i) => (
                <TableHead
                  key={header}
                  className={`h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground${i === TOKEN_HEADERS.length - 1 ? " text-right" : ""}`}
                >
                  {header}
                </TableHead>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {tokens === undefined && !tokensQuery.isError && Array.from({ length: 3 }, (_, i) => (
              <TableRow key={`skeleton-${i}`}>
                {TOKEN_HEADERS.map((header) => (
                  <TableCell key={header} className="px-3 py-2">
                    <Skeleton className="h-4 w-full" />
                  </TableCell>
                ))}
              </TableRow>
            ))}
            {tokens !== undefined && tokens.length === 0 && (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={TOKEN_HEADERS.length} className="border-0 p-0">
                  <EmptyState
                    icon={<KeyRound />}
                    title="You have no personal access tokens yet"
                    description="Create one to authenticate the CLI, the VSCode extension, or automation against the control plane API."
                    action={(
                      <Button size="sm" variant="outline" onClick={() => setCreateOpen(true)}>
                        Create token
                      </Button>
                    )}
                  />
                </TableCell>
              </TableRow>
            )}
            {tokens?.map((token) => {
              const state = tokenState(token, nowMs);
              return (
                <TableRow key={token.id} data-testid="token-row">
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px] font-medium">
                    {token.name}
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5">
                    <code className="font-mono text-[12px]">{token.prefix}...</code>
                  </TableCell>
                  <TableCell className="px-3 py-1.5">
                    <div className="flex flex-wrap gap-1">
                      {token.scopes.map((scope) => (
                        <Badge key={scope} variant="outline" className="font-mono text-[11px]">{scope}</Badge>
                      ))}
                    </div>
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5"><StateBadge state={state} /></TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                    <RelativeTime value={token.lastUsedUtc} />
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                    {token.expiresUtc === null
                      ? <span className="text-muted-foreground">never</span>
                      : <RelativeTime value={token.expiresUtc} />}
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                    <RelativeTime value={token.createdUtc} />
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-right">
                    <Button
                      variant="ghost"
                      size="xs"
                      className="text-destructive hover:text-destructive"
                      disabled={state === "revoked"}
                      onClick={() => setRevokeTarget(token)}
                      data-testid="token-revoke"
                    >
                      Revoke
                    </Button>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </Card>

      {createOpen && (
        <CreateTokenSheet
          ownScopes={ownScopes}
          onCreated={(created) => {
            setCreateOpen(false);
            setSecret(created);
          }}
          onClose={() => setCreateOpen(false)}
        />
      )}

      {secret !== null && <SecretReveal created={secret} onClose={() => setSecret(null)} />}

      <ConfirmDialog
        open={revokeTarget !== null}
        title="Revoke token"
        message={`Revoke "${revokeTarget?.name ?? ""}"? Any client using it will stop working immediately.`}
        confirmLabel="Revoke"
        danger
        busy={revoke.isPending}
        onConfirm={() => {
          if (revokeTarget !== null) {
            revoke.mutate(revokeTarget);
          }
        }}
        onClose={() => setRevokeTarget(null)}
      />
    </Page>
  );
}
