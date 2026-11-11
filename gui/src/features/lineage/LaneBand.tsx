import { memo } from "react";
import type { Node, NodeProps } from "@xyflow/react";
import { cn } from "@/lib/utils";
import type { Orientation } from "./graphLayout";

export interface LaneData extends Record<string, unknown> {
  /** What the band is: "Wave 3" when every flow in it runs in that wave, empty otherwise. */
  label: string;
  /** Every other band is shaded, so a band is followed across the drawing by eye. */
  shaded: boolean;
  orientation: Orientation;
}

export type LaneNode = Node<LaneData, "lane">;

/** The room a band's name takes beside (top to bottom) or above (left to right) its cards. */
export const LANE_LABEL_ROOM = 96;

/**
 * The backdrop of one band of the drawing: the flows of one wave with what they read first and what they write.
 * It names the wave, which is what a reader follows a chain by, and is never in the way: it takes no click, no
 * selection and no drag.
 */
function LaneBandInner({ data, width, height }: NodeProps<LaneNode>) {
  const down = data.orientation === "down";
  return (
    <div
      data-testid="graph-lane"
      aria-hidden
      style={{ width, height }}
      className={cn(
        "pointer-events-none rounded-md border border-dashed border-border/60",
        data.shaded ? "bg-muted/25" : "bg-transparent",
      )}
    >
      {data.label !== "" && (
        <span
          className={cn(
            // Larger than a card's text: a band is found from further out than a card is read from.
            "absolute text-[20px] font-semibold tracking-wide whitespace-nowrap text-muted-foreground",
            down ? "top-1/2 left-3 -translate-y-1/2" : "top-2 left-1/2 -translate-x-1/2",
          )}
        >
          {data.label}
        </span>
      )}
    </div>
  );
}

export const LaneBand = memo(LaneBandInner);
