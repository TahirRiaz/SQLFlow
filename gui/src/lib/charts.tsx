// What every chart in the product shares on top of Recharts: how it answers the pointer and the keyboard, and the order
// it paints in. Both keep the charts as they looked and behaved on Recharts 2, which Recharts 3 changed by default.

import { Curve, DefaultZIndexes, Rectangle, ZIndexLayer, type CurveProps, type RectangleProps } from "recharts";

/**
 * Props every chart element (BarChart, LineChart, ComposedChart) spreads.
 *
 * - No accessibility layer: Recharts 3 turns it on by default, which makes each chart a tab stop announced as an
 *   application, and no chart in the product is one.
 * - Pointer moves are handled as they arrive. Recharts 3 defers a move to the next animation frame, and when the
 *   pointer leaves the chart within that frame the deferred move lands after the leave and shows the tooltip again,
 *   with the pointer gone. Handled at once, the leave is always the last word.
 */
export const chartBehaviour = {
  accessibilityLayer: false,
  throttledEvents: [],
} as const;

/**
 * The paint layers (Recharts 3 z-indexes) that keep the order Recharts 2 painted a chart in, the order its children are
 * written: grid, axes, hover cursor, then the marks. Recharts 3 paints its axes over the bars, a band cursor under the
 * axes and a line cursor over everything, and an axis takes no layer of its own, so the cursor and the marks are
 * placed above the axes instead.
 */
export const chartLayers = {
  /** The hover cursor (BandCursor, LineCursor): over the axes, under the marks. */
  cursor: DefaultZIndexes.axis + 1,
  /** Bars: over the axes and the cursor. */
  bar: DefaultZIndexes.axis + 2,
  /** Lines: over the axes, the cursor and any bars they are drawn with; a line's dots and active dot stay on top. */
  line: DefaultZIndexes.axis + 3,
} as const;

/**
 * A bar chart's hover cursor, the band behind the hovered category, drawn in chartLayers.cursor. Given to a Tooltip as
 * its cursor element, it receives the props the Tooltip gives its own band (a Rectangle) on top of its own.
 */
export function BandCursor(props: RectangleProps) {
  return (
    <ZIndexLayer zIndex={chartLayers.cursor}>
      <Rectangle {...props} />
    </ZIndexLayer>
  );
}

/**
 * The hover cursor of a chart that is not a bar chart, a line across the hovered point, drawn in chartLayers.cursor.
 * Given to a Tooltip as its cursor element, it receives the props the Tooltip gives its own line (a Curve) on top of
 * its own.
 */
export function LineCursor(props: CurveProps) {
  return (
    <ZIndexLayer zIndex={chartLayers.cursor}>
      <Curve {...props} />
    </ZIndexLayer>
  );
}
