import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { useExplain } from "./ExplainContext";
import { ExplainIcon } from "./ExplainIcon";

/**
 * Explain for the page in view, beside its title: the assistant says what the page is for, what it shows and what a
 * person does there. The page headers carry it (PageHeader, DetailHeaderCard), so it sits with the name of what it
 * explains rather than in the workbench's chrome, and a page drawing its own title row places it there itself. Drawn
 * only while the assistant is configured.
 */
export function ExplainPageButton({ size = "icon-xs", className }: { size?: "icon-xs" | "icon-sm"; className?: string }) {
  const explainer = useExplain();
  if (explainer === null) {
    return null;
  }

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button
          type="button"
          variant="ghost"
          size={size}
          aria-label="Explain this page"
          onClick={() => explainer.explainPage()}
          className={cn("shrink-0 text-muted-foreground hover:text-foreground", className)}
          data-testid="explain-page"
        >
          <ExplainIcon />
        </Button>
      </TooltipTrigger>
      <TooltipContent>Explain this page: the assistant says what it is for and what you do here</TooltipContent>
    </Tooltip>
  );
}
