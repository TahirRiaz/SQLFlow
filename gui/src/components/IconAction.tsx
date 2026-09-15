import type { ComponentProps, ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

interface IconActionProps extends Omit<ComponentProps<typeof Button>, "size" | "children" | "asChild" | "aria-label"> {
  /** What the action does; the tooltip and the button's accessible name. */
  label: string;
  icon: ReactNode;
}

/**
 * A row action as a glyph: an icon button whose label is its tooltip and its accessible name. A row carrying three
 * worded buttons spends a column on them on every line; the glyphs are recognisable, and the label is a hover away.
 */
export function IconAction({ label, icon, variant = "ghost", ...props }: IconActionProps) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        {/* A disabled button takes no pointer events, so the tooltip hangs off a wrapper that still does. */}
        <span className="inline-flex">
          <Button variant={variant} size="icon-xs" aria-label={label} {...props}>{icon}</Button>
        </span>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}
