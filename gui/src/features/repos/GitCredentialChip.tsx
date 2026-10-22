import { CircleOff, KeyRound, ShieldCheck, TriangleAlert, type LucideIcon } from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { GitCredential, GitCredentialKind } from "../../api/types";
import { StatePill } from "../../components/StatusBadge";
import { credentialLabel, credentialTone } from "./gitCredential";

const kindIcon: Record<GitCredentialKind, LucideIcon> = {
  "github-app": ShieldCheck,
  reference: KeyRound,
  "host-token": KeyRound,
  none: CircleOff,
};

const glyphTone = {
  success: "text-success",
  info: "text-primary",
  muted: "text-muted-foreground",
  warning: "text-warning",
} as const;

function problemOf(credential: GitCredential): string | null {
  return credential.problem != null && credential.problem !== "" ? credential.problem : null;
}

/** What the credential is, in words, and why it cannot be used when it cannot: the tooltip of the chip and the glyph. */
function CredentialWords({ credential }: { credential: GitCredential }) {
  const problem = problemOf(credential);
  return (
    <TooltipContent className="max-w-lg break-words">
      <span className="font-medium">{credentialLabel(credential)}</span>: read with {credential.summary}.
      {problem !== null && <span className="mt-1 block">{problem}</span>}
    </TooltipContent>
  );
}

/**
 * The credential a source reads with. Its kind (a GitHub App, a token, the host's token, none) is a category, not a
 * status, so it is a neutral chip led by a glyph in its tone; only a credential that cannot be used is a warning state.
 * The tooltip says what the credential is in words, and why it cannot be used.
 */
export function GitCredentialChip({ credential, testId = "git-credential" }: { credential: GitCredential | null | undefined; testId?: string }) {
  if (credential == null) {
    return <span className="text-muted-foreground">-</span>;
  }

  const Icon = kindIcon[credential.kind] ?? KeyRound;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex">
          {problemOf(credential) !== null ? (
            <StatePill tone="warning" label={credentialLabel(credential)} icon={TriangleAlert} testId={testId} />
          ) : (
            <span
              data-testid={testId}
              className="inline-flex items-center gap-1 rounded-md border border-border bg-transparent px-1.5 py-0.5 text-xs whitespace-nowrap text-foreground"
            >
              <Icon className={cn("size-3.5 shrink-0", glyphTone[credentialTone(credential)])} />
              {credentialLabel(credential)}
            </span>
          )}
        </span>
      </TooltipTrigger>
      <CredentialWords credential={credential} />
    </Tooltip>
  );
}

/**
 * The same credential as one glyph, for a grid with no room for a chip: beside a remote, it says at a glance how the
 * remote is read (a shield for a GitHub App, a key for a token, a crossed circle for none, a warning when it cannot be
 * used), and its tooltip carries the words.
 */
export function GitCredentialGlyph({ credential, testId = "git-credential" }: { credential: GitCredential | null | undefined; testId?: string }) {
  if (credential == null) {
    return null;
  }

  const problem = problemOf(credential) !== null;
  const Icon = problem ? TriangleAlert : kindIcon[credential.kind] ?? KeyRound;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span
          data-testid={testId}
          role="img"
          aria-label={`Credential: ${credentialLabel(credential)}`}
          className="inline-flex size-5 shrink-0 items-center justify-center"
        >
          <Icon className={cn("size-3.5", problem ? "text-warning" : glyphTone[credentialTone(credential)])} />
        </span>
      </TooltipTrigger>
      <CredentialWords credential={credential} />
    </Tooltip>
  );
}
