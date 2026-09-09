import { useEffect, useRef, useState, type MouseEvent } from "react";
import { Check, Copy } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

interface CopyButtonProps {
  label: string;
  /** The text to copy, or a producer for it when building the text eagerly on every render would be wasteful. */
  text: string | (() => string);
  testId: string;
  /** Render as a bare icon (no visible label) for inline use next to an id; the label moves to a tooltip. */
  iconOnly?: boolean;
}

/**
 * A copy-to-clipboard button with inline success feedback. Clipboard access can be denied (an http origin, a
 * restrictive browser policy), so a failure surfaces as feedback on the button instead of silently doing
 * nothing.
 */
export function CopyButton({ label, text, testId, iconOnly = false }: CopyButtonProps) {
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => () => {
    if (timer.current !== null) {
      clearTimeout(timer.current);
    }
  }, []);

  const copy = async (event: MouseEvent<HTMLButtonElement>) => {
    // The button often sits inside a clickable row; a copy must not also trigger the row's navigation.
    event.stopPropagation();

    try {
      await navigator.clipboard.writeText(typeof text === "function" ? text() : text);
      setState("copied");
    } catch {
      setState("failed");
    }

    if (timer.current !== null) {
      clearTimeout(timer.current);
    }

    timer.current = setTimeout(() => setState("idle"), 1800);
  };

  // The compact form: an icon-only ghost button that sits flush against an id, with its label and any
  // clipboard failure carried by the tooltip instead of a visible caption.
  if (iconOnly) {
    return (
      <Tooltip>
        <TooltipTrigger asChild>
          <Button
            variant="ghost"
            size="icon-xs"
            onClick={(event) => void copy(event)}
            data-testid={testId}
            aria-label={label}
            className={
              state === "copied"
                ? "text-success hover:text-success"
                : state === "failed"
                  ? "text-destructive hover:text-destructive"
                  : "text-muted-foreground"
            }
          >
            {state === "copied" ? <Check /> : <Copy />}
          </Button>
        </TooltipTrigger>
        <TooltipContent>
          {state === "copied" ? "Copied" : state === "failed" ? "The browser denied clipboard access." : label}
        </TooltipContent>
      </Tooltip>
    );
  }

  const button = (
    <Button
      variant="ghost"
      size="sm"
      onClick={(event) => void copy(event)}
      data-testid={testId}
      className={state === "failed" ? "text-destructive hover:text-destructive" : undefined}
    >
      {state === "copied" ? <Check /> : <Copy />}
      {state === "copied" ? "Copied" : state === "failed" ? "Copy failed" : label}
    </Button>
  );

  if (state !== "failed") {
    return button;
  }

  return (
    <Tooltip>
      <TooltipTrigger asChild>{button}</TooltipTrigger>
      <TooltipContent>The browser denied clipboard access.</TooltipContent>
    </Tooltip>
  );
}
