import { CircleAlert, Copy } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import type { ApiError } from "../api/client";

/**
 * The one way API failures render (DESIGN.md 8.3): the RFC 7807 title/detail plus the correlation id
 * (copyable), so what a user reports maps straight to a server log line.
 */
export function CorrelationError({ error, "data-testid": testId }: { error: ApiError; "data-testid"?: string }) {
  return (
    <Alert variant="destructive" data-testid={testId ?? "api-error"}>
      <CircleAlert />
      <AlertTitle>{error.title}</AlertTitle>
      <AlertDescription>
        {error.detail && <p>{error.detail}</p>}
        {error.correlationId && (
          <span className="inline-flex items-center gap-1 text-xs text-muted-foreground">
            Correlation id: <code className="font-mono">{error.correlationId}</code>
            <Tooltip>
              <TooltipTrigger asChild>
                <button
                  aria-label="Copy correlation id"
                  onClick={() => void navigator.clipboard.writeText(error.correlationId ?? "")}
                  className="rounded-sm p-0.5 hover:bg-muted hover:text-foreground"
                >
                  <Copy className="size-3.5" />
                </button>
              </TooltipTrigger>
              <TooltipContent>Copy correlation id</TooltipContent>
            </Tooltip>
          </span>
        )}
      </AlertDescription>
    </Alert>
  );
}
